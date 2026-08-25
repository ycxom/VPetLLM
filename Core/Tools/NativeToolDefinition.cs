using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// 一个对模型暴露的原生工具（function calling）声明，与具体厂商无关。
    /// 各 provider 再把它翻译成自己的格式（OpenAI 的 tools[]、Gemini 的 functionDeclarations[]）。
    /// </summary>
    public sealed class NativeToolDefinition
    {
        /// <summary>
        /// 传给模型的函数名。必须满足各家共同的约束：只允许 [A-Za-z0-9_-]，长度 ≤ 64。
        /// </summary>
        public string Name { get; init; } = "";

        /// <summary>对应的插件名（原始大小写），回调时用它找回插件。</summary>
        public string PluginName { get; init; } = "";

        public string Description { get; init; } = "";

        /// <summary>JSON Schema（object 类型），描述入参。</summary>
        public JObject Parameters { get; init; } = new();

        /// <summary>
        /// 这个工具对应插件的哪一种调用形态。执行时要据此把 JSON 参数还原成
        /// 插件 <c>Function(string)</c> 认识的文本参数。
        /// </summary>
        public NativeToolArgumentStyle ArgumentStyle { get; init; } = NativeToolArgumentStyle.NamedArguments;

        /// <summary>RawText 形态下，整段参数取自哪个字段。</summary>
        public string? RawTextParameter { get; init; }

        /// <summary>
        /// RawText 形态下要拼回参数前面的固定前缀（含分隔符），没有则为空。
        ///
        /// 有些插件把动词编在参数名里，比如 WebSearch 声明的是 <c>search|query</c>、
        /// <c>fetch|url</c> —— 标记模式下模型照着例子写 <c>search|关键词</c>，插件靠竖线
        /// 左边那截判断执行哪个动作。而工具模式只会把**值**回传（模型填的是 query 本身），
        /// 竖线左边那截在参数名归一化时就没了，插件收到一个没有动词的裸字符串，
        /// 判不出动作直接静默返回 —— 实测就是这样白跑一轮的。
        ///
        /// 所以这里把前缀单独记下来，执行时原样拼回去。
        /// </summary>
        public string RawTextPrefix { get; init; } = "";

        /// <summary>OpenAI / Ollama / LMStudio 格式。</summary>
        public JObject ToOpenAiFormat() => new()
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = Parameters.DeepClone()
            }
        };

        /// <summary>
        /// OpenAI Responses API 格式。和 chat completions 的区别是**扁平的**：
        /// 没有嵌套的 function 对象，name/description/parameters 直接挂在工具上。
        /// 把 Responses 的载荷喂 chat completions 的格式（或反过来）会被端点判为参数非法。
        /// </summary>
        public JObject ToOpenAiResponsesFormat() => new()
        {
            ["type"] = "function",
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone()
        };

        /// <summary>
        /// Gemini 格式。Gemini 的 schema 不接受 additionalProperties 等 JSON Schema 关键字，
        /// 多传会被判为 400，所以这里过一遍白名单。
        /// </summary>
        public JObject ToGeminiFormat() => new()
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = SanitizeForGemini(Parameters)
        };

        /// <summary>Gemini 认得的 schema 关键字，其余（additionalProperties/default/examples…）一律丢掉。</summary>
        private static readonly string[] GeminiSchemaKeywords =
            { "type", "description", "properties", "required", "items", "enum", "nullable" };

        /// <summary>
        /// 按 schema 结构清洗，而不是无脑遍历所有键。
        ///
        /// 关键点：<c>properties</c> 底下那一层的键是**参数名**，不是 schema 关键字，
        /// 不能拿关键字白名单去筛 —— 那样会把整个参数表清空（而且表面上看还"过滤成功"了）。
        /// </summary>
        private static JObject SanitizeForGemini(JObject schema)
        {
            var result = new JObject();

            foreach (var property in schema.Properties())
            {
                if (!GeminiSchemaKeywords.Contains(property.Name)) continue;

                switch (property.Name)
                {
                    case "properties" when property.Value is JObject properties:
                    {
                        // 这一层的键是参数名，原样保留，只清洗各自的 schema
                        var cleaned = new JObject();
                        foreach (var parameter in properties.Properties())
                        {
                            cleaned[parameter.Name] = parameter.Value is JObject parameterSchema
                                ? SanitizeForGemini(parameterSchema)
                                : parameter.Value.DeepClone();
                        }
                        result["properties"] = cleaned;
                        break;
                    }

                    case "items" when property.Value is JObject items:
                        result["items"] = SanitizeForGemini(items);
                        break;

                    // required / enum 是字符串数组，type / description / nullable 是标量
                    default:
                        result[property.Name] = property.Value.DeepClone();
                        break;
                }
            }

            return result;
        }
    }

    public enum NativeToolArgumentStyle
    {
        /// <summary>还原成 <c>name(value), other(value)</c>。</summary>
        NamedArguments,

        /// <summary>整段参数就是一个值，直接原样传给插件。</summary>
        RawText
    }

    /// <summary>模型发起的一次工具调用。</summary>
    public sealed class NativeToolCall
    {
        /// <summary>厂商给的调用 id。Gemini 没有 id，这里用函数名兜底。</summary>
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        /// <summary>入参对象；解析失败时为空对象。</summary>
        public JObject Arguments { get; init; } = new();
    }

    /// <summary>一次工具调用的执行结果。</summary>
    public sealed class NativeToolResult
    {
        public NativeToolCall Call { get; init; } = new();
        public string Content { get; init; } = "";

        /// <summary>对应的插件名（原始大小写），用来还原标记文本。调用失败时可能为空。</summary>
        public string PluginName { get; init; } = "";

        /// <summary>
        /// 传给插件的文本参数 —— 也就是标记协议里 begin/end 之间的那段。
        ///
        /// 这个值本来就要算（插件的 Function(string) 只吃这套文本），顺手记下来，
        /// 落库时就能无损还原成 <c>&lt;|plugin_X_begin|&gt; ... &lt;|plugin_X_end|&gt;</c>，
        /// 不必再写一个"从 JSON 猜标记"的转换器。
        /// </summary>
        public string MarkerArguments { get; init; } = "";

        /// <summary>插件是否真的执行成功（失败时 Content 是 [error] 开头的说明）。</summary>
        public bool Succeeded { get; init; }
    }
}
