using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VPetLLM.Core
{
    public class HistoryManager
    {
        private List<Message> _history = new List<Message>();
       private readonly Setting _settings;
       private readonly string _historyFilePath;

       public HistoryManager(Setting settings, string name)
       {
           _settings = settings;
           _historyFilePath = GetHistoryFilePath(name);
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

      public async Task AddMessage(Message message, Func<string, Task<string>> chatFunction)
      {
          _history.Add(message);
          if (_settings.EnableHistoryCompression && _history.Count > _settings.HistoryCompressionThreshold)
          {
              await CompressHistory(chatFunction);
          }
      }

        public void ClearHistory()
        {
            _history.Clear();
        }

    private async Task CompressHistory(Func<string, Task<string>> chatFunction)
    {
        var backupFilePath = _historyFilePath.Replace(".json", "_backup.json");
        File.WriteAllText(backupFilePath, JsonConvert.SerializeObject(_history, Formatting.Indented));

        var historyToCompress = _history.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
        var lastUserMessage = _history.LastOrDefault(m => m.Role == "user");

       var historyText = string.Join("\n", historyToCompress.Where(m => m.Role != "system").Select(m => $"{m.Role}: {m.Content}"));
     var prompt = $"你是一个聊天记录总结助手，请将以下对话总结为一段摘要:\n{historyText}";

     // Temporarily disable history compression to prevent infinite recursion
        var originalCompressionState = _settings.EnableHistoryCompression;
        _settings.EnableHistoryCompression = false;

        var summary = await chatFunction(prompt);

        // Restore the original state
        _settings.EnableHistoryCompression = originalCompressionState;

        _history.Clear();
        _history.Add(new Message { Role = "system", Content = _settings.Role });
        _history.Add(new Message { Role = "assistant", Content = summary });
        if (lastUserMessage != null)
        {
            _history.Add(lastUserMessage);
        }

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