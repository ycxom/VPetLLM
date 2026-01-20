namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// Free配置清理工具 - 清理未加密的配置文件
    /// </summary>
    public class FreeConfigCleaner
    {
        /// <summary>
        /// 清理未加密的配置文件，只保留加密文件
        /// </summary>
        public static void CleanUnencryptedConfigs()
        {
            try
            {
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "VPetLLM", "FreeConfig");

                if (!Directory.Exists(configDir))
                {
                    Logger.Log("FreeConfigCleaner: 配置目录不存在");
                    return;
                }

                var files = Directory.GetFiles(configDir);
                int cleanedCount = 0;

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);

                    // 只删除.json文件（未加密的配置）
                    // 保留32位MD5文件名的加密文件
                    if (fileName.EndsWith(".json"))
                    {
                        File.Delete(file);
                        Logger.Log($"FreeConfigCleaner: 删除未加密配置: {fileName}");
                        cleanedCount++;
                    }
                    // 删除非32位的其他文件（可能是临时文件或损坏文件）
                    else if (fileName.Length != 32 || fileName.Contains("."))
                    {
                        File.Delete(file);
                        Logger.Log($"FreeConfigCleaner: 删除无效文件: {fileName}");
                        cleanedCount++;
                    }
                }

                Logger.Log($"FreeConfigCleaner: 清理完成，共删除 {cleanedCount} 个文件");
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigCleaner: 清理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 列出配置目录中的所有文件
        /// </summary>
        public static void ListConfigFiles()
        {
            try
            {
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "VPetLLM", "FreeConfig");

                if (!Directory.Exists(configDir))
                {
                    Logger.Log("FreeConfigCleaner: 配置目录不存在");
                    return;
                }

                Logger.Log("=== Free配置目录文件列表 ===");
                Logger.Log($"目录: {configDir}");

                var files = Directory.GetFiles(configDir);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var fileInfo = new FileInfo(file);
                    var fileType = fileName.Length == 32 && !fileName.Contains(".") ? "加密配置" : "其他文件";
                    Logger.Log($"  [{fileType}] {fileName} ({fileInfo.Length} bytes)");
                }

                Logger.Log($"总计: {files.Length} 个文件");
            }
            catch (Exception ex)
            {
                Logger.Log($"FreeConfigCleaner: 列出文件失败: {ex.Message}");
            }
        }
    }
}
