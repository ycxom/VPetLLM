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
        public string Description => "管理或调用已安装的插件。格式: [:plugin(操作:插件名称)]，支持的操作有 'delete', 'disable', 'enable'。";

        public void Execute(IMainWindow main)
        {
        }

        public async void Execute(string value, IMainWindow main)
        {
            VPetLLM.Instance.Log($"PluginHandler: Received value: {value}");
            var match = new Regex(@"(delete|disable|enable):(\w+)|(\w+)\((.*)\)").Match(value);
            if (match.Success)
            {
                if (match.Groups[1].Success) // Management commands
                {
                    var operation = match.Groups[1].Value;
                    var pluginName = match.Groups[2].Value.Trim();
                    var plugin = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == pluginName);

                    if (plugin == null)
                    {
                        VPetLLM.Instance.Log($"PluginHandler: Plugin not found: {pluginName}");
                        return;
                    }

                    switch (operation)
                    {
                        case "delete":
                            VPetLLM.Instance.UnloadPlugin(plugin);
                            if (System.IO.File.Exists(plugin.FilePath))
                            {
                                System.IO.File.Delete(plugin.FilePath);
                                VPetLLM.Instance.Log($"PluginHandler: Deleted plugin file: {plugin.FilePath}");
                            }
                            break;
                        case "disable":
                            plugin.Enabled = false;
                            VPetLLM.Instance.ChatCore.RemovePlugin(plugin);
                            VPetLLM.Instance.Log($"PluginHandler: Disabled plugin: {plugin.Name}");
                            VPetLLM.Instance.SavePluginStates();
                            VPetLLM.Instance.UpdateSystemMessage();
                            VPetLLM.Instance.RefreshPluginList();
                            break;
                        case "enable":
                            plugin.Enabled = true;
                            VPetLLM.Instance.ChatCore.AddPlugin(plugin);
                            VPetLLM.Instance.Log($"PluginHandler: Enabled plugin: {plugin.Name}");
                            VPetLLM.Instance.SavePluginStates();
                            VPetLLM.Instance.UpdateSystemMessage();
                            VPetLLM.Instance.RefreshPluginList();
                            break;
                    }
                }
                else // Invocation command
                {
                    var pluginName = match.Groups[3].Value.Trim();
                    var arguments = match.Groups[4].Value;
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
                            VPetLLM.Instance.Log($"PluginHandler: Plugin function returned: {result}");
                            await VPetLLM.Instance.ChatCore.Chat(result, true);
                        }
                    }
                    else
                    {
                        VPetLLM.Instance.Log($"PluginHandler: Plugin not found: {pluginName}");
                    }
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