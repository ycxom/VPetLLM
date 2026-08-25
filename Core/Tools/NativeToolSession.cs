using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;
using VPetLLM.Utils.System;
using HostPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IVPetLLMPlugin;
using HostActionPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IActionPlugin;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// 驱动一轮对话里的原生工具调用循环，并负责各厂商线格式的读写。
    ///
    /// 循环只存在于**单次请求内部**：中途产生的 assistant(tool_calls) 和 tool 结果消息
    /// 只挂在本次请求的临时消息表上，不写进持久化历史。这样既不用动数据库结构，
    /// 也不会让旧历史（标记协议）和新历史（tool 消息）混在一起。
    /// 模型最终的自然语言回复仍然走原来的处理链路。
    /// </summary>
    public sealed class NativeToolSession
    {
        /// <summary>设置里没值或越界时用的默认值。</summary>
        public const int DefaultRepeatLimit = 5;
        public const int DefaultMaxIterations = 10;

        /// <summary>
        /// 同一条调用（工具名 + 参数完全一致）连续重复几次就判定为打转。
        ///
        /// 判据只看"完全相同"，刻意不做相似度之类的模糊匹配 —— 越复杂的判据
        /// 越容易把正常行为误判成异常，而误判的代价是把用户的正当请求掐断。
        /// </summary>
        public static int RepeatLimit => CurrentLimits().RepeatLimit;

        /// <summary>
        /// 一轮对话里最多允许调用几次工具。这是**兜底**，不是正常的停止条件 ——
        /// 正常停止靠 <see cref="RepeatLimit"/>。
        ///
        /// 强制保证 <c>MaxIterations &gt; RepeatLimit</c>：两者相等或更小的话，
        /// 预算会先耗尽，打转检测永远轮不到，症状和"按轮数硬掐"一模一样。
        /// 设置界面允许用户随便填，这个不变量在读取时兜住。
        /// </summary>
        public static int MaxIterations => CurrentLimits().MaxIterations;

        private static (int RepeatLimit, int MaxIterations) CurrentLimits()
        {
            var settings = VPetLLM.Instance?.Settings;
            return ResolveLimits(settings?.ToolCallRepeatLimit, settings?.ToolCallMaxIterations);
        }

        /// <summary>
        /// 把设置里的两个数收敛成一对可用的值。抽成纯函数是为了能直接断言 ——
        /// 这里的规则（尤其是"预算必须大于重复上限"）一旦悄悄失效，
        /// 症状就是打转检测永远不触发，而外部表现和"按轮数硬掐"一模一样，很难看出来。
        /// </summary>
        public static (int RepeatLimit, int MaxIterations) ResolveLimits(int? repeatSetting, int? budgetSetting)
        {
            var repeat = Clamp(repeatSetting, DefaultRepeatLimit, 2, 20);
            var budget = Clamp(budgetSetting, DefaultMaxIterations, 1, 50);

            // 预算不大于重复上限的话，预算会先耗尽，打转检测永远轮不到
            if (budget <= repeat) budget = repeat + 1;

            return (repeat, budget);
        }

        private static int Clamp(int? value, int fallback, int min, int max)
        {
            if (value is not int v || v <= 0) return fallback;
            return v < min ? min : (v > max ? max : v);
        }

        private readonly IReadOnlyList<NativeToolDefinition> _definitions;
        private readonly List<HostPlugin> _plugins;

        private NativeToolSession(IReadOnlyList<NativeToolDefinition> definitions, List<HostPlugin> plugins)
        {
            _definitions = definitions;
            _plugins = plugins;
        }

        public bool HasTools => _definitions.Count > 0;

        public IReadOnlyList<NativeToolDefinition> Definitions => _definitions;

        /// <summary>
        /// 按当前设置创建会话；未启用或没有可用工具时返回 null，调用方据此走原来的标记协议。
        /// </summary>
        public static NativeToolSession? TryCreate(Setting? settings, bool nodeEnabled)
        {
            if (settings is null) return null;
            if (!settings.EnableNativeToolCall) return null;
            if (!nodeEnabled) return null;
            if (!settings.EnablePlugin) return null;

            var loaded = VPetLLM.Instance?.Plugins;
            var plugins = loaded is null
                ? new List<HostPlugin>()
                : loaded.Where(p => p.Enabled).ToList();
            if (plugins.Count == 0) return null;

            var definitions = NativeToolRegistry.Build(plugins);
            if (definitions.Count == 0) return null;

            return new NativeToolSession(definitions, plugins);
        }

        /// <summary>
        /// 当前配置下"有可能"走原生工具调用：全局开关开着，且当前 provider 至少有一个
        /// 启用中的节点打开了 EnableToolCall。
        ///
        /// 刻意不去解析"当前节点"—— <c>GetCurrent*Setting()</c> 在开启负载均衡时会推进
        /// CurrentNodeIndex，从提示词构建里调用它会平白轮换节点。这里只做只读判断，
        /// 供提示词决定要不要additionally 提示模型优先用工具调用。
        /// </summary>
        public static bool IsLikelyActive(Setting? settings)
        {
            if (settings is null || !settings.EnableNativeToolCall || !settings.EnablePlugin) return false;

            try
            {
                return settings.Provider switch
                {
                    Setting.LLMType.OpenAI => settings.OpenAI.OpenAINodes.Any(n => n.Enabled && n.EnableToolCall),
                    Setting.LLMType.Gemini => settings.Gemini.GeminiNodes.Any(n => n.Enabled && n.EnableToolCall),
                    Setting.LLMType.Ollama => settings.Ollama.OllamaNodes.Any(n => n.Enabled && n.EnableToolCall),
                    Setting.LLMType.LMStudio => settings.LMStudio.LMStudioNodes.Any(n => n.Enabled && n.EnableToolCall),
                    // Free 没有可勾选的节点开关：模型由云端下发，能力由云端策略 + 本地探测决定。
                    // 注意这里用 IsProven 而不是 ShouldAttachTools：探测期照挂 tools，
                    // 但**不**写"优先用工具调用"那句提示 —— 详见 FreeToolCapability.IsProven。
                    Setting.LLMType.Free => FreeToolCapability.IsProven(),
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 这一轮请求到底会不会带上 tools。
        ///
        /// 提示词里那句"本节点已开启原生工具调用"必须和实际挂没挂 tools 完全一致，
        /// 所以刻意直接复用 <see cref="TryCreate"/> 而不是另写一份条件判断 ——
        /// 两份条件早晚会分叉，而分叉的后果正是"提示模型用工具，请求里却没有工具"。
        /// 多跑一次 Build 的开销相对一次网络请求可以忽略。
        /// </summary>
        public static bool WillAttachTools(Setting? settings, bool nodeEnabled)
            => TryCreate(settings, nodeEnabled) is not null;

        #region 请求侧：把工具声明挂到 payload 上

        /// <summary>OpenAI / Ollama / LMStudio 的 chat completions 格式。</summary>
        public void AttachOpenAiTools(JObject payload)
        {
            if (!HasTools) return;
            payload["tools"] = new JArray(_definitions.Select(d => (JToken)d.ToOpenAiFormat()));
            payload["tool_choice"] = "auto";
        }

        /// <summary>OpenAI Responses API 格式（工具声明是扁平的，见 ToOpenAiResponsesFormat）。</summary>
        public void AttachOpenAiResponsesTools(JObject payload)
        {
            if (!HasTools) return;
            payload["tools"] = new JArray(_definitions.Select(d => (JToken)d.ToOpenAiResponsesFormat()));
            payload["tool_choice"] = "auto";
        }

        /// <summary>Gemini 的 generateContent 格式。</summary>
        public void AttachGeminiTools(JObject payload)
        {
            if (!HasTools) return;
            payload["tools"] = new JArray(new JObject
            {
                ["functionDeclarations"] = new JArray(_definitions.Select(d => (JToken)d.ToGeminiFormat()))
            });
        }

        #endregion

        #region 响应侧：把工具调用读出来

        /// <summary>从 OpenAI 风格的非流式响应里读取工具调用。</summary>
        public static List<NativeToolCall> ParseOpenAiToolCalls(JObject response)
        {
            var calls = new List<NativeToolCall>();
            var toolCalls = response["choices"]?[0]?["message"]?["tool_calls"] as JArray;
            if (toolCalls is null) return calls;

            foreach (var item in toolCalls)
            {
                var function = item["function"];
                if (function is null) continue;

                calls.Add(new NativeToolCall
                {
                    Id = item["id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                    Name = function["name"]?.ToString() ?? "",
                    Arguments = ParseArguments(function["arguments"])
                });
            }
            return calls;
        }

        /// <summary>
        /// Ollama 原生 /api/chat 的格式：结果在 message.tool_calls 下，没有 choices 也没有 id，
        /// 而且 arguments 直接就是对象而不是 JSON 字符串。
        /// </summary>
        public static List<NativeToolCall> ParseOllamaToolCalls(JObject response)
        {
            var calls = new List<NativeToolCall>();
            var toolCalls = response["message"]?["tool_calls"] as JArray;
            if (toolCalls is null) return calls;

            foreach (var item in toolCalls)
            {
                var function = item["function"];
                if (function is null) continue;

                var name = function["name"]?.ToString() ?? "";
                calls.Add(new NativeToolCall
                {
                    Id = item["id"]?.ToString() ?? name,
                    Name = name,
                    Arguments = ParseArguments(function["arguments"])
                });
            }
            return calls;
        }

        /// <summary>Ollama 风格：把 assistant 那条原样放回，再逐个补 role=tool。</summary>
        public static void AppendOllamaTurn(JArray messages, JObject response, IEnumerable<NativeToolResult> results)
        {
            var assistantMessage = response["message"] as JObject;
            if (assistantMessage is not null)
            {
                messages.Add(assistantMessage.DeepClone());
            }

            foreach (var result in results)
            {
                messages.Add(new JObject
                {
                    ["role"] = "tool",
                    ["content"] = result.Content
                });
            }
        }

        /// <summary>
        /// 从 Responses API 的响应里读取工具调用。
        ///
        /// 与 chat completions 的三处不同：调用项散落在 <c>output[]</c> 里（靠 type 区分，
        /// 和 message 项混在一起）；配对用的是 <c>call_id</c> 而不是 <c>id</c>
        /// （同一项上的 <c>id</c> 是这条 output 项自己的编号，拿它去配对会对不上）；
        /// arguments 是 JSON 字符串。
        /// </summary>
        public static List<NativeToolCall> ParseOpenAiResponsesToolCalls(JObject response)
        {
            var calls = new List<NativeToolCall>();
            var output = response["output"] as JArray;
            if (output is null) return calls;

            foreach (var item in output)
            {
                if (item["type"]?.ToString() != "function_call") continue;

                var name = item["name"]?.ToString() ?? "";
                var callId = item["call_id"]?.ToString();
                if (string.IsNullOrEmpty(callId)) callId = item["id"]?.ToString();

                calls.Add(new NativeToolCall
                {
                    Id = string.IsNullOrEmpty(callId) ? Guid.NewGuid().ToString("N") : callId,
                    Name = name,
                    Arguments = ParseArguments(item["arguments"])
                });
            }
            return calls;
        }

        /// <summary>
        /// Responses 风格的回填：把模型这轮产出的 output 项原样接回 input，
        /// 再为每个调用补一条 function_call_output。
        ///
        /// 不用 previous_response_id，所以每轮都要把完整 input 重发；
        /// 模型自己发出的 function_call 项必须原样回传，否则 call_id 配不上。
        /// </summary>
        public static void AppendOpenAiResponsesTurn(JArray input, JObject response, IEnumerable<NativeToolResult> results)
        {
            if (response["output"] is JArray output)
            {
                foreach (var item in output)
                {
                    // reasoning 项是各家自有的中间态，回传容易被判非法，只接回调用与消息
                    var type = item["type"]?.ToString();
                    if (type == "function_call" || type == "message")
                    {
                        input.Add(item.DeepClone());
                    }
                }
            }

            foreach (var result in results)
            {
                input.Add(new JObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = result.Call.Id,
                    ["output"] = result.Content
                });
            }
        }

        /// <summary>从 Gemini 响应里读取 functionCall。Gemini 不给调用 id。</summary>
        public static List<NativeToolCall> ParseGeminiToolCalls(JObject response)
        {
            var calls = new List<NativeToolCall>();
            var parts = response["candidates"]?[0]?["content"]?["parts"] as JArray;
            if (parts is null) return calls;

            foreach (var part in parts)
            {
                var functionCall = part["functionCall"];
                if (functionCall is null) continue;

                var name = functionCall["name"]?.ToString() ?? "";
                calls.Add(new NativeToolCall
                {
                    Id = name,
                    Name = name,
                    Arguments = functionCall["args"] as JObject ?? new JObject()
                });
            }
            return calls;
        }

        /// <summary>
        /// 参数可能是 JSON 字符串（OpenAI）也可能已经是对象。
        /// 模型偶尔会吐出不合法的 JSON，这里失败就返回空对象而不是抛。
        /// </summary>
        private static JObject ParseArguments(JToken? token)
        {
            if (token is null) return new JObject();
            if (token is JObject obj) return obj;

            var raw = token.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return new JObject();

            try
            {
                return JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                Logger.Log($"NativeToolSession: 工具入参不是合法 JSON，按空参数处理: {ex.Message} / {raw}");
                return new JObject();
            }
        }

        #endregion

        #region 执行

        /// <summary>顺序执行一批工具调用。串行是刻意的：插件会弹窗、会碰 UI，并发跑不安全。</summary>
        public async Task<List<NativeToolResult>> ExecuteAsync(IEnumerable<NativeToolCall> calls)
        {
            var results = new List<NativeToolResult>();
            foreach (var call in calls)
            {
                results.Add(await NativeToolInvoker.InvokeAsync(call, _definitions, _plugins));
            }
            return results;
        }

        #endregion

        #region 把这一轮的调用与结果追加回消息表

        /// <summary>OpenAI 风格：assistant(tool_calls) + 每个调用一条 role=tool。</summary>
        public static void AppendOpenAiTurn(JArray messages, JObject response, IEnumerable<NativeToolResult> results)
        {
            var assistantMessage = response["choices"]?[0]?["message"] as JObject;
            if (assistantMessage is not null)
            {
                // 原样回传模型自己发出的那条消息，tool_call_id 才对得上
                messages.Add(assistantMessage.DeepClone());
            }

            foreach (var result in results)
            {
                messages.Add(new JObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.Call.Id,
                    ["name"] = result.Call.Name,
                    ["content"] = result.Content
                });
            }
        }

        /// <summary>Gemini 风格：model 的 functionCall part + user 的 functionResponse part。</summary>
        public static void AppendGeminiTurn(JArray contents, JObject response, IEnumerable<NativeToolResult> results)
        {
            var modelContent = response["candidates"]?[0]?["content"] as JObject;
            if (modelContent is not null)
            {
                contents.Add(modelContent.DeepClone());
            }

            var parts = new JArray();
            foreach (var result in results)
            {
                parts.Add(new JObject
                {
                    ["functionResponse"] = new JObject
                    {
                        ["name"] = result.Call.Name,
                        // Gemini 要求 response 是对象，不能直接给字符串
                        ["response"] = new JObject { ["result"] = result.Content }
                    }
                });
            }

            contents.Add(new JObject
            {
                ["role"] = "user",
                ["parts"] = parts
            });
        }

        #endregion

        /// <summary>
        /// 给系统提示词用的一句话：启用原生工具后，告诉模型别再写标记了。
        /// 两套机制同时暴露给模型只会让它随机挑一个。
        /// </summary>
        public static string BuildPromptNotice(string language)
            => language switch
            {
                "zh-hans" => "本节点已开启原生工具调用（function calling）：插件同时以工具形式提供，" +
                             "优先直接发起工具调用；标记写法仍然有效，可作为退路。",
                "zh-hant" => "本節點已開啟原生工具調用（function calling）：插件同時以工具形式提供，" +
                             "優先直接發起工具調用；標記寫法仍然有效，可作為退路。",
                "ja" => "このノードではネイティブの function calling が有効です。プラグインはツールとしても提供されています。" +
                        "ツール呼び出しを優先してください。マーカー形式も引き続き有効で、フォールバックとして使えます。",
                _ => "Native function calling is enabled for this node: the plugins are also exposed as tools. " +
                     "Prefer calling them directly; the marker syntax still works as a fallback."
            };
    }
}
