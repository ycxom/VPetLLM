using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VPetLLM.Utils.Data
{
    public class ApiPresetItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string DefaultModel { get; set; }
        public List<string> Models { get; set; } = new List<string>();
    }

    public class ApiPresetCategory
    {
        public string Name { get; set; }
        public List<ApiPresetItem> Presets { get; set; } = new List<ApiPresetItem>();
    }

    public class ApiPresetData
    {
        public string Version { get; set; } = "1.0";
        public string UpdateUrl { get; set; }
        public List<ApiPresetCategory> Categories { get; set; } = new List<ApiPresetCategory>();
    }

    public class ApiPresetManager
    {
        private static readonly string PresetDirectory;
        private static readonly string PresetCachePath;
        private static readonly string VersionCachePath;
        private static readonly string ConfigFileName = "api_presets.json";
        private static readonly string VersionCacheFileName = "api_presets_version.txt";
        private static ApiPresetData _cachedData;

        private const string VERSION_URL = "https://vpetllm.ycxom.com/api/vpetllm.json";
        private const string PRESET_URL = "https://vpetllm.ycxom.com/api/VPetLLM_API_presets.json";

        static ApiPresetManager()
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            PresetDirectory = Path.Combine(documentsPath, "VPetLLM", "ApiPresets");
            PresetCachePath = Path.Combine(PresetDirectory, ConfigFileName);
            VersionCachePath = Path.Combine(PresetDirectory, VersionCacheFileName);

            if (!Directory.Exists(PresetDirectory))
            {
                Directory.CreateDirectory(PresetDirectory);
            }
        }

        public static async Task<ApiPresetData> GetPresetsAsync()
        {
            if (_cachedData != null)
                return _cachedData;

            try
            {
                var localData = LoadLocalPresets();
                var needUpdate = await CheckNeedUpdateAsync();

                if (!needUpdate && localData != null)
                {
                    _cachedData = localData;
                    return localData;
                }

                var cloudData = await DownloadPresetsAsync();
                if (cloudData != null)
                {
                    SaveLocalPresets(cloudData);
                    _cachedData = cloudData;
                    return cloudData;
                }

                if (localData != null)
                {
                    _cachedData = localData;
                    return localData;
                }

                return GetDefaultPresets();
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 获取预设失败: {ex.Message}");
                var localData = LoadLocalPresets();
                return localData ?? GetDefaultPresets();
            }
        }

        private static async Task<bool> CheckNeedUpdateAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var versionJson = await client.GetStringAsync(VERSION_URL);
                var versionInfo = JObject.Parse(versionJson);

                var cloudMd5 = versionInfo["vpetllm"]?["VPetLLM_API_presets"]?.ToString();
                if (string.IsNullOrEmpty(cloudMd5))
                {
                    Logger.Log("ApiPresetManager: 版本信息中未找到 api_presets 的MD5，强制更新");
                    return true;
                }

                var localMd5 = LoadLocalVersionHash();
                if (localMd5 != cloudMd5)
                {
                    Logger.Log($"ApiPresetManager: 检测到新版本 (云端:{cloudMd5}, 本地:{localMd5})");
                    return true;
                }

                Logger.Log("ApiPresetManager: 已是最新版本");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 检查更新失败: {ex.Message}，强制更新");
                return true;
            }
        }

        private static string LoadLocalVersionHash()
        {
            try
            {
                if (File.Exists(VersionCachePath))
                {
                    return File.ReadAllText(VersionCachePath).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 读取本地版本哈希失败: {ex.Message}");
            }
            return null;
        }

        private static void SaveLocalVersionHash(string md5)
        {
            try
            {
                File.WriteAllText(VersionCachePath, md5);
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 保存本地版本哈希失败: {ex.Message}");
            }
        }

        private static async Task<ApiPresetData> DownloadPresetsAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var json = await client.GetStringAsync(PRESET_URL);
                var data = JsonConvert.DeserializeObject<ApiPresetData>(json);

                if (data != null)
                {
                    var md5 = CalculateMD5(json);
                    SaveLocalVersionHash(md5);
                    Logger.Log($"ApiPresetManager: 预设下载成功，MD5: {md5}");
                }

                return data;
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 下载预设失败: {ex.Message}");
                return null;
            }
        }

        private static ApiPresetData LoadLocalPresets()
        {
            try
            {
                if (File.Exists(PresetCachePath))
                {
                    var json = File.ReadAllText(PresetCachePath);
                    return JsonConvert.DeserializeObject<ApiPresetData>(json);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 加载本地预设失败: {ex.Message}");
            }
            return null;
        }

        private static void SaveLocalPresets(ApiPresetData data)
        {
            try
            {
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(PresetCachePath, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"ApiPresetManager: 保存本地预设失败: {ex.Message}");
            }
        }

        private static string CalculateMD5(string content)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static ApiPresetData GetDefaultPresets()
        {
            return new ApiPresetData
            {
                Version = "1.0",
                UpdateUrl = PRESET_URL,
                Categories = new List<ApiPresetCategory>
                {
                    new ApiPresetCategory
                    {
                        Name = "官方",
                        Presets = new List<ApiPresetItem>
                        {
                            new ApiPresetItem
                            {
                                Name = "DeepSeek",
                                Url = "https://api.deepseek.com/v1",
                                DefaultModel = "deepseek-chat",
                                Models = new List<string> { "deepseek-chat", "deepseek-coder" }
                            },
                            new ApiPresetItem
                            {
                                Name = "OpenAI",
                                Url = "https://api.openai.com/v1",
                                DefaultModel = "gpt-4o",
                                Models = new List<string> { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4", "gpt-3.5-turbo" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Kimi (月之暗面)",
                                Url = "https://api.moonshot.cn/v1",
                                DefaultModel = "moonshot-v1-8k",
                                Models = new List<string> { "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k" }
                            },
                            new ApiPresetItem
                            {
                                Name = "智谱 GLM",
                                Url = "https://open.bigmodel.cn/api/paas/v4",
                                DefaultModel = "glm-4",
                                Models = new List<string> { "glm-4", "glm-4-flash", "glm-4v", "glm-3-turbo" }
                            },
                            new ApiPresetItem
                            {
                                Name = "百度 Qianfan",
                                Url = "https://qianfan.baidubce.com/v2/chat/completions",
                                DefaultModel = "ernie-4.0-8k-latest",
                                Models = new List<string> { "ernie-4.0-8k-latest", "ernie-4.0-8k-preview", "ernie-3.5-8k", "ernie-speed-128k" }
                            },
                            new ApiPresetItem
                            {
                                Name = "阿里 DashScope",
                                Url = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                                DefaultModel = "qwen-turbo",
                                Models = new List<string> { "qwen-plus", "qwen-turbo", "qwen-max", "qwen-vl-plus", "qwen-audio" }
                            },
                            new ApiPresetItem
                            {
                                Name = "腾讯 Hunyuan",
                                Url = "https://hunyuan.cloud.tencent.com/v1/chat/completions",
                                DefaultModel = "hunyuan",
                                Models = new List<string> { "hunyuan" }
                            },
                            new ApiPresetItem
                            {
                                Name = "MiniMax",
                                Url = "https://api.minimax.chat/v1",
                                DefaultModel = "MiniMax-Text-01",
                                Models = new List<string> { "MiniMax-Text-01", "abab6.5s-chat", "abab6.5-chat" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Mistral",
                                Url = "https://api.mistral.ai/v1",
                                DefaultModel = "mistral-large-latest",
                                Models = new List<string> { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "mistral-nemo" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Anthropic Claude",
                                Url = "https://api.anthropic.com/v1",
                                DefaultModel = "claude-sonnet-4-20250514",
                                Models = new List<string> { "claude-opus-4-5", "claude-sonnet-4-20250514", "claude-haiku-4-20250514" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Groq",
                                Url = "https://api.groq.com/openai/v1",
                                DefaultModel = "llama-3.1-70b-versatile",
                                Models = new List<string> { "llama-3.1-70b-versatile", "llama-3.1-8b-instant", "mixtral-8x7b-32768", "gemma2-9b-it" }
                            }
                        }
                    },
                    new ApiPresetCategory
                    {
                        Name = "第三方",
                        Presets = new List<ApiPresetItem>
                        {
                            new ApiPresetItem
                            {
                                Name = "轨迹流动",
                                Url = "https://api.liuliuai.com/v1",
                                DefaultModel = "gpt-4o-mini",
                                Models = new List<string> { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "claude-3-opus", "claude-3-sonnet", "gemini-1.5-pro", "gemini-1.5-flash" }
                            },
                            new ApiPresetItem
                            {
                                Name = "OpenRouter",
                                Url = "https://openrouter.ai/api/v1",
                                DefaultModel = "anthropic/claude-3-haiku",
                                Models = new List<string> { "anthropic/claude-3-haiku", "anthropic/claude-3-sonnet", "openai/gpt-4o", "google/gemini-pro-1.5" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Cloudflare Workers AI",
                                Url = "https://api.cloudflare.com/client/v4/accounts/YOUR_ACCOUNT_ID/ai/v1/run",
                                DefaultModel = "@cf/meta/llama-3-8b-instruct",
                                Models = new List<string> { "@cf/meta/llama-3-8b-instruct", "@cf/meta/llama-3-70b-instruct", "@cf/mistral/mistral-7b-instruct-v0.1" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Together AI",
                                Url = "https://api.together.xyz/v1",
                                DefaultModel = "meta-llama/Llama-3-70b-chat-hf",
                                Models = new List<string> { "meta-llama/Llama-3-70b-chat-hf", "meta-llama/Llama-3-8b-chat-hf", "mistralai/Mixtral-8x7B-Instruct-v0.1" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Fireworks AI",
                                Url = "https://api.fireworks.ai/inference/v1",
                                DefaultModel = "accounts/fireworks/models/llama-v3-70b-instruct",
                                Models = new List<string> { "accounts/fireworks/models/llama-v3-70b-instruct", "accounts/fireworks/models/llama-v3-8b-instruct", "accounts/fireworks/models/mixtral-8x7b-instruct" }
                            },
                            new ApiPresetItem
                            {
                                Name = "Replicate",
                                Url = "https://api.replicate.com/v1",
                                DefaultModel = "meta/meta-llama-3-70b-instruct",
                                Models = new List<string> { "meta/meta-llama-3-70b-instruct", "meta/meta-llama-3-8b-instruct", "mistralai/mixtral-8x7b-instruct" }
                            }
                        }
                    },
                    new ApiPresetCategory
                    {
                        Name = "兼容 Ollama",
                        Presets = new List<ApiPresetItem>
                        {
                            new ApiPresetItem
                            {
                                Name = "Ollama 本地",
                                Url = "http://localhost:11434/v1",
                                DefaultModel = "llama3.1",
                                Models = new List<string> { "llama3.1", "llama3", "mistral", "codellama", "llava", "qwen2.5" }
                            }
                        }
                    }
                }
            };
        }

        public static void ClearCache()
        {
            _cachedData = null;
        }

        public static List<(string Category, string Name, string Url, string DefaultModel)> GetFlatPresetList(ApiPresetData data)
        {
            var result = new List<(string, string, string, string)>();
            if (data?.Categories == null) return result;

            foreach (var category in data.Categories)
            {
                foreach (var preset in category.Presets)
                {
                    result.Add((category.Name, preset.Name, preset.Url, preset.DefaultModel));
                }
            }
            return result;
        }
    }
}