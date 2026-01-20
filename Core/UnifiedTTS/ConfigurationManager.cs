using System.IO;
using System.Text.Json;
using VPetLLM.Models;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.UnifiedTTS
{
    /// <summary>
    /// 配置管理器实现
    /// 处理 TTS 类型选择和基本配置管理
    /// </summary>
    public class ConfigurationManager : IConfigurationManager
    {
        private TTSConfiguration _currentConfiguration;
        private readonly object _configLock = new object();
        private readonly string _configFilePath;
        private readonly IConfigurationValidator _validator;

        /// <summary>
        /// 配置变更事件
        /// </summary>
        public event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;

        public ConfigurationManager(string configFilePath = null, IConfigurationValidator validator = null)
        {
            _configFilePath = configFilePath ?? GetDefaultConfigPath();
            _validator = validator ?? new DefaultConfigurationValidator();

            // 初始化默认配置
            _currentConfiguration = CreateDefaultConfiguration();

            // 尝试加载现有配置
            _ = Task.Run(async () => await LoadConfigurationAsync());
        }

        /// <summary>
        /// 获取当前配置
        /// </summary>
        /// <returns>当前的 TTS 配置</returns>
        public TTSConfiguration GetCurrentConfiguration()
        {
            lock (_configLock)
            {
                return _currentConfiguration?.Clone();
            }
        }

        /// <summary>
        /// 更新配置
        /// </summary>
        /// <param name="config">新的配置对象</param>
        /// <returns>是否更新成功</returns>
        public async Task<bool> UpdateConfigurationAsync(TTSConfiguration config)
        {
            if (config == null)
            {
                Logger.Log("ConfigurationManager: Cannot update with null configuration");
                return false;
            }

            try
            {
                // 验证配置
                var validationResult = ValidateConfiguration(config);
                if (!validationResult.IsValid)
                {
                    Logger.Log($"ConfigurationManager: Configuration validation failed: {validationResult.ErrorMessage}");
                    return false;
                }

                TTSConfiguration oldConfig;
                lock (_configLock)
                {
                    oldConfig = _currentConfiguration?.Clone();
                    _currentConfiguration = config.Clone();
                    _currentConfiguration.UpdatedAt = DateTime.UtcNow;
                }

                // 保存配置
                var saveSuccess = await SaveConfigurationAsync(config);
                if (!saveSuccess)
                {
                    Logger.Log("ConfigurationManager: Failed to save configuration, but keeping in memory");
                }

                // 触发配置变更事件
                OnConfigurationChanged(oldConfig, _currentConfiguration, "Manual update");

                Logger.Log($"ConfigurationManager: Configuration updated successfully. Type: {config.Type}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error updating configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证配置
        /// </summary>
        /// <param name="config">要验证的配置</param>
        /// <returns>验证结果</returns>
        public ValidationResult ValidateConfiguration(TTSConfiguration config)
        {
            if (config == null)
            {
                return ValidationResult.Failure("Configuration cannot be null");
            }

            try
            {
                // 使用配置对象自身的验证
                var result = config.Validate();

                // 使用外部验证器进行额外验证
                if (_validator != null)
                {
                    var externalValidation = _validator.Validate(config);
                    if (!externalValidation.IsValid)
                    {
                        result.Errors.AddRange(externalValidation.Errors);
                        result.IsValid = false;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error validating configuration: {ex.Message}");
                return ValidationResult.Failure($"Validation error: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新加载配置
        /// </summary>
        /// <returns>是否重新加载成功</returns>
        public async Task<bool> ReloadConfigurationAsync()
        {
            try
            {
                Logger.Log("ConfigurationManager: Reloading configuration from file");
                return await LoadConfigurationAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error reloading configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 保存配置到持久化存储
        /// </summary>
        /// <param name="config">要保存的配置</param>
        /// <returns>是否保存成功</returns>
        public async Task<bool> SaveConfigurationAsync(TTSConfiguration config)
        {
            if (config == null)
            {
                Logger.Log("ConfigurationManager: Cannot save null configuration");
                return false;
            }

            try
            {
                // 确保配置目录存在
                var configDir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                // 序列化配置
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(config, options);
                await File.WriteAllTextAsync(_configFilePath, json);

                Logger.Log($"ConfigurationManager: Configuration saved to {_configFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error saving configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        /// <returns>是否加载成功</returns>
        private async Task<bool> LoadConfigurationAsync()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    Logger.Log($"ConfigurationManager: Configuration file not found at {_configFilePath}, using default configuration");
                    return await SaveConfigurationAsync(_currentConfiguration); // 保存默认配置
                }

                var json = await File.ReadAllTextAsync(_configFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Logger.Log("ConfigurationManager: Configuration file is empty, using default configuration");
                    return false;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };

                var loadedConfig = JsonSerializer.Deserialize<TTSConfiguration>(json, options);
                if (loadedConfig == null)
                {
                    Logger.Log("ConfigurationManager: Failed to deserialize configuration, using default");
                    return false;
                }

                // 验证加载的配置
                var validationResult = ValidateConfiguration(loadedConfig);
                if (!validationResult.IsValid)
                {
                    Logger.Log($"ConfigurationManager: Loaded configuration is invalid: {validationResult.ErrorMessage}");
                    return false;
                }

                TTSConfiguration oldConfig;
                lock (_configLock)
                {
                    oldConfig = _currentConfiguration?.Clone();
                    _currentConfiguration = loadedConfig;
                }

                // 触发配置变更事件
                OnConfigurationChanged(oldConfig, _currentConfiguration, "Configuration loaded from file");

                Logger.Log($"ConfigurationManager: Configuration loaded successfully. Type: {loadedConfig.Type}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error loading configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建默认配置
        /// </summary>
        /// <returns>默认配置</returns>
        private TTSConfiguration CreateDefaultConfiguration()
        {
            return new TTSConfiguration
            {
                Type = TTSType.External,
                TimeoutSeconds = 30,
                EnableLogging = true,
                MaxRetryCount = 3,
                RetryDelayMs = 1000,
                EnableCaching = false,
                CacheExpirationMinutes = 60,
                ExternalSettings = new ExternalTTSSettings
                {
                    TTSCoreType = "URL",
                    Parameters = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "BaseUrl", "http://localhost:8080/tts" }
                    }
                },
                BuiltInSettings = new BuiltInTTSSettings
                {
                    VoiceProfile = "default",
                    Speed = 1.0f,
                    Pitch = 1.0f,
                    Volume = 1.0f,
                    UseStreaming = false
                }
            };
        }

        /// <summary>
        /// 获取默认配置文件路径
        /// </summary>
        /// <returns>默认配置文件路径</returns>
        private string GetDefaultConfigPath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configDir = Path.Combine(appDataPath, "VPetLLM", "UnifiedTTS");
            return Path.Combine(configDir, "tts-config.json");
        }

        /// <summary>
        /// 触发配置变更事件
        /// </summary>
        /// <param name="oldConfig">旧配置</param>
        /// <param name="newConfig">新配置</param>
        /// <param name="reason">变更原因</param>
        private void OnConfigurationChanged(TTSConfiguration oldConfig, TTSConfiguration newConfig, string reason)
        {
            try
            {
                var args = new ConfigurationChangedEventArgs(oldConfig, newConfig, reason);
                ConfigurationChanged?.Invoke(this, args);
                Logger.Log($"ConfigurationManager: Configuration changed event fired. Reason: {reason}");
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error firing configuration changed event: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                // 保存当前配置
                _ = Task.Run(async () => await SaveConfigurationAsync(_currentConfiguration));
            }
            catch (Exception ex)
            {
                Logger.Log($"ConfigurationManager: Error during disposal: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 配置验证器接口
    /// </summary>
    public interface IConfigurationValidator
    {
        /// <summary>
        /// 验证配置
        /// </summary>
        /// <param name="config">要验证的配置</param>
        /// <returns>验证结果</returns>
        ValidationResult Validate(TTSConfiguration config);
    }

    /// <summary>
    /// 默认配置验证器
    /// </summary>
    public class DefaultConfigurationValidator : IConfigurationValidator
    {
        /// <summary>
        /// 验证配置
        /// </summary>
        /// <param name="config">要验证的配置</param>
        /// <returns>验证结果</returns>
        public ValidationResult Validate(TTSConfiguration config)
        {
            var result = new ValidationResult { IsValid = true };

            if (config == null)
            {
                result.AddError("Configuration cannot be null");
                return result;
            }

            // 验证超时设置
            if (config.TimeoutSeconds < 5 || config.TimeoutSeconds > 300)
            {
                result.AddWarning("Timeout should be between 5 and 300 seconds for optimal performance");
            }

            // 验证重试设置
            if (config.MaxRetryCount > 10)
            {
                result.AddWarning("Max retry count is very high, this may cause long delays");
            }

            if (config.RetryDelayMs < 100)
            {
                result.AddWarning("Retry delay is very short, this may cause rapid successive failures");
            }

            // 验证缓存设置
            if (config.EnableCaching && config.CacheExpirationMinutes > 1440) // 24 hours
            {
                result.AddWarning("Cache expiration time is very long, consider shorter duration");
            }

            return result;
        }
    }
}