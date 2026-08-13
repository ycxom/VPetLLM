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
        private readonly Queue<string> _incomingChunks = new Queue<string>();
        private bool _isProcessing = false;
        private bool _pumpRunning;
        private readonly object _lock = new object();
        private readonly VPetLLM _plugin;
        private readonly PluginTakeoverManager _takeoverManager = new PluginTakeoverManager();

        // 命令批处理器
        private CommandBatcher _commandBatcher;
        // 中断丢弃日志只打一次的标记（上游是逐字符投喂）
        private bool _interruptLogged;
        private bool _oldFormatWarned;
        private bool _useBatching = false;
        private int _batchWindowMs = 100;
        private int _pluginScanFrom;
        // 已确认"插件名完整但查无此插件"的位置，回扫不再越过它，
        // 免得一个不存在的插件名把后续每一片都拖成全量正则扫描
        private int _pluginScanFloor;
        // 缓冲区里还有没扫完的命令开头：命令以 "|>" 收尾，收尾那个 chunk 往往不含 '<'，
        // 只看单个 chunk 会错过命令刚补全的那一刻
        private bool _hasPendingOpen;

        private static readonly Regex PluginBeginRegex = new(
            @"<\|\s*plugin\s*_begin\s*\|>\s*(\w+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 存活实例登记表。处理器是各 Provider / TalkBox 按请求现场 new 出来的局部对象，
        // 外面拿不到引用，中断时没法逐个通知；用弱引用登记既能广播中断，又不会把
        // 已经用完的处理器钉在内存里。
        private static readonly List<WeakReference<StreamingCommandProcessor>> _liveProcessors = new();
        private static readonly object _liveLock = new object();

        public StreamingCommandProcessor(Action<string> onCompleteCommand, VPetLLM plugin = null)
        {
            _onCompleteCommand = onCompleteCommand;
            _plugin = plugin;

            lock (_liveLock)
            {
                // 顺手清掉已被回收的登记项，避免表随会话数无限增长
                _liveProcessors.RemoveAll(w => !w.TryGetTarget(out _));
                _liveProcessors.Add(new WeakReference<StreamingCommandProcessor>(this));
            }

            // 从设置中读取批处理配置
            InitializeBatching();
        }

        /// <summary>
        /// 中断所有存活的处理器：清空命令队列、结束插件接管。
        /// </summary>
        public static async Task AbortAllAsync()
        {
            List<StreamingCommandProcessor> targets = new();
            lock (_liveLock)
            {
                foreach (var weak in _liveProcessors)
                {
                    if (weak.TryGetTarget(out var processor))
                        targets.Add(processor);
                }
                _liveProcessors.RemoveAll(w => !w.TryGetTarget(out _));
            }

            foreach (var processor in targets)
            {
                try
                {
                    await processor.AbortAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log($"StreamingCommandProcessor: 中断处理器失败: {ex.Message}");
                }
            }
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
                Logger.LogVerbose($"StreamingCommandProcessor: 批处理模式已启用，窗口: {_batchWindowMs}ms");
            }
        }

        /// <summary>
        /// 批处理回调 - 处理一批命令
        /// </summary>
        private void OnBatchReady(List<string> commands)
        {
            if (commands is null || commands.Count == 0) return;

            Logger.LogVerbose($"StreamingCommandProcessor: 批处理回调，命令数: {commands.Count}");

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
        public void AddChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            if (InterruptManager.IsInterrupted)
            {
                if (!_interruptLogged)
                {
                    _interruptLogged = true;
                    Logger.Log("StreamingCommandProcessor: 已中断，丢弃后续片段");
                }
                return;
            }

            lock (_lock)
            {
                _incomingChunks.Enqueue(chunk);
                if (_pumpRunning)
                    return;
                _pumpRunning = true;
            }

            _ = PumpChunksAsync();
        }

        /// <summary>
        /// 确保队列里剩下的片段有人消费：泵没在跑就重新拉起来，
        /// 在跑（多半是挂在接管插件的 await 上）就交给它按序处理。
        /// </summary>
        private void DrainPendingChunks()
        {
            lock (_lock)
            {
                if (_incomingChunks.Count == 0 || _pumpRunning)
                    return;
                _pumpRunning = true;
            }

            _ = PumpChunksAsync();
        }

        private async Task PumpChunksAsync()
        {
            try
            {
                while (true)
                {
                    string chunk;
                    lock (_lock)
                    {
                        if (_incomingChunks.Count == 0)
                        {
                            _pumpRunning = false;
                            return;
                        }
                        chunk = _incomingChunks.Dequeue();
                    }

                    await ProcessIncomingChunkAsync(chunk).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"StreamingCommandProcessor: AddChunk error: {ex.Message}");
                lock (_lock)
                {
                    _pumpRunning = false;
                }
            }
        }

        private async Task ProcessIncomingChunkAsync(string chunk)
        {
            if (InterruptManager.IsInterrupted)
                return;

            if (_takeoverManager.IsTakingOver)
            {
                await _takeoverManager.ProcessChunkAsync(chunk).ConfigureAwait(false);
                return;
            }

            bool maybeCommand;
            lock (_lock)
            {
                _buffer.Append(chunk);
                // 命令跨 chunk 到达：'<' 在前面的 chunk 里，收尾的 "|>" 在后面的 chunk 里。
                // 所以判据是"缓冲区里还有没扫完的 '<'"，只看本次 chunk 会把刚补全的命令漏到
                // 下一个 '<' 才发出去，最后一条更是永远发不出去。
                maybeCommand = _hasPendingOpen || chunk.IndexOf('<') >= 0 || chunk.Contains("[:");
                _hasPendingOpen = maybeCommand;
            }

            if (await TryBeginPluginTakeoverAsync().ConfigureAwait(false))
                return;

            if (maybeCommand)
                ProcessCompleteCommands();
        }

        private async Task<bool> TryBeginPluginTakeoverAsync()
        {
            string currentBuffer;
            Match match;
            lock (_lock)
            {
                var scanFrom = Math.Max(0, Math.Min(_pluginScanFrom, _buffer.Length));
                if (scanFrom >= _buffer.Length)
                    return false;

                currentBuffer = _buffer.ToString();

                // Regex.Match(input, startat) 要求匹配的"起点"不早于 startat。而
                // <|plugin_begin|>Name 几乎必然被切在两个 chunk 中间（TalkBox 逐字符投喂，
                // Provider 是 token 级 delta），起点一旦落在上轮扫描位置之前就永远匹配不上。
                // 回退到缓冲区里最后一个 '<'：任何尚未闭合的标记都从那里开始。
                // 但不越过 _pluginScanFloor —— 那之前的标记已经判定过"查无此插件"。
                var lastOpen = currentBuffer.LastIndexOf('<', Math.Max(0, scanFrom - 1));
                if (lastOpen >= 0 && lastOpen < scanFrom)
                    scanFrom = Math.Max(lastOpen, _pluginScanFloor);

                match = PluginBeginRegex.Match(currentBuffer, scanFrom);
                _pluginScanFrom = currentBuffer.Length;
            }

            if (!match.Success)
                return false;

            var pluginName = match.Groups[1].Value;
            var plugin = _plugin?.Plugins.Find(p =>
                p.Name.Replace(" ", "_").Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                p is IPluginTakeover takeover && takeover.SupportsTakeover);

            if (plugin is not IPluginTakeover)
            {
                // 匹配尾部还贴着缓冲区末尾时，\w+ 可能只吃到插件名的前半截（逐字符投喂下必然如此），
                // 名字还会变长，不能就此判死。只有名字已被非单词字符终结、仍然查不到，
                // 才把地板推过去，让后续片不再重复扫描这个死标记。
                var matchEnd = match.Index + match.Length;
                if (matchEnd < currentBuffer.Length)
                {
                    lock (_lock)
                    {
                        _pluginScanFloor = Math.Max(_pluginScanFloor, matchEnd);
                    }
                }
                return false;
            }

            var pluginStartIndex = match.Index;
            var pluginContent = currentBuffer.Substring(pluginStartIndex);
            Logger.Log($"StreamingCommandProcessor: 检测到支持接管的插件 {pluginName}，准备启动接管");

            await _takeoverManager.ProcessChunkAsync(pluginContent).ConfigureAwait(false);
            if (!_takeoverManager.IsTakingOver)
                return false;

            lock (_lock)
            {
                var kept = currentBuffer.Substring(0, pluginStartIndex);
                _buffer.Clear();
                _buffer.Append(kept);
                _lastProcessedIndex = 0;
                _pluginScanFrom = _buffer.Length;
                _pluginScanFloor = 0;
                _hasPendingOpen = HasPendingCommandStart(kept, 0);
            }

            Logger.Log($"StreamingCommandProcessor: 插件 {_takeoverManager.CurrentTakeoverPlugin} 开始接管");
            return true;
        }

        /// <summary>
        /// 获取当前累积的完整文本
        /// </summary>
        public string GetFullText()
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }

        /// <summary>
        /// 处理所有已完整接收的命令
        /// 只支持新格式: <|command_type_begin|> ... <|command_type_end|>
        /// </summary>
        private void ProcessCompleteCommands()
        {
            var found = new List<(string FullCommand, string CommandType)>();

            lock (_lock)
            {
                var text = _buffer.ToString();
                int index = _lastProcessedIndex;

                if (!_oldFormatWarned && text.Contains("[:"))
                {
                    _oldFormatWarned = true;
                    Logger.Log("StreamingCommandProcessor: 警告 - 检测到旧格式命令 [:，已弃用。请使用新格式: <|command_type_begin|> ... <|command_type_end|>");
                }

                while (index < text.Length)
                {
                    int startIndex = text.IndexOf("<|", index);
                    if (startIndex == -1)
                        break;

                    if (startIndex < _lastProcessedIndex)
                    {
                        index = startIndex + 2;
                        continue;
                    }

                    var command = ParseNewFormatCommand(text, startIndex);
                    if (command is null)
                        break;

                    _lastProcessedIndex = command.EndIndex + 1;
                    found.Add((command.FullMatch, command.CommandType));
                    index = _lastProcessedIndex;
                }

                var tail = Math.Min(Math.Max(_lastProcessedIndex, 0), text.Length);
                _hasPendingOpen = HasPendingCommandStart(text, tail);
            }

            if (found.Count == 0)
                return;

            foreach (var (fullCommand, commandType) in found)
            {
                Logger.LogVerbose($"StreamingCommandProcessor: 检测到完整命令类型: {commandType}, 格式: 新格式, 命令: {fullCommand.Substring(0, Math.Min(fullCommand.Length, 100))}...");
            }

            if (_useBatching && _commandBatcher is not null)
            {
                foreach (var (fullCommand, _) in found)
                {
                    _commandBatcher.AddCommand(fullCommand);
                }
                return;
            }

            // 整批一次性入队：Complete() 与泵可能并发跑到这里，逐条入队会让两批命令交错，
            // 顺序就乱了。ProcessQueueAsync 自己会把队列抽干，踢一次就够。
            lock (_lock)
            {
                foreach (var (fullCommand, _) in found)
                {
                    _commandQueue.Enqueue(fullCommand);
                }
            }

            _ = ProcessQueueAsync();
        }

        /// <summary>
        /// 判断 <paramref name="from"/> 之后是否还有可能长成命令的开头。
        /// 只认 "&lt;|"，或缓冲区正好以 '&lt;' 结尾（'|' 可能在下一片里）——
        /// 正文里的裸 '&lt;'（"a &lt; b"）不该让后续每一片都触发全量扫描。
        /// </summary>
        private static bool HasPendingCommandStart(string text, int from)
        {
            if (from >= text.Length)
                return false;

            return text.IndexOf("<|", from, StringComparison.Ordinal) >= 0
                || text[text.Length - 1] == '<';
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

                    // 中断：队列里还没执行的命令（很可能包含 plugin/tool）直接丢弃，
                    // 不要一条条空转过去 —— 空转本身还会带着每条命令的等待逻辑
                    if (InterruptManager.IsInterrupted)
                    {
                        lock (_lock)
                        {
                            var dropped = _commandQueue.Count;
                            _commandQueue.Clear();
                            _isProcessing = false;
                            if (dropped > 0)
                                Logger.Log($"StreamingCommandProcessor: 已中断，丢弃 {dropped} 条未执行命令");
                        }
                        break;
                    }

                    lock (_lock)
                    {
                        if (_commandQueue.Count == 0)
                        {
                            _isProcessing = false;

                            // 队列为空，使用智能等待策略检查是否需要设置Idle状态
                            _ = Task.Run(async () =>
                            {
                                await InterruptManager.Delay(500).ConfigureAwait(false);

                                int maxWaitMs = 5000;
                                int elapsedMs = 500;

                                while (elapsedMs < maxWaitMs && !InterruptManager.IsInterrupted)
                                {
                                    bool hasActivity;
                                    lock (_lock)
                                    {
                                        hasActivity = _commandQueue.Count > 0 || _isProcessing;
                                    }

                                    if (hasActivity)
                                    {
                                        Logger.LogVerbose("StreamingCommandProcessor: 检测到新活动，退出Idle等待");
                                        return;
                                    }

                                    var activeSessionCount = pluginInstance?.FloatingSidebarManager?.ActiveSessionCount ?? 0;
                                    if (activeSessionCount > 0)
                                    {
                                        Logger.LogVerbose($"StreamingCommandProcessor: 检测到活动会话({activeSessionCount})，继续等待");
                                        await InterruptManager.Delay(500).ConfigureAwait(false);
                                        elapsedMs += 500;
                                        continue;
                                    }

                                    var processor = pluginInstance?.TalkBox?.MessageProcessor;
                                    if (processor is not null && processor.IsProcessing)
                                    {
                                        await InterruptManager.Delay(500).ConfigureAwait(false);
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
                                    Logger.LogVerbose($"StreamingCommandProcessor: 最终检查发现活动会话({finalActiveSessionCount})，跳过设置Idle");
                                    return;
                                }

                                if (shouldSetIdle)
                                {
                                    var processor = pluginInstance?.TalkBox?.MessageProcessor;
                                    if (processor is null || !processor.IsProcessing)
                                    {
                                        Logger.LogVerbose("StreamingCommandProcessor: 所有命令处理完成，设置状态灯为Idle");
                                        pluginInstance?.FloatingSidebarManager?.SetIdleStatus();
                                    }
                                    else
                                    {
                                        Logger.LogVerbose("StreamingCommandProcessor: SmartMessageProcessor仍在处理中，跳过设置Idle");
                                    }
                                }
                                else
                                {
                                    Logger.LogVerbose("StreamingCommandProcessor: 检测到新命令或正在处理，跳过设置Idle");
                                }
                            });

                            break;
                        }
                        command = _commandQueue.Dequeue();
                    }

                    // 执行命令
                    Logger.LogVerbose($"StreamingCommandProcessor: 开始处理命令: {command}");
                    _onCompleteCommand?.Invoke(command);

                    // 检查是否启用实况模式
                    bool isLiveMode = pluginInstance?.Settings?.EnableLiveMode ?? false;

                    if (isLiveMode)
                    {
                        Logger.LogVerbose($"StreamingCommandProcessor: 实况模式 - 命令已发送，不等待完成: {command}");
                    }
                    else
                    {
                        Logger.LogVerbose($"StreamingCommandProcessor: 队列模式 - 开始等待命令完成: {command}");
                        await WaitForCommandCompleteAsync(command).ConfigureAwait(false);
                        Logger.LogVerbose($"StreamingCommandProcessor: 队列模式 - 命令处理完成: {command}");
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
            Logger.LogVerbose($"StreamingCommandProcessor.WaitForCommandCompleteAsync: 进入方法，命令: {command}");

            if (string.IsNullOrEmpty(command))
            {
                await InterruptManager.Delay(50).ConfigureAwait(false);
                return;
            }

            // 获取命令类型
            var match = Regex.Match(command, @"<\|\s*(\w+)\s*_begin\s*\|>");
            if (!match.Success)
            {
                await InterruptManager.Delay(50).ConfigureAwait(false);
                return;
            }
            
            var commandType = match.Groups[1].Value.ToLower();
            Logger.LogVerbose($"StreamingCommandProcessor.WaitForCommandCompleteAsync: 命令类型: {commandType}");

            var pluginInstance = _plugin ?? VPetLLM.Instance;

            // 对于plugin和tool命令，使用特殊的等待逻辑
            if (commandType == "plugin" || commandType == "tool")
            {
                Logger.LogVerbose($"StreamingCommandProcessor: 检测到{commandType}命令，使用特殊等待逻辑");
                await WaitForPluginCommandAsync().ConfigureAwait(false);
                Logger.LogVerbose($"StreamingCommandProcessor: {commandType}命令等待完成");
                return;
            }

            // 等待 SmartMessageProcessor 完成当前命令的处理
            if (pluginInstance?.TalkBox?.MessageProcessor is not null)
            {
                int maxWaitTime = 30000;
                int checkInterval = 150;  // 优化：增加轮询间隔从 50ms 改到 150ms，减少 CPU 唤醒
                int elapsedTime = 0;
                int startWaitTime = 0;

                // 等待消息开始处理
                while (!pluginInstance.TalkBox.MessageProcessor.IsProcessing && !InterruptManager.IsInterrupted && startWaitTime < 1000)
                {
                    await InterruptManager.Delay(100).ConfigureAwait(false);  // 优化：50ms → 100ms
                    startWaitTime += 100;
                }

                // 等待 FloatingSidebarManager 的活动会话结束（包括音频播放）
                while (pluginInstance.FloatingSidebarManager?.ActiveSessionCount > 0 && !InterruptManager.IsInterrupted && elapsedTime < maxWaitTime)
                {
                    await InterruptManager.Delay(checkInterval).ConfigureAwait(false);
                    elapsedTime += checkInterval;
                }
            }
            else
            {
                // 如果无法访问 MessageProcessor，使用传统的等待策略
                Logger.LogVerbose("StreamingCommandProcessor: 无法访问 MessageProcessor，使用传统等待策略");

                switch (commandType)
                {
                    case "say":
                    case "talk":
                        await InterruptManager.Delay(500).ConfigureAwait(false);
                        break;
                    case "action":
                    case "move":
                        await InterruptManager.Delay(300).ConfigureAwait(false);
                        break;
                    case "buy":
                    case "happy":
                    case "health":
                    case "exp":
                        await InterruptManager.Delay(100).ConfigureAwait(false);
                        break;
                    case "plugin":
                    case "tool":
                        await InterruptManager.Delay(800).ConfigureAwait(false);
                        break;
                    default:
                        await InterruptManager.Delay(30).ConfigureAwait(false);
                        break;
                }
            }
        }

        /// <summary>
        /// 智能等待 plugin/tool 命令完成（替代硬编码延迟）
        /// </summary>
        private async Task WaitForPluginCommandAsync()
        {
            var pluginInstance = _plugin ?? VPetLLM.Instance;
            int elapsedTime = 0;
            int maxWaitTime = 3000;
            int pollInterval = 100;  // 增加轮询间隔，减少 CPU 唤醒

            // 等待 ActiveSessionCount 变为 0（命令完成）
            while (pluginInstance?.FloatingSidebarManager?.ActiveSessionCount > 0 && !InterruptManager.IsInterrupted && elapsedTime < maxWaitTime)
            {
                await InterruptManager.Delay(pollInterval).ConfigureAwait(false);
                elapsedTime += pollInterval;
            }

            // 如果无法访问 SessionCount，用保守的固定延迟
            if (pluginInstance?.FloatingSidebarManager == null)
            {
                await InterruptManager.Delay(800).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 中断：丢弃缓冲区和未执行的命令，并结束可能正在进行的插件接管。
        /// 与 <see cref="Reset"/> 的区别只在于会把接管中的插件也收掉。
        /// </summary>
        public async Task AbortAsync()
        {
            int dropped;
            lock (_lock)
            {
                dropped = _commandQueue.Count;
                _buffer.Clear();
                _lastProcessedIndex = 0;
                _pluginScanFrom = 0;
                _pluginScanFloor = 0;
                _hasPendingOpen = false;
                _incomingChunks.Clear();
                _commandQueue.Clear();
                _isProcessing = false;
                _pumpRunning = false;
            }

            _commandBatcher?.Clear();

            if (_takeoverManager.IsTakingOver)
            {
                var pluginName = _takeoverManager.CurrentTakeoverPlugin;
                try
                {
                    await _takeoverManager.ForceEndTakeoverAsync();
                    Logger.Log($"StreamingCommandProcessor: 中断时已结束插件接管: {pluginName}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"StreamingCommandProcessor: 结束插件接管失败: {ex.Message}");
                    _takeoverManager.Reset();
                }
            }

            Logger.Log($"StreamingCommandProcessor: 已中断，丢弃 {dropped} 条待处理命令");
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
                _pluginScanFrom = 0;
                _pluginScanFloor = 0;
                _hasPendingOpen = false;
                _incomingChunks.Clear();
                _commandQueue.Clear();
                _isProcessing = false;
                _pumpRunning = false;
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
                Logger.LogVerbose($"StreamingCommandProcessor: 批处理模式已启用，窗口: {windowMs}ms");
            }
            else if (!enabled && _commandBatcher is not null)
            {
                _commandBatcher.Flush();
                _commandBatcher.Dispose();
                _commandBatcher = null;
                Logger.LogVerbose("StreamingCommandProcessor: 批处理模式已禁用");
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
            Logger.LogVerbose("StreamingCommandProcessor: Complete() 调用 - 统一流式处理完成");

            // 泵可能还挂在接管插件的 await 上。积压的片段必须走原路径由泵消费：
            // 接管期间这些内容属于插件，直接 Append 进 _buffer 会被当成普通文本吞掉。
            DrainPendingChunks();

            if (_useBatching && _commandBatcher is not null)
            {
                _commandBatcher.Flush();
                Logger.LogVerbose("StreamingCommandProcessor: 批处理器已刷新");
            }

            ProcessCompleteCommands();

            Logger.LogVerbose("StreamingCommandProcessor: 统一流式处理完成");
        }
    }
}
