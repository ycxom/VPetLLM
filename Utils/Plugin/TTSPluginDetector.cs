using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils.Plugin
{
    /// <summary>
    /// TTS 插件检测结果
    /// </summary>
    public class TTSPluginDetectionResult
    {
        /// <summary>
        /// 插件是否存在
        /// </summary>
        public bool PluginExists { get; set; }

        /// <summary>
        /// 插件是否启用
        /// </summary>
        public bool PluginEnabled { get; set; }

        /// <summary>
        /// 插件版本（如果可获取）
        /// </summary>
        public string PluginVersion { get; set; } = "";

        /// <summary>
        /// 检测时间
        /// </summary>
        public DateTime DetectionTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 是否应该禁用内置 TTS
        /// </summary>
        public bool ShouldDisableBuiltInTTS => PluginExists && PluginEnabled;
    }

    /// <summary>
    /// 其他 TTS 插件检测结果（多个插件）
    /// </summary>
    public class OtherTTSPluginsDetectionResult
    {
        /// <summary>
        /// 检测到的其他 TTS 插件列表
        /// </summary>
        public Dictionary<string, TTSPluginDetectionResult> DetectedPlugins { get; set; } = new Dictionary<string, TTSPluginDetectionResult>();

        /// <summary>
        /// 是否检测到其他已启用的 TTS 插件
        /// </summary>
        public bool HasOtherEnabledTTSPlugin
        {
            get
            {
                foreach (var plugin in DetectedPlugins.Values)
                {
                    if (plugin.ShouldDisableBuiltInTTS)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 检测到的已启用插件名称列表（用于日志）
        /// </summary>
        public string EnabledPluginNames
        {
            get
            {
                var names = new List<string>();
                foreach (var kvp in DetectedPlugins)
                {
                    if (kvp.Value.ShouldDisableBuiltInTTS)
                        names.Add(kvp.Key);
                }
                return string.Join(", ", names);
            }
        }
    }

    /// <summary>
    /// 缓存的检测结果
    /// </summary>
    public class CachedDetectionResult
    {
        /// <summary>
        /// 检测结果
        /// </summary>
        public TTSPluginDetectionResult Result { get; set; }

        /// <summary>
        /// 缓存时间
        /// </summary>
        public DateTime CacheTime { get; set; }

        /// <summary>
        /// 批次ID
        /// </summary>
        public string BatchId { get; set; }
    }

    /// <summary>
    /// TTS 插件检测器
    /// 用于检测各种 TTS 插件是否已加载并启用
    /// 优化：支持缓存机制，减少重复检测调用
    /// </summary>
    public static class TTSPluginDetector
    {
        /// <summary>
        /// VPet.Plugin.VPetTTS 插件的名称标识
        /// </summary>
        private const string VPET_TTS_PLUGIN_NAME = "VPetTTS";

        /// <summary>
        /// VPet.Plugin.EdgeTTS 插件的名称标识
        /// </summary>
        private const string EDGE_TTS_PLUGIN_NAME = "EdgeTTS";

        /// <summary>
        /// 已知的其他 TTS 插件名称列表（不包括 VPetLLM 自己）
        /// </summary>
        private static readonly string[] KNOWN_TTS_PLUGINS = new[]
        {
            VPET_TTS_PLUGIN_NAME,
            EDGE_TTS_PLUGIN_NAME,
        };

        /// <summary>
        /// 插件检测结果缓存
        /// </summary>
        private static readonly Dictionary<string, CachedDetectionResult> _cache = new Dictionary<string, CachedDetectionResult>();

        /// <summary>
        /// 缓存访问锁
        /// </summary>
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// 缓存超时时间（毫秒）
        /// </summary>
        private static int _cacheTimeoutMs = 5000;

        /// <summary>
        /// 检测 VPet.Plugin.VPetTTS 插件是否已启用
        /// </summary>
        /// <param name="mainWindow">VPet 主窗口接口</param>
        /// <returns>检测结果</returns>
        public static TTSPluginDetectionResult DetectVPetTTSPlugin(IMainWindow mainWindow)
        {
            return DetectSpecificPlugin(mainWindow, VPET_TTS_PLUGIN_NAME);
        }

        /// <summary>
        /// 使用缓存检测特定插件
        /// </summary>
        /// <param name="mainWindow">VPet 主窗口接口</param>
        /// <param name="pluginName">插件名称</param>
        /// <param name="batchId">批次ID</param>
        /// <returns>检测结果</returns>
        public static TTSPluginDetectionResult DetectWithCache(IMainWindow mainWindow, string pluginName, string batchId)
        {
            lock (_cacheLock)
            {
                var cacheKey = pluginName.ToLowerInvariant();

                // 检查缓存是否命中
                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    var isValidBatch = cached.BatchId == batchId;
                    var isNotExpired = DateTime.Now - cached.CacheTime < TimeSpan.FromMilliseconds(_cacheTimeoutMs);

                    if (isValidBatch && isNotExpired)
                    {
                        Logger.Log($"TTSPluginDetector: 缓存命中 - {pluginName}, 批次: {batchId}");
                        return cached.Result;
                    }
                    else
                    {
                        Logger.Log($"TTSPluginDetector: 缓存过期或批次不匹配 - {pluginName}, 当前批次: {batchId}, 缓存批次: {cached.BatchId}");
                    }
                }
                else
                {
                    Logger.Log($"TTSPluginDetector: 缓存未命中 - {pluginName}");
                }

                // 缓存未命中或已过期，重新检测
                Logger.Log($"TTSPluginDetector: 重新检测插件 - {pluginName}");
                var result = DetectSpecificPlugin(mainWindow, pluginName);

                // 更新缓存
                _cache[cacheKey] = new CachedDetectionResult
                {
                    Result = result,
                    CacheTime = DateTime.Now,
                    BatchId = batchId
                };

                Logger.Log($"TTSPluginDetector: 缓存已更新 - {pluginName}, 批次: {batchId}");
                return result;
            }
        }

        /// <summary>
        /// 清除指定批次的缓存
        /// </summary>
        /// <param name="batchId">批次ID</param>
        public static void ClearBatchCache(string batchId)
        {
            lock (_cacheLock)
            {
                var keysToRemove = new List<string>();
                foreach (var kvp in _cache)
                {
                    if (kvp.Value.BatchId == batchId)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }

                if (keysToRemove.Count > 0)
                {
                    Logger.Log($"TTSPluginDetector: 已清除批次 {batchId} 的 {keysToRemove.Count} 个缓存项");
                }
            }
        }

        /// <summary>
        /// 清除所有缓存
        /// </summary>
        public static void ClearAllCache()
        {
            lock (_cacheLock)
            {
                var count = _cache.Count;
                _cache.Clear();
                Logger.Log($"TTSPluginDetector: 已清除所有缓存，共 {count} 个项目");
            }
        }

        /// <summary>
        /// 设置缓存超时时间
        /// </summary>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        public static void SetCacheTimeout(int timeoutMs)
        {
            _cacheTimeoutMs = Math.Max(1000, timeoutMs); // 最小1秒
            Logger.Log($"TTSPluginDetector: 缓存超时时间已设置为 {_cacheTimeoutMs}ms");
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        /// <returns>缓存项数量</returns>
        public static int GetCacheCount()
        {
            lock (_cacheLock)
            {
                return _cache.Count;
            }
        }

        /// <summary>
        /// 检测所有其他 TTS 插件
        /// </summary>
        /// <param name="mainWindow">VPet 主窗口接口</param>
        /// <returns>检测结果</returns>
        public static OtherTTSPluginsDetectionResult DetectAllOtherTTSPlugins(IMainWindow mainWindow)
        {
            return DetectAllOtherTTSPluginsWithCache(mainWindow, null);
        }

        /// <summary>
        /// 使用缓存检测所有其他 TTS 插件
        /// </summary>
        /// <param name="mainWindow">VPet 主窗口接口</param>
        /// <param name="batchId">批次ID，为null时不使用缓存</param>
        /// <returns>检测结果</returns>
        public static OtherTTSPluginsDetectionResult DetectAllOtherTTSPluginsWithCache(IMainWindow mainWindow, string batchId)
        {
            var result = new OtherTTSPluginsDetectionResult();

            try
            {
                if (mainWindow?.Plugins is null)
                {
                    Logger.Log("TTSPluginDetector: 插件列表为空");
                    return result;
                }

                // 检测所有已知的 TTS 插件
                foreach (var pluginName in KNOWN_TTS_PLUGINS)
                {
                    TTSPluginDetectionResult detection;

                    if (!string.IsNullOrEmpty(batchId))
                    {
                        // 使用缓存检测
                        detection = DetectWithCache(mainWindow, pluginName, batchId);
                    }
                    else
                    {
                        // 直接检测，不使用缓存
                        detection = DetectSpecificPlugin(mainWindow, pluginName);
                    }

                    if (detection.PluginExists)
                    {
                        result.DetectedPlugins[pluginName] = detection;
                    }
                }

                if (result.HasOtherEnabledTTSPlugin)
                {
                    Logger.Log($"TTSPluginDetector: 检测到其他已启用的 TTS 插件: {result.EnabledPluginNames}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSPluginDetector: 检测其他 TTS 插件时发生错误: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 检测特定插件
        /// </summary>
        private static TTSPluginDetectionResult DetectSpecificPlugin(IMainWindow mainWindow, string pluginName)
        {
            var result = new TTSPluginDetectionResult
            {
                DetectionTime = DateTime.Now
            };

            try
            {
                if (mainWindow?.Plugins is null)
                {
                    return result;
                }

                // 遍历所有已加载的插件
                foreach (var plugin in mainWindow.Plugins)
                {
                    try
                    {
                        // 检查插件名称是否匹配
                        var currentPluginName = plugin.PluginName;
                        if (string.Equals(currentPluginName, pluginName, StringComparison.OrdinalIgnoreCase))
                        {
                            result.PluginExists = true;
                            Logger.Log($"TTSPluginDetector: 找到 {pluginName} 插件");

                            // 检查插件是否启用
                            result.PluginEnabled = CheckPluginEnabled(plugin);

                            // 尝试获取版本信息
                            result.PluginVersion = GetPluginVersion(plugin);

                            Logger.Log($"TTSPluginDetector: 插件状态 - 存在: {result.PluginExists}, 启用: {result.PluginEnabled}, 版本: {result.PluginVersion}");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTSPluginDetector: 检查插件时发生错误: {ex.Message}");
                    }
                }

                if (!result.PluginExists)
                {
                    Logger.Log($"TTSPluginDetector: 未找到 {pluginName} 插件");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSPluginDetector: 检测插件时发生错误: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 检查插件的 Enable 属性
        /// 通过反射访问插件的 Set.Enable 属性
        /// </summary>
        private static bool CheckPluginEnabled(MainPlugin plugin)
        {
            try
            {
                object setObject = null;

                // 首先尝试获取 Set 属性
                var setProperty = plugin.GetType().GetProperty("Set");
                if (setProperty is not null)
                {
                    setObject = setProperty.GetValue(plugin);
                    Logger.Log("TTSPluginDetector: 通过属性获取到 Set 对象");
                }

                // 如果属性不存在，尝试获取 Set 字段（VPetTTS 使用的是字段）
                if (setObject is null)
                {
                    var setField = plugin.GetType().GetField("Set");
                    if (setField is not null)
                    {
                        setObject = setField.GetValue(plugin);
                        Logger.Log("TTSPluginDetector: 通过字段获取到 Set 对象");
                    }
                }

                if (setObject is not null)
                {
                    // 尝试获取 Enable 属性
                    var enableProperty = setObject.GetType().GetProperty("Enable");
                    if (enableProperty is not null)
                    {
                        var enableValue = enableProperty.GetValue(setObject);
                        if (enableValue is bool enabled)
                        {
                            Logger.Log($"TTSPluginDetector: 成功获取 Enable 属性值: {enabled}");
                            return enabled;
                        }
                    }
                }

                // 如果无法获取 Enable 属性，假设插件未启用（保守策略）
                Logger.Log("TTSPluginDetector: 无法获取 Enable 属性，假设插件未启用");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSPluginDetector: 检查插件启用状态时发生错误: {ex.Message}");
                // 出错时假设插件未启用（保守策略）
                return false;
            }
        }

        /// <summary>
        /// 获取插件版本信息
        /// </summary>
        private static string GetPluginVersion(MainPlugin plugin)
        {
            try
            {
                // 尝试从程序集获取版本
                var assembly = plugin.GetType().Assembly;
                var version = assembly.GetName().Version;
                if (version is not null)
                {
                    return version.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSPluginDetector: 获取插件版本时发生错误: {ex.Message}");
            }

            return "Unknown";
        }
    }
}
