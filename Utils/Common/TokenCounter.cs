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

            // 手动数而不是 Regex.Matches(...).Count：这个方法会被整段历史反复调用，
            // 正则每次都要建一遍 MatchCollection，纯属白烧 CPU 和内存。
            int chineseCharCount = 0;
            foreach (var c in text)
            {
                if (c >= '一' && c <= '龥') chineseCharCount++;
            }

            int nonChineseCharCount = text.Length - chineseCharCount;

            // Token估算规则：
            // - 中文：约1.5个字符 = 1个token
            // - 英文/数字/标点：约4个字符 = 1个token
            //
            // 注意这只是"数量级正确"的估算，各家分词器差异很大 ——
            // 真要对齐服务端，靠的是 ContextLimitGuard 用错误里带回来的
            // n_prompt_tokens 反算校准系数，而不是把这两个常数调得更准。
            int chineseTokens = (int)Math.Ceiling(chineseCharCount / 1.5);
            int nonChineseTokens = (int)Math.Ceiling(nonChineseCharCount / 4.0);

            return chineseTokens + nonChineseTokens;
        }

        /// <summary>
        /// 估算消息列表的总Token数量。
        ///
        /// 数的是 <see cref="Message.DisplayContent"/> 而不是 <c>Content</c> ——
        /// **真正发上线的是前者**。user 消息的 DisplayContent 会把正文包成
        /// <c>{"NowTime": "...", "YourStatus": "...", "UserSay": "..."}</c>，
        /// 比 Content 长出一截；只数 Content 会系统性低估，
        /// 于是"按预算裁剪"在真正该裁的时候一刀都不裁。
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <returns>估算的总Token数量</returns>
        public static int EstimateMessagesTokenCount(IEnumerable<Message> messages)
        {
            int totalTokens = 0;
            foreach (var message in messages)
            {
                var wire = message.DisplayContent;
                if (!string.IsNullOrWhiteSpace(wire))
                {
                    totalTokens += EstimateTokenCount(wire);
                    // 每条消息额外增加4个token（用于角色标识和格式化）
                    totalTokens += 4;
                }
            }
            return totalTokens;
        }
    }
}
