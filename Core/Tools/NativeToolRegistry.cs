using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;
using VPetLLM.Core.Abstractions.Models;
using VPetLLM.Utils.System;
using HostPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IVPetLLMPlugin;
using HostActionPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin.IActionPlugin;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// 把已启用的插件翻译成原生 function calling 的工具声明。
    ///
    /// 数据来源优先用插件的 <see cref="IToolSchemaPlugin"/>（结构化、有类型和枚举），
    /// 没实现的退化成"一个 input 字符串参数"，让模型至少还能调用到。
    ///
    /// 一个插件可能有多种调用形态（Terminal 既收裸命令又收 action(setting)），
    /// 每种形态导出成一个独立的函数 —— function calling 没有重载，
    /// 硬塞进一个函数只会让参数表互相矛盾。
    /// </summary>
    public static class NativeToolRegistry
    {
        /// <summary>函数名允许的字符，各家共同的最小集。</summary>
        private static readonly Regex InvalidNameChars = new(@"[^A-Za-z0-9_-]", RegexOptions.Compiled);

        private const int MaxNameLength = 64;

        /// <summary>
        /// 上一次记录过的"被排除的插件"名单。工具表**每个请求都会重建一次**，
        /// 无条件打日志等于往 Debug.log 里按请求数灌重复行。只在名单变化时说话。
        /// </summary>
        private static string _lastLoggedExclusions = "";
        private static readonly object ExclusionLogGate = new();

        /// <summary>构建当前所有可用工具。</summary>
        public static IReadOnlyList<NativeToolDefinition> Build(IEnumerable<HostPlugin> plugins)
        {
            var result = new List<NativeToolDefinition>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excluded = new List<string>();

            foreach (var plugin in plugins)
            {
                if (plugin is not HostActionPlugin) continue;   // 不能被调用的插件不该出现在工具表里

                try
                {
                    var schema = (plugin as IToolSchemaPlugin)?.GetToolSchema();

                    // 依赖回复渲染会话的插件不进工具表：工具循环跑在会话建立之前，
                    // 它们必然失败，而模型会把失败当成"再试一次"的信号。
                    // 它们仍然可以走标记模式 —— 系统提示词里的插件列表不受这里影响。
                    if (schema?.RequiresReplySession == true)
                    {
                        excluded.Add(plugin.Name);
                        continue;
                    }

                    if (schema is not null && schema.Forms.Count > 0)
                    {
                        BuildFromSchema(plugin, schema, result, usedNames);
                    }
                    else
                    {
                        result.Add(BuildFallback(plugin, usedNames));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"NativeToolRegistry: 构建 {plugin.Name} 的工具声明失败，改用兜底形态: {ex.Message}");
                    try { result.Add(BuildFallback(plugin, usedNames)); } catch { }
                }
            }

            var notice = TakeExclusionNotice(excluded);
            if (notice is not null) Logger.Log(notice);

            return result;
        }

        /// <summary>
        /// 排除名单变化时返回该记的那行日志，没变则返回 null。
        ///
        /// 插件可能被启用/禁用/重载，名单不是一成不变的，所以比对的是名单本身
        /// 而不是"是否记过一次"。抽成独立方法是为了能直接断言去重行为 ——
        /// <see cref="Logger.Log"/> 在没有 WPF Application 时会直接返回，测试里观察不到。
        /// </summary>
        public static string? TakeExclusionNotice(IReadOnlyList<string> excluded)
        {
            var signature = string.Join(", ", excluded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

            lock (ExclusionLogGate)
            {
                if (signature == _lastLoggedExclusions) return null;
                _lastLoggedExclusions = signature;
            }

            // 名单从"有"变成"空"时只更新状态，不值得为"现在没有被排除的插件了"记一行
            return signature.Length > 0
                ? $"NativeToolRegistry: {signature} 依赖回复会话，不导出为原生工具（仍可用标记调用）"
                : null;
        }

        private static void BuildFromSchema(
            HostPlugin plugin, ToolSchema schema,
            List<NativeToolDefinition> result, HashSet<string> usedNames)
        {
            var single = schema.Forms.Count == 1;

            for (int i = 0; i < schema.Forms.Count; i++)
            {
                var form = schema.Forms[i];

                // 单形态直接用插件名；多形态加后缀区分，后缀优先取枚举参数的唯一值
                var suffix = single ? "" : "_" + (DeriveFormSuffix(form) ?? (i + 1).ToString());
                var name = MakeUniqueName(plugin.Name + suffix, usedNames);

                var description = string.Join(" ", new[]
                {
                    string.IsNullOrWhiteSpace(form.Summary) ? schema.Summary : form.Summary,
                    schema.Remarks
                }.Where(s => !string.IsNullOrWhiteSpace(s)));

                if (string.IsNullOrWhiteSpace(description)) description = plugin.Description;

                var isRawText = form.Style == ToolCallStyle.RawText;

                // RawText 形态下参数名可能自带动词前缀（"search|query"）；
                // 前缀要留着执行时拼回去，只有右半截才是模型该填的字段名
                var (rawPrefix, rawParam) = isRawText
                    ? SplitRawTextParameter(form.Parameters.FirstOrDefault()?.Name)
                    : ("", null);

                result.Add(new NativeToolDefinition
                {
                    Name = name,
                    PluginName = plugin.Name,
                    Description = Truncate(description, 1024),
                    Parameters = BuildParameterSchema(form),
                    ArgumentStyle = isRawText ? NativeToolArgumentStyle.RawText : NativeToolArgumentStyle.NamedArguments,
                    RawTextParameter = rawParam,
                    RawTextPrefix = rawPrefix
                });
            }
        }

        /// <summary>
        /// 多形态时给函数名找个可读后缀：优先用只有单一取值的枚举参数
        /// （比如 action:"setting" → Weather_setting），这样模型一眼能看出是哪一路。
        /// </summary>
        private static string? DeriveFormSuffix(ToolCallForm form)
        {
            var singleValueEnum = form.Parameters.FirstOrDefault(p =>
                p.Type == ToolParameterType.Enum && p.EnumValues is { Count: 1 });
            if (singleValueEnum?.EnumValues is { Count: 1 } values)
            {
                return SanitizeName(values[0]);
            }

            if (form.Style == ToolCallStyle.RawText)
            {
                var raw = form.Parameters.FirstOrDefault()?.Name;
                if (!string.IsNullOrWhiteSpace(raw)) return SanitizeName(raw);
            }

            return null;
        }

        private static JObject BuildParameterSchema(ToolCallForm form)
        {
            var properties = new JObject();
            var required = new JArray();

            var isRawText = form.Style == ToolCallStyle.RawText;

            foreach (var p in form.Parameters)
            {
                // RawText 的参数名可能带动词前缀，暴露给模型的字段名只要右半截；
                // 前缀由 RawTextPrefix 在执行时补回，不该让模型去填
                var key = isRawText ? SplitRawTextParameter(p.Name).Parameter : NormalizeParamName(p.Name);
                if (string.IsNullOrEmpty(key)) continue;

                var node = new JObject { ["type"] = MapType(p.Type) };

                if (!string.IsNullOrWhiteSpace(p.Description))
                {
                    node["description"] = Truncate(p.Description, 512);
                }
                if (p.Type == ToolParameterType.Enum && p.EnumValues is { Count: > 0 })
                {
                    node["enum"] = new JArray(p.EnumValues.Cast<object>());
                }

                properties[key] = node;
                if (p.Required) required.Add(key);
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0) schema["required"] = required;

            return schema;
        }

        private static string MapType(ToolParameterType type) => type switch
        {
            ToolParameterType.Integer => "integer",
            ToolParameterType.Number => "number",
            ToolParameterType.Boolean => "boolean",
            _ => "string"   // Enum 在 JSON Schema 里也是 string + enum
        };

        /// <summary>
        /// 没有结构化 schema 的插件：暴露成单个 input 字符串，
        /// 描述里把插件原来的 Examples 带上，模型照着写标记内容即可。
        /// </summary>
        private static NativeToolDefinition BuildFallback(HostPlugin plugin, HashSet<string> usedNames)
        {
            var description = $"{plugin.Description} {plugin.Examples}".Trim();

            return new NativeToolDefinition
            {
                Name = MakeUniqueName(plugin.Name, usedNames),
                PluginName = plugin.Name,
                Description = Truncate(description, 1024),
                ArgumentStyle = NativeToolArgumentStyle.RawText,
                RawTextParameter = "input",
                Parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["input"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "传给插件的原始参数文本"
                        }
                    },
                    ["required"] = new JArray("input")
                }
            };
        }

        /// <summary>参数名也要满足函数名字符集（Gemini 对参数名同样挑剔）。</summary>
        public static string NormalizeParamName(string? name)
            => string.IsNullOrWhiteSpace(name) ? "" : SanitizeName(name);

        /// <summary>
        /// 把 RawText 参数名拆成「固定前缀」和「模型要填的字段名」。
        ///
        /// <c>"search|query"</c> → <c>("search|", "query")</c>；不含分隔符时前缀为空。
        /// 归一化只作用于右半截 —— 前缀是要原样交给插件的协议文本，
        /// 一旦被 SanitizeName 把竖线换成下划线就再也拼不回去了。
        /// </summary>
        public static (string Prefix, string Parameter) SplitRawTextParameter(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ("", "");

            var trimmed = name.Trim();
            var cut = trimmed.LastIndexOf('|');
            if (cut < 0) return ("", NormalizeParamName(trimmed));

            return (trimmed.Substring(0, cut + 1), NormalizeParamName(trimmed.Substring(cut + 1)));
        }

        private static string SanitizeName(string raw)
        {
            var cleaned = InvalidNameChars.Replace(raw.Trim(), "_").Trim('_');
            if (cleaned.Length > MaxNameLength) cleaned = cleaned.Substring(0, MaxNameLength);
            return cleaned;
        }

        private static string MakeUniqueName(string raw, HashSet<string> usedNames)
        {
            var baseName = SanitizeName(raw);
            if (string.IsNullOrEmpty(baseName)) baseName = "plugin";

            var name = baseName;
            var suffix = 2;
            while (!usedNames.Add(name))
            {
                var trimmed = baseName.Length > MaxNameLength - 3
                    ? baseName.Substring(0, MaxNameLength - 3)
                    : baseName;
                name = $"{trimmed}_{suffix++}";
            }
            return name;
        }

        private static string Truncate(string text, int max)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
