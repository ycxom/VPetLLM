using System.Text;
using VPetLLM.Core.Data.Models;
using VPetLLM.Core.Abstractions.Base;

namespace VPetLLM.Core.Data.Managers
{
    /// <summary>
    /// Manages the lifecycle of AI-created skills: CRUD, context injection, and execution.
    /// Skills are data-driven — they expand into existing command format and execute via ActionProcessor.
    /// </summary>
    public class SkillManager
    {
        private readonly SkillsDatabase _database;
        private readonly string _providerName;

        /// <summary>
        /// Maximum number of skills the AI can create (0 = unlimited)
        /// </summary>
        public int MaxSkills { get; set; } = 50;

        public SkillManager(string providerName)
        {
            _providerName = providerName;
            _database = new SkillsDatabase(GetDatabasePath());
        }

        /// <summary>
        /// Create a new skill
        /// </summary>
        public Skill? CreateSkill(string name, string description, string triggerHint, string actionTemplate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    Logger.Log("SkillManager: Cannot create skill with empty name");
                    return null;
                }

                // Enforce max skills limit
                if (MaxSkills > 0)
                {
                    var allSkills = _database.GetAllSkills();
                    if (allSkills.Count >= MaxSkills)
                    {
                        Logger.Log($"SkillManager: Max skills limit ({MaxSkills}) reached, cannot create new skill");
                        return null;
                    }
                }

                var id = _database.CreateSkill(name.Trim(), description, triggerHint, actionTemplate);

                if (id > 0)
                {
                    Logger.Log($"SkillManager: Created skill '{name}' (id={id})");
                    return _database.GetSkill(id);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to create skill '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Modify an existing skill by name
        /// </summary>
        public Skill? ModifySkill(string name, string? newDescription, string? newTriggerHint, string? newActionTemplate, bool? enabled)
        {
            try
            {
                var skill = _database.GetSkillByName(name);
                if (skill is null)
                {
                    Logger.Log($"SkillManager: Skill '{name}' not found for modification");
                    return null;
                }

                var success = _database.UpdateSkill(skill.Id, null, newDescription, newTriggerHint, newActionTemplate, enabled);

                if (success)
                {
                    Logger.Log($"SkillManager: Modified skill '{name}' (id={skill.Id})");
                    return _database.GetSkill(skill.Id);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to modify skill '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a skill by name
        /// </summary>
        public bool DeleteSkill(string name)
        {
            try
            {
                var result = _database.DeleteSkillByName(name);

                if (result)
                    Logger.Log($"SkillManager: Deleted skill '{name}'");
                else
                    Logger.Log($"SkillManager: Skill '{name}' not found for deletion");

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to delete skill '{name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get a skill by name
        /// </summary>
        public Skill? GetSkill(string name)
        {
            try
            {
                return _database.GetSkillByName(name);
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to get skill '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all skills for listing
        /// </summary>
        public List<Skill> GetAllSkills()
        {
            try
            {
                return _database.GetAllSkills();
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to get all skills: {ex.Message}");
                return new List<Skill>();
            }
        }

        /// <summary>
        /// Get formatted skills context for system prompt injection.
        /// Tells the AI what skills are available and how to use them.
        /// </summary>
        public string GetSkillsContext()
        {
            try
            {
                var skills = _database.GetEnabledSkills();

                if (skills is null || skills.Count == 0)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("[AVAILABLE SKILLS]");
                sb.AppendLine("The following are AI-defined skills you previously created. You can call them when appropriate:");
                sb.AppendLine();

                foreach (var skill in skills)
                {
                    sb.AppendLine($"• {skill.Name}: {skill.Description}");
                    if (!string.IsNullOrWhiteSpace(skill.TriggerHint))
                        sb.AppendLine($"  Trigger: {skill.TriggerHint}");
                    sb.AppendLine($"  Usage: <|skill_call_{skill.Name}_begin|> <|skill_call_{skill.Name}_end|>");
                    sb.AppendLine();
                }

                sb.AppendLine("[END AVAILABLE SKILLS]");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to get skills context: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Inject skills context into message history (like RecordManager does for records)
        /// </summary>
        public List<Message> InjectSkillsIntoHistory(List<Message> history)
        {
            try
            {
                var skillsContext = GetSkillsContext();

                if (string.IsNullOrWhiteSpace(skillsContext))
                    return history;

                // Find the first user message
                var firstUserIndex = history.FindIndex(m => m.Role == "user");

                if (firstUserIndex == -1)
                {
                    Logger.Log("SkillManager: No user messages found, cannot inject skills");
                    return history;
                }

                var modifiedHistory = new List<Message>(history);

                var firstUserMessage = modifiedHistory[firstUserIndex];
                var modifiedContent = skillsContext + "\n" + firstUserMessage.Content;

                modifiedHistory[firstUserIndex] = new Message
                {
                    Role = firstUserMessage.Role,
                    Content = modifiedContent,
                    UnixTime = firstUserMessage.UnixTime,
                    StatusInfo = firstUserMessage.StatusInfo
                };

                Logger.Log($"SkillManager: Injected skills context into first user message");
                return modifiedHistory;
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to inject skills into history: {ex.Message}");
                return history;
            }
        }

        /// <summary>
        /// Record a skill as used (increments use count)
        /// </summary>
        public void RecordSkillUsage(string name)
        {
            try
            {
                var skill = _database.GetSkillByName(name);
                if (skill is not null)
                {
                    _database.RecordSkillUsage(skill.Id);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillManager: Failed to record skill usage for '{name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Get the database path (same as chat history / records)
        /// </summary>
        private static string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = Path.Combine(docPath, "VPetLLM", "Chat");

            if (!Directory.Exists(dataPath))
                Directory.CreateDirectory(dataPath);

            return Path.Combine(dataPath, "chat_history.db");
        }
    }
}
