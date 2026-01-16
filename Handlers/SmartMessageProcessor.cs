using System;
using System.Text;
using System.Text.RegularExpressions;
using VPetLLM.Utils;
using VPetLLM.Configuration;
using System.Linq;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 智能消息处理器，用于处理包含动作指令的AI回复
    /// 支持TTS语音播放与气泡同步显示
    /// 优化：使用 BubbleManager 统一管理气泡显示
    /// </summary>
    public class SmartMessageProcessor
    {
        private readonly VPetLLM _plugin;
        private readonly ActionProcessor _actionProcessor;
        private readonly BubbleManager _bubbleManager;
        private readonly TTSRequestSerializer _ttsSerializer;
        private VPetTTSStateMonitor _stateMonitor;
        private bool _isProcessing = false;
        private readonly object _processingLock = new object();

        public SmartMessageProcessor(VPetLLM plugin)
        {
            _plugin = plugin;
            _actionProcessor = plugin.ActionProcessor;
            _bubbleManager = new BubbleManager(plugin);
            _ttsSerializer = new TTSRequestSerializer();
            
            // 设置序列化器的SmartMessageProcessor引用
            _ttsSerializer.SetSmartMessageProcessor(this);
            
            // 初始化VPetTTS状态监控器（如果检测到VPetTTS插件）
            InitializeVPetTTSStateMonitor();
            
            Logger.Log("SmartMessageProcessor: 初始化完成，TTS序列化器已启用");
        }
        
        /// <summary>
        /// 获取气泡管理器
        /// </summary>
        public BubbleManager BubbleManager => _bubbleManager;
        
        /// <summary>
        /// 初始化VPetTTS状态监控器
        /// </summary>
        private void InitializeVPetTTSStateMonitor()
        {
            try
            {
                if (_plugin.IsVPetTTSPluginDetected)
                {
                    var vpetTTSPlugin = GetVPetTTSPlugin();
                    if (vpetTTSPlugin != null)
                    {
                        _stateMonitor = new VPetTTSStateMonitor(vpetTTSPlugin);
                        Logger.Log("SmartMessageProcessor: VPetTTS状态监控器已初始化");
                    }
                    else
                    {
                        Logger.Log("SmartMessageProcessor: 无法获取VPetTTS插件实例，状态监控器未初始化");
                    }
                }
                else
                {
                    Logger.Log("SmartMessageProcessor: 未检测到VPetTTS插件，跳过状态监控器初始化");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 初始化VPetTTS状态监控器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前是否正在处理消息
        /// </summary>
        public bool IsProcessing
        {
            get
            {
                lock (_processingLock)
                {
                    return _isProcessing;
                }
            }
        }

        /// <summary>
        /// 处理AI回复消息，解析动作指令并按顺序执行
        /// 优化：使用ConfigureAwait(false)避免UI线程阻塞
        /// </summary>
        /// <param name="response">AI回复内容</param>
        /// <param name="skipInitialization">是否跳过初始化（流式后续命令时为true）</param>
        /// <param name="autoSetIdleOnComplete">处理完成后是否自动设置Idle状态（流式处理时应为false，由StreamingCommandProcessor统一管理）</param>
        public async Task ProcessMessageAsync(string response, bool skipInitialization = false, bool autoSetIdleOnComplete = true)
        {
            if (string.IsNullOrWhiteSpace(response))
                return;

            // 设置处理状态
            lock (_processingLock)
            {
                _isProcessing = true;
            }

            // 开始消息处理会话，防止状态灯过早切换为Idle
            // 无论是否跳过初始化，都需要维护会话（确保会话计数正确）
            _plugin?.FloatingSidebarManager?.BeginActiveSession("SmartMessageProcessor");
            Logger.Log("SmartMessageProcessor: 开始消息处理会话");
            bool sessionStarted = true;

            try
            {
                Logger.Log($"SmartMessageProcessor: 开始处理消息: {response}, 跳过初始化: {skipInitialization}");
                
                // 设置状态灯为输出中（无论是首次响应还是后续命令）
                try
                {
                    _plugin.FloatingSidebarManager?.SetOutputtingStatus();
                }
                catch (Exception ex)
                {
                    Logger.Log($"SmartMessageProcessor: 设置Outputting状态失败: {ex.Message}");
                }
                
                // 只在首次响应时清理状态
                if (!skipInitialization)
                {
                    // 使用 BubbleManager 清理状态（幂等操作）
                    _bubbleManager.Clear();
                    
                    // 预初始化 MessageBarHelper（如果尚未初始化）
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var msgBar = _plugin.MW.Main.MsgBar;
                            if (msgBar != null && !MessageBarHelper.IsInitialized)
                            {
                                MessageBarHelper.PreInitialize(msgBar);
                            }
                        }
                        catch { /* 忽略初始化错误 */ }
                    }));
                }
                
                // 标记进入单条 AI 回复的处理会话，期间豁免插件/工具限流
                var _sessionId = Guid.NewGuid();
                global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value = _sessionId;

                // 通知TouchInteractionHandler开始执行VPetLLM动作
                TouchInteractionHandler.NotifyVPetLLMActionStart();

                // 解析消息，提取文本片段和动作指令
                var messageSegments = ParseMessage(response);

                Logger.Log($"SmartMessageProcessor: 解析出 {messageSegments.Count} 个消息片段");

                // 根据设置选择并行或队列下载模式
                List<TTSDownloadTask> downloadTasks = null;
                if (_plugin.Settings.TTS.IsEnabled)
                {
                    if (_plugin.Settings.TTS.UseQueueDownload)
                    {
                        // 队列下载模式：不预先启动下载任务
                        downloadTasks = PrepareQueueTTSDownload(messageSegments);
                    }
                    else
                    {
                        // 并发下载模式：启动所有下载任务
                        downloadTasks = StartParallelTTSDownload(messageSegments);
                    }
                }

                // 按顺序处理每个片段，使用智能等待机制（优化：不阻塞UI线程）
                int talkIndex = 0;
                try
                {
                    foreach (var segment in messageSegments)
                    {
                        if (segment.Type == SegmentType.Talk && downloadTasks != null)
                        {
                            await ProcessTalkSegmentWithQueueAsync(segment, downloadTasks, talkIndex++).ConfigureAwait(false);
                        }
                        else
                        {
                            await ProcessSegmentAsync(segment, null).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    // 通知TouchInteractionHandler完成VPetLLM动作
                    TouchInteractionHandler.NotifyVPetLLMActionEnd();

                    // 先统一Flush一次本次会话内所有聚合结果，确保只回灌一次
                    global::VPetLLM.Utils.ResultAggregator.FlushSession(_sessionId);

                    // 退出本次会话，恢复上下文
                    if (global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value == _sessionId)
                        global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value = null;
                }
            }
            finally
            {
                // 清除处理状态
                lock (_processingLock)
                {
                    _isProcessing = false;
                }
                
                // 结束消息处理会话
                if (sessionStarted)
                {
                    _plugin?.FloatingSidebarManager?.EndActiveSession("SmartMessageProcessor");
                    Logger.Log("SmartMessageProcessor: 结束消息处理会话");
                }
                
                // 只有在autoSetIdleOnComplete为true时才自动设置Idle状态
                // 流式处理时由StreamingCommandProcessor统一管理状态灯
                if (autoSetIdleOnComplete)
                {
                    try
                    {
                        _plugin.FloatingSidebarManager?.SetIdleStatus();
                        Logger.Log("SmartMessageProcessor: 处理完成，状态灯已切换回Idle");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"SmartMessageProcessor: 切换状态灯失败: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Log("SmartMessageProcessor: 处理完成，状态灯由调用方管理（流式处理模式）");
                }
            }
        }

        /// <summary>
        /// 解析消息，将其分解为文本片段和动作指令
        /// 优化：支持更灵活的格式，容忍空格、换行等特殊字符，支持新旧格式
        /// </summary>
        /// <param name="message">原始消息</param>
        /// <returns>消息片段列表</returns>
        private List<MessageSegment> ParseMessage(string message)
        {
            var segments = new List<MessageSegment>();

            Logger.Log($"SmartMessageProcessor: 开始解析消息，长度: {message.Length}");

            // 预处理：移除可能影响解析的多余空白字符（但保留引号内的内容）
            message = NormalizeMessage(message);

            // Use CommandFormatParser to parse both new and legacy formats
            var commands = CommandFormatParser.Parse(message);
            var format = CommandFormatParser.DetectFormat(message);
            
            Logger.Log($"SmartMessageProcessor: 检测到格式: {format}, 找到 {commands.Count} 个命令");

            foreach (var command in commands)
            {
                string actionType = command.CommandType.ToLower();
                string actionValue = command.Parameters;
                string fullMatch = command.FullMatch;

                Logger.Log($"SmartMessageProcessor: 解析到动作指令 - 类型: {actionType}, 格式: {command.Format}, 值长度: {actionValue.Length}");

                segments.Add(new MessageSegment
                {
                    Type = GetSegmentTypeFromAction(actionType),
                    Content = fullMatch,
                    ActionType = actionType,
                    ActionValue = actionValue
                });
            }

            if (segments.Count == 0)
            {
                // 没有找到动作指令，整个消息作为文本处理
                segments.Add(new MessageSegment
                {
                    Type = SegmentType.Text,
                    Content = message.Trim()
                });
                Logger.Log($"SmartMessageProcessor: 没有找到动作指令，作为纯文本处理");
            }
            else
            {
                Logger.Log($"SmartMessageProcessor: 解析完成，共 {segments.Count} 个片段");
            }

            return segments;
        }

        /// <summary>
        /// 标准化消息格式，移除可能影响解析的多余空白（保留引号内的内容）
        /// </summary>
        private string NormalizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            // 移除命令标记周围的多余空格，但保留引号内的内容
            var result = new StringBuilder();
            bool inString = false;
            char stringDelimiter = '\0';
            
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                
                // 检测字符串边界
                if ((c == '"' || c == '\'') && (i == 0 || message[i - 1] != '\\'))
                {
                    if (!inString)
                    {
                        inString = true;
                        stringDelimiter = c;
                    }
                    else if (c == stringDelimiter)
                    {
                        inString = false;
                    }
                }
                
                result.Append(c);
            }
            
            return result.ToString();
        }

        /// <summary>
        /// 查找命令开始位置（只支持新格式）
        /// </summary>
        private int FindCommandStart(string message, int startIndex)
        {
            // 只搜索新格式: <|xxx_begin|>
            for (int i = startIndex; i < message.Length - 1; i++)
            {
                if (message[i] == '<' && message[i + 1] == '|')
                {
                    return i;
                }
            }
            
            return -1;
        }

        /// <summary>
        /// 根据动作类型确定片段类型
        /// </summary>
        private SegmentType GetSegmentTypeFromAction(string actionType)
        {
            return actionType switch
            {
                "talk" => SegmentType.Talk,
                "say" => SegmentType.Talk,
                "state" => SegmentType.State,
                "happy" => SegmentType.State,
                "health" => SegmentType.State,
                "exp" => SegmentType.State,
                "move" => SegmentType.Action,
                "action" => SegmentType.Action,
                "buy" => SegmentType.Action,
                "body" => SegmentType.Action,
                "plugin" => SegmentType.Action,
                _ => SegmentType.Action
            };
        }

        /// <summary>
        /// TTS音频下载任务信息
        /// </summary>
        private class TTSDownloadTask
        {
            public int Index { get; set; }
            public string Text { get; set; }
            public Task<string> DownloadTask { get; set; }
            public string AudioFile { get; set; }
            public bool IsCompleted { get; set; }
        }

        /// <summary>
        /// 启动并行下载所有TTS音频（不等待完成）
        /// </summary>
        private List<TTSDownloadTask> StartParallelTTSDownload(List<MessageSegment> segments)
        {
            Logger.Log($"SmartMessageProcessor: 开始并行下载TTS音频");

            var downloadTasks = new List<TTSDownloadTask>();
            int index = 0;

            foreach (var segment in segments)
            {
                if (segment.Type == SegmentType.Talk)
                {
                    var talkText = ExtractTalkText(segment.ActionValue);
                    if (!string.IsNullOrEmpty(talkText))
                    {
                        var downloadTask = new TTSDownloadTask
                        {
                            Index = index++,
                            Text = talkText,
                            DownloadTask = _plugin.TTSService.DownloadTTSAudioAsync(talkText),
                            IsCompleted = false
                        };

                        downloadTasks.Add(downloadTask);
                        Logger.Log($"SmartMessageProcessor: 启动下载任务 #{downloadTask.Index}: {talkText.Substring(0, Math.Min(talkText.Length, 20))}...");
                    }
                }
            }

            Logger.Log($"SmartMessageProcessor: 已启动 {downloadTasks.Count} 个并行下载任务");
            return downloadTasks;
        }

        /// <summary>
        /// 准备队列下载所有TTS音频（启动第一个任务的下载，其余按需下载）
        /// </summary>
        private List<TTSDownloadTask> PrepareQueueTTSDownload(List<MessageSegment> segments)
        {
            Logger.Log($"SmartMessageProcessor: 准备队列下载TTS音频");

            var downloadTasks = new List<TTSDownloadTask>();
            int index = 0;

            foreach (var segment in segments)
            {
                if (segment.Type == SegmentType.Talk)
                {
                    var talkText = ExtractTalkText(segment.ActionValue);
                    if (!string.IsNullOrEmpty(talkText))
                    {
                        var downloadTask = new TTSDownloadTask
                        {
                            Index = index++,
                            Text = talkText,
                            DownloadTask = null, // 稍后启动
                            IsCompleted = false
                        };

                        downloadTasks.Add(downloadTask);
                        Logger.Log($"SmartMessageProcessor: 准备下载任务 #{downloadTask.Index}: {talkText.Substring(0, Math.Min(talkText.Length, 20))}...");
                    }
                }
            }

            // 注意：不在这里启动第一个任务的下载
            // 因为 StreamingCommandProcessor 已经处理了预下载
            // 如果没有预下载，WaitForTTSDownloadAsync 会按需启动

            Logger.Log($"SmartMessageProcessor: 已准备 {downloadTasks.Count} 个队列下载任务（队列模式）");
            return downloadTasks;
        }

        /// <summary>
        /// 等待指定索引的音频下载完成
        /// 优化：使用ConfigureAwait(false)避免UI线程阻塞
        /// 支持队列下载模式：按需启动下载，下载完成后立即启动下一个下载任务
        /// </summary>
        private async Task<string> WaitForTTSDownloadAsync(List<TTSDownloadTask> downloadTasks, int targetIndex)
        {
            var targetTask = downloadTasks.FirstOrDefault(t => t.Index == targetIndex);
            if (targetTask == null)
            {
                Logger.Log($"SmartMessageProcessor: 未找到索引 {targetIndex} 的下载任务");
                return null;
            }

            if (targetTask.IsCompleted)
            {
                Logger.Log($"SmartMessageProcessor: 任务 #{targetIndex} 已完成，直接返回: {targetTask.AudioFile}");
                return targetTask.AudioFile;
            }

            try
            {
                // 如果任务尚未启动，现在启动它
                if (targetTask.DownloadTask == null)
                {
                    Logger.Log($"SmartMessageProcessor: 按需启动任务 #{targetIndex} 下载...");
                    targetTask.DownloadTask = _plugin.TTSService.DownloadTTSAudioAsync(targetTask.Text);
                }

                Logger.Log($"SmartMessageProcessor: 等待任务 #{targetIndex} 下载完成...");
                var audioFile = await targetTask.DownloadTask.ConfigureAwait(false);
                targetTask.AudioFile = audioFile;
                targetTask.IsCompleted = true;

                Logger.Log($"SmartMessageProcessor: 任务 #{targetIndex} 下载完成: {audioFile}");

                // 注意：下一个任务的下载由 StartNextTTSDownload 在播放开始时触发
                // 这样可以实现真正的预下载，避免播放等待

                return audioFile;
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 任务 #{targetIndex} 下载失败: {ex.Message}");
                targetTask.IsCompleted = true;
                targetTask.AudioFile = null;

                // 注意：下一个任务的下载由 StartNextTTSDownload 在播放开始时触发
                // 即使当前任务失败，下一个任务也会在适当时机启动

                return null;
            }
        }

        /// <summary>
        /// 启动下一个TTS音频的下载（预下载优化）
        /// 在当前音频开始播放时调用，确保下一个音频提前下载好
        /// </summary>
        private void StartNextTTSDownload(List<TTSDownloadTask> downloadTasks, int currentIndex)
        {
            if (downloadTasks == null || !_plugin.Settings.TTS.UseQueueDownload)
            {
                return; // 非队列模式下，所有任务已经在并行下载
            }

            int nextIndex = currentIndex + 1;
            if (nextIndex < downloadTasks.Count)
            {
                var nextTask = downloadTasks[nextIndex];
                if (nextTask.DownloadTask == null && !nextTask.IsCompleted)
                {
                    Logger.Log($"SmartMessageProcessor: 预下载优化 - 启动下一个任务 #{nextIndex} 下载...");
                    nextTask.DownloadTask = _plugin.TTSService.DownloadTTSAudioAsync(nextTask.Text);
                }
            }
        }

        /// <summary>
        /// 使用智能队列处理talk动作片段
        /// 集成TTS请求序列化器，确保VPetLLM与VPetTTS的正确协调
        /// </summary>
        private async Task ProcessTalkSegmentWithQueueAsync(MessageSegment segment, List<TTSDownloadTask> downloadTasks, int talkIndex)
        {
            Logger.Log($"SmartMessageProcessor: 处理talk动作 #{talkIndex}: {segment.Content}");

            // 解析talk动作中的文本内容
            var talkText = ExtractTalkText(segment.ActionValue);

            if (!string.IsNullOrEmpty(talkText))
            {
                var operationStartTime = DateTime.Now;
                
                try
                {
                    // 检查是否有VPetTTS插件，如果有则使用序列化处理
                    if (_plugin.IsVPetTTSPluginDetected)
                    {
                        Logger.Log($"SmartMessageProcessor: 检测到VPetTTS插件，使用TTS序列化处理");
                        
                        // 使用TTS请求序列化器确保按顺序处理
                        var success = await _ttsSerializer.ProcessTTSRequestAsync(talkText, segment.Content);
                        
                        if (success)
                        {
                            var serializationDuration = (int)(DateTime.Now - operationStartTime).TotalMilliseconds;
                            Logger.Log($"SmartMessageProcessor: TTS序列化处理成功，总耗时: {serializationDuration}ms");
                        }
                        else
                        {
                            Logger.Log($"SmartMessageProcessor: TTS序列化处理失败，回退到传统处理");
                            // 回退到传统处理方式
                            await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
                            // 检查是否有外置 TTS 插件
                            if (_plugin.IsVPetTTSPluginDetected)
                            {
                                await WaitForExternalTTSCompleteAsync(talkText).ConfigureAwait(false);
                            }
                            else
                            {
                                // TTS 全部关闭：等待气泡打印完成
                                var msgBar = _plugin.MW?.Main?.MsgBar;
                                if (msgBar != null)
                                {
                                    int maxWaitMs = BubbleDisplayConfig.CalculateActualDisplayTime(talkText);
                                    Logger.Log($"SmartMessageProcessor: TTS关闭，等待气泡打印完成，预估时间: {maxWaitMs}ms");
                                    await MessageBarHelper.WaitForPrintCompleteAsync(msgBar, maxWaitMs).ConfigureAwait(false);
                                }
                            }
                        }
                        
                        return;
                    }

                    // 原有的内置TTS处理逻辑（当没有VPetTTS插件时）
                    string audioFile = null;
                    bool ttsSucceeded = false;
                    
                    // 首先检查是否有流式处理预下载的音频
                    var predownloadedAudio = StreamingCommandProcessor.GetAndRemovePredownloadedAudio(segment.Content);
                    if (!string.IsNullOrEmpty(predownloadedAudio))
                    {
                        Logger.Log($"SmartMessageProcessor: 使用流式预下载的音频: {predownloadedAudio}");
                        audioFile = predownloadedAudio;
                        ttsSucceeded = true;
                    }
                    else if (downloadTasks != null)
                    {
                        // 没有预下载音频，等待当前索引的音频下载完成
                        Logger.Log($"SmartMessageProcessor: 等待音频 #{talkIndex} 下载完成...");
                        audioFile = await WaitForTTSDownloadAsync(downloadTasks, talkIndex).ConfigureAwait(false);
                        ttsSucceeded = !string.IsNullOrEmpty(audioFile);
                    }

                    // 记录TTS下载结果
                    var downloadDuration = (int)(DateTime.Now - operationStartTime).TotalMilliseconds;
                    Logger.Log($"SmartMessageProcessor: TTS下载耗时: {downloadDuration}ms, 成功: {ttsSucceeded}");

                    // TTS成功的情况
                    if (ttsSucceeded && !string.IsNullOrEmpty(audioFile))
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 准备就绪，开始播放: {audioFile}");

                        // 使用 BubbleManager 同步 TTS
                        int estimatedDuration = Math.Max(talkText.Length * 100, 1000);
                        _bubbleManager.SyncWithTTS(true, estimatedDuration);

                        // 立即显示气泡（与音频同步）
                        var bubbleTask = ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: 气泡显示任务已启动");

                        // 播放音频（等待播放完成）
                        await _plugin.TTSService.PlayAudioFileDirectAsync(audioFile).ConfigureAwait(false);
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 播放完成");

                        // 在开始播放当前音频时，立即启动下一个音频的下载（预下载优化）
                        StartNextTTSDownload(downloadTasks, talkIndex);
                    }
                    else
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 不可用，使用外置TTS");
                        
                        // 回退到传统处理方式
                        await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
                        // 检查是否有外置 TTS 插件
                        if (_plugin.IsVPetTTSPluginDetected)
                        {
                            await WaitForExternalTTSCompleteAsync(talkText).ConfigureAwait(false);
                        }
                        else if (!_plugin.Settings.TTS.IsEnabled)
                        {
                            // TTS 全部关闭：等待气泡打印完成
                            var msgBar = _plugin.MW?.Main?.MsgBar;
                            if (msgBar != null)
                            {
                                int maxWaitMs = BubbleDisplayConfig.CalculateActualDisplayTime(talkText);
                                Logger.Log($"SmartMessageProcessor: TTS关闭，等待气泡打印完成，预估时间: {maxWaitMs}ms");
                                await MessageBarHelper.WaitForPrintCompleteAsync(msgBar, maxWaitMs).ConfigureAwait(false);
                            }
                        }
                    }

                    // 记录总操作时间
                    var totalDuration = (int)(DateTime.Now - operationStartTime).TotalMilliseconds;
                    Logger.Log($"SmartMessageProcessor: Talk动作 #{talkIndex} 总耗时: {totalDuration}ms");
                }
                catch (Exception ex)
                {
                    Logger.Log($"SmartMessageProcessor: 处理音频 #{talkIndex} 失败: {ex.Message}");
                    Logger.Log($"SmartMessageProcessor: 异常堆栈: {ex.StackTrace}");
                    
                    // 简单的异常处理：仅显示气泡
                    await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
                }
            }
            else
            {
                // 如果没有文本内容，直接执行动作
                Logger.Log($"SmartMessageProcessor: Talk动作 #{talkIndex} 无文本内容，直接执行动作");
                await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 等待外置 TTS 插件（如 VPetTTS）播放完成
        /// 使用VPetTTSStateMonitor提供更精确的状态检测
        /// 完全依赖进度检测，不使用固定超时
        /// </summary>
        private async Task WaitForExternalTTSCompleteAsync(string text)
        {
            // 开始TTS播放会话，确保等待播放完成
            // 注意：这会与 ProcessMessageAsync 的会话嵌套，但这是正确的行为
            _plugin?.FloatingSidebarManager?.BeginActiveSession("SmartMessageProcessor.TTS");
            Logger.Log("SmartMessageProcessor: 开始TTS播放会话");
            
            try
            {
                Logger.Log("SmartMessageProcessor: 开始等待外置TTS播放完成...");
                
                // 优先使用状态监控器（完全依赖进度检测，不传入超时参数）
                if (_stateMonitor != null && TTSCoordinationSettings.Instance.EnableStateMonitor)
                {
                    Logger.Log("SmartMessageProcessor: 使用VPetTTS状态监控器等待播放完成（基于进度检测）");
                    // 不传入超时参数，使用默认的5分钟最大超时作为安全保护
                    // 实际完成判断完全依赖进度检测：3秒内进度无变化才判断失败
                    var completed = await _stateMonitor.WaitForPlaybackCompleteAsync();
                    
                    if (completed)
                    {
                        Logger.Log("SmartMessageProcessor: VPetTTS播放完成");
                    }
                    else
                    {
                        Logger.Log("SmartMessageProcessor: VPetTTS状态监控器检测到播放异常，继续处理下一个请求");
                    }
                }
                else
                {
                    // 回退到传统的 VPet 语音等待
                    Logger.Log("SmartMessageProcessor: 状态监控器不可用，使用传统等待");
                    await WaitForVPetVoiceCompleteAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 等待外置 TTS 失败: {ex.Message}");
                Logger.Log($"SmartMessageProcessor: 异常堆栈: {ex.StackTrace}");
                
                // 发生异常时回退到传统等待
                try
                {
                    await WaitForVPetVoiceCompleteAsync().ConfigureAwait(false);
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log($"SmartMessageProcessor: 传统等待也失败: {fallbackEx.Message}");
                    // 最后的回退：固定等待时间
                    await Task.Delay(2000).ConfigureAwait(false);
                }
            }
            finally
            {
                // 确保TTS播放会话结束
                _plugin?.FloatingSidebarManager?.EndActiveSession("SmartMessageProcessor.TTS");
                Logger.Log("SmartMessageProcessor: 结束TTS播放会话");
            }
        }


        /// <summary>
        /// 等待 VPet 主程序的语音播放完成（EdgeTTS 或其他 TTS 插件）
        /// 优化：减少轮询频率，利用VPet新的SayInfo架构提供的状态信息
        /// 修复：正确处理VPetTTS插件协作，当检测到外部TTS插件时应该等待而不是跳过
        /// </summary>
        private async Task WaitForVPetVoiceCompleteAsync()
        {
            try
            {
                // 检查是否启用了 VPetLLM 的 TTS 且没有检测到外部 TTS 插件
                if (_plugin.Settings.TTS.IsEnabled && !_plugin.IsVPetTTSPluginDetected)
                {
                    Logger.Log("SmartMessageProcessor: VPetLLM 内置 TTS 已启用且无外部 TTS 插件，跳过 VPet 语音等待");
                    return;
                }

                // 如果检测到外部 TTS 插件（如 VPetTTS），需要等待其播放完成
                if (_plugin.IsVPetTTSPluginDetected)
                {
                    Logger.Log("SmartMessageProcessor: 检测到外部 TTS 插件，等待其播放完成");
                }

                // 检查 VPet 主程序是否正在播放语音
                if (_plugin.MW?.Main == null)
                {
                    Logger.Log("SmartMessageProcessor: MW.Main 为 null，无法检查语音状态");
                    return;
                }

                await WaitForVPetVoiceWithSayInfoAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 等待 VPet 语音失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 利用VPet新SayInfo架构智能等待语音完成（优化版）
        /// 结合 PlayingVoice 状态和 SayInfo 事件，提供更精确的等待策略
        /// 增加：为外置TTS额外添加等待时间，并改进VPetTTS插件的播放状态检测
        /// 修复：通过VPetTTS状态接口检测播放完成
        /// </summary>
        private async Task WaitForVPetVoiceWithSayInfoAsync()
        {
            try
            {
                // 等待 VPet 主程序的语音播放完成（优化：增加检查间隔到200ms，减少CPU占用）
                int maxWaitTime = 30000; // 最多等待 30 秒
                int checkInterval = 200; // 优化：从100ms增加到200ms
                int elapsedTime = 0;

                Logger.Log("SmartMessageProcessor: 开始智能等待VPet语音播放完成");

                // 第一阶段：等待VPet主程序的PlayingVoice状态
                while (_plugin.MW.Main.PlayingVoice && elapsedTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval).ConfigureAwait(false);
                    elapsedTime += checkInterval;
                }

                if (elapsedTime >= maxWaitTime)
                {
                    Logger.Log("SmartMessageProcessor: 等待 VPet 语音播放超时");
                }
                else if (elapsedTime > 0)
                {
                    Logger.Log($"SmartMessageProcessor: VPet 语音播放完成，等待时间: {elapsedTime}ms");
                }
                else
                {
                    Logger.Log("SmartMessageProcessor: VPet 无语音播放，继续执行");
                }

                // 第二阶段：如果检测到VPetTTS插件，使用其状态接口进行精确检测
                if (_plugin.IsVPetTTSPluginDetected)
                {
                    Logger.Log("SmartMessageProcessor: 检测到VPetTTS插件，使用状态接口检测播放完成");
                    
                    // 尝试获取VPetTTS插件的状态接口
                    var vpetTTSPlugin = GetVPetTTSPlugin();
                    if (vpetTTSPlugin != null)
                    {
                        int ttsWaitTime = 0;
                        int ttsMaxWait = 15000; // VPetTTS专用等待时间：最多15秒
                        
                        // 使用VPetTTS的状态接口检测播放状态
                        while (IsVPetTTSPlaying(vpetTTSPlugin) && ttsWaitTime < ttsMaxWait)
                        {
                            await Task.Delay(checkInterval).ConfigureAwait(false);
                            ttsWaitTime += checkInterval;
                        }
                        
                        if (ttsWaitTime > 0)
                        {
                            Logger.Log($"SmartMessageProcessor: VPetTTS播放完成，等待时间: {ttsWaitTime}ms");
                        }
                        else
                        {
                            Logger.Log("SmartMessageProcessor: VPetTTS未在播放，添加标准等待时间");
                            await Task.Delay(1000).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        Logger.Log("SmartMessageProcessor: 无法获取VPetTTS状态接口，使用标准等待");
                        await Task.Delay(2000).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 为其他外置TTS添加标准等待时间
                    Logger.Log("SmartMessageProcessor: 为外置TTS添加额外1秒等待时间");
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 智能等待 VPet 语音失败: {ex.Message}");
                // 发生异常时也添加额外等待时间，确保安全
                Logger.Log("SmartMessageProcessor: 异常情况下为外置TTS添加额外等待");
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 获取VPetTTS插件实例
        /// </summary>
        private object GetVPetTTSPlugin()
        {
            try
            {
                // 通过反射获取VPetTTS插件实例
                var plugins = _plugin.MW.Plugins;
                foreach (var plugin in plugins)
                {
                    if (plugin.PluginName == "VPetTTS")
                    {
                        return plugin;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 获取VPetTTS插件实例失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检测VPetTTS是否正在播放
        /// </summary>
        private bool IsVPetTTSPlaying(object vpetTTSPlugin)
        {
            try
            {
                // 通过反射访问VPetTTS的TTSState.IsPlaying属性
                var ttsStateProperty = vpetTTSPlugin.GetType().GetProperty("TTSState");
                if (ttsStateProperty != null)
                {
                    var ttsState = ttsStateProperty.GetValue(vpetTTSPlugin);
                    if (ttsState != null)
                    {
                        var isPlayingProperty = ttsState.GetType().GetProperty("IsPlaying");
                        if (isPlayingProperty != null)
                        {
                            var isPlaying = (bool)isPlayingProperty.GetValue(ttsState);
                            return isPlaying;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 检测VPetTTS播放状态失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 处理单个消息片段（优化版，支持VPet新的SayInfo架构）
        /// </summary>
        private async Task ProcessSegmentAsync(MessageSegment segment, Dictionary<string, string> audioCache = null)
        {
            Logger.Log($"SmartMessageProcessor: 处理片段类型: {segment.Type}, 内容: {segment.Content}");

            switch (segment.Type)
            {
                case SegmentType.Text:
                    await ProcessTextSegmentAsync(segment.Content);
                    break;

                case SegmentType.Talk:
                    await ProcessTalkSegmentWithSayInfoAsync(segment, audioCache);
                    break;

                case SegmentType.State:
                case SegmentType.Action:
                    await ProcessActionSegmentAsync(segment);
                    break;
            }
        }

        /// <summary>
        /// 处理纯文本片段
        /// </summary>
        private async Task ProcessTextSegmentAsync(string text)
        {
            Logger.Log($"SmartMessageProcessor: 处理文本片段: {text}");

            // 检查是否包含talk指令，如果是则提取其中的文本内容
            string actualText = text;
            if (text.Contains("[:talk(") || text.Contains("[:say("))
            {
                actualText = ExtractTextFromTalkCommand(text);
                Logger.Log($"SmartMessageProcessor: 从talk指令中提取文本: '{actualText}'");
            }

            // 如果提取到了有效文本
            if (!string.IsNullOrEmpty(actualText))
            {
                // 如果启用了TTS，先播放语音，然后显示气泡
                if (_plugin.Settings.TTS.IsEnabled)
                {
                    await PlayTTSAndShowBubbleAsync(actualText);
                    // TTS启用时不等待气泡消失或其他TTS插件
                }
                else
                {
                    // 直接显示气泡
                    _plugin.MW.Main.Say(actualText);
                    // TTS关闭时等待外置 TTS 插件（如 VPetTTS）播放完成
                    await WaitForExternalTTSCompleteAsync(actualText).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 从talk指令中提取文本内容 (只支持新格式)
        /// </summary>
        private string ExtractTextFromTalkCommand(string input)
        {
            // 新格式: <|talk_begin|> say("text", emotion) <|talk_end|>
            var newTalkPattern = @"<\|\s*talk\s*_begin\s*\|>\s*say\(""([^""]+)"",\s*\w+\)\s*<\|\s*talk\s*_end\s*\|>";
            var match = Regex.Match(input, newTalkPattern);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            // 新格式: <|say_begin|> "text", emotion <|say_end|>
            var newSayPattern = @"<\|\s*say\s*_begin\s*\|>\s*""([^""]+)"",\s*\w+\s*<\|\s*say\s*_end\s*\|>";
            match = Regex.Match(input, newSayPattern);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 处理talk动作片段（优化版，利用VPet新的SayInfo架构）
        /// 支持流式和非流式SayInfo，提供更智能的等待策略
        /// </summary>
        private async Task ProcessTalkSegmentWithSayInfoAsync(MessageSegment segment, Dictionary<string, string> audioCache = null)
        {
            Logger.Log($"SmartMessageProcessor: 处理talk动作（优化版）: {segment.Content}");

            // 解析talk动作中的文本内容
            var talkText = ExtractTalkText(segment.ActionValue);

            if (!string.IsNullOrEmpty(talkText))
            {
                // 如果启用了TTS，音频和气泡同步显示
                if (_plugin.Settings.TTS.IsEnabled)
                {
                    Logger.Log($"SmartMessageProcessor: TTS和气泡同步播放: {talkText}");

                    try
                    {
                        // 首先检查是否有流式处理预下载的音频
                        var predownloadedAudio = StreamingCommandProcessor.GetAndRemovePredownloadedAudio(segment.Content);
                        
                        // 立即显示气泡（与音频同步）
                        var bubbleTask = ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: 气泡显示任务已启动");

                        if (!string.IsNullOrEmpty(predownloadedAudio))
                        {
                            Logger.Log($"SmartMessageProcessor: 使用流式预下载的音频: {predownloadedAudio}");
                            // 播放预下载的音频（等待播放完成）
                            await _plugin.TTSService.PlayAudioFileDirectAsync(predownloadedAudio);
                        }
                        else
                        {
                            // 播放音频（等待播放完成）
                            await _plugin.TTSService.PlayTextAsync(talkText);
                        }
                        Logger.Log($"SmartMessageProcessor: TTS播放完成");

                        // TTS启用时不需要等待气泡显示完成，音频播放完成即可继续
                        Logger.Log($"SmartMessageProcessor: TTS播放完成，不等待气泡消失");
                        
                        // 注意：不等待气泡任务完成，让气泡自然显示和消失
                        // 也不等待 EdgeTTS，因为我们使用的是 VPetLLM 内置 TTS
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"SmartMessageProcessor: TTS播放失败: {ex.Message}");
                        Logger.Log($"SmartMessageProcessor: 异常堆栈: {ex.StackTrace}");
                        // TTS失败时仍然显示气泡
                        await ExecuteActionAsync(segment.Content);
                    }
                }
                else
                {
                    // 如果内置TTS未启用，直接执行动作
                    await ExecuteActionAsync(segment.Content);
                    
                    // 检查是否有外置 TTS 插件（如 VPetTTS）
                    if (_plugin.IsVPetTTSPluginDetected)
                    {
                        // 等待外置 TTS 插件播放完成
                        await WaitForExternalTTSCompleteAsync(talkText).ConfigureAwait(false);
                    }
                    else
                    {
                        // TTS 全部关闭：等待气泡打印完成（新增逻辑）
                        var msgBar = _plugin.MW?.Main?.MsgBar;
                        if (msgBar != null)
                        {
                            int maxWaitMs = BubbleDisplayConfig.CalculateActualDisplayTime(talkText);
                            Logger.Log($"SmartMessageProcessor: TTS关闭，等待气泡打印完成，预估时间: {maxWaitMs}ms");
                            await MessageBarHelper.WaitForPrintCompleteAsync(msgBar, maxWaitMs).ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                // 如果没有文本内容，直接执行动作
                await ExecuteActionAsync(segment.Content);
            }
        }

        /// <summary>
        /// 处理其他动作片段
        /// </summary>
        private async Task ProcessActionSegmentAsync(MessageSegment segment)
        {
            Logger.Log($"SmartMessageProcessor: 处理动作片段: {segment.Content}");
            Logger.Log($"SmartMessageProcessor: ActionType = '{segment.ActionType}', 检查plugin条件...");
            
            // 如果是插件类型的动作，设置蓝色状态灯
            // 支持 "plugin" 和 "plugin_xxx" 格式（不区分大小写）
            bool isPluginAction = string.Equals(segment.ActionType, "plugin", StringComparison.OrdinalIgnoreCase) 
                || (segment.ActionType?.StartsWith("plugin_", StringComparison.OrdinalIgnoreCase) ?? false);
            
            Logger.Log($"SmartMessageProcessor: isPluginAction = {isPluginAction}");
            
            if (isPluginAction)
            {
                try
                {
                    _plugin.FloatingSidebarManager?.SetPluginExecutingStatus();
                    Logger.Log($"SmartMessageProcessor: 检测到plugin命令({segment.ActionType})，状态灯切换为蓝色");
                }
                catch (Exception ex)
                {
                    Logger.Log($"SmartMessageProcessor: 设置PluginExecuting状态失败: {ex.Message}");
                }
            }
            
            await ExecuteActionAsync(segment.Content);
        }

        /// <summary>
        /// 播放TTS并同步显示气泡
        /// 优化：TTS播放完成后立即返回，不等待气泡消失
        /// </summary>
        private async Task PlayTTSAndShowBubbleAsync(string text)
        {
            Logger.Log($"SmartMessageProcessor: 开始TTS处理: {text}");

            try
            {
                // 优先请求TTS音频
                if (_plugin.TTSService != null)
                {
                    Logger.Log($"SmartMessageProcessor: 请求TTS音频...");
                    await _plugin.TTSService.PlayTextAsync(text);
                    Logger.Log($"SmartMessageProcessor: TTS音频播放完成");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: TTS音频处理失败: {ex.Message}");
            }

            // TTS完成（成功或失败）后显示气泡，但不等待气泡消失
            Logger.Log($"SmartMessageProcessor: 显示气泡: {text}");
            _plugin.MW.Main.Say(text);

            // TTS启用时不等待气泡显示时间，让气泡自然显示和消失
            Logger.Log($"SmartMessageProcessor: TTS处理完成，不等待气泡消失");
        }

        /// <summary>
        /// 执行动作指令（优化：使用ConfigureAwait避免UI线程阻塞）
        /// </summary>
        private async Task ExecuteActionAsync(string actionContent)
        {
            Logger.Log($"SmartMessageProcessor: 执行动作指令: {actionContent}");

            try
            {
                var actionQueue = _actionProcessor.Process(actionContent, _plugin.Settings);

                foreach (var action in actionQueue)
                {
                    // 对插件类动作做非用户触发限流（会话内豁免）
                    var isPluginAction = action.Type == ActionType.Plugin || string.Equals(action.Keyword, "plugin", StringComparison.OrdinalIgnoreCase);
                    if (isPluginAction)
                    {
                        var inMessageSession = global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value.HasValue;
                        if (!inMessageSession && !Utils.RateLimiter.TryAcquire("ai-plugin", 5, TimeSpan.FromMinutes(2)))
                        {
                            Logger.Log($"SmartMessageProcessor: 触发熔断 - 插件动作频率超限");
                            break;
                        }
                    }

                    // 使用ConfigureAwait(false)避免回到UI线程
                    if (string.IsNullOrEmpty(action.Value))
                        await action.Handler.Execute(_plugin.MW).ConfigureAwait(false);
                    else if (int.TryParse(action.Value, out int intValue))
                        await action.Handler.Execute(intValue, _plugin.MW).ConfigureAwait(false);
                    else
                        await action.Handler.Execute(action.Value, _plugin.MW).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 动作执行失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从talk动作中提取文本内容 (新格式)
        /// </summary>
        private string ExtractTalkText(string actionValue)
        {
            // 解析 say("text", animation) 格式
            var match = Regex.Match(actionValue, @"say\s*\(\s*""([^""]*)""\s*(?:,\s*([^)]*))?\s*\)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // 解析简单的 "text" 格式
            match = Regex.Match(actionValue, @"""([^""]*)""");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 计算文本显示时间
        /// </summary>
        private int CalculateDisplayTime(string text)
        {
            return Math.Max(text.Length * _plugin.Settings.SayTimeMultiplier, _plugin.Settings.SayTimeMin);
        }
        
        /// <summary>
        /// 内部方法：执行动作指令（供TTSRequestSerializer调用）
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        internal async Task ExecuteActionInternalAsync(string actionContent)
        {
            await ExecuteActionAsync(actionContent).ConfigureAwait(false);
        }
        
        /// <summary>
        /// 内部方法：等待外置TTS完成（供TTSRequestSerializer调用）
        /// </summary>
        /// <param name="text">TTS文本</param>
        internal async Task WaitForExternalTTSInternalAsync(string text)
        {
            await WaitForExternalTTSCompleteAsync(text).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 消息片段
    /// </summary>
    public class MessageSegment
    {
        public SegmentType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string ActionValue { get; set; } = string.Empty;
    }

    /// <summary>
    /// 片段类型
    /// </summary>
    public enum SegmentType
    {
        Text,      // 纯文本
        Talk,      // 说话动作
        State,     // 状态动作
        Action     // 其他动作
    }
}