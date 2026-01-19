using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VPetLLM.Utils.System;

namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// Free配置管理器 - 从服务器下载并管理加密配置
    /// </summary>
    public class FreeConfigManager
    {
        private const string CONFIG_BASE_URL = "https://vpetllm.ycxom.com/api";
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
                Logger.Log("FreeConfigManager: 开始初始化配置...");

                // 下载版本信息
                var versionInfo = await DownloadVersionInfoAsync();
                if (versionInfo == null)
                {
                    Logger.Log("FreeConfigManager: 无法获取版本信息，使用本地配置");
                    return File.Exists(GetConfigPath(ASR_CONFIG_NAME)) &&
                           File.Exists(GetConfigPath(CHAT_CONFIG_NAME)) &&
                           File.Exists(GetConfigPath(TTS_CONFIG_NAME));
                }

                // 检查并更新各个配置文件
                bool asrOk = await CheckAndUpdateConfigAsync(ASR_CONFIG_NAME, versionInfo);
                bool chatOk = await CheckAndUpdateConfigAsync(CHAT_CONFIG_NAME, versionInfo);
                bool ttsOk = await CheckAndUpdateConfigAsync(TTS_CONFIG_NAME, versionInfo);

                Logger.Log($"FreeConfigManager: 配置初始化完成 - ASR:{asrOk}, Chat:{chatOk}, TTS:{ttsOk}");
                return asrOk && chatOk && ttsOk;
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 初始化配置异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载版本信息
        /// </summary>
        private static async Task<JObject> DownloadVersionInfoAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var url = $"{CONFIG_BASE_URL}/{VERSION_FILE}";
                var response = await client.GetStringAsync(url);
                return JObject.Parse(response);
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 下载版本信息失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查并更新配置文件
        /// </summary>
        private static async Task<bool> CheckAndUpdateConfigAsync(string configName, JObject versionInfo)
        {
            try
            {
                // 从版本信息中获取期望的MD5
                var expectedMd5 = versionInfo["vpetllm"]?[configName.Replace(".json", "")]?.ToString();
                if (string.IsNullOrEmpty(expectedMd5))
                {
                    Logger.Log($"FreeConfigManager: 版本信息中未找到 {configName} 的MD5");
                    return false;
                }

                var configPath = GetConfigPath(configName);
                var encryptedPath = Path.Combine(ConfigDirectory, expectedMd5);

                // 检查加密文件是否存在
                if (File.Exists(encryptedPath))
                {
                    Logger.Log($"FreeConfigManager:配置已是最新 (MD5: {expectedMd5})");
                    // Logger.Log($"FreeConfigManager: {configName} 配置已是最新 (MD5: {expectedMd5})");
                    return true;
                }

                // 需要下载新配置
                Logger.Log($"FreeConfigManager: 下载新配置 {configName}...");
                var configContent = await DownloadConfigAsync(configName);
                if (string.IsNullOrEmpty(configContent))
                {
                    return false;
                }

                // 计算下载内容的MD5
                var actualMd5 = CalculateMD5(configContent);
                if (actualMd5 != expectedMd5)
                {
                    Logger.Log($"FreeConfigManager: MD5校验失败 - 期望:{expectedMd5}, 实际:{actualMd5}");
                    return false;
                }

                // 加密并保存
                var encryptedContent = EncryptConfig(configContent);
                File.WriteAllText(encryptedPath, encryptedContent);

                Logger.Log($"FreeConfigManager: 配置更新成功，已保存为: {expectedMd5}");

                // 清理旧的加密文件（同类型配置）
                var configType = configName.Contains("ASR") ? "ASR" :
                                configName.Contains("Chat") ? "Chat" : "TTS";
                CleanOldEncryptedFiles(expectedMd5, configType);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 更新配置 {configName} 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载配置文件
        /// </summary>
        private static async Task<string> DownloadConfigAsync(string configName)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var url = $"{CONFIG_BASE_URL}/{configName}";
                return await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 下载配置 {configName} 失败: {ex.Message}");
                return null;
            }
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
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 解密配置失败: {ex.Message}");
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
                    // 如果文件名是32位MD5格式且不是当前版本
                    if (fileName.Length == 32 && fileName != currentMd5 && !fileName.Contains("."))
                    {
                        // 尝试解密并检查是否是同类型配置
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                var model = json["Model"]?.ToString();

                                // 根据Model判断配置类型，只删除同类型的旧配置
                                bool isSameType = false;
                                if (configType == "ASR" && model == "LBGAME") isSameType = true;
                                else if (configType == "Chat" && model == "bymbymbym") isSameType = true;
                                else if (configType == "TTS" && model == "vpetllm") isSameType = true;

                                if (isSameType)
                                {
                                    File.Delete(file);
                                    Logger.Log($"FreeConfigManager: 清理旧{configType}配置文件: {fileName}");
                                }
                            }
                        }
                        catch
                        {
                            // 无法解密或解析的文件，可能是损坏的，也删除
                            File.Delete(file);
                            Logger.Log($"FreeConfigManager: 清理无效配置文件: {fileName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 清理旧文件失败: {ex.Message}");
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
                // 首先尝试找到对应的加密文件
                var encryptedFile = FindEncryptedConfigFile(configName);
                if (string.IsNullOrEmpty(encryptedFile))
                {
                    Logger.Log($"FreeConfigManager: 未找到 {configName} 的配置文件");
                    return null;
                }

                // 读取并解密
                var encryptedContent = File.ReadAllText(encryptedFile);
                var decryptedContent = DecryptConfig(encryptedContent);

                if (string.IsNullOrEmpty(decryptedContent))
                {
                    Logger.Log($"FreeConfigManager: 解密配置 {configName} 失败");
                    return null;
                }

                return JObject.Parse(decryptedContent);
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 读取配置 {configName} 失败: {ex.Message}");
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
                // 获取配置名称（不含.json）
                var configKey = configName.Replace(".json", "");

                // 遍历目录中的所有32位MD5文件
                var files = Directory.GetFiles(ConfigDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    // 检查是否是32位MD5格式（无扩展名）
                    if (fileName.Length == 32 && !fileName.Contains("."))
                    {
                        // 尝试解密并验证
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                // 检查是否包含预期的字段来判断配置类型
                                if (json["API_KEY"] != null && json["API_URL"] != null && json["Model"] != null)
                                {
                                    // 通过Model字段判断配置类型
                                    var model = json["Model"]?.ToString();
                                    if ((configKey == "Free_ASR_Config" && model == "LBGAME") ||
                                        (configKey == "Free_Chat_Config" && model == "bymbymbym") ||
                                        (configKey == "Free_TTS_Config" && model == "vpetllm"))
                                    {
                                        // Logger.Log($"FreeConfigManager: 找到 {configName} 的加密文件: {fileName}");
                                        return file;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 忽略解密失败的文件
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigManager: 查找加密文件失败: {ex.Message}");
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
                if (config == null) return "";

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
                if (config == null) return "";

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
