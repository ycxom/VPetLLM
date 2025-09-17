using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPetLLM.Utils
{
    /// <summary>
    /// DIY TTS 配置管理类
    /// </summary>
    public class DIYTTSConfig
    {
        private static readonly string ConfigDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "DiyTTS");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "Config.json");

        /// <summary>
        /// DIY TTS 配置数据
        /// </summary>
        public class DIYTTSConfigData
        {
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; } = false;

            [JsonPropertyName("baseUrl")]
            public string BaseUrl { get; set; } = "https://api.example.com/tts";

            [JsonPropertyName("method")]
            public string Method { get; set; } = "POST";

            [JsonPropertyName("contentType")]
            public string ContentType { get; set; } = "application/json";

            [JsonPropertyName("requestBody")]
            public Dictionary<string, object> RequestBody { get; set; } = new Dictionary<string, object>
            {
                { "text", "{text}" },
                { "voice", "default" },
                { "format", "mp3" }
            };

            [JsonPropertyName("customHeaders")]
            public Dictionary<string, string> CustomHeaders { get; set; } = new Dictionary<string, string>
            {
                { "User-Agent", "VPetLLM" },
                { "Accept", "audio/mpeg" }
            };

            [JsonPropertyName("responseFormat")]
            public string ResponseFormat { get; set; } = "mp3";

            [JsonPropertyName("timeout")]
            public int Timeout { get; set; } = 30000;

            [JsonPropertyName("description")]
            public string Description { get; set; } = "DIY TTS 配置 - 支持自定义 API 接口";
        }

        /// <summary>
        /// 加载 DIY TTS 配置
        /// </summary>
        /// <returns>配置数据</returns>
        public static DIYTTSConfigData LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    Logger.Log("DIY TTS: 配置文件不存在，创建默认配置");
                    var defaultConfig = CreateDefaultConfig();
                    SaveConfig(defaultConfig);
                    return defaultConfig;
                }

                var jsonContent = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<DIYTTSConfigData>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                });

                if (config == null)
                {
                    Logger.Log("DIY TTS: 配置文件解析失败，使用默认配置");
                    return CreateDefaultConfig();
                }

                Logger.Log($"DIY TTS: 成功加载配置文件: {ConfigFilePath}");
                return config;
            }
            catch (Exception ex)
            {
                Logger.Log($"DIY TTS: 加载配置文件失败: {ex.Message}，使用默认配置");
                return CreateDefaultConfig();
            }
        }

        /// <summary>
        /// 保存 DIY TTS 配置
        /// </summary>
        /// <param name="config">配置数据</param>
        public static void SaveConfig(DIYTTSConfigData config)
        {
            try
            {
                // 确保目录存在
                Directory.CreateDirectory(ConfigDirectory);

                // 强制设置 User-Agent 为 VPetLLM
                if (config.CustomHeaders.ContainsKey("User-Agent"))
                {
                    config.CustomHeaders["User-Agent"] = "VPetLLM";
                }
                else
                {
                    config.CustomHeaders.Add("User-Agent", "VPetLLM");
                }

                var jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                File.WriteAllText(ConfigFilePath, jsonContent);
                Logger.Log($"DIY TTS: 配置文件已保存: {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"DIY TTS: 保存配置文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建默认配置
        /// </summary>
        /// <returns>默认配置数据</returns>
        private static DIYTTSConfigData CreateDefaultConfig()
        {
            return new DIYTTSConfigData
            {
                Enabled = false,
                BaseUrl = "https://api.example.com/tts",
                Method = "POST",
                ContentType = "application/json",
                RequestBody = new Dictionary<string, object>
                {
                    { "text", "{text}" },
                    { "voice", "default" },
                    { "format", "mp3" }
                },
                CustomHeaders = new Dictionary<string, string>
                {
                    { "User-Agent", "VPetLLM" },
                    { "Accept", "audio/mpeg" },
                    { "Content-Type", "application/json" }
                },
                ResponseFormat = "mp3",
                Timeout = 30000,
                Description = "DIY TTS 配置 - 支持自定义 API 接口。请根据您的 TTS 服务 API 文档修改此配置。"
            };
        }

        /// <summary>
        /// 获取配置文件路径
        /// </summary>
        /// <returns>配置文件完整路径</returns>
        public static string GetConfigFilePath()
        {
            return ConfigFilePath;
        }

        /// <summary>
        /// 检查配置是否有效
        /// </summary>
        /// <param name="config">配置数据</param>
        /// <returns>是否有效</returns>
        public static bool IsValidConfig(DIYTTSConfigData config)
        {
            if (config == null) return false;
            if (string.IsNullOrWhiteSpace(config.BaseUrl)) return false;
            if (string.IsNullOrWhiteSpace(config.Method)) return false;
            if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _)) return false;
            
            return true;
        }
    }
}