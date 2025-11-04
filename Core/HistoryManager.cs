using Newtonsoft.Json;
using System.IO;
using VPetLLM.Utils;

namespace VPetLLM.Core
{
    public class HistoryManager
    {
        private List<Message> _history = new List<Message>();
        private readonly Setting _settings;
        private readonly string _historyFilePath;
        private readonly ChatCoreBase _chatCore;
        private SystemMessageProvider _systemMessageProvider;

        public HistoryManager(Setting settings, string name, ChatCoreBase chatCore)
        {
            _settings = settings;
            _historyFilePath = GetHistoryFilePath(name);
            _chatCore = chatCore;
            LoadHistory();
        }

        public void SetSystemMessageProvider(SystemMessageProvider provider)
        {
            _systemMessageProvider = provider;
        }

        public List<Message> GetHistory() => _history;

        public int GetCurrentTokenCount() => EstimateTokenCount();

        public void LoadHistory()
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                _history = JsonConvert.DeserializeObject<List<Message>>(json) ?? new List<Message>();
            }
        }

        public void SaveHistory()
        {
            var historyCopy = new List<Message>(_history);
            var json = JsonConvert.SerializeObject(historyCopy, Formatting.Indented);
            File.WriteAllText(_historyFilePath, json);
        }

        public async Task AddMessage(Message message)
        {
            if (_settings.EnableHistoryCompression && ShouldCompress())
            {
                await CompressHistory();
            }

            // 如果允许获取当前时间，仅写入Unix时间戳，显示时再根据UnixTime动态补全时间前缀
            if (_settings.EnableTime)
            {
                message.UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            }

            // 如果启用了减少输入token消耗且是用户消息，设置状态信息字段（不修改Content）
            if (_settings.ReduceInputTokenUsage && message.Role == "user" && _systemMessageProvider != null)
            {
                var statusString = _systemMessageProvider.GetStatusString();
                if (!string.IsNullOrEmpty(statusString))
                {
                    // 设置StatusInfo字段，DisplayContent会动态组合
                    message.StatusInfo = statusString;
                }
            }

            _history.Add(message);
        }

        private bool ShouldCompress()
        {
            switch (_settings.CompressionMode)
            {
                case Setting.CompressionTriggerMode.MessageCount:
                    return _history.Count >= _settings.HistoryCompressionThreshold;

                case Setting.CompressionTriggerMode.TokenCount:
                    return EstimateTokenCount() >= _settings.HistoryCompressionTokenThreshold;

                case Setting.CompressionTriggerMode.Both:
                    return _history.Count >= _settings.HistoryCompressionThreshold ||
                           EstimateTokenCount() >= _settings.HistoryCompressionTokenThreshold;

                default:
                    return _history.Count >= _settings.HistoryCompressionThreshold;
            }
        }

        private int EstimateTokenCount()
        {
            return TokenCounter.EstimateMessagesTokenCount(_history);
        }

        public void ClearHistory()
        {
            _history.Clear();
        }

        private async Task CompressHistory()
        {
            var backupFilePath = _historyFilePath.Replace(".json", "_backup.json");
            File.WriteAllText(backupFilePath, JsonConvert.SerializeObject(_history, Formatting.Indented));

            var validHistory = _history.Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList();
            var lastUserMessageIndex = validHistory.FindLastIndex(m => m.Role == "user");

            if (lastUserMessageIndex == -1)
            {
                return;
            }

            var messagesToKeep = validHistory.Skip(lastUserMessageIndex).ToList();
            var historyToCompress = validHistory.Take(lastUserMessageIndex)
                                                 .Where(m => m.Role == "user" || m.Role == "assistant")
                                                 .ToList();

            if (!historyToCompress.Any())
            {
                return;
            }

            var historyText = string.Join("\n", historyToCompress.Select(m => m.Content));
            var systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);
            var summary = await _chatCore.Summarize(systemPrompt, historyText);

            if (string.IsNullOrWhiteSpace(summary))
            {
                return;
            }

            var newHistory = new List<Message>
        {
            new Message { Role = "assistant", Content = summary }
        };
            newHistory.AddRange(messagesToKeep);
            _history = newHistory;
            SaveHistory();
        }

        private string GetHistoryFilePath(string name)
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");
            if (!Directory.Exists(dataPath))
            {
                Directory.CreateDirectory(dataPath);
            }

            if (_settings.SeparateChatByProvider)
            {
                return Path.Combine(dataPath, $"chat_history_{name.ToLower()}.json");
            }
            else
            {
                return Path.Combine(dataPath, "chat_history.json");
            }
        }
    }
}