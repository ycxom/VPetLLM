using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace VPetLLM.Config
{
    /// <summary>
    /// LLM后处理配置
    /// </summary>
    public class LLMConfig
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "llm_settings.json");

        /// <summary>
        /// 是否启用LLM后处理
        /// </summary>
        public bool EnableLLMPostProcessing { get; set; } = true;

        /// <summary>
        /// LLM API端点
        /// </summary>
        public string LLMApiEndpoint { get; set; } = "http://localhost:8000/v1/chat/completions";

        /// <summary>
        /// API密钥
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; set; } = "gpt-3.5-turbo";

        /// <summary>
        /// 温度参数
        /// </summary>
        public double Temperature { get; set; } = 0.7;

        /// <summary>
        /// 最大令牌数
        /// </summary>
        public int MaxTokens { get; set; } = 500;

        /// <summary>
        /// 状态更新频率（秒）
        /// </summary>
        public int StatusUpdateInterval { get; set; } = 30;

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        public static LLMConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<LLMConfig>(json) ?? new LLMConfig();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"加载LLM配置失败: {ex.Message}");
            }
            return new LLMConfig();
        }

        /// <summary>
        /// 保存配置到文件
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"保存LLM配置失败: {ex.Message}");
            }
        }
    }
}