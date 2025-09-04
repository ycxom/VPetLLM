using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VPetLLM.Core
{
    public abstract class ChatCoreBase : IChatCore
    {
        public abstract string Name { get; }
        protected List<Message> History { get; } = new List<Message>();
        public abstract Task<string> Chat(string prompt);

        public virtual void LoadHistory()
        {
            try
            {
                var historyFile = GetHistoryFilePath();
                if (File.Exists(historyFile))
                {
                    var json = File.ReadAllText(historyFile);
                    var historyData = JsonConvert.DeserializeObject<Dictionary<string, List<Message>>>(json);
                    if (historyData != null && historyData.TryGetValue(Name, out var messages))
                    {
                        History.Clear();
                        History.AddRange(messages);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"加载聊天历史失败: {ex.Message}");
            }
        }

        public virtual void SaveHistory()
        {
            try
            {
                var historyFile = GetHistoryFilePath();
                var directory = Path.GetDirectoryName(historyFile);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // 读取现有历史数据
                Dictionary<string, List<Message>> historyData = new Dictionary<string, List<Message>>();
                if (File.Exists(historyFile))
                {
                    var existingJson = File.ReadAllText(historyFile);
                    historyData = JsonConvert.DeserializeObject<Dictionary<string, List<Message>>>(existingJson) 
                        ?? new Dictionary<string, List<Message>>();
                }
                
                // 更新当前提供商的历史
                historyData[Name] = new List<Message>(History);
                
                // 保存所有提供商的历史
                var json = JsonConvert.SerializeObject(historyData, Formatting.Indented);
                File.WriteAllText(historyFile, json);
                
                // 添加调试日志
                Logger.Log($"成功保存聊天历史到: {historyFile}");
                Logger.Log($"保存了 {History.Count} 条消息");
            }
            catch (Exception ex)
            {
                Logger.Log($"保存聊天历史失败: {ex.Message}");
            }
        }

        private string GetHistoryFilePath()
        {
            // 使用相对路径，智能识别当前Mod目录
            // 从当前程序集位置向上查找Mod目录结构
            var currentAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var currentDirectory = Path.GetDirectoryName(currentAssemblyPath);
            
            // 向上查找包含data文件夹的Mod目录
            var modDataPath = FindModDataDirectory(currentDirectory);
            
            return Path.Combine(modDataPath, "chat_history.json");
        }
        
        private string FindModDataDirectory(string startDirectory)
        {
            var directory = new DirectoryInfo(startDirectory);
            
            // 向上查找直到找到包含data文件夹的目录
            while (directory != null)
            {
                var dataPath = Path.Combine(directory.FullName, "data");
                if (Directory.Exists(dataPath))
                {
                    return dataPath;
                }
                
                // 如果当前目录有plugin子目录（Mod结构特征），则使用当前目录的data文件夹
                var pluginPath = Path.Combine(directory.FullName, "plugin");
                if (Directory.Exists(pluginPath))
                {
                    dataPath = Path.Combine(directory.FullName, "data");
                    return dataPath;
                }
                
                directory = directory.Parent;
            }
            
            // 如果找不到，使用当前目录下的data文件夹
            return Path.Combine(startDirectory, "data");
        }
        public virtual List<string> GetModels()
        {
            return new List<string>();
        }

        /// <summary>
        /// 清除聊天历史上下文
        /// </summary>
        public virtual void ClearContext()
        {
            History.RemoveAll(m => m.Role != "system"); // 保留系统角色消息
            SaveHistory(); // 清除后立即保存
        }

        /// <summary>
        /// 获取聊天历史用于编辑
        /// </summary>
        public virtual List<Message> GetHistoryForEditing()
        {
            return new List<Message>(History);
        }

        /// <summary>
        /// 更新聊天历史（用户编辑后）
        /// </summary>
        public virtual void UpdateHistory(List<Message> editedHistory)
        {
            History.Clear();
            History.AddRange(editedHistory);
            SaveHistory(); // 更新后立即保存
        }
    }

    public class Message
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}