using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// "请求 → 模型要调工具 → 执行 → 带着结果再请求"这个循环的通用实现。
    ///
    /// provider 只需要提供一个 <c>send</c> 委托（怎么带认证 POST、怎么解析成 JObject），
    /// 认证、错误转移、节点选择这些仍然留在各自的 ChatCore 里。
    ///
    /// 注意：启用工具时本轮强制走非流式。流式下 tool_calls 是按 index 分片
    /// 拼回来的，跨五个 provider 做这件事很难保证正确，而且我无法对真实端点验证；
    /// 与其塞一个没验证过的拼装器，不如先牺牲打字机效果，把正确性守住。
    /// </summary>
    public static class NativeToolLoop
    {
        /// <summary>
        /// 判断模型是不是在原地打转：只认"工具名 + 参数完全一致"连续重复。
        ///
        /// 刻意做得很笨。之前按"总轮数"掐会误伤递进式取证（查 CPU→内存→显卡 五条
        /// 不同的命令被当成打转），换成任何模糊判据都会有同类风险，
        /// 而误判的代价是把用户的正当请求拦腰截断。
        /// </summary>
        public sealed class RepeatGuard
        {
            private string _last = "";
            private int _count;

            /// <summary>喂进这一轮的调用；返回 true 表示已经重复到上限。</summary>
            public bool IsStuck(IReadOnlyList<NativeToolCall> calls)
            {
                var signature = string.Join("|", calls.Select(
                    c => c.Name + "(" + c.Arguments.ToString(Newtonsoft.Json.Formatting.None) + ")"));

                if (signature == _last) _count++;
                else { _last = signature; _count = 1; }

                return _count >= NativeToolSession.RepeatLimit;
            }

            public int Count => _count;
        }

        /// <summary>
        /// 停下来的时候要让模型知道**为什么**停，否则它以为还会有下一轮，
        /// 最终回复就是空的 —— 那正是用户看到的"生硬切断"。
        /// </summary>
        private static string StopNotice(string reason) =>
            $"[tool loop stopped] {reason} " +
            "Do not call any more tools. Answer the user now, in the normal reply format, " +
            "using the information you already have.";

        private const string RepeatReason =
            "You issued the same tool call with identical arguments repeatedly; the result will not change.";

        private const string BudgetReason =
            "This turn has reached its tool-call budget.";

        /// <summary>
        /// 把"停"这件事变成一条正常的工具结果回给模型。
        ///
        /// 不能直接 break：模型已经发出了 tool_calls，OpenAI 那套协议要求每个
        /// tool_call 都必须有对应的 tool 消息，缺了下一次请求会被判非法。
        /// 而且这些调用**并没有真的执行**，所以不进转录 —— 历史里不该出现没发生过的调用。
        /// </summary>
        private static List<NativeToolResult> BuildStopResults(
            IReadOnlyList<NativeToolCall> calls, string reason)
            => calls.Select(c => new NativeToolResult
            {
                Call = c,
                Content = StopNotice(reason),
                PluginName = "",
                MarkerArguments = "",
                Succeeded = false
            }).ToList();

        /// <summary>
        /// 摘掉 tools，让收尾那一次请求只能出文字。
        /// 留着 tools 的话模型完全可能再调一次，那就白停了。
        /// </summary>
        private static void StripTools(JObject payload)
        {
            payload.Remove("tools");
            payload.Remove("tool_choice");
        }

        /// <summary>OpenAI / Ollama / LMStudio 通用（chat completions 格式）。</summary>
        public static async Task<NativeToolLoopResult> RunOpenAiAsync(
            JObject payload,
            NativeToolSession session,
            Func<JObject, Task<JObject?>> send)
        {
            var messages = payload["messages"] as JArray;
            if (messages is null)
            {
                Logger.Log("NativeToolLoop: payload 里没有 messages 数组，放弃工具循环");
                return NativeToolLoopResult.Failed();
            }

            var transcript = new List<NativeToolTranscript.Entry>();
            var guard = new RepeatGuard();

            for (int iteration = 0; ; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOpenAiToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                var reason = StopReason(guard, calls, iteration);
                if (reason is not null)
                {
                    NativeToolSession.AppendOpenAiTurn(messages, response, BuildStopResults(calls, reason));
                    StripTools(payload);
                    var final = await send(payload);
                    var finalText = final?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(finalText, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "", results);
                NativeToolSession.AppendOpenAiTurn(messages, response, results);
            }
        }

        /// <summary>
        /// 该不该停，以及停的理由（null = 继续）。两种停法走同一条收尾路径：
        /// 都是**不执行**这一轮的调用，把理由当成工具结果回给模型，再要一次纯文字回复。
        /// </summary>
        private static string? StopReason(RepeatGuard guard, IReadOnlyList<NativeToolCall> calls, int iteration)
        {
            if (guard.IsStuck(calls))
            {
                Logger.Log($"NativeToolLoop: 同一调用连续第 {guard.Count} 次，判定打转，改为要求模型直接作答");
                return RepeatReason;
            }

            if (iteration >= NativeToolSession.MaxIterations)
            {
                Logger.Log($"NativeToolLoop: 已用满 {NativeToolSession.MaxIterations} 次工具预算，改为要求模型直接作答");
                return BudgetReason;
            }

            return null;
        }

        /// <summary>OpenAI Responses API 格式（消息表叫 input，工具项散在 output[] 里）。</summary>
        public static async Task<NativeToolLoopResult> RunOpenAiResponsesAsync(
            JObject payload,
            NativeToolSession session,
            Func<JObject, Task<JObject?>> send)
        {
            var input = payload["input"] as JArray;
            if (input is null)
            {
                Logger.Log("NativeToolLoop: payload 里没有 input 数组，放弃工具循环");
                return NativeToolLoopResult.Failed();
            }

            var transcript = new List<NativeToolTranscript.Entry>();
            var guard = new RepeatGuard();

            for (int iteration = 0; ; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOpenAiResponsesToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = ExtractResponsesText(response);
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                var reason = StopReason(guard, calls, iteration);
                if (reason is not null)
                {
                    NativeToolSession.AppendOpenAiResponsesTurn(input, response, BuildStopResults(calls, reason));
                    StripTools(payload);
                    var final = await send(payload);
                    return NativeToolLoopResult.Completed(
                        final is null ? "" : ExtractResponsesText(final), iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, ExtractResponsesText(response), results);
                NativeToolSession.AppendOpenAiResponsesTurn(input, response, results);
            }
        }

        /// <summary>
        /// Responses API 的正文在 output[] 里 type=="message" 的项下，
        /// 一条 message 可能被拆成多个 output_text 片段，要全部拼起来。
        /// </summary>
        public static string ExtractResponsesText(JObject response)
        {
            var output = response["output"] as JArray;
            if (output is null) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var item in output)
            {
                if (item["type"]?.ToString() != "message") continue;
                if (item["content"] is not JArray content) continue;

                foreach (var part in content)
                {
                    if (part["type"]?.ToString() != "output_text") continue;
                    var text = part["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text)) sb.Append(text);
                }
            }
            return sb.ToString();
        }

        /// <summary>Ollama 原生 /api/chat 格式。</summary>
        public static async Task<NativeToolLoopResult> RunOllamaAsync(
            JObject payload,
            NativeToolSession session,
            Func<JObject, Task<JObject?>> send)
        {
            var messages = payload["messages"] as JArray;
            if (messages is null)
            {
                Logger.Log("NativeToolLoop: payload 里没有 messages 数组，放弃工具循环");
                return NativeToolLoopResult.Failed();
            }

            var transcript = new List<NativeToolTranscript.Entry>();
            var guard = new RepeatGuard();

            for (int iteration = 0; ; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOllamaToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                var reason = StopReason(guard, calls, iteration);
                if (reason is not null)
                {
                    NativeToolSession.AppendOllamaTurn(messages, response, BuildStopResults(calls, reason));
                    StripTools(payload);
                    var final = await send(payload);
                    var finalText = final?["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(finalText, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, response["message"]?["content"]?.ToString() ?? "", results);
                NativeToolSession.AppendOllamaTurn(messages, response, results);
            }
        }

        /// <summary>Gemini generateContent 格式。</summary>
        public static async Task<NativeToolLoopResult> RunGeminiAsync(
            JObject payload,
            NativeToolSession session,
            Func<JObject, Task<JObject?>> send)
        {
            var contents = payload["contents"] as JArray;
            if (contents is null)
            {
                Logger.Log("NativeToolLoop: payload 里没有 contents 数组，放弃工具循环");
                return NativeToolLoopResult.Failed();
            }

            var transcript = new List<NativeToolTranscript.Entry>();
            var guard = new RepeatGuard();

            for (int iteration = 0; ; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseGeminiToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = ExtractGeminiText(response);
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                var reason = StopReason(guard, calls, iteration);
                if (reason is not null)
                {
                    NativeToolSession.AppendGeminiTurn(contents, response, BuildStopResults(calls, reason));
                    StripTools(payload);
                    var final = await send(payload);
                    return NativeToolLoopResult.Completed(
                        final is null ? "" : ExtractGeminiText(final), iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, ExtractGeminiText(response), results);
                NativeToolSession.AppendGeminiTurn(contents, response, results);
            }
        }

        /// <summary>Gemini 的文本分散在 parts 里，functionCall 之外的 text 要拼起来。</summary>
        public static string ExtractGeminiText(JObject response)
        {
            var parts = response["candidates"]?[0]?["content"]?["parts"] as JArray;
            if (parts is null) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                var text = part["text"]?.ToString();
                if (!string.IsNullOrEmpty(text)) sb.Append(text);
            }
            return sb.ToString();
        }
    }

    /// <summary>工具循环的结果。</summary>
    public sealed class NativeToolLoopResult
    {
        /// <summary>循环是否正常走完（拿到了最终回复，或确定没有工具调用）。</summary>
        public bool Success { get; init; }

        /// <summary>模型最终的自然语言回复。</summary>
        public string Message { get; init; } = "";

        /// <summary>一共执行了几轮工具调用。0 表示模型压根没调工具。</summary>
        public int Iterations { get; init; }

        /// <summary>
        /// 本轮对话还原成标记协议后的消息序列，供落库。
        /// 没调工具时只有一条 assistant（就是 <see cref="Message"/>）。
        /// 见 <see cref="NativeToolTranscript"/> 说明为什么要还原成标记而不是原样存工具格式。
        /// </summary>
        public IReadOnlyList<NativeToolTranscript.Entry> Transcript { get; init; }
            = System.Array.Empty<NativeToolTranscript.Entry>();

        public static NativeToolLoopResult Completed(
            string message, int iterations, IReadOnlyList<NativeToolTranscript.Entry>? transcript = null)
            => new()
            {
                Success = true,
                Message = message,
                Iterations = iterations,
                Transcript = transcript ?? System.Array.Empty<NativeToolTranscript.Entry>()
            };

        public static NativeToolLoopResult Failed()
            => new() { Success = false };

        // 原来还有个 Exhausted()：撞上限时返回空 Message。已经删掉 ——
        // 现在无论因何停下，都会再向模型要一次纯文字回复，不存在"没有回复"的出口。
        // 那个空回复正是用户看到的"生硬切断"。
    }
}
