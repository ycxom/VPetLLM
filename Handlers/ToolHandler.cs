using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;

namespace VPetLLM.Handlers
{
    public class ToolHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Tool;
        public string Keyword => "tool";
        public string Description => Utils.PromptHelper.Get("Handler_Tool_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(IMainWindow main)
        {
            return Task.CompletedTask;
        }

        public async Task Execute(string value, IMainWindow main)
        {
            var match = new Regex(@"(.*?)\((.*)\)").Match(value);
            if (match.Success)
            {
                var toolName = match.Groups[1].Value;
                var arguments = match.Groups[2].Value;
                var tool = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == toolName);
                if (tool is IActionPlugin actionPlugin)
                {
                    var result = await actionPlugin.Function(arguments);
                    var message = new Message { Role = "tool", Content = result };
                    VPetLLM.Instance.ChatCore.GetChatHistory().Add(message);
                }
            }
        }

        public Task Execute(int value, IMainWindow main)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}