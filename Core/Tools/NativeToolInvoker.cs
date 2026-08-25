using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;
using VPetLLM.Utils.System;
using HostPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IVPetLLMPlugin;
using HostActionPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IActionPlugin;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// 执行模型发起的原生工具调用。
    ///
    /// 插件的入口始终是 <c>Function(string arguments)</c>，吃的是标记协议里那套
    /// <c>name(value), other(value)</c> 文本；而 function calling 给的是 JSON。
    /// 所以这里要做一次回译。<see cref="NativeToolDefinition.ArgumentStyle"/> 决定怎么译
    /// —— 这正是之前给插件加结构化 schema 时顺带拿到的信息。
    /// </summary>
    public static class NativeToolInvoker
    {
        /// <summary>单次工具调用的兜底超时，防止某个插件把整轮对话挂死。</summary>
        public static TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(2);

        public static async Task<NativeToolResult> InvokeAsync(
            NativeToolCall call,
            IReadOnlyList<NativeToolDefinition> definitions,
            IEnumerable<HostPlugin> plugins)
        {
            var definition = definitions.FirstOrDefault(d =>
                string.Equals(d.Name, call.Name, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                var available = string.Join(", ", definitions.Select(d => d.Name));
                return Fail(call, $"Unknown tool '{call.Name}'. Available tools: {available}");
            }

            var plugin = plugins.FirstOrDefault(p =>
                string.Equals(p.Name, definition.PluginName, StringComparison.OrdinalIgnoreCase));

            if (plugin is null)
            {
                return Fail(call, $"Plugin '{definition.PluginName}' is not loaded.");
            }
            if (!plugin.Enabled)
            {
                return Fail(call, $"Plugin '{definition.PluginName}' is disabled.");
            }
            if (plugin is not HostActionPlugin actionPlugin)
            {
                return Fail(call, $"Plugin '{definition.PluginName}' cannot be invoked.");
            }

            var arguments = BuildPluginArguments(definition, call.Arguments);
            Logger.Log($"NativeToolInvoker: {call.Name} -> {definition.PluginName}(\"{arguments}\")");

            try
            {
                RemoteChat.RemoteChatSessionContext.PluginStarted(definition.PluginName, arguments);

                var task = actionPlugin.Function(arguments);
                var finished = await Task.WhenAny(task, Task.Delay(CallTimeout));
                if (finished != task)
                {
                    // 不取消任务（插件接口没有取消通道），只是不再等它
                    RemoteChat.RemoteChatSessionContext.PluginCompleted(definition.PluginName, "timeout", false);
                    return Fail(call, $"Plugin '{definition.PluginName}' did not finish within {CallTimeout.TotalSeconds:F0}s.",
                        definition.PluginName, arguments);
                }

                var result = await task ?? "";
                RemoteChat.RemoteChatSessionContext.PluginCompleted(definition.PluginName, result, true);

                if (string.IsNullOrWhiteSpace(result))
                {
                    // 空结果对 function calling 是非法的（必须回一条 tool 消息），给个明确说明
                    result = "(no output)";
                }

                return new NativeToolResult
                {
                    Call = call,
                    Content = result,
                    PluginName = definition.PluginName,
                    MarkerArguments = arguments,
                    Succeeded = true
                };
            }
            catch (Exception ex)
            {
                RemoteChat.RemoteChatSessionContext.PluginCompleted(definition.PluginName, ex.Message, false);
                Logger.Log($"NativeToolInvoker: {call.Name} 抛出异常: {ex.Message}");
                return Fail(call, $"Plugin error: {ex.Message}", definition.PluginName, arguments);
            }
        }

        /// <summary>把 JSON 入参还原成插件认识的文本参数。</summary>
        public static string BuildPluginArguments(NativeToolDefinition definition, JObject arguments)
        {
            if (definition.ArgumentStyle == NativeToolArgumentStyle.RawText)
            {
                var key = definition.RawTextParameter;

                // 先按声明的字段取；取不到就退而用对象里的第一个值，
                // 模型偶尔会把字段名写成别的东西，没必要为此整个失败
                var token = (!string.IsNullOrEmpty(key) ? arguments[key] : null)
                            ?? arguments.Properties().FirstOrDefault()?.Value;

                var text = TokenToText(token);

                // 把参数名里编码的动词前缀拼回去（"search|" + 关键词）。
                // 模型偶尔会自己把前缀也写进值里，那就别拼第二遍。
                var prefix = definition.RawTextPrefix;
                if (!string.IsNullOrEmpty(prefix) && !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = prefix + text;
                }

                return text;
            }

            var parts = new List<string>();
            foreach (var property in arguments.Properties())
            {
                var value = TokenToText(property.Value);
                if (string.IsNullOrEmpty(value)) continue;
                parts.Add($"{property.Name}({value})");
            }

            return string.Join(", ", parts);
        }

        private static string TokenToText(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null) return "";

            return token.Type switch
            {
                JTokenType.String => token.Value<string>() ?? "",
                JTokenType.Boolean => token.Value<bool>() ? "true" : "false",
                // 对象/数组原样序列化，插件自己解析（OneBot 就吃 JSON）
                JTokenType.Object or JTokenType.Array => token.ToString(Newtonsoft.Json.Formatting.None),
                _ => token.ToString()
            };
        }

        private static NativeToolResult Fail(
            NativeToolCall call, string message, string pluginName = "", string markerArguments = "")
        {
            Logger.Log($"NativeToolInvoker: {message}");
            return new NativeToolResult
            {
                Call = call,
                Content = "[error] " + message,
                PluginName = pluginName,
                MarkerArguments = markerArguments,
                Succeeded = false
            };
        }
    }
}
