using System.Text.RegularExpressions;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using System.Threading.Tasks;

namespace VPetLLM.Handlers
{
    public class PluginHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Plugin;
        public string Keyword => "plugin";
        public string Description => "调用一个已安装的插件。格式: [:plugin(插件名称(参数))]";

        public void Execute(IMainWindow main)
        {
        }

        public async void Execute(string value, IMainWindow main)
        {
            VPetLLM.Instance.Log($"PluginHandler: Received value: {value}");
            var match = new Regex(@"(\w+)(?:\((.*)\))?").Match(value);
            if (match.Success)
            {
                var pluginName = match.Groups[1].Value.Trim();
                var arguments = match.Groups[2].Value;
                VPetLLM.Instance.Log($"PluginHandler: Parsed plugin name: {pluginName}, arguments: {arguments}");
                var plugin = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == pluginName);
                if (plugin != null)
                {
                    VPetLLM.Instance.Log($"PluginHandler: Found plugin: {plugin.Name}");
                    if (plugin is IActionPlugin actionPlugin)
                    {
                        var result = await actionPlugin.Function(arguments);
                        VPetLLM.Instance.Log($"PluginHandler: Plugin function returned: {result}");
                        var message = new Message { Role = "plugin", Content = result };
                        VPetLLM.Instance.ChatCore.GetChatHistory().Add(message);
                        await VPetLLM.Instance.ChatCore.Chat(result, true);
                    }
                }
                else
                {
                    VPetLLM.Instance.Log($"PluginHandler: Plugin not found: {pluginName}");
                }
            }
            else
            {
                VPetLLM.Instance.Log($"PluginHandler: Regex match failed for value: {value}");
            }
        }

        public void Execute(int value, IMainWindow main)
        {
        }
    }
}