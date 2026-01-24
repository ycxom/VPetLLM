using System.Collections.Concurrent;

namespace VPetLLM.Utils.Audio
{
    /// <summary>
    /// TTS 音频预加载管理器
    /// 实现音频顺序下载和缓存管理，优化用户体验
    /// 
    /// 工作流程：
    /// 1. 下载音频1 -> 播放音频1（后台顺序下载音频2、3、4...，每次间隔3秒）
    /// 2. 播放音频2（如果已下载完成，否则等待下载完成）
    /// 3. 播放音频3（如果已下载完成，否则等待下载完成）
    /// 4. 播放音频4（如果已下载完成，否则等待下载完成）
    /// 
    /// 注意：为避免服务器压力，音频下载采用顺序方式，每次下载之间间隔3秒
    /// </summary>
    public class TTSAudioPreloadManager
    {
        private readonly TTSService _ttsService;
        private readonly ConcurrentDictionary<string, AudioCacheEntry> _audioCache;
        private readonly object _cacheLock = new object();
        private const int MAX_CACHE_SIZE = 20; // 最多缓存20个音频文件
        private const int DOWNLOAD_INTERVAL_MS = 3000; // 下载间隔时间（毫秒）

        public TTSAudioPreloadManager(TTSService ttsService)
        {
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
            _audioCache = new ConcurrentDictionary<string, AudioCacheEntry>();
            Logger.Log("TTSAudioPreloadManager: 初始化完成");
        }

        /// <summary>
        /// 预加载多个文本的音频
        /// 顺序下载，每次下载之间间隔3秒，避免服务器压力过大
        /// </summary>
        /// <param name="texts">要预加载的文本列表</param>
        public void PreloadAudios(List<string> texts)
        {
            if (texts is null || texts.Count == 0)
            {
                return;
            }

            Logger.Log($"TTSAudioPreloadManager: 开始预加载 {texts.Count} 个音频（顺序下载，间隔3秒）");

            // 启动后台任务预加载音频
            _ = Task.Run(async () =>
            {
                try
                {
                    int downloadedCount = 0;

                    foreach (var text in texts)
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        // 检查是否已经在缓存中或正在下载
                        var cacheKey = GetCacheKey(text);
                        if (_audioCache.ContainsKey(cacheKey))
                        {
                            Logger.Log($"TTSAudioPreloadManager: 音频已在缓存中，跳过: {text.Substring(0, Math.Min(30, text.Length))}...");
                            continue;
                        }

                        // 如果不是第一个下载，等待间隔时间
                        if (downloadedCount > 0)
                        {
                            Logger.Log($"TTSAudioPreloadManager: 等待{DOWNLOAD_INTERVAL_MS / 1000}秒后下载下一个音频...");
                            await Task.Delay(DOWNLOAD_INTERVAL_MS);
                        }

                        // 下载音频
                        await DownloadAndCacheAudioAsync(text);
                        downloadedCount++;
                    }

                    Logger.Log($"TTSAudioPreloadManager: 所有音频预加载完成，共下载 {downloadedCount} 个");
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTSAudioPreloadManager: 预加载音频时发生错误: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 获取音频文件路径（如果未下载完成则等待）
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <param name="timeoutMs">超时时间（毫秒），默认30秒</param>
        /// <returns>音频文件路径，失败时返回null</returns>
        public async Task<string?> GetAudioAsync(string text, int timeoutMs = 30000)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var cacheKey = GetCacheKey(text);

            // 检查缓存
            if (_audioCache.TryGetValue(cacheKey, out var entry))
            {
                // 如果正在下载，等待下载完成
                if (entry.DownloadTask is not null && !entry.DownloadTask.IsCompleted)
                {
                    Logger.Log($"TTSAudioPreloadManager: 音频正在下载，等待完成: {text.Substring(0, Math.Min(30, text.Length))}...");

                    try
                    {
                        // 使用超时等待
                        using var cts = new CancellationTokenSource(timeoutMs);
                        await entry.DownloadTask.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log($"TTSAudioPreloadManager: 等待音频下载超时: {text.Substring(0, Math.Min(30, text.Length))}...");
                        return null;
                    }
                }

                // 返回音频文件路径
                if (!string.IsNullOrEmpty(entry.AudioFilePath) && File.Exists(entry.AudioFilePath))
                {
                    Logger.Log($"TTSAudioPreloadManager: 从缓存获取音频: {text.Substring(0, Math.Min(30, text.Length))}...");
                    entry.LastAccessTime = DateTime.Now;
                    return entry.AudioFilePath;
                }
            }

            // 缓存中没有，立即下载
            Logger.Log($"TTSAudioPreloadManager: 缓存未命中，立即下载: {text.Substring(0, Math.Min(30, text.Length))}...");
            return await DownloadAndCacheAudioAsync(text);
        }

        /// <summary>
        /// 下载并缓存音频
        /// </summary>
        private async Task<string?> DownloadAndCacheAudioAsync(string text)
        {
            var cacheKey = GetCacheKey(text);

            // 创建缓存条目
            var entry = new AudioCacheEntry
            {
                Text = text,
                CacheKey = cacheKey,
                CreatedTime = DateTime.Now,
                LastAccessTime = DateTime.Now
            };

            // 创建下载任务
            var downloadTask = Task.Run(async () =>
            {
                try
                {
                    Logger.Log($"TTSAudioPreloadManager: 开始下载音频: {text.Substring(0, Math.Min(30, text.Length))}...");
                    var audioPath = await _ttsService.DownloadTTSAudioAsync(text);

                    if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
                    {
                        entry.AudioFilePath = audioPath;
                        entry.IsDownloaded = true;
                        Logger.Log($"TTSAudioPreloadManager: 音频下载完成: {text.Substring(0, Math.Min(30, text.Length))}...");
                    }
                    else
                    {
                        Logger.Log($"TTSAudioPreloadManager: 音频下载失败: {text.Substring(0, Math.Min(30, text.Length))}...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTSAudioPreloadManager: 下载音频时发生错误: {ex.Message}");
                }
            });

            entry.DownloadTask = downloadTask;

            // 添加到缓存（如果已存在则更新）
            _audioCache.AddOrUpdate(cacheKey, entry, (key, oldEntry) => entry);

            // 清理旧缓存
            CleanupOldCache();

            // 等待下载完成
            await downloadTask;

            return entry.AudioFilePath;
        }

        /// <summary>
        /// 清理旧的缓存条目
        /// </summary>
        private void CleanupOldCache()
        {
            lock (_cacheLock)
            {
                if (_audioCache.Count <= MAX_CACHE_SIZE)
                {
                    return;
                }

                Logger.Log($"TTSAudioPreloadManager: 缓存已满({_audioCache.Count})，开始清理旧缓存");

                // 按最后访问时间排序，删除最旧的条目
                var entriesToRemove = _audioCache.Values
                    .OrderBy(e => e.LastAccessTime)
                    .Take(_audioCache.Count - MAX_CACHE_SIZE)
                    .ToList();

                foreach (var entry in entriesToRemove)
                {
                    if (_audioCache.TryRemove(entry.CacheKey, out var removedEntry))
                    {
                        // 删除音频文件
                        if (!string.IsNullOrEmpty(removedEntry.AudioFilePath) && File.Exists(removedEntry.AudioFilePath))
                        {
                            try
                            {
                                File.Delete(removedEntry.AudioFilePath);
                                Logger.Log($"TTSAudioPreloadManager: 删除旧音频文件: {removedEntry.AudioFilePath}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTSAudioPreloadManager: 删除音频文件失败: {ex.Message}");
                            }
                        }
                    }
                }

                Logger.Log($"TTSAudioPreloadManager: 清理完成，当前缓存数量: {_audioCache.Count}");
            }
        }

        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                Logger.Log($"TTSAudioPreloadManager: 清空所有缓存({_audioCache.Count}个条目)");

                foreach (var entry in _audioCache.Values)
                {
                    // 删除音频文件
                    if (!string.IsNullOrEmpty(entry.AudioFilePath) && File.Exists(entry.AudioFilePath))
                    {
                        try
                        {
                            File.Delete(entry.AudioFilePath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"TTSAudioPreloadManager: 删除音频文件失败: {ex.Message}");
                        }
                    }
                }

                _audioCache.Clear();
                Logger.Log("TTSAudioPreloadManager: 缓存已清空");
            }
        }

        /// <summary>
        /// 生成缓存键
        /// </summary>
        private string GetCacheKey(string text)
        {
            // 使用文本的哈希值作为缓存键
            using var sha256 = global::System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(global::System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// 音频缓存条目
        /// </summary>
        private class AudioCacheEntry
        {
            public string Text { get; set; } = string.Empty;
            public string CacheKey { get; set; } = string.Empty;
            public string? AudioFilePath { get; set; }
            public bool IsDownloaded { get; set; }
            public Task? DownloadTask { get; set; }
            public DateTime CreatedTime { get; set; }
            public DateTime LastAccessTime { get; set; }
        }
    }
}
