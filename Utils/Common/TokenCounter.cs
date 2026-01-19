using System.Text.RegularExpressions;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// Token计数工具类，用于估算文本的Token数量
    /// </summary>
    public static class TokenCounter
    {
        /// <summary>
        /// 估算文本的Token数量
        /// 使用更精确的混合估算方法
        /// </summary>
        /// <param name="text">要估算的文本</param>
        /// <returns>估算的Token数量</returns>
        public static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            // 统计中文字符数量
            int chineseCharCount = Regex.Matches(text, @"[\u4e00-\u9fa5]").Count;

            // 统计英文单词数量（包括数字和标点）
            int nonChineseCharCount = text.Length - chineseCharCount;

            // Token估算规则：
            // - 中文：约1.5个字符 = 1个token
            // - 英文/数字/标点：约4个字符 = 1个token
            int chineseTokens = (int)Math.Ceiling(chineseCharCount / 1.5);
            int nonChineseTokens = (int)Math.Ceiling(nonChineseCharCount / 4.0);

            return chineseTokens + nonChineseTokens;
        }

        /// <summary>
        /// 估算消息列表的总Token数量
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <returns>估算的总Token数量</returns>
        public static int EstimateMessagesTokenCount(IEnumerable<Core.Message> messages)
        {
            int totalTokens = 0;
            foreach (var message in messages)
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    totalTokens += EstimateTokenCount(message.Content);
                    // 每条消息额外增加4个token（用于角色标识和格式化）
                    totalTokens += 4;
                }
            }
            return totalTokens;
        }
    }
}
