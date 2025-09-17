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
                            
                            // 检查是否已存在同名插件
                            var existingPlugin = Plugins.FirstOrDefault(p => p.Name == plugin.Name);
                            if (existingPlugin != null)
                            {
                                Logger.Log($"Warning: Plugin with name '{plugin.Name}' already exists. Skipping duplicate from {file}");
                                Logger.Log($"Existing plugin from: {existingPlugin.FilePath}");
                                Logger.Log($"Duplicate plugin from: {file}");
                                continue; // 跳过重复的插件
                            }
                            
                            if (plugin is IPluginWithData pluginWithData)
                            {
                                var pluginDataDir = Path.Combine(pluginDir, "PluginData", plugin.Name);
                                Directory.CreateDirectory(pluginDataDir);
                                pluginWithData.PluginDataDir = pluginDataDir;
                            }
                            plugin.Enabled = pluginStates.TryGetValue(plugin.Name, out var enabled) ? enabled : true;
                            Plugins.Add(plugin);
                            if (plugin.Enabled)
                            {
                                plugin.Initialize(VPetLLM.Instance);
                                if (chatCore != null)
                                {
                                    chatCore.AddPlugin(plugin);
                                }
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
            
            // 使用安全的方式创建字典，避免重复键的问题
            var pluginStates = new Dictionary<string, bool>();
            foreach (var plugin in Plugins)
            {
                if (!string.IsNullOrEmpty(plugin.Name))
                {
                    // 如果存在重复名称，使用最后一个插件的状态
                    pluginStates[plugin.Name] = plugin.Enabled;
                }
            }
            
            File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
            Logger.Log($"Saved plugin states for {pluginStates.Count} plugins");
        }

        public static async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin, IChatCore chatCore)
        {
            string filePath = plugin.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Log($"Plugin file path is invalid or does not exist for {plugin.Name}: '{filePath}'");
                return false;
            }

            var configFile = Path.Combine(PluginPath, "plugins.json");
            if (File.Exists(configFile))
            {
                var pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                if (pluginStates.Remove(plugin.Name))
                {
                    File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
                }
            }

            if (chatCore != null)
            {
                chatCore.RemovePlugin(plugin);
            }
            plugin.Unload();
            Plugins.Remove(plugin);

            if (_pluginContexts.TryGetValue(filePath, out var context))
            {
                var weakContext = new WeakReference(context);
                context.Unload();
                _pluginContexts.Remove(filePath);
                Logger.Log($"Unloaded AssemblyLoadContext for {plugin.Name}. Waiting for garbage collection...");

                // Wait for the context to be actually collected
                for (int i = 0; weakContext.IsAlive && (i < 10); i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(100);
                }

                if (weakContext.IsAlive)
                {
                    Logger.Log($"Warning: AssemblyLoadContext for {plugin.Name} could not be fully unloaded. File handles may remain locked.");
                }
                else
                {
                    Logger.Log($"AssemblyLoadContext for {plugin.Name} has been garbage collected.");
                }
            }

            // Retry deleting the shadow copy directory
            if (_shadowCopyDirectories.TryGetValue(filePath, out var shadowDir) && Directory.Exists(shadowDir))
            {
                bool shadowDeleted = false;
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.Delete(shadowDir, true);
                        _shadowCopyDirectories.Remove(filePath);
                        Logger.Log($"Successfully deleted shadow copy directory for {plugin.Name}: {shadowDir}");
                        shadowDeleted = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Attempt {i + 1} to delete shadow copy directory {shadowDir} failed: {ex.Message}. Retrying...");
                        await Task.Delay(200);
                    }
                }
                if (!shadowDeleted)
                {
                     Logger.Log($"Failed to delete shadow copy directory after multiple attempts: {shadowDir}");
                }
            }
            
            // Retry deleting the original plugin file
            return await DeletePluginFile(filePath);
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