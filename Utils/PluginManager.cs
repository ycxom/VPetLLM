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
        public static List<FailedPlugin> FailedPlugins { get; } = new List<FailedPlugin>();
        private static readonly Dictionary<string, AssemblyLoadContext> _pluginContexts = new();
        private static readonly Dictionary<string, string> _shadowCopyDirectories = new();
        public static string PluginPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");

        public static void LoadPlugins(IChatCore chatCore)
        {
            var pluginDir = PluginPath;
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
                return;
            }
            
            UnloadAllPlugins(chatCore);

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
                    
                    var shadowCopyDir = Path.Combine(Path.GetTempPath(), "VPetLLM_Plugins", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(shadowCopyDir);
                    var shadowCopiedFile = Path.Combine(shadowCopyDir, Path.GetFileName(file));
                    File.Copy(file, shadowCopiedFile, true);
                    _shadowCopyDirectories[file] = shadowCopyDir;

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
                    FailedPlugins.Add(new FailedPlugin
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = file,
                        Error = ex,
                        Description = ex.Message
                    });
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
            if (string.IsNullOrEmpty(filePath))
            {
                Logger.Log($"Plugin file path is empty for {plugin.Name}.");
                return false;
            }

            if (chatCore != null)
            {
                chatCore.RemovePlugin(plugin);
            }
            plugin.Unload();
            Plugins.Remove(plugin);

            if (_pluginContexts.TryGetValue(filePath, out var context))
            {
                context.Unload();
                _pluginContexts.Remove(filePath);
                Logger.Log($"Unloaded AssemblyLoadContext for {plugin.Name}.");
            }
            
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (_shadowCopyDirectories.TryGetValue(filePath, out var shadowDir) && Directory.Exists(shadowDir))
            {
                try
                {
                    Directory.Delete(shadowDir, true);
                    _shadowCopyDirectories.Remove(filePath);
                    Logger.Log($"Deleted shadow copy directory for {plugin.Name}: {shadowDir}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete shadow copy directory {shadowDir}: {ex.Message}");
                }
            }
            
            if (!File.Exists(filePath))
            {
                Logger.Log($"Plugin file does not exist, no need to delete: '{filePath}'");
                return true;
            }

            for (int i = 0; i < 5; i++)
            {
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
        public static void UnloadAllPlugins(IChatCore chatCore)
        {
            if (chatCore != null)
            {
                foreach (var p in Plugins.ToList())
                {
                    chatCore.RemovePlugin(p);
                    p.Unload();
                }
            }
            Plugins.Clear();

            foreach (var context in _pluginContexts.Values)
            {
                context.Unload();
            }
            _pluginContexts.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();

            foreach (var dir in _shadowCopyDirectories.Values)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete shadow copy directory {dir}: {ex.Message}");
                }
            }
            _shadowCopyDirectories.Clear();
            FailedPlugins.Clear();
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
        public static async Task<bool> DeletePluginFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Log($"Invalid or non-existent plugin file path: '{filePath}'");
                return false;
            }

            for (int i = 0; i < 5; i++)
            {
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
        public static string GetFileSha256(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }
}