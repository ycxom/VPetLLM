using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VPetLLM.Core.Abstractions.Models;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 把 <see cref="ToolSchema"/> 渲染成写进系统提示词的文本。
    ///
    /// 输出是 TypeScript 风格的函数签名 + 一条对应的标记调用示例，例如：
    /// <code>
    /// Terminal(command: string): string
    ///   // 执行终端命令，使用 PowerShell 语法
    ///   &lt;|plugin_Terminal_begin|&gt; Get-Process &lt;|plugin_Terminal_end|&gt;
    /// Terminal(action: "setting" | "info"): string
    ///   // 打开设置窗口 / 查看 shell 状态
    ///   &lt;|plugin_Terminal_begin|&gt; action(setting) &lt;|plugin_Terminal_end|&gt;
    /// </code>
    ///
    /// 为什么是 TypeScript 而不是 JSON Schema：签名一行就能表达"叫什么、要什么、可选与否、
    /// 取值范围"，比等价的 JSON Schema 省一个数量级的 token，而模型对它的解析准确率更高。
    /// 这个取法来自 codex 的 code-mode-protocol（render_json_schema_to_typescript）。
    /// </summary>
    public static class ToolSchemaRenderer
    {
        /// <summary>渲染单个插件。</summary>
        public static string Render(string pluginName, ToolSchema schema, string fallbackDescription)
        {
            var sb = new StringBuilder();

            var summary = string.IsNullOrWhiteSpace(schema.Summary) ? fallbackDescription : schema.Summary;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                sb.Append("// ").AppendLine(Flatten(summary));
            }

            if (schema.Forms.Count == 0)
            {
                // 没声明任何形态：至少给出一条无参签名，别让模型以为这个插件不能调
                sb.AppendLine($"{pluginName}(): string");
                sb.AppendLine($"  <|plugin_{pluginName}_begin|> <|plugin_{pluginName}_end|>");
            }

            foreach (var form in schema.Forms)
            {
                sb.AppendLine($"{pluginName}({RenderParameterList(form)}): string");

                if (!string.IsNullOrWhiteSpace(form.Summary))
                {
                    sb.Append("  // ").AppendLine(Flatten(form.Summary));
                }

                foreach (var line in RenderParameterNotes(form))
                {
                    sb.Append("  // ").AppendLine(line);
                }

                // 编不出真实取值时宁可不给示例：带 <占位符> 的示例会被模型原样抄进调用里
                var example = form.Example ?? BuildExample(form);
                if (example is not null)
                {
                    var body = example.Length == 0 ? "" : example + " ";
                    sb.AppendLine($"  <|plugin_{pluginName}_begin|> {body}<|plugin_{pluginName}_end|>");
                }
            }

            if (!string.IsNullOrWhiteSpace(schema.Remarks))
            {
                sb.Append("// ").AppendLine(Flatten(schema.Remarks!));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 渲染一整批插件。实现了 <c>IToolSchemaPlugin</c> 的走结构化签名，
        /// 其余的保持原来的 <c>Name: Description Examples</c> 单行格式。
        /// </summary>
        public static string RenderAll(IEnumerable<Abstractions.Interfaces.Plugin.IVPetLLMPlugin> plugins)
        {
            var blocks = new List<string>();

            foreach (var plugin in plugins)
            {
                string? block = null;

                if (plugin is Abstractions.Interfaces.Plugin.IToolSchemaPlugin schemaPlugin)
                {
                    try
                    {
                        var schema = schemaPlugin.GetToolSchema();
                        if (schema is not null)
                        {
                            block = Render(plugin.Name, schema, plugin.Description);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 插件的 schema 出问题不该拖垮整个提示词，退回老格式即可
                        Utils.System.Logger.Log($"ToolSchemaRenderer: {plugin.Name} GetToolSchema failed: {ex.Message}");
                    }
                }

                block ??= $"{plugin.Name}: {plugin.Description} {plugin.Examples}".Trim();
                blocks.Add(block);
            }

            return string.Join("\n", blocks);
        }

        private static string RenderParameterList(ToolCallForm form)
        {
            if (form.Parameters.Count == 0) return "";

            // 必填排在可选前面：TypeScript 里 `a: T, b?: T, c: T` 是非法签名，
            // 而这里的参数是具名的，顺序本来就不影响调用，排一下能让签名合法可读。
            var ordered = form.Parameters
                .OrderByDescending(p => p.Required)
                .ToList();

            return string.Join(", ", ordered.Select(p =>
            {
                var optional = p.Required ? "" : "?";
                return $"{p.Name}{optional}: {RenderType(p)}";
            }));
        }

        private static string RenderType(ToolParameter p) => p.Type switch
        {
            ToolParameterType.Integer => "int",
            ToolParameterType.Number => "number",
            ToolParameterType.Boolean => "boolean",
            ToolParameterType.Enum => p.EnumValues is { Count: > 0 }
                ? string.Join(" | ", p.EnumValues.Select(v => $"\"{v}\""))
                : "string",
            _ => "string"
        };

        /// <summary>只有带说明或默认值的参数才值得占一行注释，其余靠签名本身自解释。</summary>
        private static IEnumerable<string> RenderParameterNotes(ToolCallForm form)
        {
            foreach (var p in form.Parameters)
            {
                var hasDescription = !string.IsNullOrWhiteSpace(p.Description);
                var hasDefault = !string.IsNullOrWhiteSpace(p.Default);
                if (!hasDescription && !hasDefault) continue;

                var note = new StringBuilder();
                note.Append(p.Name).Append(": ");
                if (hasDescription) note.Append(Flatten(p.Description));
                if (hasDefault)
                {
                    // 用中性写法，否则英文/日文的说明后面会缀上一句中文
                    if (hasDescription) note.Append(' ');
                    note.Append("[default: ").Append(p.Default).Append(']');
                }
                yield return note.ToString();
            }
        }

        /// <summary>
        /// 按参数自动编一个示例调用，让模型看到实际的书写语法。
        /// 任何一个要展示的参数编不出真实取值就整体放弃（返回 null），由调用方跳过示例行。
        /// </summary>
        private static string? BuildExample(ToolCallForm form)
        {
            if (form.Parameters.Count == 0) return "";

            if (form.Style == ToolCallStyle.RawText)
            {
                return SampleValue(form.Parameters[0]);
            }

            // 只示范必填参数，示例太长反而干扰；全可选时退而示范第一个
            var shown = form.Parameters.Where(p => p.Required).ToList();
            if (shown.Count == 0) shown.Add(form.Parameters[0]);

            var parts = new List<string>(shown.Count);
            foreach (var p in shown)
            {
                var value = SampleValue(p);
                if (value is null) return null;
                parts.Add($"{p.Name}({value})");
            }

            return string.Join(", ", parts);
        }

        /// <summary>返回一个可以照抄的真实取值；编不出来返回 null。</summary>
        private static string? SampleValue(ToolParameter p)
        {
            if (!string.IsNullOrWhiteSpace(p.Sample)) return p.Sample;
            if (!string.IsNullOrWhiteSpace(p.Default)) return p.Default;
            if (p.Type == ToolParameterType.Enum && p.EnumValues is { Count: > 0 }) return p.EnumValues[0];

            return p.Type switch
            {
                ToolParameterType.Integer => "1",
                ToolParameterType.Number => "1.0",
                ToolParameterType.Boolean => "true",
                // 字符串没有样例就不要编 <name> 占位符，模型会照抄
                _ => null
            };
        }

        /// <summary>注释必须是单行，把换行压掉，避免撑乱提示词结构。</summary>
        private static string Flatten(string text)
            => text.Replace("\r", "").Replace("\n", " ").Trim();
    }
}
