using System.Runtime.CompilerServices;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 判定一条模型回复的格式，并给出它的规范写法。
    ///
    /// 两个用处：
    ///   1. 路由拿到完整回复后判定一次，推进 <see cref="FormatComplianceTracker"/>；
    ///   2. 组装请求时把历史里格式错误的 assistant 回复换成规范写法再发 ——
    ///      模型会照着上下文里自己的"先例"写，历史里躺着几条不带标记的回复，
    ///      提示词说得再多也拉不回来。只改发出去的副本，聊天记录本身不动。
    ///
    /// 规范写法 = 按显示管线同一套解析规则拆出来的片段依次拼接：标记外的正文包成 say，
    /// 写坏的标记当正文处理。和桌宠实际说出来、做出来的完全一致。
    /// </summary>
    public static class ReplyFormatInspector
    {
        public readonly record struct Result(FormatComplianceTracker.Violation Violation, string? Canonical)
        {
            public bool IsCompliant => Violation == FormatComplianceTracker.Violation.None;
        }

        private sealed class CacheEntry
        {
            public string Source = "";
            public Result Result;
        }

        // 历史每次请求都要扫一遍，按消息对象缓存；内容变了（用户编辑过）就重算
        private static readonly ConditionalWeakTable<Message, CacheEntry> Cache = new();

        public static Result Inspect(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return new Result(FormatComplianceTracker.Violation.None, null);

            var segments = SmartMessageProcessor.ParseMessage(reply, quiet: true, out var violation);
            if (violation == FormatComplianceTracker.Violation.None)
                return new Result(violation, null);

            var canonical = string.Join("\n", segments.Select(s => s.Content).Where(c => !string.IsNullOrWhiteSpace(c)));
            return new Result(violation, canonical);
        }

        /// <summary>
        /// 判定一条已入库的回复。被用户打断的回复末尾带着一段系统写的中断说明
        /// （见 ChatCoreBase.InterruptedMarker），它本来就在标记之外、也不是模型的台词 ——
        /// 不剥出来的话会被判成"正文写在标记外"，再被包成 say，模型就以为那是自己说过的话。
        /// </summary>
        private static Result InspectStored(string content)
        {
            var cut = content.IndexOf(ChatCoreBase.InterruptedMarkerTag, StringComparison.Ordinal);
            if (cut < 0)
                return Inspect(content);

            // 从标记所在行的开头切（它前面是 "\n[System: "）
            var lineStart = content.LastIndexOf('[', cut);
            if (lineStart < 0) lineStart = cut;
            var body = content.Substring(0, lineStart);
            var suffix = content.Substring(lineStart).Trim();

            var result = Inspect(body);
            return result.Canonical is null
                ? result
                : result with { Canonical = result.Canonical + "\n" + suffix };
        }

        /// <summary>
        /// 把请求里格式错误的 assistant 回复换成规范写法（返回新对象，原消息不动）。
        /// 就地修改传入的列表 —— 它本来就是为这次请求新建的。返回改写了几条。
        /// </summary>
        public static int NormalizeAssistantHistory(List<Message> history)
        {
            var rewritten = 0;
            for (var i = 0; i < history.Count; i++)
            {
                var message = history[i];
                if (message is null || message.NormalizedRole != "assistant") continue;

                var content = message.Content ?? "";
                var entry = Cache.GetOrCreateValue(message);
                if (entry.Source != content)
                {
                    entry.Source = content;
                    entry.Result = InspectStored(content);
                }

                if (entry.Result.Canonical is { Length: > 0 } canonical)
                {
                    history[i] = message.WithContent(canonical);
                    rewritten++;
                }
            }
            return rewritten;
        }
    }
}
