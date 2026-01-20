namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 上下文过滤器，处理图片数据的智能屏蔽和恢复
    /// </summary>
    public class ContextFilter
    {
        /// <summary>
        /// 图片占位符文本
        /// </summary>
        public const string IMAGE_PLACEHOLDER = "[图片内容已省略]";

        /// <summary>
        /// 根据当前模型的视觉能力过滤上下文
        /// </summary>
        /// <param name="messages">原始消息列表</param>
        /// <param name="supportsVision">当前模型是否支持视觉</param>
        /// <returns>过滤后的消息列表（用于发送给 LLM）</returns>
        public List<Message> FilterForModel(List<Message> messages, bool supportsVision)
        {
            if (messages is null || messages.Count == 0)
            {
                return new List<Message>();
            }

            if (supportsVision)
            {
                // 视觉模型：返回完整消息（包含图片）
                return messages;
            }

            // 非视觉模型：屏蔽图片数据，用占位符替代
            return messages.Select(m => FilterMessage(m)).ToList();
        }

        /// <summary>
        /// 过滤单条消息，移除图片数据并添加占位符
        /// </summary>
        private Message FilterMessage(Message original)
        {
            if (!original.HasImage)
            {
                return original;
            }

            // 创建新消息，不复制图片数据
            var filtered = new Message
            {
                Role = original.Role,
                Content = ReplaceImageReference(original.Content),
                UnixTime = original.UnixTime,
                StatusInfo = original.StatusInfo,
                MessageType = original.MessageType
                // ImageData 和 ImageMimeType 不复制，保持为 null
            };

            return filtered;
        }

        /// <summary>
        /// 替换消息内容中的图片引用为占位符
        /// </summary>
        private string ReplaceImageReference(string? content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return IMAGE_PLACEHOLDER;
            }

            // 替换 [图像] 标记为占位符
            var result = content.Replace("[图像]", IMAGE_PLACEHOLDER);

            // 如果内容没有变化且原消息有图片，在开头添加占位符
            if (result == content)
            {
                result = $"{IMAGE_PLACEHOLDER}\n{content}";
            }

            return result;
        }

        /// <summary>
        /// 检查当前 LLM 是否支持视觉
        /// </summary>
        /// <param name="settings">设置对象</param>
        /// <returns>是否支持视觉</returns>
        public bool CheckVisionSupport(Setting settings)
        {
            if (settings is null)
            {
                return false;
            }

            return settings.Provider switch
            {
                Setting.LLMType.Free => settings.Free?.EnableVision ?? false,
                Setting.LLMType.OpenAI => GetCurrentOpenAINodeVision(settings),
                Setting.LLMType.Gemini => GetCurrentGeminiNodeVision(settings),
                Setting.LLMType.Ollama => settings.Ollama?.EnableVision ?? false,
                _ => false
            };
        }

        /// <summary>
        /// 获取当前 OpenAI 节点的视觉能力
        /// </summary>
        private bool GetCurrentOpenAINodeVision(Setting settings)
        {
            var currentNode = settings.OpenAI?.GetCurrentOpenAISetting();
            return currentNode?.EnableVision ?? false;
        }

        /// <summary>
        /// 获取当前 Gemini 节点的视觉能力
        /// </summary>
        private bool GetCurrentGeminiNodeVision(Setting settings)
        {
            var currentNode = settings.Gemini?.GetCurrentGeminiSetting();
            return currentNode?.EnableVision ?? false;
        }

        /// <summary>
        /// 检查消息列表中是否包含图片
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <returns>是否包含图片</returns>
        public bool HasImagesInContext(List<Message> messages)
        {
            return messages?.Any(m => m.HasImage) ?? false;
        }
    }
}
