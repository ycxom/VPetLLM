using VPetLLM.Core.Abstractions.Models;

namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 可选接口：让插件用结构化的方式声明自己的调用契约，而不是靠
    /// <see cref="IVPetLLMPlugin.Parameters"/> 那段自由文本。
    ///
    /// 没实现本接口的插件行为完全不变（继续走 Description + Examples 的老路），
    /// 所以这是纯增量的，不会破坏任何既有插件。
    /// </summary>
    public interface IToolSchemaPlugin : IVPetLLMPlugin
    {
        /// <summary>
        /// 返回本插件的调用契约。宿主会把它渲染成 TypeScript 风格的函数签名写进系统提示词。
        ///
        /// 实现应当是纯函数：可以读 host 的语言设置来本地化文案，但不要连网络、不要起线程。
        /// 返回 null 表示放弃结构化声明、回退到老行为。
        /// </summary>
        ToolSchema? GetToolSchema();
    }
}
