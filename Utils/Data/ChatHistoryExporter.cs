using Newtonsoft.Json;
using System.IO;
using VPetLLM.Core;
using VPetLLM.Utils.System;

namespace VPetLLM.Utils.Data
{
    /// <summary>
    /// 聊天历史导出工具
    /// </summary>
    public static class ChatHistoryExporter
    {
        /// <summary>
        /// 导出所有历史记录到 JSON 文件
        /// </summary>
        public static bool ExportToJson(string dbPath, string outputPath)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    Logger.Log("数据库文件不存在");
                    return false;
                }

                using var database = new ChatHistoryDatabase(dbPath);
                var allHistory = database.GetAllHistory();

                if (allHistory.Count == 0)
                {
                    Logger.Log("没有历史记录可导出");
                    return false;
                }

                var json = JsonConvert.SerializeObject(allHistory, Formatting.Indented);
                File.WriteAllText(outputPath, json);

                Logger.Log($"成功导出 {allHistory.Count} 条历史记录到: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"导出历史记录失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 按提供商导出历史记录
        /// </summary>
        public static bool ExportByProvider(string dbPath, string provider, string outputPath)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    Logger.Log("数据库文件不存在");
                    return false;
                }

                using var database = new ChatHistoryDatabase(dbPath);
                var history = database.GetHistory(provider);

                if (history.Count == 0)
                {
                    Logger.Log($"提供商 {provider} 没有历史记录");
                    return false;
                }

                var json = JsonConvert.SerializeObject(history, Formatting.Indented);
                File.WriteAllText(outputPath, json);

                Logger.Log($"成功导出 {history.Count} 条历史记录 (提供商: {provider}) 到: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"导出历史记录失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 JSON 文件导入历史记录到数据库
        /// </summary>
        public static bool ImportFromJson(string dbPath, string jsonPath, string provider)
        {
            try
            {
                if (!File.Exists(jsonPath))
                {
                    Logger.Log("JSON 文件不存在");
                    return false;
                }

                var json = File.ReadAllText(jsonPath);
                var messages = JsonConvert.DeserializeObject<List<Message>>(json);

                if (messages == null || messages.Count == 0)
                {
                    Logger.Log("JSON 文件中没有有效的消息");
                    return false;
                }

                using var database = new ChatHistoryDatabase(dbPath);
                database.AddMessages(provider, messages);

                Logger.Log($"成功导入 {messages.Count} 条历史记录到数据库 (提供商: {provider})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"导入历史记录失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出为可读的文本格式
        /// </summary>
        public static bool ExportToText(string dbPath, string outputPath, string provider = null)
        {
            try
            {
                if (!File.Exists(dbPath))
                {
                    Logger.Log("数据库文件不存在");
                    return false;
                }

                using var database = new ChatHistoryDatabase(dbPath);
                var history = string.IsNullOrEmpty(provider)
                    ? database.GetAllHistory()
                    : database.GetHistory(provider);

                if (history.Count == 0)
                {
                    Logger.Log("没有历史记录可导出");
                    return false;
                }

                using var writer = new StreamWriter(outputPath);
                writer.WriteLine($"聊天历史记录导出");
                writer.WriteLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                if (!string.IsNullOrEmpty(provider))
                {
                    writer.WriteLine($"提供商: {provider}");
                }
                writer.WriteLine($"总消息数: {history.Count}");
                writer.WriteLine(new string('=', 80));
                writer.WriteLine();

                foreach (var message in history)
                {
                    var timestamp = message.UnixTime.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(message.UnixTime.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : "未知时间";

                    writer.WriteLine($"[{timestamp}] {message.Role}:");
                    writer.WriteLine(message.Content);

                    if (!string.IsNullOrEmpty(message.StatusInfo))
                    {
                        writer.WriteLine($"  (状态: {message.StatusInfo})");
                    }

                    writer.WriteLine(new string('-', 80));
                    writer.WriteLine();
                }

                Logger.Log($"成功导出历史记录到文本文件: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"导出文本格式失败: {ex.Message}");
                return false;
            }
        }
    }
}
