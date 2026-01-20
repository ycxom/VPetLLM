namespace VPetLLM.Configuration
{
    /// <summary>
    /// 配置管理器实现
    /// </summary>
    public class SettingsManager : ISettingsManager
    {
        private readonly string _basePath;
        private readonly Dictionary<Type, ISettings> _settingsCache = new();
        private readonly object _lock = new();

        /// <inheritdoc/>
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

        public SettingsManager(string basePath)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        }

        /// <inheritdoc/>
        public T GetSettings<T>() where T : class, ISettings
        {
            lock (_lock)
            {
                if (_settingsCache.TryGetValue(typeof(T), out var cached))
                {
                    return (T)cached;
                }

                var settings = LoadSettings<T>();
                _settingsCache[typeof(T)] = settings;
                return settings;
            }
        }

        /// <inheritdoc/>
        public void SaveSettings<T>(T settings) where T : class, ISettings
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            // 验证配置
            var validationResult = settings.Validate();
            if (!validationResult.IsValid)
            {
                throw new SettingsValidationException(validationResult.Errors);
            }

            lock (_lock)
            {
                _settingsCache[typeof(T)] = settings;
                PersistSettings(settings);
            }

            // 触发变更事件
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(typeof(T), settings));
        }

        /// <inheritdoc/>
        public void ReloadSettings()
        {
            lock (_lock)
            {
                _settingsCache.Clear();
                Logger.Log("All settings cache cleared, will reload on next access");
            }
        }

        /// <inheritdoc/>
        public SettingsValidationResult ValidateAll()
        {
            var result = new SettingsValidationResult { IsValid = true };

            lock (_lock)
            {
                foreach (var settings in _settingsCache.Values)
                {
                    var settingsResult = settings.Validate();
                    if (!settingsResult.IsValid)
                    {
                        result.IsValid = false;
                        foreach (var error in settingsResult.Errors)
                        {
                            result.AddError($"[{settings.GetType().Name}] {error}");
                        }
                    }
                    foreach (var warning in settingsResult.Warnings)
                    {
                        result.AddWarning($"[{settings.GetType().Name}] {warning}");
                    }
                }
            }

            return result;
        }

        private T LoadSettings<T>() where T : class, ISettings
        {
            var filePath = GetSettingsFilePath<T>();

            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var settings = JsonConvert.DeserializeObject<T>(json);
                        if (settings is not null)
                        {
                            Logger.Log($"Loaded settings from {filePath}");
                            return settings;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error loading settings from {filePath}: {ex.Message}");
                }
            }

            // 返回默认实例
            var defaultSettings = Activator.CreateInstance<T>();
            Logger.Log($"Using default settings for {typeof(T).Name}");
            return defaultSettings;
        }

        private void PersistSettings<T>(T settings) where T : class, ISettings
        {
            var filePath = GetSettingsFilePath<T>();

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Logger.Log($"Saved settings to {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error saving settings to {filePath}: {ex.Message}");
                throw;
            }
        }

        private string GetSettingsFilePath<T>()
        {
            var typeName = typeof(T).Name;
            // 移除 "Settings" 后缀以获得更简洁的文件名
            if (typeName.EndsWith("Settings"))
            {
                typeName = typeName.Substring(0, typeName.Length - 8);
            }
            return Path.Combine(_basePath, $"VPetLLM_{typeName}.json");
        }
    }
}
