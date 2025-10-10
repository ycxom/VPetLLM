using System;
using System.Text.RegularExpressions;
using VPetLLM.Utils;

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

        public SmartMessageProcessor(VPetLLM plugin)
        {
            _plugin = plugin;
            _actionProcessor = plugin.ActionProcessor;
        }

        /// <summary>
        /// 处理AI回复消息，解析动作指令并按顺序执行
        /// </summary>
        /// <param name="response">AI回复内容</param>
        public async Task ProcessMessageAsync(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return;

            Logger.Log($"SmartMessageProcessor: 开始处理消息: {response}");
            
            // 标记进入单条 AI 回复的处理会话，期间豁免插件/工具限流
            var _sessionId = Guid.NewGuid();
            global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value = _sessionId;

            // 通知TouchInteractionHandler开始执行VPetLLM动作
            TouchInteractionHandler.NotifyVPetLLMActionStart();

            // 解析消息，提取文本片段和动作指令
            var messageSegments = ParseMessage(response);

            Logger.Log($"SmartMessageProcessor: 解析出 {messageSegments.Count} 个消息片段");

            // 启动并行下载所有TTS音频（不等待完成）
            List<TTSDownloadTask> downloadTasks = null;
            if (_plugin.Settings.TTS.IsEnabled)
            {
                downloadTasks = StartParallelTTSDownload(messageSegments);
            }

            // 按顺序处理每个片段，使用智能等待机制
            int talkIndex = 0;
            try
            {
                foreach (var segment in messageSegments)
                {
                    if (segment.Type == SegmentType.Talk && downloadTasks != null)
                    {
                        await ProcessTalkSegmentWithQueueAsync(segment, downloadTasks, talkIndex++);
                    }
                    else
                    {
                        await ProcessSegmentAsync(segment, null);
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

        /// <summary>
        /// 解析消息，将其分解为文本片段和动作指令
        /// </summary>
        /// <param name="message">原始消息</param>
        /// <returns>消息片段列表</returns>
        private List<MessageSegment> ParseMessage(string message)
        {
            var segments = new List<MessageSegment>();

            Logger.Log($"SmartMessageProcessor: 开始解析消息，长度: {message.Length}");

            // 匹配完整的动作指令，包括嵌套的括号
            var actionPattern = @"\[:(\w+)\(([^[\]]*(?:\([^)]*\)[^[\]]*)*)\)\]";
            var regex = new Regex(actionPattern);

            var matches = regex.Matches(message);
            Logger.Log($"SmartMessageProcessor: 找到 {matches.Count} 个动作指令");

            if (matches.Count == 0)
            {
                // 没有找到动作指令，整个消息作为文本处理
                segments.Add(new MessageSegment
                {
                    Type = SegmentType.Text,
                    Content = message.Trim()
                });
                Logger.Log($"SmartMessageProcessor: 没有找到动作指令，作为纯文本处理");
                return segments;
            }

            // 直接处理所有匹配到的动作指令
            foreach (Match match in matches)
            {
                var actionType = match.Groups[1].Value.ToLower();
                var actionValue = match.Groups[2].Value;
                var fullMatch = match.Value;

                Logger.Log($"SmartMessageProcessor: 解析到动作指令 - 类型: {actionType}, 值: {actionValue}");

                segments.Add(new MessageSegment
                {
                    Type = GetSegmentTypeFromAction(actionType),
                    Content = fullMatch,
                    ActionType = actionType,
                    ActionValue = actionValue
                });
            }

            Logger.Log($"SmartMessageProcessor: 解析完成，共 {segments.Count} 个片段");
            return segments;
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
        /// 等待指定索引的音频下载完成
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
                Logger.Log($"SmartMessageProcessor: 等待任务 #{targetIndex} 下载完成...");
                var audioFile = await targetTask.DownloadTask;
                targetTask.AudioFile = audioFile;
                targetTask.IsCompleted = true;

                Logger.Log($"SmartMessageProcessor: 任务 #{targetIndex} 下载完成: {audioFile}");
                return audioFile;
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 任务 #{targetIndex} 下载失败: {ex.Message}");
                targetTask.IsCompleted = true;
                targetTask.AudioFile = null;
                return null;
            }
        }

        /// <summary>
        /// 使用智能队列处理talk动作片段
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
                    // 等待当前索引的音频下载完成
                    Logger.Log($"SmartMessageProcessor: 等待音频 #{talkIndex} 下载完成...");
                    var audioFile = await WaitForTTSDownloadAsync(downloadTasks, talkIndex);

                    if (!string.IsNullOrEmpty(audioFile))
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 下载完成，开始播放: {audioFile}");

                        // 立即显示气泡（与音频同步）
                        var bubbleTask = ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: 气泡显示任务已启动");

                        // 播放音频（等待播放完成）
                        await _plugin.TTSService.PlayAudioFileDirectAsync(audioFile);
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 播放完成");

                        // 等待气泡显示完成
                        await bubbleTask;
                        Logger.Log($"SmartMessageProcessor: 音频和气泡 #{talkIndex} 处理完成");
                    }
                    else
                    {
                        Logger.Log($"SmartMessageProcessor: 音频 #{talkIndex} 下载失败，仅显示气泡");
                        // 音频下载失败，仅显示气泡
                        await ExecuteActionAsync(segment.Content);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"SmartMessageProcessor: 处理音频 #{talkIndex} 失败: {ex.Message}");
                    Logger.Log($"SmartMessageProcessor: 异常堆栈: {ex.StackTrace}");
                    // 失败时仍然显示气泡
                    await ExecuteActionAsync(segment.Content);
                }
            }
            else
            {
                // 如果没有文本内容，直接执行动作
                await ExecuteActionAsync(segment.Content);
            }
        }

        /// <summary>
        /// 处理单个消息片段
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
                    await ProcessTalkSegmentAsync(segment, audioCache);
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
                }
                else
                {
                    // 直接显示气泡
                    _plugin.MW.Main.Say(actualText);
                    await Task.Delay(CalculateDisplayTime(actualText));
                }
            }
        }

        /// <summary>
        /// 从talk指令中提取文本内容
        /// </summary>
        private string ExtractTextFromTalkCommand(string input)
        {
            // 匹配 [:talk(say("文本内容", emotion))] 格式
            var talkPattern = @"\[:talk\(say\(""([^""]+)"",\s*\w+\)\)\]";
            var match = Regex.Match(input, talkPattern);

            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            // 匹配 [:say("文本内容", emotion)] 格式
            var sayPattern = @"\[:say\(""([^""]+)"",\s*\w+\)\]";
            match = Regex.Match(input, sayPattern);

            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 处理talk动作片段（兼容模式）
        /// </summary>
        private async Task ProcessTalkSegmentAsync(MessageSegment segment, Dictionary<string, string> audioCache = null)
        {
            Logger.Log($"SmartMessageProcessor: 处理talk动作（兼容模式）: {segment.Content}");

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
                        // 立即显示气泡（与音频同步）
                        var bubbleTask = ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: 气泡显示任务已启动");

                        // 播放音频（等待播放完成）
                        await _plugin.TTSService.PlayTextAsync(talkText);
                        Logger.Log($"SmartMessageProcessor: TTS播放完成");

                        // 等待气泡显示完成
                        await bubbleTask;
                        Logger.Log($"SmartMessageProcessor: TTS和气泡同步处理完成");
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
                    await ExecuteActionAsync(segment.Content);
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
                    Logger.Log($"SmartMessageProcessor: TTS音频播放成功");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: TTS音频处理失败: {ex.Message}");
            }

            // TTS完成（成功或失败）后才显示气泡
            Logger.Log($"SmartMessageProcessor: TTS处理完成，显示气泡: {text}");
            _plugin.MW.Main.Say(text);

            // 等待气泡显示时间
            var displayTime = CalculateDisplayTime(text);
            await Task.Delay(displayTime);

            Logger.Log($"SmartMessageProcessor: 消息处理完成");
        }

        /// <summary>
        /// 执行动作指令
        /// </summary>
        private async Task ExecuteActionAsync(string actionContent)
        {
            Logger.Log($"SmartMessageProcessor: 执行动作指令: {actionContent}");

            try
            {
                var actionQueue = _actionProcessor.Process(actionContent, _plugin.Settings);

                foreach (var action in actionQueue)
                {
                    Logger.Log($"SmartMessageProcessor: 执行动作: {action.Keyword}, 值: {action.Value}");

                    // 对插件类动作做非用户触发限流（会话内豁免）：2分钟内最多5次
                    var isPluginAction = action.Type == ActionType.Plugin || string.Equals(action.Keyword, "plugin", StringComparison.OrdinalIgnoreCase);
                    if (isPluginAction)
                    {
                        var inMessageSession = global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value.HasValue;
                        if (!inMessageSession)
                        {
                            if (!Utils.RateLimiter.TryAcquire("ai-plugin", 5, TimeSpan.FromMinutes(2)))
                            {
                                Logger.Log("SmartMessageProcessor: 插件动作频率超限（跨消息），终止剩余动作执行以丢弃非用户触发链。");
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(action.Value))
                        await action.Handler.Execute(_plugin.MW);
                    else if (int.TryParse(action.Value, out int intValue))
                        await action.Handler.Execute(intValue, _plugin.MW);
                    else
                        await action.Handler.Execute(action.Value, _plugin.MW);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: 动作执行失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从talk动作中提取文本内容
        /// </summary>
        private string ExtractTalkText(string actionValue)
        {
            // 解析 say("文本内容", 动画) 格式
            var match = Regex.Match(actionValue, @"say\s*\(\s*""([^""]*)""\s*(?:,\s*([^)]*))?\s*\)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // 解析简单的 "文本内容" 格式
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