using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 从"上下文超长"这一类 400 错误里把服务端真实的上下文窗口读出来，记住它，
    /// 让后续请求自动按这个上限裁剪历史。
    ///
    /// 存在的理由：<see cref="Setting.MaxContextTokens"/> 默认是 0（不限制），
    /// 而本地推理服务端（llama.cpp / LM Studio / vLLM / Ollama）的 n_ctx 常常只有
    /// 4096~8192。用户没手工填过这个数字时，历史一长就必然撞上：
    ///
    ///   {"error":{"code":400,"message":"request (13392 tokens) exceeds the available
    ///    context size (8192 tokens), try increasing it","type":"exceed_context_size_error",
    ///    "n_prompt_tokens":13392,"n_ctx":8192}}
    ///
    /// 而且撞上之后**每一轮都会再撞一次** —— 历史只增不减，错误信息里明明写着 8192，
    /// 却没有任何一处代码去读它。这个类就是去读它。
    ///
    /// 记住的值只活在本次进程内，按"渠道|主机|模型"分别记 —— 同一个 provider 下
    /// 不同节点的窗口大小可以差一个数量级，混着记会把大窗口的节点也一起裁短。
    /// </summary>
    public static class ContextLimitGuard
    {
        /// <summary>已探明的上下文上限，键为 <see cref="MakeKey"/> 生成的标识。</summary>
        private static readonly ConcurrentDictionary<string, int> Learned = new();

        /// <summary>
        /// 同一个上限重复撞第二次，说明我们的 token 估算比服务端的真实分词偏低，
        /// 光按报回来的 n_ctx 裁还不够。这时按这个系数继续收紧。
        /// </summary>
        private const double ShrinkFactor = 0.7;

        /// <summary>再怎么收紧也不低于这个值，否则窗口小到连当前这轮提问都放不下。</summary>
        private const int MinimumLimit = 512;

        /// <summary>
        /// 状态码 + 响应体是否是"上下文超长"。
        ///
        /// 只认 4xx 里那几个语义明确的状态码：5xx 是服务端自己的问题，401/403 是鉴权，
        /// 把它们一并当成上下文超长会导致无谓的裁剪和重试。
        /// </summary>
        public static bool IsContextLimitError(int status, string? body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            if (status != 400 && status != 413 && status != 422) return false;

            foreach (var marker in Markers)
            {
                if (body!.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>各家对"上下文超长"的说法。命中任意一条即认定。</summary>
        private static readonly string[] Markers =
        {
            "exceed_context_size_error",   // llama.cpp / llama-server
            "context_length_exceeded",     // OpenAI
            "context size",                // "exceeds the available context size"
            "context length",              // "maximum context length is 8192 tokens"
            "context window",              // Anthropic 兼容网关等
            "reduce the length of the messages",
            "input token count exceeds",   // Gemini
            "prompt is too long",
            "too many tokens"
        };

        /// <summary>
        /// 从错误响应体里解析出上下文窗口大小。解析不到返回 0 ——
        /// 有些网关只说"太长了"却不给数字，那种情况没有可信的裁剪目标，宁可不动。
        /// </summary>
        public static int ParseContextTokens(string? body)
        {
            if (string.IsNullOrEmpty(body)) return 0;

            foreach (var pattern in ContextPatterns)
            {
                var match = pattern.Match(body!);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var tokens) && tokens > 0)
                    return tokens;
            }
            return 0;
        }

        /// <summary>
        /// 上下文窗口大小的几种写法。顺序即优先级：结构化字段最可信，散在自然语言里的次之。
        /// </summary>
        private static readonly Regex[] ContextPatterns =
        {
            // {"n_ctx":8192}
            new(@"""n_ctx""\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // exceeds the available context size (8192 tokens)
            new(@"context size\s*\(?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // This model's maximum context length is 8192 tokens
            new(@"context length is\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // maximum context length of 8192 / context window of 8192
            new(@"context (?:length|window) of (?:only )?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>
        /// 服务端数出来的"我们刚发过去那一坨"的 token 数（n_prompt_tokens / "request (N tokens)"）。
        /// 解析不到返回 0。
        ///
        /// 这个数字是校准的关键：它和我们自己对同一份内容的估算一比，就得出
        /// "我们的估算差了多少倍"，从而把窗口大小换算成**我们估算器单位**下的预算。
        /// 否则光知道 n_ctx=8192 没用 —— 我们按自己的尺子量出 5000 就以为没超，
        /// 照发不误，然后再挨一次 400。
        /// </summary>
        public static int ParsePromptTokens(string? body)
        {
            if (string.IsNullOrEmpty(body)) return 0;

            foreach (var pattern in PromptPatterns)
            {
                var match = pattern.Match(body!);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var tokens) && tokens > 0)
                    return tokens;
            }
            return 0;
        }

        private static readonly Regex[] PromptPatterns =
        {
            // {"n_prompt_tokens":13392}
            new(@"""n_prompt_tokens""\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // request (13392 tokens) exceeds ...
            new(@"request\s*\(\s*(\d+)\s*tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // ... your messages resulted in 13392 tokens
            new(@"resulted in\s*(\d+)\s*tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // ... however you requested 13392 tokens
            new(@"you requested\s*(\d+)\s*tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>
        /// 估算误差的封顶。分词器再怎么不一样也就差这个量级；
        /// 万一某次"我们的估算"因为别的原因异常地小，别让它把预算算成个位数。
        /// </summary>
        private const double MaxCalibrationRatio = 8.0;

        /// <summary>低于这个估算值就不做校准：样本太小，比出来的倍率没有意义。</summary>
        private const int MinCalibrationSample = 200;

        public static string MakeKey(string provider, string? url, string? model)
        {
            var host = url ?? "";
            try
            {
                if (!string.IsNullOrWhiteSpace(url) &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    host = uri.Host + ":" + uri.Port;
                }
            }
            catch { /* URL 拼错不该影响主流程，退回用原串当键 */ }

            return $"{provider}|{host}|{model ?? ""}";
        }

        /// <summary>已探明的上限，未知返回 0。</summary>
        public static int GetLimit(string? key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            return Learned.TryGetValue(key!, out var limit) ? limit : 0;
        }

        /// <summary>
        /// 记下一个新探明的上限。返回 true 表示这次确实收紧了（调用方据此决定要不要重试）。
        ///
        /// 已经记过同样或更小的值时会**再收紧一档**而不是原样返回 false：
        /// 走到这里说明按上一次记的值裁完仍然超长，即我们的 token 估算偏低，
        /// 继续用同一个数字重试只会再失败一次。
        /// </summary>
        public static bool Remember(string? key, int contextTokens, int ourEstimate = 0, int serverPromptTokens = 0)
        {
            if (string.IsNullOrEmpty(key) || contextTokens <= 0) return false;

            // 把服务端的窗口大小换算成**我们估算器单位**下的预算。
            // 不换算的话，"按 8192 裁剪"用的是我们自己那把偏小的尺子，
            // 量出来 5000 < 8192 就不裁了，重发的还是同一坨 13392。
            var proposed = (int)(contextTokens / CalibrationRatio(ourEstimate, serverPromptTokens));
            if (Learned.TryGetValue(key!, out var existing) && existing > 0 && proposed >= existing)
            {
                proposed = (int)(existing * ShrinkFactor);
                if (proposed < MinimumLimit)
                {
                    // 已经收紧到不能再收，再退让也换不来成功，交给上层报错
                    return false;
                }
            }

            Learned[key!] = proposed;
            return true;
        }

        /// <summary>
        /// "我们估算的 1 个 token，服务端实际数成几个"。拿不到样本时返回 1（即不校准）。
        /// </summary>
        public static double CalibrationRatio(int ourEstimate, int serverPromptTokens)
        {
            if (ourEstimate < MinCalibrationSample || serverPromptTokens <= 0)
                return 1.0;

            var ratio = (double)serverPromptTokens / ourEstimate;

            // 我们估多了（ratio < 1）就按 1 算：那种情况下按原样裁剪已经足够保守
            if (ratio <= 1.0) return 1.0;
            return Math.Min(ratio, MaxCalibrationRatio);
        }

        /// <summary>用户手工改了上下文设置后清空探明值，让新配置立刻生效。</summary>
        public static void Reset() => Learned.Clear();
    }
}
