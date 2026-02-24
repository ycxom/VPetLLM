using System.Text.RegularExpressions;

namespace VPetLLM.Core.Data.Managers
{
    public class HistoryManager
    {
        private List<Message> _history = new List<Message>();
        private readonly Setting _settings;
        private readonly string _providerName;
        private readonly ChatCoreBase _chatCore;
        private SystemMessageProvider _systemMessageProvider;
        private readonly ChatHistoryDatabase _database;

        public HistoryManager(Setting settings, string name, ChatCoreBase chatCore)
        {
            _settings = settings;
            _providerName = name;
            _chatCore = chatCore;
            _database = new ChatHistoryDatabase(GetDatabasePath());

            // 迁移旧的 JSON 数据到 SQLite
            MigrateFromJsonIfNeeded();

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
            try
            {
                if (_settings.SeparateChatByProvider)
                {
                    _history = _database.GetHistory(_providerName);
                }
                else
                {
                    _history = _database.GetAllHistory();
                }

                Logger.Log($"从数据库加载了 {_history.Count} 条历史记录");
            }
            catch (Exception ex)
            {
                Logger.Log($"加载历史记录失败: {ex.Message}");
                _history = new List<Message>();
            }
        }

        public void SaveHistory()
        {
            try
            {
                // SQLite 模式下，消息已经实时保存，这里只是为了兼容性保留接口
                // 如果需要，可以在这里做一次完整的同步
                Logger.Log($"历史记录已保存到数据库 (提供商: {_providerName})");
            }
            catch (Exception ex)
            {
                Logger.Log($"保存历史记录失败: {ex.Message}");
            }
        }

        public async Task AddMessage(Message message)
        {
            if (_settings.EnableHistoryCompression && ShouldCompress())
            {
                await CompressHistory();
            }

            // 如果允许获取当前时间，仅写入Unix时间戳，显示时再根据UnixTime动态补全时间前缀
            // 只在UnixTime未设置时才设置（避免覆盖CreateUserMessage中已设置的值）
            if (_settings.EnableTime && message.Role == "user" && !message.UnixTime.HasValue)
            {
                message.UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            }

            // 如果启用了减少输入token消耗且是用户消息，设置状态信息字段（不修改Content）
            // 只在StatusInfo未设置时才设置（避免覆盖CreateUserMessage中已设置的值）
            if (_settings.ReduceInputTokenUsage && message.Role == "user" && _systemMessageProvider is not null && string.IsNullOrEmpty(message.StatusInfo))
            {
                var statusString = _systemMessageProvider.GetStatusString();
                if (!string.IsNullOrEmpty(statusString))
                {
                    // 设置StatusInfo字段，DisplayContent会动态组合
                    message.StatusInfo = statusString;
                }
            }

            _history.Add(message);

            // 实时保存到数据库
            try
            {
                _database.AddMessage(_providerName, message);
            }
            catch (Exception ex)
            {
                Logger.Log($"保存消息到数据库失败: {ex.Message}");
            }
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

            // 从数据库中清除
            try
            {
                if (_settings.SeparateChatByProvider)
                {
                    _database.ClearHistory(_providerName);
                }
                else
                {
                    _database.ClearAllHistory();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"清除数据库历史记录失败: {ex.Message}");
            }
        }

        private async Task CompressHistory()
        {
            var validHistory = _history.Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList();

            // 计算保留消息数
            var retainCount = Math.Max(1, _settings.CompressionRetainCount);

            // 保留最后 retainCount 条消息（确保不超过总消息数）
            if (validHistory.Count <= retainCount)
            {
                return; // 消息不够，无需压缩
            }

            var messagesToKeep = validHistory.Skip(validHistory.Count - retainCount).ToList();
            var historyToCompress = validHistory.Take(validHistory.Count - retainCount)
                                                 .Where(m => m.Role == "user" || m.Role == "assistant")
                                                 .ToList();

            if (!historyToCompress.Any())
            {
                return;
            }

            var historyText = string.Join("\n", historyToCompress.Select(m => m.Content));
            var systemPrompt = PromptHelper.Get("Context_Summary_Prefix", _settings.PromptLanguage);

            // AI 动态保留模式：让 AI 判断应该额外保留多少条近期消息
            if (_settings.EnableAIRetainCount)
            {
                systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RetainHint", _settings.PromptLanguage);
            }

            // 压缩时写入记忆：指示 AI 将重要事件写入记忆库
            if (_settings.EnableCompressionRecords && _chatCore?.RecordManager != null)
            {
                systemPrompt += "\n" + PromptHelper.Get("Context_Summary_RecordHint", _settings.PromptLanguage);
            }

            var summary = await _chatCore.Summarize(systemPrompt, historyText);

            if (string.IsNullOrWhiteSpace(summary))
            {
                return;
            }

            // 从总结中提取并执行记忆命令，然后清理总结文本
            if (_settings.EnableCompressionRecords && _chatCore?.RecordManager != null)
            {
                summary = ExtractAndExecuteRecordCommands(summary);
            }

            var newHistory = new List<Message>
            {
                new Message { Role = "assistant", Content = summary }
            };
            newHistory.AddRange(messagesToKeep);
            _history = newHistory;

            // 更新数据库
            try
            {
                _database.UpdateHistory(_providerName, _history);
            }
            catch (Exception ex)
            {
                Logger.Log($"压缩历史记录后更新数据库失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从总结文本中提取 record 命令并执行，返回清理后的纯总结文本
        /// </summary>
        private string ExtractAndExecuteRecordCommands(string summary)
        {
            try
            {
                var recordRegex = new Regex(
                    @"<\|\s*record\s*_begin\s*\|>(.*?)<\|\s*record\s*_end\s*\|>",
                    RegexOptions.Singleline);

                var matches = recordRegex.Matches(summary);
                if (matches.Count == 0)
                    return summary;

                var textRegex = new Regex(@"text\s*\(\s*""([^""]*)""\s*\)");
                var weightRegex = new Regex(@"weight\s*\(\s*(\d+)\s*\)");

                foreach (Match match in matches)
                {
                    var commandValue = match.Groups[1].Value;
                    var textMatch = textRegex.Match(commandValue);
                    var weightMatch = weightRegex.Match(commandValue);

                    if (textMatch.Success)
                    {
                        var content = textMatch.Groups[1].Value;
                        var weight = weightMatch.Success ? int.Parse(weightMatch.Groups[1].Value) : 5;
                        weight = Math.Clamp(weight, 1, 10);

                        var recordId = _chatCore.RecordManager.CreateRecord(content, weight);
                        Logger.Log($"压缩记忆提取: 已创建记录 #{recordId} - '{content}' (权重: {weight})");
                    }
                }

                // 清理总结文本：移除所有 record 命令标签
                var cleaned = recordRegex.Replace(summary, "").Trim();
                // 清理多余空行
                cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
                return cleaned;
            }
            catch (Exception ex)
            {
                Logger.Log($"压缩记忆提取失败: {ex.Message}");
                return summary;
            }
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");
            if (!Directory.Exists(dataPath))
            {
                Directory.CreateDirectory(dataPath);
            }

            return Path.Combine(dataPath, "chat_history.db");
        }

        /// <summary>
        /// 从旧的 JSON 文件迁移数据到 SQLite
        /// </summary>
        private void MigrateFromJsonIfNeeded()
        {
            try
            {
                var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");

                if (!Directory.Exists(dataPath))
                {
                    return;
                }

                // 检查是否已经迁移过
                var migrationFlagFile = Path.Combine(dataPath, ".migrated_to_sqlite");
                if (File.Exists(migrationFlagFile))
                {
                    return;
                }

                // 查找所有 JSON 历史文件
                var jsonFiles = Directory.GetFiles(dataPath, "chat_history*.json");
                if (jsonFiles.Length == 0)
                {
                    // 没有旧文件，标记为已迁移
                    File.WriteAllText(migrationFlagFile, DateTime.Now.ToString());
                    return;
                }

                Logger.Log($"发现 {jsonFiles.Length} 个旧的 JSON 历史文件，开始迁移到 SQLite...");

                foreach (var jsonFile in jsonFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(jsonFile);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            continue;
                        }

                        var messages = JsonConvert.DeserializeObject<List<Message>>(json);
                        if (messages is not null && messages.Count > 0)
                        {
                            // 从文件名提取提供商名称
                            var fileName = Path.GetFileNameWithoutExtension(jsonFile);
                            var provider = fileName.Replace("chat_history_", "").Replace("chat_history", "default");

                            _database.AddMessages(provider, messages);
                            Logger.Log($"已迁移 {messages.Count} 条消息从 {Path.GetFileName(jsonFile)}");
                        }

                        // 备份旧文件
                        var backupPath = jsonFile + ".backup";
                        File.Move(jsonFile, backupPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"迁移文件 {Path.GetFileName(jsonFile)} 失败: {ex.Message}");
                    }
                }

                // 标记迁移完成
                File.WriteAllText(migrationFlagFile, DateTime.Now.ToString());
                Logger.Log("JSON 到 SQLite 迁移完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"迁移过程出错: {ex.Message}");
            }
        }
    }
}