using System;
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOpenAiToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolSession.AppendOpenAiTurn(messages, response, results);
            }

            // 到上限还在要工具：把最后一次的文本（可能为空）交回去，别无限转
            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted();
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseOllamaToolCalls(response);
                if (calls.Count == 0)
                {
                    var text = response["message"]?["content"]?.ToString() ?? "";
                    return NativeToolLoopResult.Completed(text, iteration);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolSession.AppendOllamaTurn(messages, response, results);
            }

            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted();
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

            for (int iteration = 0; iteration < NativeToolSession.MaxIterations; iteration++)
            {
                var response = await send(payload);
                if (response is null) return NativeToolLoopResult.Failed();

                var calls = NativeToolSession.ParseGeminiToolCalls(response);
                if (calls.Count == 0)
                {
                    return NativeToolLoopResult.Completed(ExtractGeminiText(response), iteration);
                }

                Logger.Log($"NativeToolLoop: 第 {iteration + 1} 轮，模型请求 {calls.Count} 个工具调用");
                var results = await session.ExecuteAsync(calls);
                NativeToolSession.AppendGeminiTurn(contents, response, results);
            }

            Logger.Log($"NativeToolLoop: 达到 {NativeToolSession.MaxIterations} 轮上限，停止工具循环");
            return NativeToolLoopResult.Exhausted();
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

        public static NativeToolLoopResult Completed(string message, int iterations)
            => new() { Success = true, Message = message, Iterations = iterations };

        public static NativeToolLoopResult Failed()
            => new() { Success = false };

        public static NativeToolLoopResult Exhausted()
            => new() { Success = true, Message = "", HitLimit = true, Iterations = NativeToolSession.MaxIterations };
    }
}
