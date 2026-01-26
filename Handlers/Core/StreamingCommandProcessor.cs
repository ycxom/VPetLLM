using System.Text.RegularExpressions;
using VPetLLM.Handlers.Infrastructure;

namespace VPetLLM.Handlers.Core
{
    /// <summary>
    /// 流式命令处理器 - 在流式传输过程中实时检测和处理完整的命令
    /// 优化：支持命令批处理，减少UI更新频率
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
        private readonly PluginTakeoverManager _takeoverManager = new PluginTakeoverManager();

        // 命令批处理器
        private CommandBatcher _commandBatcher;
        private bool _useBatching = false;
        private int _batchWindowMs = 100;

        public StreamingCommandProcessor(Action<string> onCompleteCommand, VPetLLM plugin = null)
        {
            _onCompleteCommand = onCompleteCommand;
            _plugin = plugin;

            // 从设置中读取批处理配置
            InitializeBatching();
        }

        /// <summary>
        /// 初始化批处理配置
        /// </summary>
        private void InitializeBatching()
        {
            var pluginInstance = _plugin ?? VPetLLM.Instance;
            _useBatching = pluginInstance?.Settings?.EnableStreamingBatch ?? false;
            _batchWindowMs = pluginInstance?.Settings?.StreamingBatchWindowMs ?? 100;

            if (_useBatching)
            {
                _commandBatcher = new CommandBatcher(_batchWindowMs, OnBatchReady);
                Logger.Log($"StreamingCommandProcessor: 批处理模式已启用，窗口: {_batchWindowMs}ms");
            }
        }

        /// <summary>
        /// 批处理回调 - 处理一批命令
        /// </summary>
        private void OnBatchReady(List<string> commands)
        {
            if (commands is null || commands.Count == 0) return;

            Logger.Log($"StreamingCommandProcessor: 批处理回调，命令数: {commands.Count}");

            // 将命令添加到队列
            lock (_lock)
            {
                foreach (var command in commands)
                {
                    _commandQueue.Enqueue(command);
                }
            }

            // 启动队列处理
            _ = ProcessQueueAsync();
        }

        /// <summary>
        /// 添加新的文本片段并检测完整的命令
        /// 优先检测接管请求，确保流式接管能够正常工作
        /// </summary>
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

                // 检查是否有 plugin 接管请求 (只支持新格式)
                Match takeoverMatch = null;
                string pluginName = null;
                int pluginStartIndex = -1;

                // 尝试新格式
                var newFormatMatch = Regex.Match(currentBuffer, @"<\|\s*plugin\s*_begin\s*\|>\s*(\w+)");
                if (newFormatMatch.Success)
                {
                    takeoverMatch = newFormatMatch;
                    pluginName = newFormatMatch.Groups[1].Value;
                    pluginStartIndex = newFormatMatch.Index;
                }

                if (takeoverMatch is not null && !string.IsNullOrEmpty(pluginName))
                {
                    // 查找支持接管的插件
                    var plugin = _plugin?.Plugins.Find(p =>
                        p.Name.Replace(" ", "_").Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                        p is IPluginTakeover takeover && takeover.SupportsTakeover);

                    if (plugin is IPluginTakeover)
                    {
                        // 提取从 plugin 开始到当前的内容
                        var pluginContent = currentBuffer.Substring(pluginStartIndex);

                        Logger.Log($"StreamingCommandProcessor: 检测到支持接管的插件 {pluginName}，准备启动接管");

                        // 启动接管（传递完整的 plugin 命令内容）
                        var processedChunk = await _takeoverManager.ProcessChunkAsync(pluginContent);

                        // 如果接管成功，从缓冲区移除已接管的内容
                        if (_takeoverManager.IsTakingOver)
                        {
                            _buffer.Clear();
                            _buffer.Append(currentBuffer.Substring(0, pluginStartIndex));
                            _lastProcessedIndex = 0;
                            Logger.Log($"StreamingCommandProcessor: 插件 {_takeoverManager.CurrentTakeoverPlugin} 开始接管");
                            return;
                        }
                    }
                }
            }
            else
            {
                // 如果正在接管中，继续传递内容给接管插件
                await _takeoverManager.ProcessChunkAsync(chunk);
                return;
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
        /// 只支持新格式: <|command_type_begin|> ... <|command_type_end|>
        /// </summary>
        private void ProcessCompleteCommands()
        {
            var text = _buffer.ToString();
            int index = _lastProcessedIndex;

            // 检测并警告旧格式
            if (text.Contains("[:"))
            {
                Logger.Log("StreamingCommandProcessor: 警告 - 检测到旧格式命令 [:，已弃用。请使用新格式: <|command_type_begin|> ... <|command_type_end|>");
            }

            while (index < text.Length)
            {
                // 只查找新格式命令: <|xxx_begin|>
                int startIndex = text.IndexOf("<|", index);

                if (startIndex == -1)
                    break;

                // 跳过已处理的命令
                if (startIndex < _lastProcessedIndex)
                {
                    index = startIndex + 2;
                    continue;
                }

                // 解析新格式
                var command = ParseNewFormatCommand(text, startIndex);
                if (command is null)
                    break; // 不完整的命令，等待更多数据

                string fullCommand = command.FullMatch;
                string commandType = command.CommandType;
                _lastProcessedIndex = command.EndIndex + 1;

                // 记录检测到的命令
                Logger.Log($"StreamingCommandProcessor: 检测到完整命令类型: {commandType}, 格式: 新格式, 命令: {fullCommand.Substring(0, Math.Min(fullCommand.Length, 100))}...");

                // 根据是否启用批处理选择处理方式
                if (_useBatching && _commandBatcher is not null)
                {
                    _commandBatcher.AddCommand(fullCommand);
                }
                else
                {
                    lock (_lock)
                    {
                        _commandQueue.Enqueue(fullCommand);
                    }
                    _ = ProcessQueueAsync();
                }

                // 移动到下一个位置
                index = _lastProcessedIndex;
            }
        }

        /// <summary>
        /// 解析新格式命令: <|command_type_begin|> ... <|command_type_end|>
        /// </summary>
        private CommandMatch ParseNewFormatCommand(string text, int startIndex)
        {
            // 提取命令类型
            int typeStart = startIndex + 2;
            int typeEnd = text.IndexOf("_begin|>", typeStart);

            if (typeEnd == -1)
                return null;

            string commandType = text.Substring(typeStart, typeEnd - typeStart).Trim();

            // 查找结束标签
            string closingTag = $"<|{commandType}_end|>";
            int closingIndex = text.IndexOf(closingTag, typeEnd + 8);

            if (closingIndex == -1)
                return null;

            // 提取参数
            int paramsStart = typeEnd + 8;
            string parameters = text.Substring(paramsStart, closingIndex - paramsStart).Trim();

            // 提取完整匹配
            int endIndex = closingIndex + closingTag.Length - 1;
            string fullMatch = text.Substring(startIndex, endIndex - startIndex + 1);

            return new CommandMatch
            {
                CommandType = commandType,
                Parameters = parameters,
                FullMatch = fullMatch,
                StartIndex = startIndex,
                EndIndex = endIndex,
                Format = CommandFormat.New
            };
        }

        /// <summary>
        /// 异步处理命令队列，确保命令按顺序执行
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            lock (_lock)
            {
                if (_isProcessing)
                    return;
                _isProcessing = true;
            }

            try
            {
                var pluginInstance = _plugin ?? VPetLLM.Instance;

                while (true)
                {
                    string command;

                    lock (_lock)
                    {
                        if (_commandQueue.Count == 0)
                        {
                            _isProcessing = false;

                            // 队列为空，使用智能等待策略检查是否需要设置Idle状态
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(500).ConfigureAwait(false);

                                int maxWaitMs = 5000;
                                int elapsedMs = 500;

                                while (elapsedMs < maxWaitMs)
                                {
                                    bool hasActivity;
                                    lock (_lock)
                                    {
                                        hasActivity = _commandQueue.Count > 0 || _isProcessing;
                                    }

                                    if (hasActivity)
                                    {
                                        Logger.Log("StreamingCommandProcessor: 检测到新活动，退出Idle等待");
                                        return;
                                    }

                                    var activeSessionCount = pluginInstance?.FloatingSidebarManager?.ActiveSessionCount ?? 0;
                                    if (activeSessionCount > 0)
                                    {
                                        Logger.Log($"StreamingCommandProcessor: 检测到活动会话({activeSessionCount})，继续等待");
                                        await Task.Delay(500).ConfigureAwait(false);
                                        elapsedMs += 500;
                                        continue;
                                    }

                                    var processor = pluginInstance?.TalkBox?.MessageProcessor;
                                    if (processor is not null && processor.IsProcessing)
                                    {
                                        await Task.Delay(500).ConfigureAwait(false);
                                        elapsedMs += 500;
                                        continue;
                                    }

                                    break;
                                }

                                // 最终检查
                                bool shouldSetIdle;
                                lock (_lock)
                                {
                                    shouldSetIdle = _commandQueue.Count == 0 && !_isProcessing;
                                }

                                var finalActiveSessionCount = pluginInstance?.FloatingSidebarManager?.ActiveSessionCount ?? 0;
                                if (finalActiveSessionCount > 0)
                                {
                                    Logger.Log($"StreamingCommandProcessor: 最终检查发现活动会话({finalActiveSessionCount})，跳过设置Idle");
                                    return;
                                }

                                if (shouldSetIdle)
                                {
                                    var processor = pluginInstance?.TalkBox?.MessageProcessor;
                                    if (processor is null || !processor.IsProcessing)
                                    {
                                        Logger.Log("StreamingCommandProcessor: 所有命令处理完成，设置状态灯为Idle");
                                        pluginInstance?.FloatingSidebarManager?.SetIdleStatus();
                                    }
                                    else
                                    {
                                        Logger.Log("StreamingCommandProcessor: SmartMessageProcessor仍在处理中，跳过设置Idle");
                                    }
                                }
                                else
                                {
                                    Logger.Log("StreamingCommandProcessor: 检测到新命令或正在处理，跳过设置Idle");
                                }
                            });

                            break;
                        }
                        command = _commandQueue.Dequeue();
                    }

                    // 执行命令
                    Logger.Log($"StreamingCommandProcessor: 开始处理命令: {command}");
                    _onCompleteCommand?.Invoke(command);

                    // 检查是否启用实况模式
                    bool isLiveMode = pluginInstance?.Settings?.EnableLiveMode ?? false;

                    if (isLiveMode)
                    {
                        Logger.Log($"StreamingCommandProcessor: 实况模式 - 命令已发送，不等待完成: {command}");
                    }
                    else
                    {
                        Logger.Log($"StreamingCommandProcessor: 队列模式 - 开始等待命令完成: {command}");
                        await WaitForCommandCompleteAsync(command).ConfigureAwait(false);
                        Logger.Log($"StreamingCommandProcessor: 队列模式 - 命令处理完成: {command}");
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
        /// 智能等待命令执行完成
        /// </summary>
        private async Task WaitForCommandCompleteAsync(string command)
        {
            Logger.Log($"StreamingCommandProcessor.WaitForCommandCompleteAsync: 进入方法，命令: {command}");

            if (string.IsNullOrEmpty(command))
            {
                await Task.Delay(50).ConfigureAwait(false);
                return;
            }

            // 获取命令类型
            var match = Regex.Match(command, @"<\|\s*(\w+)\s*_begin\s*\|>");
            if (!match.Success)
            {
                await Task.Delay(50).ConfigureAwait(false);
                return;
            }
            
            var commandType = match.Groups[1].Value.ToLower();
            Logger.Log($"StreamingCommandProcessor.WaitForCommandCompleteAsync: 命令类型: {commandType}");

            var pluginInstance = _plugin ?? VPetLLM.Instance;

            // 对于plugin和tool命令，使用特殊的等待逻辑
            if (commandType == "plugin" || commandType == "tool")
            {
                Logger.Log($"StreamingCommandProcessor: 检测到{commandType}命令，使用特殊等待逻辑");
                await Task.Delay(50).ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);
                Logger.Log($"StreamingCommandProcessor: {commandType}命令等待完成");
                return;
            }

            // 等待 SmartMessageProcessor 完成当前命令的处理
            if (pluginInstance?.TalkBox?.MessageProcessor is not null)
            {
                await Task.Delay(20).ConfigureAwait(false);

                int maxWaitTime = 30000;
                int checkInterval = 100;
                int elapsedTime = 0;
                int startWaitTime = 0;

                // 等待消息开始处理
                while (!pluginInstance.TalkBox.MessageProcessor.IsProcessing && startWaitTime < 1000)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    startWaitTime += 50;
                }

                // 等待 FloatingSidebarManager 的活动会话结束（包括音频播放）
                while (pluginInstance.FloatingSidebarManager?.ActiveSessionCount > 0 && elapsedTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval).ConfigureAwait(false);
                    elapsedTime += checkInterval;
                }
            }
            else
            {
                // 如果无法访问 MessageProcessor，使用传统的等待策略
                Logger.Log("StreamingCommandProcessor: 无法访问 MessageProcessor，使用传统等待策略");

                switch (commandType)
                {
                    case "say":
                    case "talk":
                        await Task.Delay(500).ConfigureAwait(false);
                        break;
                    case "action":
                    case "move":
                        await Task.Delay(300).ConfigureAwait(false);
                        break;
                    case "buy":
                    case "happy":
                    case "health":
                    case "exp":
                        await Task.Delay(100).ConfigureAwait(false);
                        break;
                    case "plugin":
                    case "tool":
                        await Task.Delay(800).ConfigureAwait(false);
                        break;
                    default:
                        await Task.Delay(30).ConfigureAwait(false);
                        break;
                }
            }
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
            _commandBatcher?.Clear();
        }

        /// <summary>
        /// 刷新批处理器
        /// </summary>
        public void FlushBatch()
        {
            _commandBatcher?.Flush();
        }

        /// <summary>
        /// 设置批处理配置
        /// </summary>
        public void SetBatchingConfig(bool enabled, int windowMs = 100)
        {
            _useBatching = enabled;
            _batchWindowMs = windowMs;

            if (enabled && _commandBatcher is null)
            {
                _commandBatcher = new CommandBatcher(windowMs, OnBatchReady);
                Logger.Log($"StreamingCommandProcessor: 批处理模式已启用，窗口: {windowMs}ms");
            }
            else if (!enabled && _commandBatcher is not null)
            {
                _commandBatcher.Flush();
                _commandBatcher.Dispose();
                _commandBatcher = null;
                Logger.Log("StreamingCommandProcessor: 批处理模式已禁用");
            }
        }

        /// <summary>
        /// 获取接管管理器
        /// </summary>
        public PluginTakeoverManager TakeoverManager => _takeoverManager;

        /// <summary>
        /// 是否启用批处理
        /// </summary>
        public bool IsBatchingEnabled => _useBatching;

        /// <summary>
        /// 添加文本片段（用于统一流式处理）
        /// </summary>
        public void AddText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            AddChunk(text);
        }

        /// <summary>
        /// 完成文本输入（用于统一流式处理）
        /// </summary>
        public void Complete()
        {
            Logger.Log("StreamingCommandProcessor: Complete() 调用 - 统一流式处理完成");

            // 刷新批处理器
            if (_useBatching && _commandBatcher is not null)
            {
                _commandBatcher.Flush();
                Logger.Log("StreamingCommandProcessor: 批处理器已刷新");
            }

            // 处理任何剩余的完整命令
            ProcessCompleteCommands();

            Logger.Log("StreamingCommandProcessor: 统一流式处理完成");
        }
    }
}
