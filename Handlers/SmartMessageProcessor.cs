using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
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

            // 解析消息，提取文本片段和动作指令
            var messageSegments = ParseMessage(response);

            Logger.Log($"SmartMessageProcessor: 解析出 {messageSegments.Count} 个消息片段");

            // 按顺序处理每个片段
            foreach (var segment in messageSegments)
            {
                await ProcessSegmentAsync(segment);
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

            // 按行分割消息，逐行处理
            var lines = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                var lineMatch = regex.Match(trimmedLine);
                if (lineMatch.Success)
                {
                    // 这是一个动作指令行
                    var actionType = lineMatch.Groups[1].Value.ToLower();
                    var actionValue = lineMatch.Groups[2].Value;

                    Logger.Log($"SmartMessageProcessor: 解析到动作指令 - 类型: {actionType}, 值: {actionValue}");

                    segments.Add(new MessageSegment
                    {
                        Type = GetSegmentTypeFromAction(actionType),
                        Content = trimmedLine,
                        ActionType = actionType,
                        ActionValue = actionValue
                    });
                }
                else
                {
                    // 这是普通文本行
                    Logger.Log($"SmartMessageProcessor: 解析到文本行: {trimmedLine}");
                    segments.Add(new MessageSegment
                    {
                        Type = SegmentType.Text,
                        Content = trimmedLine
                    });
                }
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
                _ => SegmentType.Action
            };
        }

        /// <summary>
        /// 处理单个消息片段
        /// </summary>
        private async Task ProcessSegmentAsync(MessageSegment segment)
        {
            Logger.Log($"SmartMessageProcessor: 处理片段类型: {segment.Type}, 内容: {segment.Content}");

            switch (segment.Type)
            {
                case SegmentType.Text:
                    await ProcessTextSegmentAsync(segment.Content);
                    break;

                case SegmentType.Talk:
                    await ProcessTalkSegmentAsync(segment);
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
        /// 处理talk动作片段
        /// </summary>
        private async Task ProcessTalkSegmentAsync(MessageSegment segment)
        {
            Logger.Log($"SmartMessageProcessor: 处理talk动作: {segment.Content}");

            // 解析talk动作中的文本内容
            var talkText = ExtractTalkText(segment.ActionValue);

            if (!string.IsNullOrEmpty(talkText))
            {
                // 如果启用了TTS，音频开始播放后立即显示气泡
                if (_plugin.Settings.TTS.IsEnabled)
                {
                    Logger.Log($"SmartMessageProcessor: TTS和气泡同步播放: {talkText}");
                    
                    try
                    {
                        // 启动TTS播放，音频开始播放时立即返回
                        await _plugin.TTSService.StartPlayTextAsync(talkText);
                        Logger.Log($"SmartMessageProcessor: TTS已开始播放");
                        
                        // TTS开始播放后立即显示气泡
                        await ExecuteActionAsync(segment.Content);
                        Logger.Log($"SmartMessageProcessor: TTS和气泡同步完成");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"SmartMessageProcessor: TTS启动失败: {ex.Message}");
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

            bool ttsSuccess = false;

            try
            {
                // 优先请求TTS音频
                if (_plugin.TTSService != null)
                {
                    Logger.Log($"SmartMessageProcessor: 请求TTS音频...");
                    await _plugin.TTSService.PlayTextAsync(text);
                    ttsSuccess = true;
                    Logger.Log($"SmartMessageProcessor: TTS音频播放成功");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: TTS音频处理失败: {ex.Message}");
                ttsSuccess = false;
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
        /// 仅播放TTS语音
        /// </summary>
        private async Task PlayTTSAsync(string text)
        {
            Logger.Log($"SmartMessageProcessor: 播放TTS: {text}");

            try
            {
                if (_plugin.TTSService != null)
                {
                    await _plugin.TTSService.PlayTextAsync(text);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SmartMessageProcessor: TTS播放失败: {ex.Message}");
            }
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