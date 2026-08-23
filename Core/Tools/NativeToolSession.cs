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
        /// <summary>一轮对话里最多允许连续调用几次工具，防止模型打转。</summary>
        public const int MaxIterations = 5;

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

        #region 请求侧：把工具声明挂到 payload 上

        /// <summary>OpenAI / Ollama / LMStudio 的 chat completions 格式。</summary>
        public void AttachOpenAiTools(JObject payload)
        {
            if (!HasTools) return;
            payload["tools"] = new JArray(_definitions.Select(d => (JToken)d.ToOpenAiFormat()));
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
