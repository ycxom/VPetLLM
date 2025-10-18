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
        private readonly Queue<string> _commandQueue = new Queue<string>();
        private bool _isProcessing = false;
        private readonly object _lock = new object();
        private readonly VPetLLM _plugin;

        public StreamingCommandProcessor(Action<string> onCompleteCommand, VPetLLM plugin = null)
        {
            _onCompleteCommand = onCompleteCommand;
            _plugin = plugin;
        }

        /// <summary>
        /// 添加新的文本片段并检测完整的命令
        /// </summary>
        /// <param name="chunk">新接收的文本片段</param>
        public void AddChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            _buffer.Append(chunk);
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
                        
                        // 将命令加入队列
                        lock (_lock)
                        {
                            _commandQueue.Enqueue(fullCommand);
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
                    string command;
                    lock (_lock)
                    {
                        if (_commandQueue.Count == 0)
                        {
                            _isProcessing = false;
                            break;
                        }
                        command = _commandQueue.Dequeue();
                    }

                    // 执行命令（同步调用，等待完成）
                    _onCompleteCommand?.Invoke(command);
                    
                    // 智能等待命令执行完成
                    await WaitForCommandCompleteAsync(command);
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
        /// 智能等待命令执行完成
        /// </summary>
        private async Task WaitForCommandCompleteAsync(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                await Task.Delay(100);
                return;
            }

            // 等待 SmartMessageProcessor 完成当前命令的处理
            if (_plugin?.TalkBox?.MessageProcessor != null)
            {
                int maxWaitTime = 60000; // 最多等待 60 秒
                int checkInterval = 100; // 每 100ms 检查一次
                int elapsedTime = 0;

                while (_plugin.TalkBox.MessageProcessor.IsProcessing && elapsedTime < maxWaitTime)
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
        }
    }
}
