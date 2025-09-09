using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;

namespace VPetLLM.Handlers
{
    public class PluginHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Plugin;
        public string Keyword => "plugin";
        public string Description => Utils.PromptHelper.Get("Handler_Plugin_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(IMainWindow main)
        {
            return Task.CompletedTask;
        }

        public async Task Execute(string value, IMainWindow main)
        {
            VPetLLM.Instance.Log($"PluginHandler: Received value: {value}");
            var firstParen = value.IndexOf('(');
            var lastParen = value.LastIndexOf(')');

            if (firstParen != -1 && lastParen != -1 && lastParen > firstParen)
            {
                var pluginName = value.Substring(0, firstParen).Trim();
                var arguments = value.Substring(firstParen + 1, lastParen - firstParen - 1);
                VPetLLM.Instance.Log($"PluginHandler: Parsed plugin name: {pluginName}, arguments: {arguments}");
                var plugin = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == pluginName.ToLower());
                if (plugin != null)
                {
                    if (!plugin.Enabled)
                    {
                        VPetLLM.Instance.Log($"PluginHandler: Plugin '{plugin.Name}' is disabled.");
                        return;
                    }
                    VPetLLM.Instance.Log($"PluginHandler: Found plugin: {plugin.Name}");
                    if (plugin is IActionPlugin actionPlugin)
                    {
                        var result = await actionPlugin.Function(arguments);
                        var formattedResult = $"[Plugin.{pluginName}: \"{result}\"]";
                        VPetLLM.Instance.Log($"PluginHandler: Plugin function returned: {result}, formatted: {formattedResult}");
                        _ = VPetLLM.Instance.ChatCore.Chat(formattedResult, true);
                    }
                }
                else
                {
                    VPetLLM.Instance.Log($"PluginHandler: Plugin not found: {pluginName}");
                    var availablePlugins = string.Join(", ", VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => p.Name));
                    var errorMessage = $"[SYSTEM] Error: Plugin '{pluginName}' not found. Available plugins are: {availablePlugins}";
                    await VPetLLM.Instance.ChatCore.Chat(errorMessage, true);
                }
            }
            else
            {
                VPetLLM.Instance.Log($"PluginHandler: Parentheses not found or mismatched for value: {value}");
            }
        }

        public Task Execute(int value, IMainWindow main)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}