using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VPetLLM.Core;

namespace VPetLLM.Utils
{
    public static class PluginManager
    {
        public static List<IVPetLLMPlugin> Plugins { get; } = new List<IVPetLLMPlugin>();
        private static readonly Dictionary<string, AssemblyLoadContext> _pluginContexts = new();
        public static string PluginPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");

        public static void LoadPlugins(IChatCore chatCore)
        {
            var pluginDir = PluginPath;
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
                return;
            }

            // Unload existing plugins before reloading
            if (chatCore != null)
            {
                foreach (var p in Plugins.ToList())
                {
                    chatCore.RemovePlugin(p);
                }
            }
            Plugins.Clear();
            _pluginContexts.Clear();

            var configFile = Path.Combine(pluginDir, "plugins.json");
            var pluginStates = new Dictionary<string, bool>();
            if (File.Exists(configFile))
            {
                pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
            }

            foreach (var file in Directory.GetFiles(pluginDir, "*.dll"))
            {
                try
                {
                    var context = new AssemblyLoadContext($"{Path.GetFileNameWithoutExtension(file)}_{Guid.NewGuid()}", isCollectible: true);
                    // Use a different shadow copy directory to avoid conflicts
                    var shadowCopyDir = Path.Combine(Path.GetTempPath(), "VPetLLM_Plugins", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(shadowCopyDir);
                    var shadowCopiedFile = Path.Combine(shadowCopyDir, Path.GetFileName(file));
                    File.Copy(file, shadowCopiedFile, true);

                    var pdbFile = Path.ChangeExtension(file, ".pdb");
                    if (File.Exists(pdbFile))
                    {
                        var shadowCopiedPdb = Path.ChangeExtension(shadowCopiedFile, ".pdb");
                        File.Copy(pdbFile, shadowCopiedPdb, true);
                    }

                    var assembly = context.LoadFromAssemblyPath(shadowCopiedFile);
                    _pluginContexts[file] = context;

                    foreach (var type in assembly.GetTypes())
                    {
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                            plugin.FilePath = file;
                            plugin.Enabled = pluginStates.TryGetValue(plugin.Name, out var enabled) ? enabled : true;
                            plugin.Initialize(VPetLLM.Instance);
                            Plugins.Add(plugin);
                            if (plugin.Enabled && chatCore != null)
                            {
                                chatCore.AddPlugin(plugin);
                            }
                            Logger.Log($"Loaded plugin: {plugin.Name}, Enabled: {plugin.Enabled}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to load plugin {file}: {ex.Message}");
                }
            }
        }

        public static void SavePluginStates()
        {
            var configFile = Path.Combine(PluginPath, "plugins.json");
            var pluginStates = Plugins.ToDictionary(p => p.Name, p => p.Enabled);
            File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
        }

        public static async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin, IChatCore chatCore)
        {
            string filePath = plugin.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Log($"Invalid or non-existent plugin file path for {plugin.Name}: '{filePath}'");
                return false;
            }

            // Perform logical unload
            if (chatCore != null)
            {
                chatCore.RemovePlugin(plugin);
            }
            plugin.Unload();
            Plugins.Remove(plugin);
            Logger.Log($"Logically unloaded plugin: {plugin.Name}");
            
            // Try to delete the file with context unloading
            for (int i = 0; i < 5; i++)
            {
                if (_pluginContexts.TryGetValue(filePath, out var context))
                {
                    context.Unload();
                    _pluginContexts.Remove(filePath);
                    Logger.Log($"Unloading AssemblyLoadContext for {plugin.Name} (Attempt {i + 1})");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                try
                {
                    File.Delete(filePath);
                    var pdbPath = Path.ChangeExtension(filePath, ".pdb");
                    if (File.Exists(pdbPath))
                    {
                        File.Delete(pdbPath);
                    }
                    Logger.Log($"Successfully deleted plugin files: {Path.GetFileName(filePath)}");
                    return true;
                }
                catch (IOException)
                {
                    Logger.Log($"Attempt {i + 1} to delete {Path.GetFileName(filePath)} failed, retrying...");
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    Logger.Log($"An unexpected error occurred while deleting plugin {Path.GetFileName(filePath)}: {ex.Message}");
                    return false;
                }
            }

            Logger.Log($"Failed to delete plugin after multiple attempts: {Path.GetFileName(filePath)}");
            return false;
        }

        public static void ImportPlugin(string sourceFilePath)
        {
            var pluginDir = PluginPath;
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
            }

            var fileName = Path.GetFileName(sourceFilePath);
            var destPath = Path.Combine(pluginDir, fileName);

            try
            {
                File.Copy(sourceFilePath, destPath, true);
                Logger.Log($"Imported plugin: {fileName}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to import plugin {fileName}: {ex.Message}");
            }
        }
    }
}