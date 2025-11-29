using System;
using System.Text;
using System.Text.RegularExpressions;
using VPetLLM.Utils;
using System.Linq;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 智能消息处理器，用于处理包含动作指令的AI回复
    /// 支持TTS语音播放与气泡同步显示
    /// </summary>
    public class SmartMessageProcessor
    {
        private readonly VPetLLM _plugin;
        private readonly ActionProcessor _actionProcessor;
        private bool _isProcessing = false;
        private readonly object _processingLock = new object();

        public SmartMessageProcessor(VPetLLM plugin)
        {
            _plugin = plugin;
            _actionProcessor = plugin.ActionProcessor;
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
        public async Task ProcessMessageAsync(string response, bool skipInitialization = false)
        {
            if (string.IsNullOrWhiteSpace(response))
                return;

            // 设置处理状态
            lock (_processingLock)
            {
                _isProcessing = true;
            }

            try
            {
                Logger.Log($"SmartMessageProcessor: 开始处理消息: {response}, 跳过初始化: {skipInitialization}");
                
                // 只在首次响应时清理MessageBar状态
                if (!skipInitialization)
                {
                    // 清理MessageBar状态，准备显示新内容
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var msgBar = _plugin.MW.Main.MsgBar;
                            if (msgBar != null)
                            {
                                MessageBarHelper.Initialize(msgBar);
                                MessageBarHelper.StopAllTimers(msgBar);
                                MessageBarHelper.ClearStreamState(msgBar);
                            }
                        }
                        catch { /* 忽略清理错误 */ }
                    }));
                    // 移除延迟，直接继续处理
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
        /// 优化：使用ConfigureAwait(false)避免UI线程阻塞
        /// </summary>
        private async Task ProcessTalkSegmentWithQueueAsync(MessageSegment segment, List<TTSDownloadTask> downloadTasks, int talkIndex)
        {
            Logger.Log($"SmartMessageProcessor: 处理talk动作 #{talkIndex}: {segment.Content}");

            // 解析talk动作中的文本内容
            var talkText = ExtractTalkText(segment.ActionValue);

            if (!string.IsNullOrEmpty(talkText))
            {
                try
                {
                    string audioFile = null;

                    // 首先检查是否有流式处理预下载的音频
                    var predownloadedAudio = StreamingCommandProcessor.GetAndRemovePredownloadedAudio(segment.Content);
                    if (!string.IsNullOrEmpty(predownloadedAudio))
                    {
                        Logger.Log($"SmartMessageProcessor: 使用流式预下载的音频: {predownloadedAudio}");
                        audioFile = predownloadedAudio;
                    }
                    else if (downloadTasks != null)
                    {
                        // 没有预下载音频，等待当前索引的音频下载完成（不阻塞UI线程）
                        Logger.Log($"SmartMessageProcessor: 等待音频 #{talkIndex} 下载完成...");
                        audioFile = await WaitForTTSDownloadAsync(downloadTasks, talkIndex).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrEmpty(audioFile))
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 准备就绪，开始播放: {audioFile}");

                        // 立即显示气泡（与音频同步）
                        var bubbleTask = ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: 气泡显示任务已启动");

                        // 在开始播放当前音频时，立即启动下一个音频的下载（预下载优化）
                        StartNextTTSDownload(downloadTasks, talkIndex);

                        // 播放音频（等待播放完成，不阻塞UI线程）
                        await _plugin.TTSService.PlayAudioFileDirectAsync(audioFile).ConfigureAwait(false);
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 播放完成");

                        // TTS启用时不需要等待气泡显示完成，音频播放完成即可继续
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 播放完成，不等待气泡消失");
                        
                        // 注意：不等待气泡任务完成，让气泡自然显示和消失
                        // 也不等待 EdgeTTS，因为我们使用的是 VPetLLM 内置 TTS
                    }
                    else
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 不可用，仅显示气泡");
                        // 音频不可用，仅显示气泡
                        await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SmartMessageProcessor: 处理音频 #{talkIndex} 失败: {ex.Message}");
                    Logger.Log($"SmartMessageProcessor: 异常堆栈: {ex.StackTrace}");
                    // 失败时仍然显示气泡
                    await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
                }
            }
            else
            {
                // 如果没有文本内容，直接执行动作
                await ExecuteActionAsync(segment.Content).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 等待 VPet 主程序的语音播放完成（EdgeTTS 或其他 TTS 插件）
        /// 优化：减少轮询频率，利用VPet新的SayInfo架构提供的状态信息
        /// </summary>
        private async Task WaitForVPetVoiceCompleteAsync()
        {
            try
            {
                // 检查是否启用了 VPetLLM 的 TTS
                if (_plugin.Settings.TTS.IsEnabled)
                {
                    Logger.Log("SmartMessageProcessor: VPetLLM TTS 已启用，跳过 VPet 语音等待");
                    return;
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
        /// 增加：为外置TTS额外添加等待时间
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

                // 为外置TTS添加额外1秒等待时间，确保播放完全
                Logger.Log("SmartMessageProcessor: 为外置TTS添加额外1秒等待时间");
                await Task.Delay(1000).ConfigureAwait(false);
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
                    await Task.Delay(CalculateDisplayTime(actualText));
                    // TTS关闭时等待 EdgeTTS 或其他 TTS 插件播放完成
                    await WaitForVPetVoiceCompleteAsync();
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
                    // 如果TTS未启用，直接执行动作
                    // 优化：利用VPet新的SayInfo架构，更智能地等待
                    await ExecuteActionAsync(segment.Content);
                    
                    // 计算气泡显示时间
                    int displayTime = CalculateDisplayTime(talkText);
                    Logger.Log($"SmartMessageProcessor: TTS未启用，等待气泡显示 {displayTime}ms");
                    await Task.Delay(displayTime).ConfigureAwait(false);
                    
                    // TTS关闭时等待 VPet 语音完成（如EdgeTTS）
                    // 优化：利用VPet的PlayingVoice状态进行智能等待
                    await WaitForVPetVoiceWithSayInfoAsync();
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