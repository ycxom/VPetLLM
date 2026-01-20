using System.Reflection;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// 聊天历史转换工具，用于将旧的按提供商分离的格式转换为新的合并格式
    /// </summary>
    public static class ChatHistoryConverter
    {
        /// <summary>
        /// 转换所有聊天历史文件到新格式
        /// </summary>
        public static void ConvertAllChatHistories(string dataDirectory)
        {
            if (!Directory.Exists(dataDirectory))
            {
                VPetLLMUtils.Logger.Log($"数据目录不存在: {dataDirectory}");
                return;
            }

            try
            {
                // 查找所有旧的聊天历史文件
                var oldFiles = Directory.GetFiles(dataDirectory, "chat_history_*.json")
                    .Concat(Directory.GetFiles(dataDirectory, "chat_history.json"))
                    .Distinct()
                    .ToArray();

                if (oldFiles.Length == 0)
                {
                    VPetLLMUtils.Logger.Log("未找到旧的聊天历史文件");
                    return;
                }

                VPetLLMUtils.Logger.Log($"找到 {oldFiles.Length} 个聊天历史文件，开始转换...");

                // 收集所有消息
                var allMessages = new List<Message>();
                var convertedFiles = new List<string>();

                foreach (var filePath in oldFiles)
                {
                    try
                    {
                        var messages = ConvertFileToMessages(filePath);
                        if (messages.Count > 0)
                        {
                            allMessages.AddRange(messages);
                            convertedFiles.Add(Path.GetFileName(filePath));
                        }
                    }
                    catch (Exception ex)
                    {
                        VPetLLMUtils.Logger.Log($"转换文件 {Path.GetFileName(filePath)} 失败: {ex.Message}");
                    }
                }

                if (allMessages.Count > 0)
                {
                    // 保存合并后的聊天历史
                    var newFilePath = Path.Combine(dataDirectory, "chat_history.json");
                    SaveMergedHistory(newFilePath, allMessages);

                    VPetLLMUtils.Logger.Log($"成功转换 {allMessages.Count} 条消息到新格式");
                    VPetLLMUtils.Logger.Log($"转换的文件: {string.Join(", ", convertedFiles)}");

                    // 可选：备份或删除旧文件
                    BackupOldFiles(oldFiles, dataDirectory);
                }
                else
                {
                    VPetLLMUtils.Logger.Log("没有找到可转换的消息");
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"转换聊天历史失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 转换单个文件为消息列表
        /// </summary>
        private static List<Message> ConvertFileToMessages(string filePath)
        {
            var messages = new List<Message>();
            var fileName = Path.GetFileName(filePath);
            var json = File.ReadAllText(filePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                return messages;
            }

            // 尝试解析为旧格式（字典格式）
            try
            {
                var oldFormat = JsonConvert.DeserializeObject<Dictionary<string, List<Message>>>(json);
                if (oldFormat is not null)
                {
                    foreach (var providerMessages in oldFormat.Values)
                    {
                        if (providerMessages is not null)
                        {
                            messages.AddRange(providerMessages);
                        }
                    }
                    VPetLLMUtils.Logger.Log($"从 {fileName} 转换了 {messages.Count} 条消息（旧字典格式）");
                    return messages;
                }
            }
            catch
            {
                // 不是旧格式，继续尝试新格式
            }

            // 尝试解析为新格式（直接消息列表）
            try
            {
                var newFormat = JsonConvert.DeserializeObject<List<Message>>(json);
                if (newFormat is not null)
                {
                    messages.AddRange(newFormat);
                    VPetLLMUtils.Logger.Log($"从 {fileName} 读取了 {messages.Count} 条消息（新列表格式）");
                    return messages;
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"文件 {fileName} 格式无法识别: {ex.Message}");
            }

            return messages;
        }

        /// <summary>
        /// 保存合并后的聊天历史
        /// </summary>
        private static void SaveMergedHistory(string filePath, List<Message> messages)
        {
            try
            {
                var json = JsonConvert.SerializeObject(messages, Formatting.Indented);

                // 使用临时文件确保写入完整性
                var tempFile = filePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // 原子替换
                if (File.Exists(filePath))
                {
                    File.Replace(tempFile, filePath, null);
                }
                else
                {
                    File.Move(tempFile, filePath);
                }

                VPetLLMUtils.Logger.Log($"已保存合并的聊天历史到: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"保存合并聊天历史失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 备份旧文件
        /// </summary>
        private static void BackupOldFiles(string[] oldFiles, string dataDirectory)
        {
            var backupDir = Path.Combine(dataDirectory, "backup_old_chat_history");
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            foreach (var filePath in oldFiles)
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    var backupPath = Path.Combine(backupDir, fileName);
                    File.Copy(filePath, backupPath, true);
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"备份文件 {Path.GetFileName(filePath)} 失败: {ex.Message}");
                }
            }

            VPetLLMUtils.Logger.Log($"旧文件已备份到: {backupDir}");
        }

        /// <summary>
        /// 在ChatCoreBase初始化时自动调用转换
        /// </summary>
        public static void InitializeConversion()
        {
            var dataDirectory = GetDataDirectory();
            ConvertAllChatHistories(dataDirectory);
        }

        private static string GetDataDirectory()
        {
            // 使用与ChatCoreBase相同的逻辑获取数据目录
            var currentAssemblyPath = Assembly.GetExecutingAssembly().Location;
            var currentDirectory = Path.GetDirectoryName(currentAssemblyPath);

            // 向上查找包含data文件夹的Mod目录
            var directory = new DirectoryInfo(currentDirectory);
            while (directory is not null)
            {
                var dataPath = Path.Combine(directory.FullName, "data");
                if (Directory.Exists(dataPath))
                {
                    return dataPath;
                }

                var pluginPath = Path.Combine(directory.FullName, "plugin");
                if (Directory.Exists(pluginPath))
                {
                    dataPath = Path.Combine(directory.FullName, "data");
                    return dataPath;
                }

                directory = directory.Parent;
            }

            return Path.Combine(currentDirectory, "data");
        }
    }
}