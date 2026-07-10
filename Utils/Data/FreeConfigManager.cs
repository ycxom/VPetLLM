using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using Logger = VPetLLM.Utils.System.Logger;

namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// Free配置管理器 - 从服务器下载并管理加密配置
    /// </summary>
    public class FreeConfigManager
    {
        // 主地址在前，备用地址在后；主地址失败或CDN缓存导致内容不一致时依次尝试后续地址
        private static readonly string[] CONFIG_BASE_URLS =
        {
            "https://vpetllm.ycxom.top/api",
            "https://vpetllm.ycxom.com/api"
        };
        private const string VERSION_FILE = "vpetllm.json";
        private static readonly string ConfigDirectory;

        // 配置文件名称
        private const string ASR_CONFIG_NAME = "Free_ASR_Config.json";
        private const string CHAT_CONFIG_NAME = "Free_Chat_Config.json";
        private const string TTS_CONFIG_NAME = "Free_TTS_Config.json";

        static FreeConfigManager()
        {
            // 配置目录：文档\VPetLLM\FreeConfig\
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            ConfigDirectory = Path.Combine(documentsPath, "VPetLLM", "FreeConfig");

            // 确保目录存在
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }
        }

        /// <summary>
        /// 初始化配置 - 检查并更新所有配置文件
        /// </summary>
        public static async Task<bool> InitializeConfigsAsync()
        {
            try
            {
                var versionInfo = await DownloadVersionInfoAsync();
                if (versionInfo is null)
                {
                    var hasCached = File.Exists(GetConfigPath(ASR_CONFIG_NAME)) &&
                           File.Exists(GetConfigPath(CHAT_CONFIG_NAME)) &&
                           File.Exists(GetConfigPath(TTS_CONFIG_NAME));
                    Logger.Log($"FreeConfigManager: 版本信息下载失败，使用本地缓存: {hasCached}");
                    return hasCached;
                }

                bool asrOk = await CheckAndUpdateConfigAsync(ASR_CONFIG_NAME, versionInfo);
                bool chatOk = await CheckAndUpdateConfigAsync(CHAT_CONFIG_NAME, versionInfo);
                bool ttsOk = await CheckAndUpdateConfigAsync(TTS_CONFIG_NAME, versionInfo);

                if (!asrOk || !chatOk || !ttsOk)
                {
                    Logger.Log($"FreeConfigManager: 配置检查结果 - ASR:{asrOk}, Chat:{chatOk}, TTS:{ttsOk}");
                }

                return asrOk && chatOk && ttsOk;
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: InitializeConfigsAsync 异常: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载版本信息 - 依次尝试主/备用地址
        /// </summary>
        private static async Task<JObject> DownloadVersionInfoAsync()
        {
            foreach (var baseUrl in CONFIG_BASE_URLS)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var url = $"{baseUrl}/{VERSION_FILE}";
                    var response = await client.GetStringAsync(url);
                    return JObject.Parse(response);
                }
                catch (TaskCanceledException ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载版本信息超时: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载版本信息网络错误: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载版本信息异常: {ex.GetType().Name} - {ex.Message}");
                }
            }

            Logger.Log("FreeConfigManager: 所有地址均无法下载版本信息");
            return null;
        }

        /// <summary>
        /// 检查并更新配置文件
        /// </summary>
        private static async Task<bool> CheckAndUpdateConfigAsync(string configName, JObject versionInfo)
        {
            try
            {
                var expectedMd5 = versionInfo["vpetllm"]?[configName.Replace(".json", "")]?.ToString();
                if (string.IsNullOrEmpty(expectedMd5))
                {
                    Logger.Log($"FreeConfigManager: 版本信息中缺少 {configName} 的MD5，服务器下发的 vpetllm.json 可能未包含该字段");
                    return false;
                }

                var encryptedPath = Path.Combine(ConfigDirectory, expectedMd5);

                if (File.Exists(encryptedPath))
                {
                    return true;
                }

                var configContent = await DownloadConfigWithVerificationAsync(configName, expectedMd5);
                if (string.IsNullOrEmpty(configContent))
                {
                    Logger.Log($"FreeConfigManager: {configName} 在所有地址均下载失败或MD5校验未通过");
                    return false;
                }

                var encryptedContent = EncryptConfig(configContent);
                File.WriteAllText(encryptedPath, encryptedContent);

                var configType = configName.Contains("ASR") ? "ASR" :
                                configName.Contains("Chat") ? "Chat" : "TTS";
                CleanOldEncryptedFiles(expectedMd5, configType);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 更新配置 {configName} 异常: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载配置文件并校验MD5 - 依次尝试主/备用地址；某地址返回内容但MD5不匹配（如CDN缓存了旧内容）时自动尝试下一个地址
        /// </summary>
        private static async Task<string> DownloadConfigWithVerificationAsync(string configName, string expectedMd5)
        {
            foreach (var baseUrl in CONFIG_BASE_URLS)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var url = $"{baseUrl}/{configName}";
                    var content = await client.GetStringAsync(url);

                    if (string.IsNullOrEmpty(content))
                    {
                        Logger.Log($"FreeConfigManager: [{baseUrl}] {configName} 下载内容为空");
                        continue;
                    }

                    var actualMd5 = CalculateMD5(content);
                    if (actualMd5 != expectedMd5)
                    {
                        Logger.Log($"FreeConfigManager: [{baseUrl}] {configName} MD5校验不一致 (期望:{expectedMd5}, 实际:{actualMd5})，可能是CDN缓存了旧内容，尝试下一个地址");
                        continue;
                    }

                    return content;
                }
                catch (TaskCanceledException ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载 {configName} 超时: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载 {configName} 网络错误: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"FreeConfigManager: [{baseUrl}] 下载 {configName} 异常: {ex.GetType().Name} - {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// 加密配置内容
        /// </summary>
        private static string EncryptConfig(string content)
        {
            // 使用简单的XOR加密 + Base64
            var key = "VPetLLM_Free_Config_Key_2024";
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var keyBytes = Encoding.UTF8.GetBytes(key);

            for (int i = 0; i < contentBytes.Length; i++)
            {
                contentBytes[i] ^= keyBytes[i % keyBytes.Length];
            }

            return Convert.ToBase64String(contentBytes);
        }

        /// <summary>
        /// 解密配置内容
        /// </summary>
        private static string DecryptConfig(string encryptedContent)
        {
            try
            {
                var key = "VPetLLM_Free_Config_Key_2024";
                var contentBytes = Convert.FromBase64String(encryptedContent);
                var keyBytes = Encoding.UTF8.GetBytes(key);

                for (int i = 0; i < contentBytes.Length; i++)
                {
                    contentBytes[i] ^= keyBytes[i % keyBytes.Length];
                }

                return Encoding.UTF8.GetString(contentBytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 计算字符串的MD5
        /// </summary>
        private static string CalculateMD5(string content)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 清理旧的加密文件（同类型配置的旧版本）
        /// </summary>
        private static void CleanOldEncryptedFiles(string currentMd5, string configType)
        {
            try
            {
                var files = Directory.GetFiles(ConfigDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Length == 32 && fileName != currentMd5 && !fileName.Contains("."))
                    {
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                var model = json["Model"]?.ToString();

                                bool isSameType = false;
                                if (configType == "ASR" && model == "LBGAME") isSameType = true;
                                else if (configType == "Chat" && model == "bymbymbym") isSameType = true;
                                else if (configType == "TTS" && model == "vpetllm") isSameType = true;

                                if (isSameType)
                                {
                                    File.Delete(file);
                                }
                            }
                        }
                        catch
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 获取配置文件路径
        /// </summary>
        private static string GetConfigPath(string configName)
        {
            return Path.Combine(ConfigDirectory, configName);
        }

        /// <summary>
        /// 读取配置文件（从加密文件中读取并解密）
        /// </summary>
        public static JObject ReadConfig(string configName)
        {
            try
            {
                var encryptedFile = FindEncryptedConfigFile(configName);
                if (string.IsNullOrEmpty(encryptedFile))
                {
                    return null;
                }

                var encryptedContent = File.ReadAllText(encryptedFile);
                var decryptedContent = DecryptConfig(encryptedContent);

                if (string.IsNullOrEmpty(decryptedContent))
                {
                    return null;
                }

                return JObject.Parse(decryptedContent);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 查找配置文件对应的加密文件
        /// </summary>
        private static string FindEncryptedConfigFile(string configName)
        {
            try
            {
                var configKey = configName.Replace(".json", "");

                var files = Directory.GetFiles(ConfigDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Length == 32 && !fileName.Contains("."))
                    {
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                if (json["API_KEY"] is not null && json["API_URL"] is not null && json["Model"] is not null)
                                {
                                    var model = json["Model"]?.ToString();
                                    if ((configKey == "Free_ASR_Config" && model == "LBGAME") ||
                                        (configKey == "Free_Chat_Config" && model == "bymbymbym") ||
                                        (configKey == "Free_TTS_Config" && model == "vpetllm"))
                                    {
                                        return file;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 获取ASR配置
        /// </summary>
        public static JObject GetASRConfig() => ReadConfig(ASR_CONFIG_NAME);

        /// <summary>
        /// 获取Chat配置
        /// </summary>
        public static JObject GetChatConfig() => ReadConfig(CHAT_CONFIG_NAME);

        /// <summary>
        /// 获取TTS配置
        /// </summary>
        public static JObject GetTTSConfig() => ReadConfig(TTS_CONFIG_NAME);

        /// <summary>
        /// 获取配置中的提供者信息（根据当前语言）
        /// </summary>
        public static string GetProviderInfo(JObject config, string language = "zh-hans")
        {
            try
            {
                if (config is null) return "";

                var provider = config["Language"]?["Provider"]?[language]?.ToString();
                return provider ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 获取配置中的描述信息（根据当前语言）
        /// </summary>
        public static string GetDescription(JObject config, string language = "zh-hans")
        {
            try
            {
                if (config is null) return "";

                var description = config["Language"]?["Description"]?[language]?.ToString();
                return description ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
