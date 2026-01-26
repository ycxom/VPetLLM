using System.Collections.Concurrent;
using TTSServiceType = VPetLLM.Utils.Audio.TTSService;

namespace VPetLLM.Handlers.Core
{
    /// <summary>
    /// 统一的TTS处理器 - 整合所有内置TTS的下载、缓存、播放逻辑
    /// 
    /// 职责：
    /// 1. 管理TTS音频下载（第一个立即下载，其余按需下载）
    /// 2. 智能预下载策略（播放音频N时，自动下载音频N+1）
    /// 3. 音频缓存管理（避免重复下载，自动清理）
    /// 
    /// 使用流程：
    /// StreamingCommandProcessor → StartBatchDownload() → 启动第一个下载
    /// SmartMessageProcessor → GetAudioAsync() → 获取音频（等待下载完成）
    /// SmartMessageProcessor → OnAudioPlayStart() → 触发下一个预下载
    /// </summary>
    public class UnifiedTTSProcessor
    {
        private readonly TTSServiceType _ttsService;
        private readonly ConcurrentDictionary<string, AudioCacheEntry> _audioCache;
        private readonly object _lock = new object();
        
        private List<string>? _currentBatchTexts = null;
        private int _currentPlayingIndex = -1;
        
        private const int MAX_CACHE_SIZE = 20;
        private const int DOWNLOAD_TIMEOUT_MS = 30000;

        public UnifiedTTSProcessor(TTSServiceType ttsService)
        {
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
            _audioCache = new ConcurrentDictionary<string, AudioCacheEntry>();
            Logger.Log("UnifiedTTSProcessor: 初始化完成");
        }

        #region 公共接口

        /// <summary>
        /// 开始批次下载（由 StreamingCommandProcessor 调用）
        /// 立即启动第一个音频的下载
        /// </summary>
        public Task<string?> StartBatchDownload(List<string> texts)
        {
            if (texts is null || texts.Count == 0)
                return Task.FromResult<string?>(null);

            lock (_lock)
            {
                _currentBatchTexts = texts;
                _currentPlayingIndex = -1;
                
                Logger.Log($"UnifiedTTSProcessor: 开始批次下载，共 {texts.Count} 个文本");
                
                if (texts.Count > 0 && !string.IsNullOrWhiteSpace(texts[0]))
                {
                    var firstText = texts[0];
                    
                    // 检查缓存
                    if (TryGetFromCache(firstText, out var cachedPath))
                    {
                        Logger.Log("UnifiedTTSProcessor: 第一个音频已在缓存中");
                        return Task.FromResult<string?>(cachedPath);
                    }
                    
                    // 启动下载
                    Logger.Log($"UnifiedTTSProcessor: 启动第一个音频下载: {Truncate(firstText, 20)}");
                    return StartDownloadAsync(firstText, 0);
                }
            }
            
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// 获取音频文件（由 SmartMessageProcessor 调用）
        /// 等待下载完成或立即启动下载
        /// </summary>
        public async Task<string?> GetAudioAsync(string text, int index)
        {
            var cacheKey = GetCacheKey(text);
            
            // 检查缓存
            if (_audioCache.TryGetValue(cacheKey, out var entry))
            {
                // 等待下载完成
                if (entry.DownloadTask is not null && !entry.DownloadTask.IsCompleted)
                {
                    Logger.Log($"UnifiedTTSProcessor: 等待音频下载完成 #{index}");
                    
                    try
                    {
                        using var cts = new CancellationTokenSource(DOWNLOAD_TIMEOUT_MS);
                        await entry.DownloadTask.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log($"UnifiedTTSProcessor: 音频下载超时 #{index}");
                        return null;
                    }
                }
                
                // 返回缓存的音频
                if (!string.IsNullOrEmpty(entry.AudioFilePath) && File.Exists(entry.AudioFilePath))
                {
                    Logger.Log($"UnifiedTTSProcessor: 从缓存获取音频 #{index}");
                    entry.LastAccessTime = DateTime.Now;
                    return entry.AudioFilePath;
                }
            }
            
            // 缓存未命中，立即下载
            Logger.Log($"UnifiedTTSProcessor: 缓存未命中，立即下载 #{index}");
            return await StartDownloadAsync(text, index);
        }

        /// <summary>
        /// 通知开始播放（触发下一个音频预下载）
        /// </summary>
        public void OnAudioPlayStart(int index)
        {
            lock (_lock)
            {
                _currentPlayingIndex = index;
                
                // 检查是否有下一个音频
                if (_currentBatchTexts is null || index + 1 >= _currentBatchTexts.Count)
                    return;
                
                var nextText = _currentBatchTexts[index + 1];
                if (string.IsNullOrWhiteSpace(nextText))
                    return;
                
                var cacheKey = GetCacheKey(nextText);
                
                // 检查是否已在下载或已完成
                if (_audioCache.TryGetValue(cacheKey, out var entry))
                {
                    if (entry.IsDownloaded || (entry.DownloadTask is not null && !entry.DownloadTask.IsCompleted))
                    {
                        Logger.Log($"UnifiedTTSProcessor: 音频 #{index + 1} 已在下载或已完成");
                        return;
                    }
                }
                
                // 启动预下载
                Logger.Log($"UnifiedTTSProcessor: 播放驱动 - 启动音频 #{index + 1} 预下载");
                _ = StartDownloadAsync(nextText, index + 1);
            }
        }

        /// <summary>
        /// 通知播放完成
        /// </summary>
        public void OnAudioPlayComplete(int index)
        {
            Logger.Log($"UnifiedTTSProcessor: 音频 #{index} 播放完成");
        }

        /// <summary>
        /// 重置状态（新批次开始时调用）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentBatchTexts = null;
                _currentPlayingIndex = -1;
                Logger.Log("UnifiedTTSProcessor: 状态已重置");
            }
        }

        /// <summary>
        /// 清理所有缓存
        /// </summary>
        public void ClearCache()
        {
            lock (_lock)
            {
                foreach (var entry in _audioCache.Values)
                {
                    DeleteAudioFile(entry.AudioFilePath);
                }
                
                _audioCache.Clear();
                Logger.Log("UnifiedTTSProcessor: 缓存已清理");
            }
        }

        #endregion

        #region 内部实现

        /// <summary>
        /// 启动音频下载
        /// </summary>
        private async Task<string?> StartDownloadAsync(string text, int index)
        {
            var cacheKey = GetCacheKey(text);
            
            // 创建或获取缓存条目
            var entry = _audioCache.GetOrAdd(cacheKey, _ => new AudioCacheEntry
            {
                Text = text,
                CacheKey = cacheKey,
                CreatedTime = DateTime.Now,
                LastAccessTime = DateTime.Now
            });
            
            // 如果已有下载任务，直接返回
            if (entry.DownloadTask is not null)
                return await entry.DownloadTask;
            
            // 创建下载任务
            var downloadTask = DownloadAudioAsync(text, index);
            entry.DownloadTask = downloadTask;
            
            // 清理旧缓存
            CleanupOldCache();
            
            return await downloadTask;
        }

        /// <summary>
        /// 下载音频文件
        /// </summary>
        private async Task<string?> DownloadAudioAsync(string text, int index)
        {
            try
            {
                Logger.Log($"UnifiedTTSProcessor: 开始下载音频 #{index}: {Truncate(text, 30)}");
                
                var audioFile = await _ttsService.DownloadTTSAudioAsync(text);
                
                if (!string.IsNullOrEmpty(audioFile) && File.Exists(audioFile))
                {
                    Logger.Log($"UnifiedTTSProcessor: 音频下载成功 #{index}: {audioFile}");
                    
                    // 更新缓存
                    var cacheKey = GetCacheKey(text);
                    if (_audioCache.TryGetValue(cacheKey, out var entry))
                    {
                        entry.AudioFilePath = audioFile;
                        entry.IsDownloaded = true;
                        entry.LastAccessTime = DateTime.Now;
                    }
                    
                    return audioFile;
                }
                
                Logger.Log($"UnifiedTTSProcessor: 音频下载失败 #{index}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"UnifiedTTSProcessor: 音频下载异常 #{index}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 清理旧缓存（保持缓存大小在限制内）
        /// </summary>
        private void CleanupOldCache()
        {
            if (_audioCache.Count <= MAX_CACHE_SIZE)
                return;
            
            var entriesToRemove = _audioCache.Values
                .OrderBy(e => e.LastAccessTime)
                .Take(_audioCache.Count - MAX_CACHE_SIZE)
                .ToList();
            
            foreach (var entry in entriesToRemove)
            {
                if (_audioCache.TryRemove(entry.CacheKey, out var removed))
                {
                    DeleteAudioFile(removed.AudioFilePath);
                }
            }
            
            Logger.Log($"UnifiedTTSProcessor: 清理了 {entriesToRemove.Count} 个旧缓存");
        }

        /// <summary>
        /// 尝试从缓存获取音频
        /// </summary>
        private bool TryGetFromCache(string text, out string? audioPath)
        {
            var cacheKey = GetCacheKey(text);
            
            if (_audioCache.TryGetValue(cacheKey, out var entry) && 
                !string.IsNullOrEmpty(entry.AudioFilePath) && 
                File.Exists(entry.AudioFilePath))
            {
                audioPath = entry.AudioFilePath;
                return true;
            }
            
            audioPath = null;
            return false;
        }

        /// <summary>
        /// 删除音频文件
        /// </summary>
        private void DeleteAudioFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;
            
            try
            {
                File.Delete(filePath);
            }
            catch { }
        }

        /// <summary>
        /// 生成缓存键
        /// </summary>
        private string GetCacheKey(string text)
        {
            return $"{text}_{_ttsService.GetHashCode()}";
        }

        /// <summary>
        /// 截断文本用于日志
        /// </summary>
        private string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;
            
            return text.Substring(0, maxLength) + "...";
        }

        #endregion
    }

    /// <summary>
    /// 音频缓存条目
    /// </summary>
    public class AudioCacheEntry
    {
        public string Text { get; set; } = string.Empty;
        public string CacheKey { get; set; } = string.Empty;
        public string? AudioFilePath { get; set; }
        public bool IsDownloaded { get; set; }
        public Task<string?>? DownloadTask { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastAccessTime { get; set; }
    }
}
