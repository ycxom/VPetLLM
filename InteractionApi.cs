using VPetLLM.Core.Interaction;

namespace VPetLLM;

public partial class VPetLLM
{
    private IInteractionService? _interaction;

    /// <summary>
    /// 用户交互入口。插件应通过本接口发起确认/输入/选择请求，
    /// 而不是直接弹出自己的 WPF 窗口——这样同一次请求既能在本地弹窗，
    /// 也能在远端 WebUI 上应答。
    /// </summary>
    public IInteractionService Interaction => _interaction ??= new InteractionBroker();
}
