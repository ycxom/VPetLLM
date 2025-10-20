using System.Threading.Tasks;

namespace VPetLLM.Core
{
    /// <summary>
    /// 插件接管接口 - 允许插件完全接管消息处理流程
    /// </summary>
    public interface IPluginTakeover : IVPetLLMPlugin
    {
        /// <summary>
        /// 是否支持接管模式
        /// </summary>
        bool SupportsTakeover { get; }

        /// <summary>
        /// 开始接管处理
        /// </summary>
        /// <param name="initialContent">初始内容</param>
        /// <returns>是否成功开始接管</returns>
        Task<bool> BeginTakeoverAsync(string initialContent);

        /// <summary>
        /// 处理接管期间的内容片段
        /// </summary>
        /// <param name="content">内容片段</param>
        /// <returns>是否继续接管</returns>
        Task<bool> ProcessTakeoverContentAsync(string content);

        /// <summary>
        /// 结束接管处理
        /// </summary>
        /// <returns>处理结果</returns>
        Task<string> EndTakeoverAsync();

        /// <summary>
        /// 检查是否应该结束接管
        /// </summary>
        /// <param name="content">当前累积的内容</param>
        /// <returns>是否应该结束接管</returns>
        bool ShouldEndTakeover(string content);
    }
}
