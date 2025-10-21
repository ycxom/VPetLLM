using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 流式命令处理器 - 在流式传输过程中实时检测和处理完整的命令
    /// </summary>
    public class StreamingCommandProcessor
    {
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly Action<string> _onCompleteCommand;
        private int _lastProcessedIndex = 0;
        private readonly Queue<CommandTask> _commandQueue = new Queue<CommandTask>();
        private bool _isProcessing = false;
        private readonly object _lock = new object();
        private readonly VPetLLM _plugin;
        private readonly PluginTakeoverManager _takeoverManager = new PluginTakeoverManager();

        /// <summary>
        /// 命令任务，包含命令字符串和预下载的TTS音频文件路径
        /// </summary>
        private class CommandTask
        {
            public string Command { get; set; }
            public Task<string> TTSDownloadTask { get; set; }
        }

        public StreamingCommandProcessor(Action<string> onCompleteCommand, VPetLLM plugin = null)
        {
            _onCompleteCommand = onCompleteCommand;
            _plugin = plugin;
        }

        /// <summary>
        /// 添加新的文本片段并检测完整的命令
        /// 优先检测接管请求，确保流式接管能够正常工作
        /// </summary>
        /// <param name="chunk">新接收的文本片段</param>
        public async void AddChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            // 先添加到缓冲区
            _buffer.Append(chunk);
            
            // 优先检测接管请求（在处理完整命令之前）
            if (!_takeoverManager.IsTakingOver)
            {
                var currentBuffer = _buffer.ToString();
                
                // 检查是否有 plugin 接管请求
                var takeoverMatch = System.Text.RegularExpressions.Regex.Match(currentBuffer, @"\[:plugin\((\w+)");
                if (takeoverMatch.Success)
                {
                    var pluginName = takeoverMatch.Groups[1].Value;
                    
                    // 查找支持接管的插件
                    var plugin = _plugin?.Plugins.Find(p => 
                        p.Name.Replace(" ", "_").Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                        p is global::VPetLLM.Core.IPluginTakeover takeover && takeover.SupportsTakeover);

                    if (plugin is global::VPetLLM.Core.IPluginTakeover)
                    {
                        // 提取从 plugin 开始到当前的内容
                        var pluginStartIndex = takeoverMatch.Index;
                        var pluginContent = currentBuffer.Substring(pluginStartIndex);
                        
                        Utils.Logger.Log($"StreamingCommandProcessor: 检测到支持接管的插件 {pluginName}，准备启动接管");
                        
                        // 启动接管（传递完整的 plugin 命令内容）
                        var processedChunk = await _takeoverManager.ProcessChunkAsync(pluginContent);
                        
                        // 如果接管成功，从缓冲区移除已接管的内容
                        if (_takeoverManager.IsTakingOver)
                        {
                            _buffer.Clear();
                            _buffer.Append(currentBuffer.Substring(0, pluginStartIndex));
                            _lastProcessedIndex = 0;
                            Utils.Logger.Log($"StreamingCommandProcessor: 插件 {_takeoverManager.CurrentTakeoverPlugin} 开始接管");
                            return; // 接管成功，不再处理完整命令
                        }
                    }
                }
            }
            else
            {
                // 如果正在接管中，继续传递内容给接管插件
                await _takeoverManager.ProcessChunkAsync(chunk);
                return; // 接管中，不处理完整命令
            }
            
            // 只有在没有接管的情况下，才处理完整命令
            ProcessCompleteCommands();
        }

        /// <summary>
        /// 获取当前累积的完整文本
        /// </summary>
        public string GetFullText()
        {
            return _buffer.ToString();
        }

        /// <summary>
        /// 处理所有已完整接收的命令
        /// </summary>
        private void ProcessCompleteCommands()
        {
            var text = _buffer.ToString();
            var regex = new Regex(@"\[:([^\]]*)\]");
            var matches = regex.Matches(text);

            foreach (Match match in matches)
            {
                // 只处理在上次处理位置之后的命令
                if (match.Index >= _lastProcessedIndex)
                {
                    // 检查命令是否完整（包含闭合的括号）
                    var commandContent = match.Groups[1].Value;
                    if (IsCommandComplete(commandContent))
                    {
                        // 构造完整的命令字符串
                        var fullCommand = match.Value;
                        
                        // 检查是否是say/talk命令，如果是且TTS启用，则立即开始下载音频
                        Task<string> ttsDownloadTask = null;
                        var plugin = _plugin ?? VPetLLM.Instance;
                        if (plugin?.Settings?.TTS?.IsEnabled == true && plugin.TTSService != null)
                        {
                            var talkText = ExtractTalkText(fullCommand);
                            if (!string.IsNullOrEmpty(talkText))
                            {
                                Utils.Logger.Log($"StreamingCommandProcessor: 检测到say命令，立即开始下载TTS音频: {talkText.Substring(0, Math.Min(talkText.Length, 20))}...");
                                ttsDownloadTask = plugin.TTSService.DownloadTTSAudioAsync(talkText);
                            }
                        }
                        
                        // 将命令和TTS下载任务加入队列
                        lock (_lock)
                        {
                            _commandQueue.Enqueue(new CommandTask
                            {
                                Command = fullCommand,
                                TTSDownloadTask = ttsDownloadTask
                            });
                        }
                        
                        // 更新已处理的位置
                        _lastProcessedIndex = match.Index + match.Length;
                        
                        // 启动队列处理（如果还没有在处理）
                        _ = ProcessQueueAsync();
                    }
                }
            }
        }

        /// <summary>
        /// 从命令中提取talk文本内容
        /// </summary>
        private string ExtractTalkText(string command)
        {
            // 匹配 [:say("文本", ...)] 或 [:talk(say("文本", ...))]
            var sayPattern = @"\[:(?:say|talk)\((?:say\()?""([^""]+)""";
            var match = Regex.Match(command, sayPattern);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }
            return string.Empty;
        }

        /// <summary>
        /// 异步处理命令队列，确保命令按顺序执行
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            lock (_lock)
            {
                if (_isProcessing)
                    return; // 已经有处理任务在运行
                _isProcessing = true;
            }

            try
            {
                while (true)
                {
                    CommandTask commandTask;
                    lock (_lock)
                    {
                        if (_commandQueue.Count == 0)
                        {
                            _isProcessing = false;
                            break;
                        }
                        commandTask = _commandQueue.Dequeue();
                    }

                    var command = commandTask.Command;
                    
                    // 如果有TTS下载任务，等待下载完成
                    string audioFile = null;
                    if (commandTask.TTSDownloadTask != null)
                    {
                        try
                        {
                            Utils.Logger.Log($"StreamingCommandProcessor: 等待TTS音频下载完成: {command}");
                            audioFile = await commandTask.TTSDownloadTask;
                            if (!string.IsNullOrEmpty(audioFile))
                            {
                                Utils.Logger.Log($"StreamingCommandProcessor: TTS音频下载完成: {audioFile}");
                            }
                            else
                            {
                                Utils.Logger.Log($"StreamingCommandProcessor: TTS音频下载失败");
                            }
                        }
                        catch (Exception ex)
                        {
                            Utils.Logger.Log($"StreamingCommandProcessor: TTS音频下载异常: {ex.Message}");
                        }
                    }

                    // 执行命令（同步调用，等待完成）
                    Utils.Logger.Log($"StreamingCommandProcessor: 开始处理命令: {command}");
                    
                    // 获取插件实例（用于后续操作）
                    var pluginInstance = _plugin ?? VPetLLM.Instance;
                    
                    // 如果有预下载的音频文件，通过特殊方式传递给处理器
                    if (!string.IsNullOrEmpty(audioFile))
                    {
                        // 将音频文件路径临时存储，供SmartMessageProcessor使用
                        if (pluginInstance?.TalkBox?.MessageProcessor != null)
                        {
                            // 通过自定义属性传递音频文件路径
                            Utils.Logger.Log($"StreamingCommandProcessor: 传递预下载音频文件: {audioFile}");
                            SetPredownloadedAudio(command, audioFile);
                        }
                    }
                    
                    _onCompleteCommand?.Invoke(command);
                    
                    // 检查是否启用实况模式
                    bool isLiveMode = pluginInstance?.Settings?.EnableLiveMode ?? false;
                    
                    if (isLiveMode)
                    {
                        // 实况模式：直接返回，不等待命令完成
                        Utils.Logger.Log($"StreamingCommandProcessor: 实况模式 - 命令已发送，不等待完成: {command}");
                    }
                    else
                    {
                        // 队列模式：等待命令执行完成
                        Utils.Logger.Log($"StreamingCommandProcessor: 队列模式 - 开始等待命令完成: {command}");
                        await WaitForCommandCompleteAsync(command);
                        Utils.Logger.Log($"StreamingCommandProcessor: 队列模式 - 命令处理完成: {command}");
                    }
                }
            }
            catch
            {
                lock (_lock)
                {
                    _isProcessing = false;
                }
            }
        }

        /// <summary>
        /// 存储预下载的音频文件路径（用于流式处理）
        /// </summary>
        private static readonly Dictionary<string, string> _predownloadedAudioCache = new Dictionary<string, string>();
        private static readonly object _audioCacheLock = new object();

        private void SetPredownloadedAudio(string command, string audioFile)
        {
            lock (_audioCacheLock)
            {
                _predownloadedAudioCache[command] = audioFile;
            }
        }

        /// <summary>
        /// 获取并移除预下载的音频文件路径
        /// </summary>
        public static string GetAndRemovePredownloadedAudio(string command)
        {
            lock (_audioCacheLock)
            {
                if (_predownloadedAudioCache.TryGetValue(command, out var audioFile))
                {
                    _predownloadedAudioCache.Remove(command);
                    return audioFile;
                }
                return null;
            }
        }

        /// <summary>
        /// 智能等待命令执行完成
        /// </summary>
        private async Task WaitForCommandCompleteAsync(string command)
        {
            Utils.Logger.Log($"StreamingCommandProcessor.WaitForCommandCompleteAsync: 进入方法，命令: {command}");
            
            if (string.IsNullOrEmpty(command))
            {
                Utils.Logger.Log("StreamingCommandProcessor.WaitForCommandCompleteAsync: 命令为空，跳过");
                await Task.Delay(100);
                return;
            }

            // 尝试通过静态实例访问 MessageProcessor
            var plugin = _plugin ?? VPetLLM.Instance;
            Utils.Logger.Log($"StreamingCommandProcessor.WaitForCommandCompleteAsync: plugin = {(plugin != null ? "有效" : "null")}, TalkBox = {(plugin?.TalkBox != null ? "有效" : "null")}, MessageProcessor = {(plugin?.TalkBox?.MessageProcessor != null ? "有效" : "null")}");
            
            // 等待 SmartMessageProcessor 完成当前命令的处理
            if (plugin?.TalkBox?.MessageProcessor != null)
            {
                int maxWaitTime = 60000; // 最多等待 60 秒
                int checkInterval = 100; // 每 100ms 检查一次
                int elapsedTime = 0;

                while (plugin.TalkBox.MessageProcessor.IsProcessing && elapsedTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval);
                    elapsedTime += checkInterval;
                }

                if (elapsedTime > 0)
                {
                    Utils.Logger.Log($"StreamingCommandProcessor: 等待命令处理完成，耗时: {elapsedTime}ms");
                }
            }
            else
            {
                // 如果无法访问 MessageProcessor，使用传统的等待策略
                Utils.Logger.Log("StreamingCommandProcessor: 无法访问 MessageProcessor，使用传统等待策略");
                var match = Regex.Match(command, @"\[:(\w+)");
                if (!match.Success)
                {
                    await Task.Delay(100);
                    return;
                }

                var commandType = match.Groups[1].Value.ToLower();

                // 根据命令类型采用不同的等待策略
                switch (commandType)
                {
                    case "say":
                    case "talk":
                        // 语音命令：使用保守的延迟估算
                        await Task.Delay(3000);
                        break;
                    
                    case "action":
                    case "move":
                        // 动作命令：等待动画完成
                        await Task.Delay(1000);
                        break;
                    
                    case "buy":
                    case "happy":
                    case "health":
                    case "exp":
                        // 状态命令：短暂延迟
                        await Task.Delay(500);
                        break;
                    
                    default:
                        await Task.Delay(100);
                        break;
                }
            }
        }

        /// <summary>
        /// 等待语音播放完成（支持 VPetLLM TTS 和 EdgeTTS）
        /// </summary>
        private async Task WaitForVoiceCompleteAsync()
        {
            if (_plugin == null)
            {
                // 如果没有插件引用，使用保守的延迟估算
                await Task.Delay(2000);
                return;
            }

            try
            {
                // 1. 等待 VPetLLM 内置 TTS 播放完成
                if (_plugin.Settings?.TTS?.IsEnabled == true && _plugin.TTSService != null)
                {
                    int maxWaitTime = 30000; // 最多等待 30 秒
                    int checkInterval = 100; // 每 100ms 检查一次
                    int elapsedTime = 0;

                    while (_plugin.TTSService.IsPlaying && elapsedTime < maxWaitTime)
                    {
                        await Task.Delay(checkInterval);
                        elapsedTime += checkInterval;
                    }

                    if (elapsedTime > 0)
                    {
                        Utils.Logger.Log($"StreamingCommandProcessor: VPetLLM TTS 播放完成，等待时间: {elapsedTime}ms");
                    }
                }

                // 2. 等待 VPet 主程序的语音播放完成（EdgeTTS 或其他 TTS 插件）
                if (_plugin.MW?.Main != null)
                {
                    int maxWaitTime = 30000; // 最多等待 30 秒
                    int checkInterval = 100; // 每 100ms 检查一次
                    int elapsedTime = 0;

                    while (_plugin.MW.Main.PlayingVoice && elapsedTime < maxWaitTime)
                    {
                        await Task.Delay(checkInterval);
                        elapsedTime += checkInterval;
                    }

                    if (elapsedTime > 0)
                    {
                        Utils.Logger.Log($"StreamingCommandProcessor: VPet 语音播放完成，等待时间: {elapsedTime}ms");
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"StreamingCommandProcessor: 等待语音完成失败: {ex.Message}");
                // 发生异常时使用保守的延迟
                await Task.Delay(2000);
            }
        }

        /// <summary>
        /// 检查命令内容是否完整（括号是否匹配）
        /// </summary>
        private bool IsCommandComplete(string commandContent)
        {
            // 如果命令不包含括号，则认为是完整的
            if (!commandContent.Contains("("))
                return true;

            // 检查括号是否匹配
            int openCount = 0;
            int closeCount = 0;

            foreach (char c in commandContent)
            {
                if (c == '(')
                    openCount++;
                else if (c == ')')
                    closeCount++;
            }

            // 括号数量匹配且至少有一对括号
            return openCount > 0 && openCount == closeCount;
        }

        /// <summary>
        /// 重置处理器状态
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _lastProcessedIndex = 0;
                _commandQueue.Clear();
                _isProcessing = false;
            }
            _takeoverManager.Reset();
        }

        /// <summary>
        /// 获取接管管理器
        /// </summary>
        public PluginTakeoverManager TakeoverManager => _takeoverManager;
    }
}
