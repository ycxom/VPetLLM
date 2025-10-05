using Newtonsoft.Json;
using System.IO;

namespace VPetLLM.Core
{
    public class HistoryManager
    {
        private List<Message> _history = new List<Message>();
        private readonly Setting _settings;
        private readonly string _historyFilePath;
        public string HistoryFilePath => _historyFilePath;
        public string LongTermMemoryFilePath { get; }

        private readonly ChatCoreBase _chatCore;

        public HistoryManager(Setting settings, string name, ChatCoreBase chatCore)
        {
            _settings = settings;
            _historyFilePath = GetHistoryFilePath(name);
            LongTermMemoryFilePath = Path.ChangeExtension(_historyFilePath, ".memory.txt");
            _chatCore = chatCore;
            LoadHistory();
        }

        public List<Message> GetHistory() => _history;

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
            if (_settings.EnableHistoryCompression && _history.Count >= _settings.HistoryCompressionThreshold)
            {
                await CompressHistory();
            }
            _history.Add(message);
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
            var prompt = $"请将以下多轮对话总结为一段摘要，以便后续机器人能理解上下文。总结时不要模仿任何角色，内容要尽可能简洁，只保留核心信息，避免任何与总结任务无关的词语。要总结的内容如下:\n{historyText}";

            var summary = await _chatCore.Summarize(prompt);

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