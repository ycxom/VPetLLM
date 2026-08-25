using System.Runtime.Loader;
using System.Security.Cryptography;
using LegacyPlugin = VPetLLM.Core;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Plugin
{
    public static class PluginManager
    {
        public static List<IVPetLLMPlugin> Plugins { get; } = new List<IVPetLLMPlugin>();
        public static List<FailedPlugin> FailedPlugins { get; } = new List<FailedPlugin>();
        private static readonly Dictionary<string, AssemblyLoadContext> _pluginContexts = new();
        private static readonly Dictionary<string, string> _shadowCopyDirectories = new();

        // SHA256 结果缓存。插件列表每刷新一次就要把每个插件 DLL 从头哈一遍，
        // 而这活儿是在 UI 线程上干的——插件一多、刷新一频繁就直接卡住窗口。
        // 用 (长度, 最后写入时间) 当版本戳：文件被换掉戳就变，自然失效，不需要手动清。
        private static readonly Dictionary<string, (long Length, long TicksUtc, string Hash)> _sha256Cache =
            new(StringComparer.OrdinalIgnoreCase);

        // UpdatePlugin 会连着改 Plugins / _pluginContexts / _shadowCopyDirectories 三个共享集合，
        // 中间还夹着 await（卸载 ALC、等 GC、删影子目录）。两个更新叠在一起跑，
        // 这些没上锁的 List/Dictionary 会被写坏——轻则插件列表错乱，重则字典内部成环、查找时死循环。
        // 用异步闸串起来：等的时候不占线程，也就不会把 UI 线程堵死。
        private static readonly SemaphoreSlim _updateGate = new(1, 1);
        public static string PluginPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");

        // 影子拷贝的落地目录。
        //
        // 这里刻意不用 %TEMP%：从 %TEMP% 下的随机名目录加载 DLL 是落地器(dropper)
        // 最典型的行为特征，杀软启发式引擎对这条路径给的权重很高，会把宿主连同
        // 插件一起判成木马。换到应用自己的 LocalAppData 子目录后，行为语义变成
        // "程序在自己的缓存目录里工作"，静态评分显著下降。
        //
        // 代价是 Windows 不再帮忙兜底清理，必须自己扫孤儿目录，见 SweepOrphanedShadowCopies。
        public static string PluginCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPetLLM", "PluginCache");

        /// <summary>建立一个新的影子拷贝目录。</summary>
        private static string CreateShadowCopyDirectory()
        {
            var dir = Path.Combine(PluginCachePath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// 清掉上次运行残留的影子拷贝目录。
        ///
        /// 正常退出会走 CleanupShadowDirectory，但崩溃和强杀不会。以前放 %TEMP% 时
        /// 靠系统定期清理兜底，改到 LocalAppData 之后没人兜底，不扫就会无限堆积
        /// （每次崩溃泄漏一整套插件 DLL）。
        ///
        /// 多开场景下别的实例可能正占着某个目录，所以先用独占打开探一次：探不到就
        /// 整个目录跳过，免得把对方的 pdb 删掉只留个半残目录。删不掉的一律忽略，
        /// 下次启动再试。
        /// </summary>
        private static void SweepOrphanedShadowCopies()
        {
            try
            {
                if (!Directory.Exists(PluginCachePath))
                    return;

                foreach (var dir in Directory.EnumerateDirectories(PluginCachePath))
                {
                    if (IsShadowDirectoryInUse(dir))
                        continue;

                    try { Directory.Delete(dir, true); }
                    catch { /* 被占用或权限不足，下次启动再试 */ }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Sweeping orphaned plugin cache failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 一次性清掉历史版本残留在 %TEMP%\VPetLLM_Plugins 下的影子拷贝。
        ///
        /// 影子拷贝改到 LocalAppData 之前是落在 %TEMP% 的，退出时清理失败的目录就
        /// 一直留在那儿——实测老机器上能攒到上千个目录、几十 MB，而新版本已经不再
        /// 往那里写，所以没有任何代码会再回头收拾它们。
        ///
        /// 这里做的是迁移清扫：清空后连父目录一起删掉，之后每次启动只剩一次
        /// Directory.Exists 的开销。仍在跑的旧版实例占着的目录会删失败，忽略即可。
        /// </summary>
        private static void SweepLegacyTempShadowCopies()
        {
            try
            {
                var legacyRoot = Path.Combine(Path.GetTempPath(), "VPetLLM_Plugins");
                if (!Directory.Exists(legacyRoot)) return;

                var removed = 0;
                foreach (var dir in Directory.EnumerateDirectories(legacyRoot))
                {
                    if (IsShadowDirectoryInUse(dir)) continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch { /* 下次启动再试 */ }
                }

                // 全清干净了就把根目录也去掉，下次启动直接短路
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
                    {
                        Directory.Delete(legacyRoot);
                    }
                }
                catch { }

                if (removed > 0)
                {
                    VPetLLMUtils.Logger.Log(
                        $"Cleaned {removed} legacy shadow copy directories from %TEMP%\\VPetLLM_Plugins");
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Sweeping legacy plugin cache failed: {ex.Message}");
            }
        }

        /// <summary>探测影子目录里的 DLL 是否正被本机其他 VPet 实例映射着。</summary>
        private static bool IsShadowDirectoryInUse(string shadowDir)
        {
            try
            {
                foreach (var dll in Directory.EnumerateFiles(shadowDir, "*.dll"))
                {
                    using var probe = File.Open(dll, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                return false;
            }
            catch (IOException)
            {
                return true;    // 已被映射，是别的实例在用
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                return false;   // 目录本身有问题，交给外层 Delete 收拾
            }
        }

        public static void LoadPlugins(IChatCore chatCore)
        {
            SweepOrphanedShadowCopies();
            SweepLegacyTempShadowCopies();

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

            var dllFiles = Directory.GetFiles(pluginDir, "*.dll");

            if (dllFiles.Length == 0)
            {
                var allDllFiles = Directory.GetFiles(pluginDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.ToLowerInvariant().EndsWith(".dll"))
                    .ToArray();

                if (allDllFiles.Length > 0)
                {
                    dllFiles = allDllFiles;
                }
            }

            // 优化：并行加载插件，减少启动时间
            // 使用 Parallel.ForEach + 线程安全的锁，支持多个插件同时加载
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = GetLoadParallelism(dllFiles.Length)
            };

            Parallel.ForEach(dllFiles, parallelOptions, file =>
            {
                try
                {
                    var context = new AssemblyLoadContext($"{Path.GetFileNameWithoutExtension(file)}_{Guid.NewGuid()}", isCollectible: true);

                    var shadowCopyDir = CreateShadowCopyDirectory();
                    var shadowCopiedFile = Path.Combine(shadowCopyDir, Path.GetFileName(file));
                    File.Copy(file, shadowCopiedFile, true);

                    lock (_pluginContexts)  // 线程安全：保护字典访问
                    {
                        _shadowCopyDirectories[file] = shadowCopyDir;
                    }

                    var pdbFile = Path.ChangeExtension(file, ".pdb");
                    if (File.Exists(pdbFile))
                    {
                        var shadowCopiedPdb = Path.ChangeExtension(shadowCopiedFile, ".pdb");
                        File.Copy(pdbFile, shadowCopiedPdb, true);
                    }

                    var assembly = context.LoadFromAssemblyPath(shadowCopiedFile);

                    lock (_pluginContexts)  // 线程安全：保护字典访问
                    {
                        _pluginContexts[file] = context;
                    }

                    var types = assembly.GetTypes();

                    bool foundCompatiblePlugin = false;
                    foreach (var type in types)
                    {
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            var plugin = (IVPetLLMPlugin)Activator.CreateInstance(type);
                            plugin.FilePath = file;

                            lock (Plugins)  // 线程安全：检查重复
                            {
                                var existingPlugin = Plugins.FirstOrDefault(p => p.Name == plugin.Name);
                                if (existingPlugin is not null)
                                {
                                    continue;
                                }
                            }

                            if (plugin is IPluginWithData pluginWithData)
                            {
                                var pluginDataDir = Path.Combine(pluginDir, "PluginData", plugin.Name);
                                Directory.CreateDirectory(pluginDataDir);
                                pluginWithData.PluginDataDir = pluginDataDir;
                            }
                            plugin.Enabled = pluginStates.TryGetValue(plugin.Name, out var enabled) ? enabled : true;

                            lock (Plugins)  // 线程安全：添加到列表
                            {
                                Plugins.Add(plugin);
                            }

                            if (plugin.Enabled)
                            {
                                PluginLifecycleGuard.SafeInitialize(plugin, VPetLLM.Instance);
                                if (chatCore is not null)
                                {
                                    var legacyPlugin = LegacyPlugin.PluginCompatibility.ToLegacy(plugin);
                                    chatCore.AddPlugin(legacyPlugin);
                                }
                            }
                            foundCompatiblePlugin = true;
                        }
                    }

                    if (!foundCompatiblePlugin)
                    {
                        lock (FailedPlugins)  // 线程安全：记录失败的插件
                        {
                            FailedPlugins.Add(new FailedPlugin
                            {
                                Name = Path.GetFileNameWithoutExtension(file),
                                FilePath = file,
                                Error = new InvalidOperationException("插件使用旧版接口，需要更新"),
                                Description = "此插件使用旧版接口编译，与当前版本不兼容。请更新插件。"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (FailedPlugins)  // 线程安全：记录异常
                    {
                        FailedPlugins.Add(new FailedPlugin
                        {
                            Name = Path.GetFileNameWithoutExtension(file),
                            FilePath = file,
                            Error = ex,
                            Description = ex.Message
                        });
                    }
                }
            });
        }

        private static int GetLoadParallelism(int pluginCount)
        {
            return Math.Max(1, Math.Min(pluginCount, Environment.ProcessorCount));
        }

        public static void SavePluginStates()
        {
            var configFile = Path.Combine(PluginPath, "plugins.json");

            var pluginStates = new Dictionary<string, bool>();
            foreach (var plugin in Plugins)
            {
                if (!string.IsNullOrEmpty(plugin.Name))
                {
                    pluginStates[plugin.Name] = plugin.Enabled;
                }
            }

            File.WriteAllText(configFile, JsonConvert.SerializeObject(pluginStates, Formatting.Indented));
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
            await PluginLifecycleGuard.SafeUnloadAsync(plugin);
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
            var fileDeleted = await DeletePluginFile(filePath);

            // 卸载后可选清除插件数据：仅当插件用到了 PluginData 且目录非空时询问
            TryPromptDeletePluginData(plugin);

            return fileDeleted;
        }

        /// <summary>
        /// 卸载插件后，若该插件在 PluginData 下存有数据，弹窗询问用户是否一并删除。
        /// 对所有 IPluginWithData 插件通用。删除失败不影响卸载结果。
        /// </summary>
        private static void TryPromptDeletePluginData(IVPetLLMPlugin plugin)
        {
            try
            {
                if (plugin is not IPluginWithData)
                    return;

                var dataDir = Path.Combine(PluginPath, "PluginData", plugin.Name);
                if (!Directory.Exists(dataDir))
                    return;
                // 目录为空则无需询问，直接静默删除空壳
                if (!Directory.EnumerateFileSystemEntries(dataDir).Any())
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                    return;
                }

                var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
                Action prompt = () =>
                {
                    var result = global::System.Windows.MessageBox.Show(
                        $"插件 \"{plugin.Name}\" 已卸载。是否同时删除它保存的数据？\n\n路径: {dataDir}\n\n选择\"否\"将保留数据，重新安装该插件后可继续使用。",
                        "删除插件数据",
                        global::System.Windows.MessageBoxButton.YesNo,
                        global::System.Windows.MessageBoxImage.Question);

                    if (result == global::System.Windows.MessageBoxResult.Yes)
                    {
                        try
                        {
                            Directory.Delete(dataDir, true);
                            VPetLLMUtils.Logger.Log($"Deleted plugin data directory for {plugin.Name}: {dataDir}");
                        }
                        catch (Exception ex)
                        {
                            VPetLLMUtils.Logger.Log($"Failed to delete plugin data for {plugin.Name}: {ex.Message}");
                            global::System.Windows.MessageBox.Show(
                                $"删除数据失败: {ex.Message}\n可稍后手动删除该文件夹。",
                                "删除插件数据", global::System.Windows.MessageBoxButton.OK, global::System.Windows.MessageBoxImage.Warning);
                        }
                    }
                };

                if (dispatcher is not null && !dispatcher.CheckAccess())
                    dispatcher.Invoke(prompt);
                else
                    prompt();
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"TryPromptDeletePluginData error for {plugin.Name}: {ex.Message}");
            }
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
                    PluginLifecycleGuard.SafeUnload(p);
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

            // 同一时刻只允许一个更新在改共享集合。见 _updateGate 上的说明。
            await _updateGate.WaitAsync();
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
                    await PluginLifecycleGuard.SafeUnloadAsync(existingPlugin);
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
            finally
            {
                _updateGate.Release();
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
                        PluginLifecycleGuard.SafeUnload(duplicatePlugin);
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

                var shadowCopyDir = CreateShadowCopyDirectory();
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
                            PluginLifecycleGuard.SafeUnload(existingPlugin);
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
                Directory.CreateDirectory(PluginCachePath);
                var failedCleanupFile = Path.Combine(PluginCachePath, "FailedCleanup.log");
                File.AppendAllText(failedCleanupFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {directory}\n");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Failed to record failed cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// 主动作废某个文件的哈希缓存。
        /// 用在「刚把新 DLL 写下去、马上要校验它」这种地方：那次校验是安全检查，
        /// 必须真读文件，不能拿缓存糊弄——两次写入间隔短且长度恰好相同时，
        /// (长度, 最后写入时间) 这个版本戳理论上认不出变化（系统时钟粒度约 15ms）。
        /// </summary>
        public static void InvalidateSha256Cache(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            lock (_sha256Cache) { _sha256Cache.Remove(filePath); }
        }

        public static string GetFileSha256(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                // 文件没了，顺手把缓存条目也清掉，别让字典跟着删掉的插件一直长。
                if (!string.IsNullOrEmpty(filePath))
                {
                    lock (_sha256Cache) { _sha256Cache.Remove(filePath); }
                }
                return null;
            }

            // 先看缓存：同一个文件只要长度和最后写入时间没变，就还是上次那个哈希。
            // 更新插件会重写文件，两者必变，所以不会拿到过期结果。
            long length, ticks;
            try
            {
                var info = new FileInfo(filePath);
                length = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception)
            {
                length = -1;
                ticks = -1;
            }

            if (length >= 0)
            {
                lock (_sha256Cache)
                {
                    if (_sha256Cache.TryGetValue(filePath, out var cached) &&
                        cached.Length == length && cached.TicksUtc == ticks)
                    {
                        return cached.Hash;
                    }
                }
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

                            if (length >= 0)
                            {
                                lock (_sha256Cache)
                                {
                                    _sha256Cache[filePath] = (length, ticks, result);
                                }
                            }

                            return result;
                        }
                    }
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(200);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }
    }
}
