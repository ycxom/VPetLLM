using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 气泡延迟控制器
    /// 通过添加适当的延迟来减少瞬时性能压力，适合低性能设备
    /// 支持设备识别和性能配置缓存
    /// </summary>
    public static class BubbleDelayController
    {
        private static DateTime _lastBubbleTime = DateTime.MinValue;
        private static readonly object _delayLock = new object();

        // 延迟配置
        private static int _minDelayMs = 50;        // 最小延迟
        private static int _adaptiveDelayMs = 100;  // 自适应延迟
        private static bool _enableAdaptiveDelay = true;

        // 设备识别和缓存
        private static string _currentDeviceHash = null;
        private static DevicePerformanceProfile _cachedProfile = null;

        /// <summary>
        /// 获取设置数据库路径（复用现有的 settings.db）
        /// </summary>
        private static string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docPath, "VPetLLM", "settings.db");
        }

        private static readonly string _dbFilePath = GetDatabasePath();

        // 防抖控制
        //private const int DEBOUNCE_MS = 100;

        /// <summary>
        /// 初始化设备性能检测和缓存系统
        /// </summary>
        public static async Task InitializeAsync()
        {
            try
            {
                Logger.Log("BubbleDelayController: 开始初始化设备性能检测");

                // 生成当前设备的唯一标识
                _currentDeviceHash = await GenerateDeviceHashAsync();
                Logger.Log($"BubbleDelayController: 设备标识: {_currentDeviceHash?.Substring(0, 8)}...");

                // 尝试加载缓存的性能配置
                _cachedProfile = LoadCachedProfile(_currentDeviceHash);

                if (_cachedProfile != null)
                {
                    // 使用缓存的配置
                    ApplyPerformanceProfile(_cachedProfile);
                    Logger.Log($"BubbleDelayController: 使用缓存配置 - {_cachedProfile.DeviceType}");
                }
                else
                {
                    // 执行性能检测
                    Logger.Log("BubbleDelayController: 执行设备性能检测...");
                    var profile = await PerformDevicePerformanceTestAsync();

                    // 保存检测结果
                    SavePerformanceProfile(_currentDeviceHash, profile);
                    ApplyPerformanceProfile(profile);

                    Logger.Log($"BubbleDelayController: 性能检测完成 - {profile.DeviceType}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 初始化失败，使用默认配置: {ex.Message}");
                ConfigureDelay(); // 使用默认配置
            }
        }

        /// <summary>
        /// 生成设备唯一标识（CPU + 内存 + 显卡 + 系统信息的MD5）
        /// </summary>
        private static async Task<string> GenerateDeviceHashAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var deviceInfo = new StringBuilder();

                    // CPU 信息（从注册表获取）
                    try
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                        {
                            if (key != null)
                            {
                                var cpuName = key.GetValue("ProcessorNameString")?.ToString() ?? "";
                                var cpuSpeed = key.GetValue("~MHz")?.ToString() ?? "";
                                deviceInfo.Append($"CPU:{cpuName}:{cpuSpeed};");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取CPU信息失败: {ex.Message}");
                        deviceInfo.Append($"CPU:{Environment.ProcessorCount}cores;");
                    }

                    // 内存信息（使用 .NET 方法）
                    try
                    {
                        var totalMemory = GC.GetTotalMemory(false);
                        // 尝试获取物理内存信息
                        var memoryStatus = new MEMORYSTATUSEX();
                        if (NativeMethods.GlobalMemoryStatusEx(memoryStatus))
                        {
                            deviceInfo.Append($"MEM:{memoryStatus.ullTotalPhys};");
                        }
                        else
                        {
                            deviceInfo.Append($"MEM:{totalMemory};");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取内存信息失败: {ex.Message}");
                        deviceInfo.Append($"MEM:{GC.GetTotalMemory(false)};");
                    }

                    // 显卡信息（从注册表获取）
                    bool hasDiscreteGPU = false;
                    try
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                        {
                            if (key != null)
                            {
                                foreach (var subKeyName in key.GetSubKeyNames())
                                {
                                    if (subKeyName.StartsWith("0"))
                                    {
                                        using (var subKey = key.OpenSubKey(subKeyName))
                                        {
                                            var driverDesc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
                                            var memorySize = subKey?.GetValue("HardwareInformation.MemorySize")?.ToString() ?? "0";

                                            deviceInfo.Append($"GPU:{driverDesc}:{memorySize};");

                                            // 简单判断是否为独立显卡
                                            if (!driverDesc.Contains("Intel") && !string.IsNullOrEmpty(driverDesc))
                                            {
                                                hasDiscreteGPU = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取显卡信息失败: {ex.Message}");
                        deviceInfo.Append("GPU:Unknown;");
                    }
                    deviceInfo.Append($"DISCRETE_GPU:{hasDiscreteGPU};");

                    // 系统信息
                    try
                    {
                        deviceInfo.Append($"MACHINE:{Environment.MachineName};");
                        deviceInfo.Append($"USER:{Environment.UserName};");
                        deviceInfo.Append($"OS:{Environment.OSVersion};");

                        // 尝试获取系统序列号
                        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                        {
                            var serialNumber = key?.GetValue("SystemSerialNumber")?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(serialNumber))
                            {
                                deviceInfo.Append($"SN:{serialNumber};");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取系统信息失败: {ex.Message}");
                        deviceInfo.Append($"SYS:{Environment.MachineName};");
                    }

                    // 生成 MD5 哈希
                    using (var md5 = MD5.Create())
                    {
                        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(deviceInfo.ToString()));
                        return Convert.ToHexString(hash);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleDelayController: 生成设备标识失败: {ex.Message}");
                    // 使用简化的标识
                    var fallbackInfo = $"{Environment.ProcessorCount}:{GC.GetTotalMemory(false)}:{Environment.MachineName}:{Environment.UserName}";
                    using (var md5 = MD5.Create())
                    {
                        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(fallbackInfo));
                        return Convert.ToHexString(hash);
                    }
                }
            });
        }

        /// <summary>
        /// 执行设备性能测试
        /// </summary>
        private static async Task<DevicePerformanceProfile> PerformDevicePerformanceTestAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var profile = new DevicePerformanceProfile
                    {
                        DeviceHash = _currentDeviceHash,
                        TestDate = DateTime.Now
                    };

                    Logger.Log("BubbleDelayController: 开始CPU计算能力测试（3秒圆周率运算）...");

                    // 执行3秒的圆周率计算测试
                    var cpuScore = PerformPiCalculationTest(3000); // 3秒测试
                    Logger.Log($"BubbleDelayController: CPU计算得分: {cpuScore}");

                    // 内存测试
                    var memoryScore = 100; // 默认分数
                    try
                    {
                        var memoryStatus = new MEMORYSTATUSEX();
                        if (NativeMethods.GlobalMemoryStatusEx(memoryStatus))
                        {
                            var totalMemoryGB = memoryStatus.ullTotalPhys / (1024 * 1024 * 1024);
                            memoryScore = totalMemoryGB >= 16 ? 300 : // 16GB+
                                         totalMemoryGB >= 8 ? 200 :   // 8GB+
                                         totalMemoryGB >= 4 ? 100 : 50; // 4GB+
                            Logger.Log($"BubbleDelayController: 内存: {totalMemoryGB}GB, 得分: {memoryScore}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 内存测试失败: {ex.Message}");
                    }

                    // 显卡测试
                    var gpuScore = HasDiscreteGPUFromRegistry() ? 200 : 50;
                    Logger.Log($"BubbleDelayController: GPU得分: {gpuScore}");

                    // 综合评分
                    var totalScore = cpuScore + memoryScore + gpuScore;
                    Logger.Log($"BubbleDelayController: 综合性能评分: {totalScore}");

                    // 根据评分确定设备类型和配置
                    if (totalScore >= 1000)
                    {
                        profile.DeviceType = "HighEnd";
                        profile.MinDelay = 20;
                        profile.AdaptiveDelay = 50;
                        profile.EnableAdaptive = false;
                        Logger.Log("BubbleDelayController: 设备类型: 高端 (延迟: 20-50ms)");
                    }
                    else if (totalScore >= 500)
                    {
                        profile.DeviceType = "MidRange";
                        profile.MinDelay = 50;
                        profile.AdaptiveDelay = 100;
                        profile.EnableAdaptive = true;
                        Logger.Log("BubbleDelayController: 设备类型: 中端 (延迟: 50-100ms)");
                    }
                    else
                    {
                        profile.DeviceType = "LowEnd";
                        profile.MinDelay = 100;
                        profile.AdaptiveDelay = 200;
                        profile.EnableAdaptive = true;
                        Logger.Log("BubbleDelayController: 设备类型: 低端 (延迟: 100-200ms)");
                    }

                    profile.PerformanceScore = totalScore;

                    return profile;
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleDelayController: 性能测试失败: {ex.Message}");
                    return new DevicePerformanceProfile
                    {
                        DeviceHash = _currentDeviceHash,
                        DeviceType = "Unknown",
                        MinDelay = 50,
                        AdaptiveDelay = 100,
                        EnableAdaptive = true,
                        PerformanceScore = 0,
                        TestDate = DateTime.Now
                    };
                }
            });
        }

        /// <summary>
        /// 执行圆周率计算测试（Leibniz公式）
        /// 在指定时间内计算尽可能多的迭代次数
        /// </summary>
        /// <param name="durationMs">测试持续时间（毫秒）</param>
        /// <returns>性能得分（基于迭代次数）</returns>
        private static int PerformPiCalculationTest(int durationMs)
        {
            try
            {
                var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();
                long iterations = 0;
                double pi = 0.0;
                int sign = 1;

                // 使用 Leibniz 公式计算 π: π/4 = 1 - 1/3 + 1/5 - 1/7 + 1/9 - ...
                while (stopwatch.ElapsedMilliseconds < durationMs)
                {
                    pi += sign * (1.0 / (2 * iterations + 1));
                    sign = -sign;
                    iterations++;
                }

                stopwatch.Stop();

                // 计算实际的π值
                pi *= 4;

                Logger.Log($"BubbleDelayController: π计算完成 - 迭代次数: {iterations:N0}, 计算结果: {pi:F10}");

                // 根据迭代次数计算得分
                // 现代CPU在3秒内通常能完成数亿次迭代
                // 高端: > 500M 迭代/3秒 = 500分
                // 中端: 100M-500M 迭代/3秒 = 200-500分
                // 低端: < 100M 迭代/3秒 = 100-200分

                int score;
                if (iterations > 500_000_000)
                {
                    score = 500; // 高端CPU
                }
                else if (iterations > 300_000_000)
                {
                    score = 400; // 中高端CPU
                }
                else if (iterations > 150_000_000)
                {
                    score = 300; // 中端CPU
                }
                else if (iterations > 50_000_000)
                {
                    score = 200; // 中低端CPU
                }
                else
                {
                    score = 100; // 低端CPU
                }

                Logger.Log($"BubbleDelayController: CPU性能等级 - 迭代: {iterations:N0}, 得分: {score}");

                return score;
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: π计算测试失败: {ex.Message}");
                return 100; // 默认低端得分
            }
        }

        /// <summary>
        /// 从注册表检测是否有独立显卡
        /// </summary>
        private static bool HasDiscreteGPUFromRegistry()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (key != null)
                    {
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            if (subKeyName.StartsWith("0"))
                            {
                                using (var subKey = key.OpenSubKey(subKeyName))
                                {
                                    var driverDesc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";

                                    // 简单判断：非Intel集显
                                    if (!driverDesc.Contains("Intel") && !string.IsNullOrEmpty(driverDesc))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 检测显卡失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 获取数据库连接（确保数据库已初始化）
        /// </summary>
        private static SqliteConnection GetDatabaseConnection()
        {
            // 确保目录存在
            var directory = Path.GetDirectoryName(_dbFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connectionString = $"Data Source={_dbFilePath}";
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            // 确保表存在
            EnsureTablesExist(connection);

            return connection;
        }

        /// <summary>
        /// 确保数据库表存在
        /// </summary>
        private static void EnsureTablesExist(SqliteConnection connection)
        {
            try
            {
                // 创建设备性能表
                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS DevicePerformance (
                        DeviceHash TEXT PRIMARY KEY,
                        DeviceType TEXT NOT NULL,
                        MinDelay INTEGER NOT NULL,
                        AdaptiveDelay INTEGER NOT NULL,
                        EnableAdaptive INTEGER NOT NULL,
                        PerformanceScore INTEGER NOT NULL,
                        TestDate TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    )";

                using (var command = new SqliteCommand(createTableSql, connection))
                {
                    command.ExecuteNonQuery();
                }

                // 创建索引
                var createIndexSql = @"
                    CREATE INDEX IF NOT EXISTS idx_device_hash ON DevicePerformance(DeviceHash);
                    CREATE INDEX IF NOT EXISTS idx_test_date ON DevicePerformance(TestDate);
                ";

                using (var command = new SqliteCommand(createIndexSql, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 创建数据库表失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载缓存的性能配置
        /// </summary>
        private static DevicePerformanceProfile LoadCachedProfile(string deviceHash)
        {
            try
            {
                using (var connection = GetDatabaseConnection())
                {
                    var selectSql = @"
                        SELECT DeviceHash, DeviceType, MinDelay, AdaptiveDelay, EnableAdaptive, 
                               PerformanceScore, TestDate
                        FROM DevicePerformance 
                        WHERE DeviceHash = @DeviceHash
                        ORDER BY TestDate DESC 
                        LIMIT 1";

                    using (var command = new SqliteCommand(selectSql, connection))
                    {
                        command.Parameters.AddWithValue("@DeviceHash", deviceHash);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var profile = new DevicePerformanceProfile
                                {
                                    DeviceHash = reader.GetString(0),
                                    DeviceType = reader.GetString(1),
                                    MinDelay = reader.GetInt32(2),
                                    AdaptiveDelay = reader.GetInt32(3),
                                    EnableAdaptive = reader.GetInt32(4) == 1,
                                    PerformanceScore = reader.GetInt32(5),
                                    TestDate = DateTime.Parse(reader.GetString(6))
                                };

                                // 检查缓存是否过期（30天）
                                if ((DateTime.Now - profile.TestDate).TotalDays < 30)
                                {
                                    return profile;
                                }
                                else
                                {
                                    Logger.Log($"BubbleDelayController: 缓存已过期，需要重新检测");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 加载缓存失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 保存性能配置到数据库
        /// </summary>
        private static void SavePerformanceProfile(string deviceHash, DevicePerformanceProfile profile)
        {
            try
            {
                using (var connection = GetDatabaseConnection())
                {
                    // 使用 UPSERT 语法（INSERT OR REPLACE）
                    var upsertSql = @"
                        INSERT OR REPLACE INTO DevicePerformance 
                        (DeviceHash, DeviceType, MinDelay, AdaptiveDelay, EnableAdaptive, 
                         PerformanceScore, TestDate, UpdatedAt)
                        VALUES 
                        (@DeviceHash, @DeviceType, @MinDelay, @AdaptiveDelay, @EnableAdaptive, 
                         @PerformanceScore, @TestDate, @UpdatedAt)";

                    using (var command = new SqliteCommand(upsertSql, connection))
                    {
                        command.Parameters.AddWithValue("@DeviceHash", deviceHash);
                        command.Parameters.AddWithValue("@DeviceType", profile.DeviceType);
                        command.Parameters.AddWithValue("@MinDelay", profile.MinDelay);
                        command.Parameters.AddWithValue("@AdaptiveDelay", profile.AdaptiveDelay);
                        command.Parameters.AddWithValue("@EnableAdaptive", profile.EnableAdaptive ? 1 : 0);
                        command.Parameters.AddWithValue("@PerformanceScore", profile.PerformanceScore);
                        command.Parameters.AddWithValue("@TestDate", profile.TestDate.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                        command.ExecuteNonQuery();
                    }

                    // 清理过期记录（保留最近30天）
                    var cleanupSql = @"
                        DELETE FROM DevicePerformance 
                        WHERE TestDate < @CutoffDate";

                    using (var command = new SqliteCommand(cleanupSql, connection))
                    {
                        var cutoffDate = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd HH:mm:ss");
                        command.Parameters.AddWithValue("@CutoffDate", cutoffDate);

                        var deletedRows = command.ExecuteNonQuery();
                        if (deletedRows > 0)
                        {
                            Logger.Log($"BubbleDelayController: 清理了 {deletedRows} 条过期记录");
                        }
                    }
                }

                Logger.Log($"BubbleDelayController: 性能配置已保存到数据库");
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 保存到数据库失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 应用性能配置
        /// </summary>
        private static void ApplyPerformanceProfile(DevicePerformanceProfile profile)
        {
            _cachedProfile = profile;
            ConfigureDelay(profile.MinDelay, profile.AdaptiveDelay, profile.EnableAdaptive);
        }

        /// <summary>
        /// 设置延迟配置
        /// </summary>
        /// <param name="minDelay">最小延迟（毫秒）</param>
        /// <param name="adaptiveDelay">自适应延迟（毫秒）</param>
        /// <param name="enableAdaptive">是否启用自适应延迟</param>
        public static void ConfigureDelay(int minDelay = 50, int adaptiveDelay = 100, bool enableAdaptive = true)
        {
            _minDelayMs = Math.Max(0, minDelay);
            _adaptiveDelayMs = Math.Max(_minDelayMs, adaptiveDelay);
            _enableAdaptiveDelay = enableAdaptive;

            Logger.Log($"BubbleDelayController: 延迟配置已更新 - 最小:{_minDelayMs}ms, 自适应:{_adaptiveDelayMs}ms, 启用自适应:{_enableAdaptiveDelay}");
        }

        /// <summary>
        /// 显示气泡前的延迟处理
        /// </summary>
        /// <param name="text">要显示的文本</param>
        /// <returns>建议的延迟时间（毫秒）</returns>
        public static async Task<int> ApplyDelayBeforeShow(string text)
        {
            lock (_delayLock)
            {
                var now = DateTime.Now;
                var timeSinceLastBubble = (now - _lastBubbleTime).TotalMilliseconds;

                // 计算需要的延迟时间
                int requiredDelay = CalculateRequiredDelay(text, timeSinceLastBubble);

                if (requiredDelay > 0)
                {
                    _lastBubbleTime = now.AddMilliseconds(requiredDelay);
                    return requiredDelay;
                }

                _lastBubbleTime = now;
                return 0;
            }
        }

        /// <summary>
        /// 计算所需的延迟时间
        /// </summary>
        private static int CalculateRequiredDelay(string text, double timeSinceLastBubble)
        {
            // 始终应用基础延迟（性能优化的核心）
            int baseDelay = _minDelayMs;

            // 如果距离上次显示时间太短，增加额外延迟
            if (timeSinceLastBubble < _minDelayMs)
            {
                int additionalDelay = _minDelayMs - (int)timeSinceLastBubble;
                baseDelay += additionalDelay;
            }

            // 自适应延迟（根据文本长度和系统状态）
            if (_enableAdaptiveDelay)
            {
                int adaptiveDelay = CalculateAdaptiveDelay(text);
                baseDelay = Math.Max(baseDelay, adaptiveDelay);
            }

            return Math.Max(0, baseDelay);
        }

        /// <summary>
        /// 计算自适应延迟
        /// </summary>
        private static int CalculateAdaptiveDelay(string text)
        {
            int adaptiveDelay = 0;

            // 根据文本长度调整延迟
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > 100)
                {
                    adaptiveDelay += 50; // 长文本增加延迟
                }
                else if (text.Length > 50)
                {
                    adaptiveDelay += 25; // 中等文本增加少量延迟
                }
            }

            // 根据内存压力调整延迟
            try
            {
                var currentMemory = GC.GetTotalMemory(false);
                if (currentMemory > 500 * 1024 * 1024) // 500MB+
                {
                    adaptiveDelay += 30; // 内存压力大时增加延迟
                }
            }
            catch
            {
                // 忽略内存检查异常
            }

            return Math.Min(adaptiveDelay, _adaptiveDelayMs);
        }

        /// <summary>
        /// 获取配置的延迟时间（简单版本）
        /// </summary>
        public static int GetConfiguredDelay()
        {
            return _minDelayMs;
        }

        /// <summary>
        /// 应用UI操作延迟（减少瞬时性能压力）
        /// </summary>
        public static void ApplyUIDelay()
        {
            if (_minDelayMs > 0)
            {
                Logger.Log($"BubbleDelayController: 应用UI延迟 {_minDelayMs}ms");
                global::System.Threading.Thread.Sleep(_minDelayMs);
            }
        }

        /// <summary>
        /// 带延迟的气泡显示（扩展 DirectBubbleManager）
        /// </summary>
        public static async Task<bool> ShowBubbleWithDelay(VPetLLM plugin, string text, string animation = null)
        {
            if (plugin == null || string.IsNullOrEmpty(text))
                return false;

            try
            {
                // 应用延迟
                int delayMs = await ApplyDelayBeforeShow(text);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                    Logger.Log($"BubbleDelayController: 应用延迟 {delayMs}ms");
                }

                // 显示气泡
                return await ShowBubbleInternal(plugin, text, animation);
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 显示气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 带延迟的思考气泡显示
        /// </summary>
        public static async Task<bool> ShowThinkingBubbleWithDelay(VPetLLM plugin, string thinkingText = "思考中...")
        {
            if (plugin == null)
                return false;

            try
            {
                // 思考气泡使用较短的延迟
                int delayMs = _minDelayMs / 2;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                // 显示思考气泡
                return await ShowThinkingBubbleInternal(plugin, thinkingText);
            }
            catch (Exception ex)
            {
                Logger.Log($"BubbleDelayController: 显示思考气泡失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 内部气泡显示逻辑
        /// </summary>
        private static async Task<bool> ShowBubbleInternal(VPetLLM plugin, string text, string animation)
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(animation))
                    {
                        plugin.MW.Main.Say(text, animation, true);
                    }
                    else
                    {
                        plugin.MW.Main.Say(text, null, false);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleDelayController: UI 显示失败: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 内部思考气泡显示逻辑
        /// </summary>
        private static async Task<bool> ShowThinkingBubbleInternal(VPetLLM plugin, string thinkingText)
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var msgBar = plugin?.MW?.Main?.MsgBar;
                    if (msgBar != null)
                    {
                        MessageBarHelper.ShowBubbleQuick(msgBar, thinkingText, plugin.MW.Core.Save.Name);
                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleDelayController: 思考气泡显示失败: {ex.Message}");
                    return false;
                }
            });
        }
        /*
                /// <summary>
                /// 重置延迟状态
                /// </summary>
                public static void ResetDelayState()
                {
                    lock (_delayLock)
                    {
                        _lastBubbleTime = DateTime.MinValue;
                        Logger.Log("BubbleDelayController: 延迟状态已重置");
                    }
                }

                /// <summary>
                /// 获取当前延迟配置信息
                /// </summary>
                public static string GetDelayInfo()
                {
                    var deviceInfo = _cachedProfile != null ? $"设备类型:{_cachedProfile.DeviceType}, " : "";
                    return $"{deviceInfo}最小延迟:{_minDelayMs}ms, 自适应延迟:{_adaptiveDelayMs}ms, 启用自适应:{_enableAdaptiveDelay}";
                }

                /// <summary>
                /// 获取当前设备信息
                /// </summary>
                public static string GetDeviceInfo()
                {
                    if (_cachedProfile == null)
                        return "设备信息未初始化";

                    return $"设备标识: {_currentDeviceHash?.Substring(0, 8)}..., " +
                           $"设备类型: {_cachedProfile.DeviceType}, " +
                           $"性能评分: {_cachedProfile.PerformanceScore}, " +
                           $"检测时间: {_cachedProfile.TestDate:yyyy-MM-dd HH:mm:ss}";
                }

                /// <summary>
                /// 获取所有设备的性能记录
                /// </summary>
                public static List<DevicePerformanceProfile> GetAllDeviceProfiles()
                {
                    var profiles = new List<DevicePerformanceProfile>();

                    try
                    {
                        using (var connection = GetDatabaseConnection())
                        {
                            var selectSql = @"
                                SELECT DeviceHash, DeviceType, MinDelay, AdaptiveDelay, EnableAdaptive, 
                                       PerformanceScore, TestDate
                                FROM DevicePerformance 
                                ORDER BY TestDate DESC";

                            using (var command = new SqliteCommand(selectSql, connection))
                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    profiles.Add(new DevicePerformanceProfile
                                    {
                                        DeviceHash = reader.GetString(0),
                                        DeviceType = reader.GetString(1),
                                        MinDelay = reader.GetInt32(2),
                                        AdaptiveDelay = reader.GetInt32(3),
                                        EnableAdaptive = reader.GetInt32(4) == 1,
                                        PerformanceScore = reader.GetInt32(5),
                                        TestDate = DateTime.Parse(reader.GetString(6))
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取设备记录失败: {ex.Message}");
                    }

                    return profiles;
                }

                /// <summary>
                /// 清理数据库（删除所有记录）
                /// </summary>
                public static void ClearDatabase()
                {
                    try
                    {
                        using (var connection = GetDatabaseConnection())
                        {
                            var deleteSql = "DELETE FROM DevicePerformance";
                            using (var command = new SqliteCommand(deleteSql, connection))
                            {
                                var deletedRows = command.ExecuteNonQuery();
                                Logger.Log($"BubbleDelayController: 已清理数据库，删除了 {deletedRows} 条记录");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 清理数据库失败: {ex.Message}");
                    }
                }

                /// <summary>
                /// 获取数据库统计信息
                /// </summary>
                public static string GetDatabaseStats()
                {
                    try
                    {
                        using (var connection = GetDatabaseConnection())
                        {
                            // 获取总记录数
                            var countSql = "SELECT COUNT(*) FROM DevicePerformance";
                            int totalRecords = 0;
                            using (var command = new SqliteCommand(countSql, connection))
                            {
                                totalRecords = Convert.ToInt32(command.ExecuteScalar());
                            }

                            // 获取设备类型分布
                            var typeSql = @"
                                SELECT DeviceType, COUNT(*) as Count 
                                FROM DevicePerformance 
                                GROUP BY DeviceType 
                                ORDER BY Count DESC";

                            var typeStats = new List<string>();
                            using (var command = new SqliteCommand(typeSql, connection))
                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    typeStats.Add($"{reader.GetString(0)}: {reader.GetInt32(1)}");
                                }
                            }

                            // 获取数据库文件大小
                            var fileSize = File.Exists(_dbFilePath) ? new FileInfo(_dbFilePath).Length : 0;
                            var fileSizeKB = fileSize / 1024.0;

                            return $"数据库统计 - 总记录: {totalRecords}, 文件大小: {fileSizeKB:F1}KB, " +
                                   $"设备类型分布: [{string.Join(", ", typeStats)}]";
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 获取数据库统计失败: {ex.Message}");
                        return $"数据库统计获取失败: {ex.Message}";
                    }
                }

                /// <summary>
                /// 强制重新检测设备性能
                /// </summary>
                public static async Task ForceRedetectAsync()
                {
                    try
                    {
                        Logger.Log("BubbleDelayController: 强制重新检测设备性能");

                        if (string.IsNullOrEmpty(_currentDeviceHash))
                        {
                            _currentDeviceHash = await GenerateDeviceHashAsync();
                        }

                        var profile = await PerformDevicePerformanceTestAsync();
                        SavePerformanceProfile(_currentDeviceHash, profile);
                        ApplyPerformanceProfile(profile);

                        Logger.Log($"BubbleDelayController: 重新检测完成 - {profile.DeviceType}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"BubbleDelayController: 强制重新检测失败: {ex.Message}");
                    }
                }*/
    }

    /// <summary>
    /// 设备性能配置文件
    /// </summary>
    public class DevicePerformanceProfile
    {
        public string DeviceHash { get; set; }
        public string DeviceType { get; set; }
        public int MinDelay { get; set; }
        public int AdaptiveDelay { get; set; }
        public bool EnableAdaptive { get; set; }
        public int PerformanceScore { get; set; }
        public DateTime TestDate { get; set; }
    }

    /// <summary>
    /// Windows API 内存状态结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    /// <summary>
    /// Windows API 声明
    /// </summary>
    public static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}