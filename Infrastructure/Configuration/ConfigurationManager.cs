using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.IO;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Infrastructure.Logging;

namespace VPetLLM.Infrastructure.Configuration
{
    /// <summary>
    /// 配置管理器实现
    /// </summary>
    public class ConfigurationManager : IConfigurationManager
    {
        private readonly string _basePath;
        private readonly IStructuredLogger _logger;
        private readonly ConcurrentDictionary<Type, IConfiguration> _configurations = new();
        private readonly ConcurrentDictionary<Type, FileSystemWatcher> _fileWatchers = new();
        private readonly ConcurrentDictionary<Type, DateTime> _lastFileWriteTimes = new();
        private readonly ReaderWriterLockSlim _lock = new();
        private readonly Timer _saveTimer;
        private readonly ConcurrentQueue<Type> _pendingSaves = new();
        private bool _disposed = false;

        public event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;
        public event EventHandler<ConfigurationLoadedEventArgs> ConfigurationLoaded;
        public event EventHandler<ConfigurationSavedEventArgs> ConfigurationSaved;

        public ConfigurationManager(string basePath, IStructuredLogger logger = null)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _logger = logger;

            // 确保配置目录存在
            Directory.CreateDirectory(_basePath);

            // 启动定时保存器（每5秒检查一次待保存的配置）
            _saveTimer = new Timer(ProcessPendingSaves, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            _logger?.LogInformation("ConfigurationManager initialized", new { BasePath = _basePath });
        }

        public T GetConfiguration<T>() where T : class, IConfiguration, new()
        {
            ThrowIfDisposed();

            _lock.EnterReadLock();
            try
            {
                if (_configurations.TryGetValue(typeof(T), out var cached))
                {
                    return (T)cached;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            // 需要加载配置
            _lock.EnterWriteLock();
            try
            {
                // 双重检查锁定
                if (_configurations.TryGetValue(typeof(T), out var cached))
                {
                    return (T)cached;
                }

                var configuration = LoadConfiguration<T>();
                _configurations[typeof(T)] = configuration;

                OnConfigurationLoaded(new ConfigurationLoadedEventArgs(
                    typeof(T), configuration.ConfigurationName, configuration, File.Exists(GetConfigurationFilePath<T>())));

                return configuration;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public async Task SaveConfigurationAsync<T>(T configuration) where T : class, IConfiguration
        {
            ThrowIfDisposed();
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var filePath = GetConfigurationFilePath<T>();

            try
            {
                // 验证配置
                var validationResult = configuration.Validate();
                if (!validationResult.IsValid)
                {
                    throw new ConfigurationException(configuration.ConfigurationName,
                        $"Configuration validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // 序列化配置
                var json = JsonConvert.SerializeObject(configuration, Formatting.Indented, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Include
                });

                // 异步写入文件
                await File.WriteAllTextAsync(filePath, json);

                // 更新缓存
                _lock.EnterWriteLock();
                try
                {
                    var oldConfiguration = _configurations.TryGetValue(typeof(T), out var old) ? old : null;
                    _configurations[typeof(T)] = configuration;
                    configuration.IsModified = false;

                    // 触发配置变更事件
                    if (oldConfiguration != null)
                    {
                        OnConfigurationChanged(new ConfigurationChangedEventArgs(
                            typeof(T), configuration.ConfigurationName, oldConfiguration, configuration,
                            ConfigurationChangeReason.ManualUpdate));
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                OnConfigurationSaved(new ConfigurationSavedEventArgs(
                    typeof(T), configuration.ConfigurationName, configuration, filePath));

                _logger?.LogInformation("Configuration saved successfully", new
                {
                    ConfigurationType = typeof(T).Name,
                    ConfigurationName = configuration.ConfigurationName,
                    FilePath = filePath
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save configuration", new
                {
                    ConfigurationType = typeof(T).Name,
                    ConfigurationName = configuration.ConfigurationName,
                    FilePath = filePath
                });
                throw new ConfigurationException(configuration.ConfigurationName, "Failed to save configuration", ex);
            }
        }

        public async Task SaveAllAsync()
        {
            ThrowIfDisposed();

            var tasks = new List<Task>();

            _lock.EnterReadLock();
            try
            {
                foreach (var kvp in _configurations)
                {
                    var configurationType = kvp.Key;
                    var configuration = kvp.Value;

                    if (configuration.IsModified)
                    {
                        // 使用反射调用泛型方法
                        var method = typeof(ConfigurationManager).GetMethod(nameof(SaveConfigurationAsync))?.MakeGenericMethod(configurationType);
                        if (method != null)
                        {
                            var task = (Task)method.Invoke(this, new object[] { configuration });
                            tasks.Add(task);
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            // 等待所有保存操作完成
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                _logger?.LogInformation("All configurations saved successfully", new { ConfigurationCount = tasks.Count });
            }
        }

        public async Task ReloadConfigurationAsync<T>() where T : class, IConfiguration, new()
        {
            ThrowIfDisposed();

            _lock.EnterWriteLock();
            try
            {
                var oldConfiguration = _configurations.TryGetValue(typeof(T), out var old) ? old : null;
                var newConfiguration = LoadConfiguration<T>();
                _configurations[typeof(T)] = newConfiguration;

                OnConfigurationLoaded(new ConfigurationLoadedEventArgs(
                    typeof(T), newConfiguration.ConfigurationName, newConfiguration, File.Exists(GetConfigurationFilePath<T>())));

                if (oldConfiguration != null)
                {
                    OnConfigurationChanged(new ConfigurationChangedEventArgs(
                        typeof(T), newConfiguration.ConfigurationName, oldConfiguration, newConfiguration,
                        ConfigurationChangeReason.HotReload));
                }

                _logger?.LogInformation("Configuration reloaded", new
                {
                    ConfigurationType = typeof(T).Name,
                    ConfigurationName = newConfiguration.ConfigurationName
                });
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public async Task ReloadAllConfigurationsAsync()
        {
            ThrowIfDisposed();

            var configurationTypes = GetConfigurationTypes().ToList();
            var reloadTasks = new List<Task>();

            foreach (var type in configurationTypes)
            {
                var method = typeof(ConfigurationManager).GetMethod(nameof(ReloadConfigurationAsync))?.MakeGenericMethod(type);
                if (method != null)
                {
                    var task = (Task)method.Invoke(this, null);
                    reloadTasks.Add(task);
                }
            }

            await Task.WhenAll(reloadTasks);
            _logger?.LogInformation("All configurations reloaded", new { Count = configurationTypes.Count });
        }

        public bool ConfigurationExists<T>() where T : class, IConfiguration
        {
            ThrowIfDisposed();
            return File.Exists(GetConfigurationFilePath<T>());
        }

        public IEnumerable<Type> GetConfigurationTypes()
        {
            ThrowIfDisposed();
            _lock.EnterReadLock();
            try
            {
                return _configurations.Keys.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void EnableHotReload<T>() where T : class, IConfiguration, new()
        {
            ThrowIfDisposed();

            var filePath = GetConfigurationFilePath<T>();
            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);

            if (_fileWatchers.ContainsKey(typeof(T)))
            {
                return; // 已经启用
            }

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += async (sender, e) =>
            {
                try
                {
                    // 防止重复触发
                    var lastWriteTime = File.GetLastWriteTime(e.FullPath);
                    if (_lastFileWriteTimes.TryGetValue(typeof(T), out var lastTime) &&
                        Math.Abs((lastWriteTime - lastTime).TotalMilliseconds) < 1000)
                    {
                        return;
                    }
                    _lastFileWriteTimes[typeof(T)] = lastWriteTime;

                    // 延迟一点时间，确保文件写入完成
                    await Task.Delay(500);

                    await ReloadConfigurationAsync<T>();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during hot reload", new
                    {
                        ConfigurationType = typeof(T).Name,
                        FilePath = e.FullPath
                    });
                }
            };

            _fileWatchers[typeof(T)] = watcher;
            _logger?.LogInformation("Hot reload enabled for configuration", new { ConfigurationType = typeof(T).Name });
        }

        public void DisableHotReload<T>() where T : class, IConfiguration
        {
            ThrowIfDisposed();

            if (_fileWatchers.TryRemove(typeof(T), out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _lastFileWriteTimes.TryRemove(typeof(T), out _);
                _logger?.LogInformation("Hot reload disabled for configuration", new { ConfigurationType = typeof(T).Name });
            }
        }

        private T LoadConfiguration<T>() where T : class, IConfiguration, new()
        {
            var filePath = GetConfigurationFilePath<T>();

            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var configuration = JsonConvert.DeserializeObject<T>(json);
                        if (configuration != null)
                        {
                            configuration.IsModified = false;
                            _logger?.LogDebug("Configuration loaded from file", new
                            {
                                ConfigurationType = typeof(T).Name,
                                FilePath = filePath
                            });
                            return configuration;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Failed to load configuration from file, using defaults", new
                    {
                        ConfigurationType = typeof(T).Name,
                        FilePath = filePath,
                        Error = ex.Message
                    });
                }
            }

            // 返回默认配置
            var defaultConfiguration = new T();
            defaultConfiguration.IsModified = false;
            _logger?.LogDebug("Using default configuration", new { ConfigurationType = typeof(T).Name });
            return defaultConfiguration;
        }

        private string GetConfigurationFilePath<T>()
        {
            var typeName = typeof(T).Name;
            if (typeName.EndsWith("Configuration"))
            {
                typeName = typeName.Substring(0, typeName.Length - 13); // 移除 "Configuration" 后缀
            }
            return Path.Combine(_basePath, $"{typeName}.json");
        }

        private void ProcessPendingSaves(object state)
        {
            if (_disposed)
                return;

            var typesToSave = new List<Type>();
            while (_pendingSaves.TryDequeue(out var type))
            {
                typesToSave.Add(type);
            }

            foreach (var type in typesToSave.Distinct())
            {
                try
                {
                    _lock.EnterReadLock();
                    IConfiguration configuration;
                    try
                    {
                        if (!_configurations.TryGetValue(type, out configuration) || !configuration.IsModified)
                        {
                            continue;
                        }
                    }
                    finally
                    {
                        _lock.ExitReadLock();
                    }

                    // 使用反射调用泛型方法
                    var method = typeof(ConfigurationManager).GetMethod(nameof(SaveConfigurationAsync))?.MakeGenericMethod(type);
                    if (method != null)
                    {
                        var task = (Task)method.Invoke(this, new object[] { configuration });
                        task.Wait(TimeSpan.FromSeconds(30)); // 等待最多30秒
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during automatic save", new { ConfigurationType = type.Name });
                }
            }
        }

        private void OnConfigurationChanged(ConfigurationChangedEventArgs e)
        {
            ConfigurationChanged?.Invoke(this, e);
        }

        private void OnConfigurationLoaded(ConfigurationLoadedEventArgs e)
        {
            ConfigurationLoaded?.Invoke(this, e);
        }

        private void OnConfigurationSaved(ConfigurationSavedEventArgs e)
        {
            ConfigurationSaved?.Invoke(this, e);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConfigurationManager));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // 保存所有修改的配置
            ProcessPendingSaves(null);

            // 停止文件监视器
            foreach (var watcher in _fileWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _fileWatchers.Clear();

            // 释放其他资源
            _saveTimer?.Dispose();
            _lock?.Dispose();

            _logger?.LogInformation("ConfigurationManager disposed");
        }
    }
}