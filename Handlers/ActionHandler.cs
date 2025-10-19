using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;
using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Handlers
{
    public class ActionHandler : IActionHandler
    {
        public string Keyword => "action";
        public ActionType ActionType => ActionType.Body;
        public string Description => PromptHelper.Get("Handler_Action_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public async Task Execute(string actionName, IMainWindow mainWindow)
        {
            Utils.Logger.Log($"ActionHandler executed with value: {actionName}");
            
            // 检查VPet是否正在执行重要动画
            if (AnimationStateChecker.IsPlayingImportantAnimation(mainWindow))
            {
                Logger.Log($"ActionHandler: 当前VPet状态 ({AnimationStateChecker.GetCurrentAnimationDescription(mainWindow)}) 不允许执行动作，已跳过");
                return;
            }
            
            var action = string.IsNullOrEmpty(actionName) ? "idel" : actionName;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                bool actionTriggered = false;
                switch (action.ToLower())
                {
                    case "touch_head":
                    case "touchhead":
                        mainWindow.Main.DisplayTouchHead();
                        actionTriggered = true;
                        break;
                    case "touch_body":
                    case "touchbody":
                        mainWindow.Main.DisplayTouchBody();
                        actionTriggered = true;
                        break;
                    case "move":
                        actionTriggered = mainWindow.Main.DisplayMove();
                        break;
                    case "sleep":
                        mainWindow.Main.DisplaySleep();
                        actionTriggered = true;
                        break;
                    case "idel":
                        actionTriggered = mainWindow.Main.DisplayIdel();
                        break;
                    case "sideleft":
                        // TODO: 贴墙状态（左边）- 需要 VPet >= 11057 (提交 8acf02c0)
                        // 当前版本暂不支持，等待 VPet 更新后取消注释以下代码：
                        // mainWindow.Main.Display(VPet_Simulator.Core.GraphInfo.GraphType.SideLeft, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        // actionTriggered = true;
                        Logger.Log("ActionHandler: 'sideleft' action requires VPet >= 11057, falling back to idel");
                        actionTriggered = mainWindow.Main.DisplayIdel();
                        break;
                    case "sideright":
                        // TODO: 贴墙状态（右边）- 需要 VPet >= 11057 (提交 8acf02c0)
                        // 当前版本暂不支持，等待 VPet 更新后取消注释以下代码：
                        // mainWindow.Main.Display(VPet_Simulator.Core.GraphInfo.GraphType.SideRight, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        // actionTriggered = true;
                        Logger.Log("ActionHandler: 'sideright' action requires VPet >= 11057, falling back to idel");
                        actionTriggered = mainWindow.Main.DisplayIdel();
                        break;
                    default:
                        mainWindow.Main.Display(action, AnimatType.Single, mainWindow.Main.DisplayToNomal);
                        actionTriggered = true;
                        break;
                }
                
                if (!actionTriggered)
                {
                    Logger.Log($"ActionHandler: Action '{action}' failed to trigger");
                }
                
                await Task.Delay(1000);
            });
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }
        public Task Execute(IMainWindow mainWindow)
        {
            return Execute("idel", mainWindow);
        }
        public int GetAnimationDuration(string animationName) => 1000;
    }
}