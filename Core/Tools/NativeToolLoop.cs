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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOpenAiToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "", results);
                NativeToolSession.AppendOpenAiTurn(messages, response, results);
            }

            // 到上限还在要工具：把最后一次的文本（可能为空）交回去，别无限转
            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted(transcript);
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOpenAiResponsesToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = ExtractResponsesText(response);
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, ExtractResponsesText(response), results);
                NativeToolSession.AppendOpenAiResponsesTurn(input, response, results);
            }

            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted(transcript);
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOllamaToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, response["message"]?["content"]?.ToString() ?? "", results);
                NativeToolSession.AppendOllamaTurn(messages, response, results);
            }

            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted(transcript);
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseGeminiToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = ExtractGeminiText(response);
                    return NativeToolLoopResult.Completed(text, iteration, transcript);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolTranscript.AppendRound(transcript, ExtractGeminiText(response), results);
                NativeToolSession.AppendGeminiTurn(contents, response, results);
            }

            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted(transcript);
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

        /// <summary>是否因为撞上轮次上限而中止。</summary>
        public bool HitLimit { get; init; }

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

        public static NativeToolLoopResult Exhausted(IReadOnlyList<NativeToolTranscript.Entry>? transcript = null)
            => new()
            {
                Success = true,
                Message = "",
                HitLimit = true,
                Iterations = NativeToolSession.MaxIterations,
                Transcript = transcript ?? System.Array.Empty<NativeToolTranscript.Entry>()
            };
    }
}
