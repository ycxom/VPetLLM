using System;
using System.Collections.Generic;

namespace VPetLLM.Core.Abstractions.Models
{
    /// <summary>参数的基础类型。渲染进 Prompt 时会变成 TypeScript 的类型名。</summary>
    public enum ToolParameterType
    {
        String,
        Integer,
        Number,
        Boolean,
        /// <summary>取值受限于 <see cref="ToolParameter.EnumValues"/>。</summary>
        Enum
    }

    /// <summary>一次插件调用里的一个参数。</summary>
    public sealed class ToolParameter
    {
        public string Name { get; init; } = "";

        public ToolParameterType Type { get; init; } = ToolParameterType.String;

        /// <summary>可选参数会在签名里渲染成 <c>name?: type</c>。</summary>
        public bool Required { get; init; } = true;

        /// <summary>一句话说明。渲染成签名后面的行内注释。</summary>
        public string Description { get; init; } = "";

        /// <summary><see cref="ToolParameterType.Enum"/> 的候选值。</summary>
        public IReadOnlyList<string>? EnumValues { get; init; }

        /// <summary>默认值，仅用于展示。</summary>
        public string? Default { get; init; }

        /// <summary>
        /// 用于自动生成示例的真实取值（例如 city 给 "北京"）。
        ///
        /// 没有它时渲染器**不会**编一个 <c>&lt;city&gt;</c> 这样的占位符 —— 模型见了很可能原样抄进去。
        /// 宁可不给示例，也不给会被照抄的假值。
        /// </summary>
        public string? Sample { get; init; }

        public static ToolParameter Str(string name, string description, bool required = true, string? sample = null, string? @default = null)
            => new() { Name = name, Type = ToolParameterType.String, Description = description, Required = required, Sample = sample, Default = @default };

        public static ToolParameter Int(string name, string description, bool required = true, string? sample = null, string? @default = null)
            => new() { Name = name, Type = ToolParameterType.Integer, Description = description, Required = required, Sample = sample, Default = @default };

        public static ToolParameter Num(string name, string description, bool required = true, string? sample = null, string? @default = null)
            => new() { Name = name, Type = ToolParameterType.Number, Description = description, Required = required, Sample = sample, Default = @default };

        public static ToolParameter Bool(string name, string description, bool required = true, string? sample = null, string? @default = null)
            => new() { Name = name, Type = ToolParameterType.Boolean, Description = description, Required = required, Sample = sample, Default = @default };

        public static ToolParameter Choice(string name, string description, IReadOnlyList<string> values, bool required = true, string? @default = null)
            => new() { Name = name, Type = ToolParameterType.Enum, Description = description, EnumValues = values, Required = required, Default = @default };
    }

    /// <summary>参数在标记之间怎么写。</summary>
    public enum ToolCallStyle
    {
        /// <summary><c>name(value), other(value)</c> —— 绝大多数插件用这种。</summary>
        NamedArguments,

        /// <summary>整段标记内容就是唯一的参数值（Terminal 的命令、MarkdownViewer 的正文）。</summary>
        RawText
    }

    /// <summary>
    /// 插件的一种调用形态。
    ///
    /// 不少插件其实是"多形态"的 —— Diary 既能 <c>action(write)</c> 又能 <c>search(keyword)</c>，
    /// Terminal 既接收裸命令又接收 <c>action(setting)</c>。硬塞进一个参数列表只会让模型猜，
    /// 所以每种形态单独声明、单独渲染出一条签名。
    /// </summary>
    public sealed class ToolCallForm
    {
        /// <summary>这种形态做什么。</summary>
        public string Summary { get; init; } = "";

        public ToolCallStyle Style { get; init; } = ToolCallStyle.NamedArguments;

        public IReadOnlyList<ToolParameter> Parameters { get; init; } = Array.Empty<ToolParameter>();

        /// <summary>
        /// 自定义示例（标记之间的那段文本）。留空时由渲染器按参数自动生成。
        /// </summary>
        public string? Example { get; init; }
    }

    /// <summary>
    /// 一个插件对模型暴露的完整调用契约。
    ///
    /// 取代原来那个自由文本的 <c>Parameters</c> 属性 —— 后者各插件各写各的
    /// （有的写 <c>"setting"</c>，有的写一整段 JSON 说明），而且根本没有被拼进 Prompt。
    /// 思路来自 codex 的 code-mode-protocol：把工具的入参渲染成 TypeScript 类型声明，
    /// 模型对函数签名的解析准确率远高于散文。
    /// </summary>
    public sealed class ToolSchema
    {
        /// <summary>插件整体的一句话说明。留空时回退到插件的 Description。</summary>
        public string Summary { get; init; } = "";

        public IReadOnlyList<ToolCallForm> Forms { get; init; } = Array.Empty<ToolCallForm>();

        /// <summary>补充说明，渲染在所有签名之后（限制条件、注意事项等）。</summary>
        public string? Remarks { get; init; }
    }
}
