namespace VPetLLM.Core.Abstractions.Interfaces.Plugin
{
    /// <summary>
    /// 可选接口：让插件在**未初始化**的情况下也能给出本地化描述。
    /// 设置窗口只是要显示一行文字，实现了它的插件就不会被主程序临时启停
    /// （否则只能靠反射注入宿主引用，见 PluginDescriptionProbe）。
    /// 实现时必须是纯函数：只读 host 的语言设置，不要连网络、不要起线程、不要写配置。
    /// </summary>
    public interface ILocalizedDescription
    {
        string GetLocalizedDescription(VPetLLM host);
    }
}
