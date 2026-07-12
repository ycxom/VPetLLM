namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 插件面板接口 —— 让带 UI 的插件把自己的界面"释放"到 VPetLLM 设置窗口的
    /// 「插件面板」Tab 下，以内嵌子 Tab 呈现（而非各自弹独立窗口）。
    ///
    /// 面板是一个 WPF <see cref="System.Windows.FrameworkElement"/>（通常是 UserControl）。
    /// 返回共享框架类型，宿主与插件之间无跨 AssemblyLoadContext 的类型耦合。
    /// </summary>
    public interface IPluginTab : IVPetLLMPlugin
    {
        /// <summary>
        /// 子 Tab 标题。插件按 <c>Settings.Language</c> 自行返回本地化文本。
        /// </summary>
        string TabTitle { get; }

        /// <summary>
        /// 创建面板内容。宿主在 UI 线程调用，并把返回元素挂进 Tab。
        /// 每次调用应返回新实例（设置窗口每次打开都会重建面板）。
        /// </summary>
        System.Windows.FrameworkElement CreatePanel();
    }
}
