namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 消息组合器，用于将图片描述和用户问题组合成格式化消息
    /// </summary>
    public static class MessageCombiner
    {
        /// <summary>
        /// 组合图片描述和用户问题
        /// </summary>
        /// <param name="imageDescription">图片描述（由多模态模型生成）</param>
        /// <param name="userQuestion">用户问题</param>
        /// <returns>组合后的消息</returns>
        public static string Combine(string imageDescription, string userQuestion)
        {
            var hasDescription = !string.IsNullOrWhiteSpace(imageDescription);
            var hasQuestion = !string.IsNullOrWhiteSpace(userQuestion);

            if (!hasDescription && !hasQuestion)
            {
                return "";
            }

            if (!hasDescription)
            {
                return userQuestion;
            }

            if (!hasQuestion)
            {
                return $"[图片内容]\n{imageDescription}";
            }

            return $"[图片内容]\n{imageDescription}\n\n[用户问题]\n{userQuestion}";
        }

        /// <summary>
        /// 组合图片描述和用户问题（带自定义标签）
        /// </summary>
        /// <param name="imageDescription">图片描述</param>
        /// <param name="userQuestion">用户问题</param>
        /// <param name="imageLabel">图片内容标签</param>
        /// <param name="questionLabel">用户问题标签</param>
        /// <returns>组合后的消息</returns>
        public static string Combine(string imageDescription, string userQuestion,
            string imageLabel, string questionLabel)
        {
            var hasDescription = !string.IsNullOrWhiteSpace(imageDescription);
            var hasQuestion = !string.IsNullOrWhiteSpace(userQuestion);

            if (!hasDescription && !hasQuestion)
            {
                return "";
            }

            if (!hasDescription)
            {
                return userQuestion;
            }

            if (!hasQuestion)
            {
                return $"[{imageLabel}]\n{imageDescription}";
            }

            return $"[{imageLabel}]\n{imageDescription}\n\n[{questionLabel}]\n{userQuestion}";
        }
    }
}
