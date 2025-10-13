using Newtonsoft.Json;
using System.IO;
using System.Runtime.Loader;
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

            // 卸载所有程序集上下文
            var contextList = _pluginContexts.Values.ToList();
            foreach (var context in contextList)
            {
                context.Unload();
            }
            _pluginContexts.Clear();

            // 强制垃圾回收，等待程序集卸载完成
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                System.Threading.Thread.Sleep(100);
            }

            // 异步清理影子拷贝目录，避免阻塞UI
            var shadowDirs = _shadowCopyDirectories.Values.ToList();
            _shadowCopyDirectories.Clear();
            
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // 等待1秒确保文件句柄释放
                
                foreach (var dir in shadowDirs)
                {
                    await CleanupShadowDirectory(dir);
                }
            });
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
        public static async Task<bool> UpdatePlugin(string pluginFilePath, IChatCore chatCore)
        {
            if (string.IsNullOrEmpty(pluginFilePath) || !File.Exists(pluginFilePath))
            {
                Logger.Log($"Plugin file path is invalid or does not exist: '{pluginFilePath}'");
                return false;
            }

            try
            {
                // 查找需要更新的插件
                var existingPlugin = Plugins.FirstOrDefault(p => p.FilePath == pluginFilePath);
                string pluginName = null;
                
                if (existingPlugin != null)
                {
                    pluginName = existingPlugin.Name;
                    Logger.Log($"Found existing plugin to update: {pluginName}");
                    
                    // 先卸载旧版本插件
                    if (chatCore != null)
                    {
                        chatCore.RemovePlugin(existingPlugin);
                    }
                    existingPlugin.Unload();
                    Plugins.Remove(existingPlugin);

                    // 卸载旧的 AssemblyLoadContext
                    if (_pluginContexts.TryGetValue(pluginFilePath, out var context))
                    {
                        var weakContext = new WeakReference(context);
                        context.Unload();
                        _pluginContexts.Remove(pluginFilePath);
                        Logger.Log($"Unloaded AssemblyLoadContext for {pluginName}");

                        // 等待垃圾回收
                        for (int i = 0; weakContext.IsAlive && (i < 10); i++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(200);
                        }
                    }

                    // 清理影子拷贝目录
                    if (_shadowCopyDirectories.TryGetValue(pluginFilePath, out var shadowDir) && Directory.Exists(shadowDir))
                    {
                        try
                        {
                            Directory.Delete(shadowDir, true);
                            _shadowCopyDirectories.Remove(pluginFilePath);
                            Logger.Log($"Cleaned up shadow copy directory for {pluginName}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to clean up shadow copy directory: {ex.Message}");
                        }
                    }

                    // 查找并清理其他可能包含同名插件的文件
                    await CleanupDuplicatePluginFiles(pluginName, pluginFilePath, chatCore);

                    // 额外等待确保文件句柄完全释放
                    await Task.Delay(500);
                }

                // 重新加载单个插件
                await LoadSinglePlugin(pluginFilePath, chatCore);
                
                // 确保文件系统操作完成后再返回
                await Task.Delay(300);
                
                Logger.Log($"Successfully updated plugin: {pluginFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update plugin {pluginFilePath}: {ex.Message}");
                return false;
            }
        }

        private static Task CleanupDuplicatePluginFiles(string pluginName, string currentFilePath, IChatCore chatCore)
        {
            if (string.IsNullOrEmpty(pluginName))
                return Task.CompletedTask;

            try
            {
                var pluginDir = PluginPath;
                var allPluginFiles = Directory.GetFiles(pluginDir, "*.dll");
                
                foreach (var file in allPluginFiles)
                {
                    if (file.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase))
                        continue; // 跳过当前文件
                    
                    // 查找是否有其他插件实例使用相同的插件名
                    var duplicatePlugin = Plugins.FirstOrDefault(p => p.Name == pluginName && p.FilePath == file);
                    if (duplicatePlugin != null)
                    {
                        Logger.Log($"Found duplicate plugin file for '{pluginName}': {file}");
                        Logger.Log($"Removing duplicate plugin instance...");
                        
                        // 卸载重复的插件
                        if (chatCore != null)
                        {
                            chatCore.RemovePlugin(duplicatePlugin);
                        }
                        duplicatePlugin.Unload();
                        Plugins.Remove(duplicatePlugin);

                        // 清理相关资源
                        if (_pluginContexts.TryGetValue(file, out var context))
                        {
                            context.Unload();
                            _pluginContexts.Remove(file);
                        }

                        if (_shadowCopyDirectories.TryGetValue(file, out var shadowDir) && Directory.Exists(shadowDir))
                        {
                            try
                            {
                                Directory.Delete(shadowDir, true);
                                _shadowCopyDirectories.Remove(file);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Failed to clean up shadow copy directory for duplicate: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error during duplicate plugin cleanup: {ex.Message}");
            }
            
            return Task.CompletedTask;
        }

        private static Task LoadSinglePlugin(string pluginFilePath, IChatCore chatCore)
        {
            try
            {
                var context = new AssemblyLoadContext($"{Path.GetFileNameWithoutExtension(pluginFilePath)}_{Guid.NewGuid()}", isCollectible: true);

                var shadowCopyDir = Path.Combine(Path.GetTempPath(), "VPetLLM_Plugins", Guid.NewGuid().ToString());
                Directory.CreateDirectory(shadowCopyDir);
                var shadowCopiedFile = Path.Combine(shadowCopyDir, Path.GetFileName(pluginFilePath));
                File.Copy(pluginFilePath, shadowCopiedFile, true);
                _shadowCopyDirectories[pluginFilePath] = shadowCopyDir;

                var pdbFile = Path.ChangeExtension(pluginFilePath, ".pdb");
                if (File.Exists(pdbFile))
                {
                    var shadowCopiedPdb = Path.ChangeExtension(shadowCopiedFile, ".pdb");
                    File.Copy(pdbFile, shadowCopiedPdb, true);
                }

                var assembly = context.LoadFromAssemblyPath(shadowCopiedFile);
                _pluginContexts[pluginFilePath] = context;

                // 读取插件状态配置
                var pluginDir = PluginPath;
                var configFile = Path.Combine(pluginDir, "plugins.json");
                var pluginStates = new Dictionary<string, bool>();
                if (File.Exists(configFile))
                {
                    pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                }

                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                    {
                        var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                        plugin.FilePath = pluginFilePath;

                        // 在单个插件加载中，不应该有重复插件，因为我们已经在更新前移除了旧插件
                        // 如果仍然存在重复，说明有其他同名插件文件，这是一个问题
                        var existingPlugin = Plugins.FirstOrDefault(p => p.Name == plugin.Name);
                        if (existingPlugin != null)
                        {
                            Logger.Log($"Critical: Plugin with name '{plugin.Name}' already exists during single plugin load!");
                            Logger.Log($"  Existing plugin from: {existingPlugin.FilePath}");
                            Logger.Log($"  New plugin from: {pluginFilePath}");
                            Logger.Log($"  This indicates multiple plugin files contain the same plugin name.");
                            
                            // 在更新场景下，我们应该替换现有插件而不是跳过
                            Logger.Log($"  Removing existing plugin and loading the new one...");
                            if (chatCore != null)
                            {
                                chatCore.RemovePlugin(existingPlugin);
                            }
                            existingPlugin.Unload();
                            Plugins.Remove(existingPlugin);
                        }
                        
                        Plugins.Add(plugin);
                        _pluginContexts[pluginFilePath] = context;
                        
                        // 应用插件状态配置
                        if (pluginStates.TryGetValue(plugin.Name, out var isEnabled))
                        {
                            plugin.Enabled = isEnabled;
                        }
                        
                        if (plugin.Enabled && chatCore != null)
                        {
                            chatCore.AddPlugin(plugin);
                        }
                        
                        Logger.Log($"Plugin loaded: {plugin.Name} from {pluginFilePath}");
                    }
                }
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading plugin {pluginFilePath}: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private static async Task CleanupShadowDirectory(string shadowDir)
        {
            if (string.IsNullOrEmpty(shadowDir) || !Directory.Exists(shadowDir))
                return;

            // 重试删除影子拷贝目录，最多尝试5次
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(shadowDir, true);
                    Logger.Log($"Successfully deleted shadow copy directory: {shadowDir}");
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Logger.Log($"Attempt {attempt}/5: Access denied when deleting {shadowDir}. Waiting...");
                    await Task.Delay(2000 * attempt); // 递增等待时间
                }
                catch (DirectoryNotFoundException)
                {
                    // 目录已经不存在，认为清理成功
                    Logger.Log($"Shadow copy directory already deleted: {shadowDir}");
                    return;
                }
                catch (IOException ex)
                {
                    Logger.Log($"Attempt {attempt}/5: IO error when deleting {shadowDir}: {ex.Message}. Waiting...");
                    await Task.Delay(1000 * attempt);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Attempt {attempt}/5: Unexpected error when deleting {shadowDir}: {ex.Message}");
                    if (attempt == 5)
                    {
                        Logger.Log($"Failed to delete shadow copy directory after 5 attempts: {shadowDir}");
                        // 记录到失败列表，可以考虑在应用程序启动时清理
                        RecordFailedCleanup(shadowDir);
                    }
                    else
                    {
                        await Task.Delay(1000 * attempt);
                    }
                }
            }
        }

        private static void RecordFailedCleanup(string directory)
        {
            try
            {
                var failedCleanupFile = Path.Combine(Path.GetTempPath(), "VPetLLM_FailedCleanup.txt");
                File.AppendAllText(failedCleanupFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {directory}\n");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to record failed cleanup: {ex.Message}");
            }
        }

        public static string GetFileSha256(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"GetFileSha256: File does not exist: {filePath}");
                return null;
            }

            // 重试机制，防止文件被锁定
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var sha256 = System.Security.Cryptography.SHA256.Create())
                    {
                        using (var stream = File.OpenRead(filePath))
                        {
                            var hash = sha256.ComputeHash(stream);
                            var result = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            Logger.Log($"GetFileSha256: Successfully calculated hash for {Path.GetFileName(filePath)}: {result}");
                            return result;
                        }
                    }
                }
                catch (IOException ex) when (attempt < 2)
                {
                    Logger.Log($"GetFileSha256: Attempt {attempt + 1} failed for {filePath}: {ex.Message}. Retrying...");
                    System.Threading.Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GetFileSha256: Error calculating hash for {filePath}: {ex.Message}");
                    return null;
                }
            }

            Logger.Log($"GetFileSha256: Failed to calculate hash after 3 attempts for {filePath}");
            return null;
        }
    }
}