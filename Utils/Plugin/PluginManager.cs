using System.Runtime.Loader;
using System.Security.Cryptography;
using VPetLLMUtils = VPetLLM.Utils.System;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;
using LegacyPlugin = VPetLLM.Core;

namespace VPetLLM.Utils.Plugin
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
            VPetLLMUtils.Logger.Log($"LoadPlugins: PluginPath property returns: {pluginDir}");
            
            if (!Directory.Exists(pluginDir))
            {
                VPetLLMUtils.Logger.Log($"LoadPlugins: Directory does not exist, creating: {pluginDir}");
                Directory.CreateDirectory(pluginDir);
                return;
            }

            VPetLLMUtils.Logger.Log($"LoadPlugins: Directory exists, checking contents");
            
            // 详细检查目录内容
            try
            {
                var allFiles = Directory.GetFiles(pluginDir);
                VPetLLMUtils.Logger.Log($"LoadPlugins: Total files in directory: {allFiles.Length}");
                foreach (var file in allFiles)
                {
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Found file: {Path.GetFileName(file)}");
                }
                
                var allDirectories = Directory.GetDirectories(pluginDir);
                VPetLLMUtils.Logger.Log($"LoadPlugins: Total subdirectories: {allDirectories.Length}");
                foreach (var dir in allDirectories)
                {
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Found directory: {Path.GetFileName(dir)}");
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"LoadPlugins: Error listing directory contents: {ex.Message}");
            }

            VPetLLMUtils.Logger.Log($"LoadPlugins: Starting plugin load process. Plugin directory: {pluginDir}");
            UnloadAllPlugins(chatCore);
            VPetLLMUtils.Logger.Log($"LoadPlugins: After UnloadAllPlugins - FailedPlugins.Count: {FailedPlugins.Count}");

            var configFile = Path.Combine(pluginDir, "plugins.json");
            var pluginStates = new Dictionary<string, bool>();
            if (File.Exists(configFile))
            {
                pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                VPetLLMUtils.Logger.Log($"LoadPlugins: Loaded plugin states from config file, {pluginStates.Count} entries");
            }
            else
            {
                VPetLLMUtils.Logger.Log($"LoadPlugins: No plugins.json config file found");
            }

            VPetLLMUtils.Logger.Log($"LoadPlugins: Searching for DLL files with pattern '*.dll' in: {pluginDir}");
            var dllFiles = Directory.GetFiles(pluginDir, "*.dll");
            VPetLLMUtils.Logger.Log($"LoadPlugins: Directory.GetFiles returned {dllFiles.Length} DLL files");
            
            if (dllFiles.Length == 0)
            {
                VPetLLMUtils.Logger.Log($"LoadPlugins: No DLL files found. Checking for case-sensitive issues...");
                // 尝试不同的搜索模式
                var allDllFiles = Directory.GetFiles(pluginDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.ToLowerInvariant().EndsWith(".dll"))
                    .ToArray();
                VPetLLMUtils.Logger.Log($"LoadPlugins: Case-insensitive search found {allDllFiles.Length} .dll files");
                
                if (allDllFiles.Length > 0)
                {
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Using case-insensitive results");
                    dllFiles = allDllFiles;
                }
            }
            
            foreach (var file in dllFiles)
            {
                VPetLLMUtils.Logger.Log($"LoadPlugins: Processing file: {Path.GetFileName(file)} (Full path: {file})");
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
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Successfully loaded assembly from {Path.GetFileName(file)}");

                    var types = assembly.GetTypes();
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Assembly contains {types.Length} types");

                    bool foundCompatiblePlugin = false;
                    foreach (var type in types)
                    {
                        // Check for new-style plugins (IVPetLLMPlugin)
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            VPetLLMUtils.Logger.Log($"LoadPlugins: Found IVPetLLMPlugin implementation: {type.FullName}");
                            var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                            plugin.FilePath = file;

                            // 检查是否已存在同名插件
                            var existingPlugin = Plugins.FirstOrDefault(p => p.Name == plugin.Name);
                            if (existingPlugin is not null)
                            {
                                VPetLLMUtils.Logger.Log($"Warning: Plugin with name '{plugin.Name}' already exists. Skipping duplicate from {file}");
                                VPetLLMUtils.Logger.Log($"Existing plugin from: {existingPlugin.FilePath}");
                                VPetLLMUtils.Logger.Log($"Duplicate plugin from: {file}");
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
                                if (chatCore is not null)
                                {
                                    // Convert new-style plugin to legacy interface for chatCore
                                    var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(plugin);
                                    chatCore.AddPlugin(legacyPlugin);
                                }
                            }
                            VPetLLMUtils.Logger.Log($"Loaded plugin: {plugin.Name}, Enabled: {plugin.Enabled}");
                            foundCompatiblePlugin = true;
                        }
                    }
                    
                    // 如果没有找到兼容的插件类型，将其标记为失败（可能是旧版本插件）
                    if (!foundCompatiblePlugin)
                    {
                        VPetLLMUtils.Logger.Log($"LoadPlugins: No compatible plugin types found in {Path.GetFileName(file)} - likely an outdated plugin");
                        FailedPlugins.Add(new FailedPlugin
                        {
                            Name = Path.GetFileNameWithoutExtension(file),
                            FilePath = file,
                            Error = new InvalidOperationException("插件使用旧版接口，需要更新"),
                            Description = "此插件使用旧版接口编译，与当前版本不兼容。请更新插件。"
                        });
                        VPetLLMUtils.Logger.Log($"LoadPlugins: Added outdated plugin to FailedPlugins: {Path.GetFileNameWithoutExtension(file)}");
                    }
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"Failed to load plugin {file}: {ex.Message}");
                    FailedPlugins.Add(new FailedPlugin
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = file,
                        Error = ex,
                        Description = ex.Message
                    });
                    VPetLLMUtils.Logger.Log($"LoadPlugins: Added failed plugin '{Path.GetFileNameWithoutExtension(file)}'. Total failed plugins: {FailedPlugins.Count}");
                }
            }
            
            VPetLLMUtils.Logger.Log($"LoadPlugins: Completed. Loaded plugins: {Plugins.Count}, Failed plugins: {FailedPlugins.Count}");
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
                    // 如果存在重复名称，使用最后一个插件的状�?
                    pluginStates[plugin.Name] = plugin.Enabled;
                }
            }

            File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
            VPetLLMUtils.Logger.Log($"Saved plugin states for {pluginStates.Count} plugins");
        }

        public static async Task<bool> UnloadAndTryDeletePlugin(IVPetLLMPlugin plugin, IChatCore chatCore)
        {
            string filePath = plugin.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                VPetLLMUtils.Logger.Log($"Plugin file path is invalid or does not exist for {plugin.Name}: '{filePath}'");
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

            if (chatCore is not null)
            {
                // Convert to legacy interface for chatCore
                var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(plugin);
                chatCore.RemovePlugin(legacyPlugin);
            }
            plugin.Unload();
            Plugins.Remove(plugin);

            if (_pluginContexts.TryGetValue(filePath, out var context))
            {
                var weakContext = new WeakReference(context);
                context.Unload();
                _pluginContexts.Remove(filePath);
                VPetLLMUtils.Logger.Log($"Unloaded AssemblyLoadContext for {plugin.Name}. Waiting for garbage collection...");

                // Wait for the context to be actually collected
                for (int i = 0; weakContext.IsAlive && (i < 10); i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(100);
                }

                if (weakContext.IsAlive)
                {
                    VPetLLMUtils.Logger.Log($"Warning: AssemblyLoadContext for {plugin.Name} could not be fully unloaded. File handles may remain locked.");
                }
                else
                {
                    VPetLLMUtils.Logger.Log($"AssemblyLoadContext for {plugin.Name} has been garbage collected.");
                }
            }

            // Retry deleting the shadow copy directory
            if (_shadowCopyDirectories.TryGetValue(filePath, out var shadowDir) && Directory.Exists(shadowDir))
            {
                bool shadowDeleted = false;
                bool loggedError = false;
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.Delete(shadowDir, true);
                        _shadowCopyDirectories.Remove(filePath);
                        if (loggedError)
                        {
                            VPetLLMUtils.Logger.Log($"Successfully deleted shadow copy directory for {plugin.Name} after {i + 1} attempts");
                        }
                        shadowDeleted = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 只记录第一次和最后一次失�?
                        if (i == 0 || i == 4)
                        {
                            VPetLLMUtils.Logger.Log($"Attempt {i + 1}/5 to delete shadow copy for {plugin.Name} failed: {ex.Message}");
                            loggedError = true;
                        }
                        await Task.Delay(200);
                    }
                }
                if (!shadowDeleted)
                {
                    VPetLLMUtils.Logger.Log($"Failed to delete shadow copy directory for {plugin.Name}, will retry on next startup");
                }
            }

            // Retry deleting the original plugin file
            return await DeletePluginFile(filePath);
        }
        public static void UnloadAllPlugins(IChatCore chatCore)
        {
            if (chatCore is not null)
            {
                foreach (var p in Plugins.ToList())
                {
                    // Convert to legacy interface for chatCore
                    var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(p);
                    chatCore.RemovePlugin(legacyPlugin);
                    p.Unload();
                }
            }
            Plugins.Clear();

            // 卸载所有程序集上下�?
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
                Thread.Sleep(100);
            }

            // 异步清理影子拷贝目录，避免阻塞UI
            var shadowDirs = _shadowCopyDirectories.Values.ToList();
            _shadowCopyDirectories.Clear();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // 等待1秒确保文件句柄释�?

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
                VPetLLMUtils.Logger.Log($"Imported plugin: {fileName}");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Failed to import plugin {fileName}: {ex.Message}");
            }
        }
        public static async Task<bool> DeletePluginFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                VPetLLMUtils.Logger.Log($"Invalid or non-existent plugin file path: '{filePath}'");
                return false;
            }

            bool loggedRetry = false;
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

                    // 只在重试后成功时记录日志
                    if (loggedRetry)
                    {
                        VPetLLMUtils.Logger.Log($"Successfully deleted plugin files after {i + 1} attempts: {Path.GetFileName(filePath)}");
                    }
                    else
                    {
                        VPetLLMUtils.Logger.Log($"Successfully deleted plugin files: {Path.GetFileName(filePath)}");
                    }
                    return true;
                }
                catch (IOException)
                {
                    // 只记录第一次和最后一次失�?
                    if (i == 0 || i == 4)
                    {
                        VPetLLMUtils.Logger.Log($"Attempt {i + 1}/5 to delete {Path.GetFileName(filePath)} failed (file locked)");
                        loggedRetry = true;
                    }
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"Error deleting plugin {Path.GetFileName(filePath)}: {ex.Message}");
                    return false;
                }
            }

            VPetLLMUtils.Logger.Log($"Failed to delete plugin after 5 attempts: {Path.GetFileName(filePath)}");
            return false;
        }

        public static async Task<bool> DeletePluginByName(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName))
            {
                VPetLLMUtils.Logger.Log($"DeletePluginByName: Plugin name is null or empty");
                return false;
            }

            VPetLLMUtils.Logger.Log($"DeletePluginByName: Attempting to locate and delete plugin: {pluginName}");

            try
            {
                var pluginDir = PluginPath;
                if (!Directory.Exists(pluginDir))
                {
                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Plugin directory does not exist: {pluginDir}");
                    return false;
                }

                // 查找所有可能的插件文件
                var allPluginFiles = Directory.GetFiles(pluginDir, "*.dll");
                var candidateFiles = new List<string>();

                // 方法1: 文件名匹配（最常见的情况）
                var exactMatch = allPluginFiles.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                if (exactMatch is not null)
                {
                    candidateFiles.Add(exactMatch);
                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Found exact filename match: {exactMatch}");
                }

                // 方法2: 部分匹配（处理带版本号或后缀的情况）
                var partialMatches = allPluginFiles.Where(f =>
                    Path.GetFileNameWithoutExtension(f).Contains(pluginName, StringComparison.OrdinalIgnoreCase) &&
                    !candidateFiles.Contains(f)).ToList();

                if (partialMatches.Any())
                {
                    candidateFiles.AddRange(partialMatches);
                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Found {partialMatches.Count} partial filename matches");
                }

                // 方法3: 检查已加载的插件列�?
                var loadedPlugin = Plugins.FirstOrDefault(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                if (loadedPlugin is not null && !string.IsNullOrEmpty(loadedPlugin.FilePath))
                {
                    // 验证FilePath是否是有效的文件路径（而不是目录）
                    string validFilePath = loadedPlugin.FilePath;
                    if (Directory.Exists(validFilePath))
                    {
                        // FilePath是目录，尝试在该目录中根据插件名称查找匹配的dll文件
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Plugin FilePath is a directory, searching for matching dll: {validFilePath}");
                        var filesInDir = Directory.GetFiles(validFilePath, "*.dll");

                        // 尝试精确匹配插件名称
                        var matchedFile = filesInDir.FirstOrDefault(f =>
                            Path.GetFileNameWithoutExtension(f).Equals(pluginName, StringComparison.OrdinalIgnoreCase));

                        // 如果没有精确匹配，尝试部分匹�?
                        if (matchedFile is null)
                        {
                            matchedFile = filesInDir.FirstOrDefault(f =>
                                Path.GetFileNameWithoutExtension(f).Contains(pluginName, StringComparison.OrdinalIgnoreCase));
                        }

                        if (matchedFile is not null)
                        {
                            validFilePath = matchedFile;
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: Found matching dll in directory: {validFilePath}");
                        }
                        else
                        {
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: No matching dll found in directory for plugin: {pluginName}");
                            validFilePath = null;
                        }
                    }
                    else if (!File.Exists(validFilePath))
                    {
                        // FilePath既不是目录也不是文件，可能是无效路径
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Plugin FilePath is invalid: {validFilePath}");
                        validFilePath = null;
                    }

                    if (!string.IsNullOrEmpty(validFilePath) && !candidateFiles.Contains(validFilePath))
                    {
                        candidateFiles.Add(validFilePath);
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Found plugin in loaded plugins list: {validFilePath}");
                    }
                }

                // 方法4: 检查失败的插件列表
                var failedPlugin = FailedPlugins.FirstOrDefault(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                if (failedPlugin is not null && !string.IsNullOrEmpty(failedPlugin.FilePath))
                {
                    // 验证FilePath是否是有效的文件路径（而不是目录）
                    string validFilePath = failedPlugin.FilePath;
                    if (Directory.Exists(validFilePath))
                    {
                        // FilePath是目录，尝试在该目录中根据插件名称查找匹配的dll文件
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Failed plugin FilePath is a directory, searching for matching dll: {validFilePath}");
                        var filesInDir = Directory.GetFiles(validFilePath, "*.dll");

                        // 尝试精确匹配插件名称
                        var matchedFile = filesInDir.FirstOrDefault(f =>
                            Path.GetFileNameWithoutExtension(f).Equals(pluginName, StringComparison.OrdinalIgnoreCase));

                        // 如果没有精确匹配，尝试部分匹�?
                        if (matchedFile is null)
                        {
                            matchedFile = filesInDir.FirstOrDefault(f =>
                                Path.GetFileNameWithoutExtension(f).Contains(pluginName, StringComparison.OrdinalIgnoreCase));
                        }

                        if (matchedFile is not null)
                        {
                            validFilePath = matchedFile;
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: Found matching dll in failed plugin directory: {validFilePath}");
                        }
                        else
                        {
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: No matching dll found in directory for failed plugin: {pluginName}");
                            validFilePath = null;
                        }
                    }
                    else if (!File.Exists(validFilePath))
                    {
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Failed plugin FilePath is invalid: {validFilePath}");
                        validFilePath = null;
                    }

                    if (!string.IsNullOrEmpty(validFilePath) && !candidateFiles.Contains(validFilePath))
                    {
                        candidateFiles.Add(validFilePath);
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Found plugin in failed plugins list: {validFilePath}");
                    }
                }

                // 方法5: 读取plugins.json配置文件查找可能的文件名
                var configFile = Path.Combine(pluginDir, "plugins.json");
                if (File.Exists(configFile))
                {
                    try
                    {
                        var pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                        if (pluginStates is not null && pluginStates.ContainsKey(pluginName))
                        {
                            // 插件在配置中存在，尝试常见的文件名模�?
                            var possibleNames = new[]
                            {
                                $"{pluginName}.dll",
                                $"VPetLLM.{pluginName}.dll",
                                $"{pluginName}.Plugin.dll"
                            };

                            foreach (var possibleName in possibleNames)
                            {
                                var possiblePath = Path.Combine(pluginDir, possibleName);
                                if (File.Exists(possiblePath) && !candidateFiles.Contains(possiblePath))
                                {
                                    candidateFiles.Add(possiblePath);
                                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Found plugin via config-based name pattern: {possiblePath}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Error reading plugins.json: {ex.Message}");
                    }
                }

                // 尝试删除找到的所有候选文�?
                bool anyDeleted = false;
                foreach (var filePath in candidateFiles.Distinct())
                {
                    if (File.Exists(filePath))
                    {
                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Attempting to delete: {filePath}");
                        bool deleted = await DeletePluginFile(filePath);
                        if (deleted)
                        {
                            anyDeleted = true;
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: Successfully deleted: {filePath}");

                            // 从配置文件中移除
                            if (File.Exists(configFile))
                            {
                                try
                                {
                                    var pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                                    if (pluginStates is not null && pluginStates.Remove(pluginName))
                                    {
                                        File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
                                        VPetLLMUtils.Logger.Log($"DeletePluginByName: Removed plugin from config: {pluginName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Error updating plugins.json: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            VPetLLMUtils.Logger.Log($"DeletePluginByName: Failed to delete: {filePath}");
                        }
                    }
                }

                if (anyDeleted)
                {
                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Successfully deleted plugin: {pluginName}");
                    return true;
                }
                else
                {
                    VPetLLMUtils.Logger.Log($"DeletePluginByName: Could not find or delete any files for plugin: {pluginName}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"DeletePluginByName: Unexpected error: {ex.Message}");
                return false;
            }
        }
        public static async Task<bool> UpdatePlugin(string pluginFilePath, IChatCore chatCore)
        {
            if (string.IsNullOrEmpty(pluginFilePath) || !File.Exists(pluginFilePath))
            {
                VPetLLMUtils.Logger.Log($"Plugin file path is invalid or does not exist: '{pluginFilePath}'");
                return false;
            }

            try
            {
                // 查找需要更新的插件
                var existingPlugin = Plugins.FirstOrDefault(p => p.FilePath == pluginFilePath);
                string pluginName = null;

                if (existingPlugin is not null)
                {
                    pluginName = existingPlugin.Name;
                    VPetLLMUtils.Logger.Log($"Found existing plugin to update: {pluginName}");

                    // 先卸载旧版本插件
                    if (chatCore is not null)
                    {
                        // Convert to legacy interface for chatCore
                        var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(existingPlugin);
                        chatCore.RemovePlugin(legacyPlugin);
                    }
                    existingPlugin.Unload();
                    Plugins.Remove(existingPlugin);

                    // 卸载旧的 AssemblyLoadContext
                    if (_pluginContexts.TryGetValue(pluginFilePath, out var context))
                    {
                        var weakContext = new WeakReference(context);
                        context.Unload();
                        _pluginContexts.Remove(pluginFilePath);
                        VPetLLMUtils.Logger.Log($"Unloaded AssemblyLoadContext for {pluginName}");

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
                            VPetLLMUtils.Logger.Log($"Cleaned up shadow copy directory for {pluginName}");
                        }
                        catch (Exception ex)
                        {
                            VPetLLMUtils.Logger.Log($"Failed to clean up shadow copy directory: {ex.Message}");
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

                VPetLLMUtils.Logger.Log($"Successfully updated plugin: {pluginFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Failed to update plugin {pluginFilePath}: {ex.Message}");
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

                    // 查找是否有其他插件实例使用相同的插件�?
                    var duplicatePlugin = Plugins.FirstOrDefault(p => p.Name == pluginName && p.FilePath == file);
                    if (duplicatePlugin is not null)
                    {
                        VPetLLMUtils.Logger.Log($"Found duplicate plugin file for '{pluginName}': {file}");
                        VPetLLMUtils.Logger.Log($"Removing duplicate plugin instance...");

                        // 卸载重复的插�?
                        if (chatCore is not null)
                        {
                            // Convert to legacy interface for chatCore
                            var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(duplicatePlugin);
                            chatCore.RemovePlugin(legacyPlugin);
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
                                VPetLLMUtils.Logger.Log($"Failed to clean up shadow copy directory for duplicate: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Error during duplicate plugin cleanup: {ex.Message}");
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

                // 读取插件状态配�?
                var pluginDir = PluginPath;
                var configFile = Path.Combine(pluginDir, "plugins.json");
                var pluginStates = new Dictionary<string, bool>();
                if (File.Exists(configFile))
                {
                    pluginStates = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(configFile));
                }

                foreach (var type in assembly.GetTypes())
                {
                    // Check for new-style plugins (IVPetLLMPlugin)
                    if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                    {
                        var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                        plugin.FilePath = pluginFilePath;

                        // 在单个插件加载中，不应该有重复插件，因为我们已经在更新前移除了旧插件
                        // 如果仍然存在重复，说明有其他同名插件文件，这是一个问题
                        var existingPlugin = Plugins.FirstOrDefault(p => p.Name == plugin.Name);
                        if (existingPlugin is not null)
                        {
                            VPetLLMUtils.Logger.Log($"Critical: Plugin with name '{plugin.Name}' already exists during single plugin load!");
                            VPetLLMUtils.Logger.Log($"  Existing plugin from: {existingPlugin.FilePath}");
                            VPetLLMUtils.Logger.Log($"  New plugin from: {pluginFilePath}");
                            VPetLLMUtils.Logger.Log($"  This indicates multiple plugin files contain the same plugin name.");

                            // 在更新场景下，我们应该替换现有插件而不是跳过
                            VPetLLMUtils.Logger.Log($"  Removing existing plugin and loading the new one...");
                            if (chatCore is not null)
                            {
                                // Convert to legacy interface for chatCore
                                var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(existingPlugin);
                                chatCore.RemovePlugin(legacyPlugin);
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

                        if (plugin.Enabled && chatCore is not null)
                        {
                            // Convert to legacy interface for chatCore
                            var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(plugin);
                            chatCore.AddPlugin(legacyPlugin);
                        }

                        VPetLLMUtils.Logger.Log($"Plugin loaded: {plugin.Name} from {pluginFilePath}");
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Error loading plugin {pluginFilePath}: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private static async Task CleanupShadowDirectory(string shadowDir)
        {
            if (string.IsNullOrEmpty(shadowDir) || !Directory.Exists(shadowDir))
                return;

            // 重试删除影子拷贝目录，最多尝�?�?
            bool loggedFirstAttempt = false;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(shadowDir, true);
                    // 只在第一次尝试失败后才记录成功日�?
                    if (loggedFirstAttempt)
                    {
                        VPetLLMUtils.Logger.Log($"Successfully deleted shadow copy directory after {attempt} attempts: {Path.GetFileName(shadowDir)}");
                    }
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    // 只记录第一次和最后一次尝�?
                    if (attempt == 1 || attempt == 5)
                    {
                        VPetLLMUtils.Logger.Log($"Attempt {attempt}/5: Access denied when deleting shadow directory {Path.GetFileName(shadowDir)}");
                        loggedFirstAttempt = true;
                    }
                    await Task.Delay(2000 * attempt); // 递增等待时间
                }
                catch (DirectoryNotFoundException)
                {
                    // 目录已经不存在，静默返回
                    return;
                }
                catch (IOException ex)
                {
                    // 只记录第一次和最后一次尝�?
                    if (attempt == 1 || attempt == 5)
                    {
                        VPetLLMUtils.Logger.Log($"Attempt {attempt}/5: IO error when deleting shadow directory {Path.GetFileName(shadowDir)}: {ex.Message}");
                        loggedFirstAttempt = true;
                    }
                    await Task.Delay(1000 * attempt);
                }
                catch (Exception ex)
                {
                    // 只记录第一次和最后一次尝�?
                    if (attempt == 1 || attempt == 5)
                    {
                        VPetLLMUtils.Logger.Log($"Attempt {attempt}/5: Error deleting shadow directory {Path.GetFileName(shadowDir)}: {ex.Message}");
                        loggedFirstAttempt = true;
                    }

                    if (attempt == 5)
                    {
                        VPetLLMUtils.Logger.Log($"Failed to delete shadow copy directory after 5 attempts, will retry on next startup");
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
                VPetLLMUtils.Logger.Log($"Failed to record failed cleanup: {ex.Message}");
            }
        }

        public static string GetFileSha256(string filePath)
        {
            if (!File.Exists(filePath))
            {
                VPetLLMUtils.Logger.Log($"GetFileSha256: File does not exist: {filePath}");
                return null;
            }

            // 重试机制，防止文件被锁定
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var sha256 = SHA256.Create())
                    {
                        using (var stream = File.OpenRead(filePath))
                        {
                            var hash = sha256.ComputeHash(stream);
                            var result = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            VPetLLMUtils.Logger.Log($"GetFileSha256: Successfully calculated hash for {Path.GetFileName(filePath)}: {result}");
                            return result;
                        }
                    }
                }
                catch (IOException ex) when (attempt < 2)
                {
                    VPetLLMUtils.Logger.Log($"GetFileSha256: Attempt {attempt + 1} failed for {filePath}: {ex.Message}. Retrying...");
                    Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"GetFileSha256: Error calculating hash for {filePath}: {ex.Message}");
                    return null;
                }
            }

            VPetLLMUtils.Logger.Log($"GetFileSha256: Failed to calculate hash after 3 attempts for {filePath}");
            return null;
        }
    }
}
