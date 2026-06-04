using System.Text;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Utils.Common;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// Handles skill creation command using Markdown-style key:value format.
    /// 
    /// Format:
    ///   name: SkillName
    ///   description: What this skill does
    ///   trigger: When to use it
    ///   action:
    ///   &lt;|say_begin|&gt; "Hello!" &lt;|say_end|&gt;
    ///   &lt;|plugin_Xxx_begin|&gt; args &lt;|plugin_Xxx_end|&gt;
    /// 
    /// The "action:" line starts the action template — all remaining lines
    /// (including blank lines) are collected verbatim as the action template.
    /// This avoids regex escaping issues with embedded quotes/commands.
    /// </summary>
    public class SkillCreateHandler : IActionHandler
    {
        private readonly SkillManager _skillManager;

        public string Keyword => "skill_create";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => PromptHelper.Get("Handler_SkillCreate_Description",
            VPetLLM.Instance.Settings?.PromptLanguage ?? "zh");

        public SkillCreateHandler(SkillManager skillManager)
        {
            _skillManager = skillManager;
        }

        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                Logger.Log($"SkillCreateHandler: Processing ({commandValue.Length} chars)");

                var parsed = ParseKeyValueLines(commandValue);

                var name = parsed.GetValueOrDefault("name");
                var description = parsed.GetValueOrDefault("description");
                var trigger = parsed.GetValueOrDefault("trigger");
                var action = parsed.GetValueOrDefault("action");

                if (string.IsNullOrWhiteSpace(name))
                {
                    Logger.Log("SkillCreateHandler: Missing required 'name'");
                    ResultAggregator.Enqueue("[SYSTEM] Skill creation failed: 'name' is required. Use format: name: MySkill");
                    await Task.CompletedTask;
                    return;
                }

                if (string.IsNullOrWhiteSpace(action))
                {
                    Logger.Log("SkillCreateHandler: Missing required 'action'");
                    ResultAggregator.Enqueue("[SYSTEM] Skill creation failed: 'action' is required. Put 'action:' on its own line, then the command template on following lines.");
                    await Task.CompletedTask;
                    return;
                }

                var skill = _skillManager.CreateSkill(name, description ?? "", trigger ?? "", action);

                if (skill is not null)
                {
                    Logger.Log($"SkillCreateHandler: Created skill '{skill.Name}' (id={skill.Id})");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{skill.Name}' created successfully! Use <|skill_call_{skill.Name}_begin|> <|skill_call_{skill.Name}_end|> to invoke it.");
                }
                else
                {
                    Logger.Log($"SkillCreateHandler: Failed to create skill '{name}' (may already exist or limit reached)");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{name}' creation failed. It may already exist or the max skill limit ({_skillManager.MaxSkills}) has been reached.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillCreateHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[SYSTEM] Skill creation error: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;

        /// <summary>
        /// Parse key:value lines. If a line is just "action:" (or "action: "),
        /// the rest of the text (all remaining lines) becomes the action value verbatim.
        /// </summary>
        internal static Dictionary<string, string> ParseKeyValueLines(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (string.IsNullOrEmpty(trimmed))
                    continue;

                // Check for "key:" pattern
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0)
                    continue;

                var key = trimmed.Substring(0, colonIdx).Trim();
                var value = trimmed.Substring(colonIdx + 1).Trim();

                if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    // Special case: if "action:" has text on the same line, use it as the value
                    // (single-line shorthand)
                    if (!string.IsNullOrEmpty(value))
                    {
                        // Check if value is a parsable key:val afterward — if so it's one-liner
                        result["action"] = value;
                    }
                    else
                    {
                        // action: on its own line → collect everything after this line
                        var sb = new StringBuilder();
                        for (int j = i + 1; j < lines.Length; j++)
                        {
                            if (sb.Length > 0)
                                sb.AppendLine();
                            sb.Append(lines[j]);
                        }
                        result["action"] = sb.ToString().TrimEnd();
                        break; // action consumes to end, no more keys
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(value))
                        result[key] = value;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Handles skill modification using the same key:value line format as create.
    /// All fields except 'name' are optional — only specified fields are updated.
    /// </summary>
    public class SkillModifyHandler : IActionHandler
    {
        private readonly SkillManager _skillManager;

        public string Keyword => "skill_modify";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => PromptHelper.Get("Handler_SkillModify_Description",
            VPetLLM.Instance.Settings?.PromptLanguage ?? "zh");

        public SkillModifyHandler(SkillManager skillManager)
        {
            _skillManager = skillManager;
        }

        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                Logger.Log($"SkillModifyHandler: Processing ({commandValue.Length} chars)");

                var parsed = SkillCreateHandler.ParseKeyValueLines(commandValue);

                var name = parsed.GetValueOrDefault("name");
                var description = parsed.GetValueOrDefault("description");
                var trigger = parsed.GetValueOrDefault("trigger");
                var action = parsed.GetValueOrDefault("action");

                if (string.IsNullOrWhiteSpace(name))
                {
                    Logger.Log("SkillModifyHandler: Missing required 'name'");
                    ResultAggregator.Enqueue("[SYSTEM] Skill modification failed: 'name' is required.");
                    await Task.CompletedTask;
                    return;
                }

                bool? enabled = null;
                var enabledStr = parsed.GetValueOrDefault("enabled");
                if (!string.IsNullOrWhiteSpace(enabledStr))
                {
                    if (bool.TryParse(enabledStr, out var e))
                        enabled = e;
                }

                // Normalize: pass null for empty strings so we don't overwrite with blanks
                var skill = _skillManager.ModifySkill(
                    name,
                    string.IsNullOrWhiteSpace(description) ? null : description,
                    string.IsNullOrWhiteSpace(trigger) ? null : trigger,
                    string.IsNullOrWhiteSpace(action) ? null : action,
                    enabled);

                if (skill is not null)
                {
                    Logger.Log($"SkillModifyHandler: Modified skill '{skill.Name}' (id={skill.Id})");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{skill.Name}' updated successfully.");
                }
                else
                {
                    Logger.Log($"SkillModifyHandler: Skill '{name}' not found");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{name}' not found. Use <|skill_list_begin|> <|skill_list_end|> to see all skills.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillModifyHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[SYSTEM] Skill modification error: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }

    /// <summary>
    /// Handles skill deletion.
    /// Format: name: SkillName (or just the skill name on its own)
    /// </summary>
    public class SkillDeleteHandler : IActionHandler
    {
        private readonly SkillManager _skillManager;

        public string Keyword => "skill_delete";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => PromptHelper.Get("Handler_SkillDelete_Description",
            VPetLLM.Instance.Settings?.PromptLanguage ?? "zh");

        public SkillDeleteHandler(SkillManager skillManager)
        {
            _skillManager = skillManager;
        }

        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                Logger.Log($"SkillDeleteHandler: Processing: {commandValue}");

                // Try key:value first, then fall back to plain text
                var parsed = SkillCreateHandler.ParseKeyValueLines(commandValue);
                var name = parsed.GetValueOrDefault("name") ?? commandValue.Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    Logger.Log("SkillDeleteHandler: Missing required 'name'");
                    ResultAggregator.Enqueue("[SYSTEM] Skill deletion failed: 'name' is required.");
                    await Task.CompletedTask;
                    return;
                }

                var success = _skillManager.DeleteSkill(name);

                if (success)
                {
                    Logger.Log($"SkillDeleteHandler: Deleted skill '{name}'");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{name}' deleted successfully.");
                }
                else
                {
                    Logger.Log($"SkillDeleteHandler: Skill '{name}' not found");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{name}' not found. Use <|skill_list_begin|> <|skill_list_end|> to see all skills.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillDeleteHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[SYSTEM] Skill deletion error: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }

    /// <summary>
    /// Handles skill invocation: executed when AI outputs <|skill_call_Xxx_begin|> ... <|skill_call_Xxx_end|>
    /// Expands the skill's ActionTemplate and executes via ActionProcessor.
    /// </summary>
    public class SkillCallHandler : IActionHandler
    {
        private readonly SkillManager _skillManager;

        /// <summary>
        /// Thread-static storage for the skill name (set by ActionProcessor when detecting skill_call_ prefix)
        /// </summary>
        [ThreadStatic]
        private static string? _currentSkillName;

        public string Keyword => "skill_call";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => PromptHelper.Get("Handler_SkillCall_Description",
            VPetLLM.Instance.Settings?.PromptLanguage ?? "zh");

        public SkillCallHandler(SkillManager skillManager)
        {
            _skillManager = skillManager;
        }

        /// <summary>
        /// Set the skill name for the next Execute call (used by ActionProcessor for skill_call_ prefix)
        /// </summary>
        public static void SetSkillName(string skillName)
        {
            _currentSkillName = skillName;
        }

        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                string skillName;

                if (!string.IsNullOrEmpty(_currentSkillName))
                {
                    skillName = _currentSkillName;
                    _currentSkillName = null;
                    Logger.Log($"SkillCallHandler: New format - skill name: {skillName}");
                }
                else
                {
                    skillName = commandValue.Trim();
                    Logger.Log($"SkillCallHandler: Skill name from value: {skillName}");
                }

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    Logger.Log("SkillCallHandler: No skill name provided");
                    ResultAggregator.Enqueue("[SYSTEM] Skill call failed: no skill name specified.");
                    return;
                }

                var skill = _skillManager.GetSkill(skillName);

                if (skill is null)
                {
                    Logger.Log($"SkillCallHandler: Skill '{skillName}' not found");
                    var availableSkills = string.Join(", ", _skillManager.GetAllSkills().Select(s => s.Name));
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{skillName}' not found. Available skills: {(string.IsNullOrEmpty(availableSkills) ? "(none)" : availableSkills)}");
                    return;
                }

                if (!skill.Enabled)
                {
                    Logger.Log($"SkillCallHandler: Skill '{skillName}' is disabled");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{skillName}' is currently disabled.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(skill.ActionTemplate))
                {
                    Logger.Log($"SkillCallHandler: Skill '{skillName}' has empty action template");
                    ResultAggregator.Enqueue($"[SYSTEM] Skill '{skillName}' has no action template defined.");
                    return;
                }

                // Record usage
                _skillManager.RecordSkillUsage(skillName);

                var lang = VPetLLM.Instance.Settings?.PromptLanguage ?? "zh";
                var aiName = VPetLLM.Instance.Settings?.AiName ?? "AI";
                var userName = VPetLLM.Instance.Settings?.UserName ?? "User";

                // Build expert system prompt — concise, no conversation history
                var systemPrompt = lang == "zh"
                    ? $"你是 {aiName}，正在执行技能 \"{skill.Name}\"。只输出执行该技能所需的命令，不要聊天。技能模板：{skill.ActionTemplate}"
                    : $"You are {aiName}, executing skill \"{skill.Name}\". Only output the commands needed to execute this skill. Do not chat. Skill template: {skill.ActionTemplate}";

                var userMessage = lang == "zh"
                    ? $"请执行技能: {skill.Name}\n描述: {skill.Description}\n触发条件: {skill.TriggerHint}\n\n根据模板执行并返回结果。"
                    : $"Execute skill: {skill.Name}\nDescription: {skill.Description}\nTrigger: {skill.TriggerHint}\n\nExecute based on the template and return results.";

                var chatCore = VPetLLM.Instance.ChatCore;
                string expertResponse = null;
                bool usedExpertLlama = false;

                // Try expert LLM call first
                if (chatCore is not null)
                {
                    try
                    {
                        Logger.Log($"SkillCallHandler: Invoking expert LLM for skill '{skillName}'...");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        expertResponse = await chatCore.Summarize(systemPrompt, userMessage);
                        sw.Stop();
                        usedExpertLlama = true;
                        Logger.Log($"SkillCallHandler: Expert LLM responded in {sw.ElapsedMilliseconds}ms: {expertResponse}");
                    }
                    catch (Exception llamaEx)
                    {
                        Logger.Log($"SkillCallHandler: Expert LLM call failed, falling back to direct execution: {llamaEx.Message}");
                    }
                }

                // Process the response (from expert LLM or direct template fallback)
                var responseToProcess = usedExpertLlama && !string.IsNullOrWhiteSpace(expertResponse)
                    ? expertResponse
                    : skill.ActionTemplate;

                if (!usedExpertLlama)
                {
                    Logger.Log($"SkillCallHandler: Using direct ActionTemplate execution for '{skillName}'");
                }

                var actionProcessor = VPetLLM.Instance.ActionProcessor;
                if (actionProcessor is not null)
                {
                    var settings = VPetLLM.Instance.Settings;
                    if (settings is not null)
                    {
                        var actions = actionProcessor.Process(responseToProcess, settings);

                        foreach (var action in actions)
                        {
                            try
                            {
                                await action.Handler.Execute(action.Value, mainWindow);
                                Logger.Log($"SkillCallHandler: Executed action '{action.Keyword}' for skill '{skillName}'");
                            }
                            catch (Exception actionEx)
                            {
                                Logger.Log($"SkillCallHandler: Error executing action '{action.Keyword}' in skill '{skillName}': {actionEx.Message}");
                            }
                        }

                        if (actions.Count == 0)
                        {
                            Logger.Log($"SkillCallHandler: No actions parsed from response for skill '{skillName}'");
                        }
                    }
                }

                // If expert LLM was used, feed results back to main conversation
                if (usedExpertLlama && !string.IsNullOrWhiteSpace(expertResponse))
                {
                    var summary = lang == "zh"
                        ? $"[技能 {skill.Name} 执行完成] {expertResponse}"
                        : $"[Skill {skill.Name} executed] {expertResponse}";
                    ResultAggregator.Enqueue(summary);
                }

                Logger.Log($"SkillCallHandler: Skill '{skillName}' executed successfully (use count incremented)");
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillCallHandler: Error executing skill: {ex.Message}");
                ResultAggregator.Enqueue($"[SYSTEM] Skill execution error: {ex.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Execute("", mainWindow);
        public int GetAnimationDuration(string animationName) => 0;
    }

    /// <summary>
    /// Handles skill listing: <|skill_list_begin|> <|skill_list_end|>
    /// Returns a formatted list of all skills to the AI context.
    /// </summary>
    public class SkillListHandler : IActionHandler
    {
        private readonly SkillManager _skillManager;

        public string Keyword => "skill_list";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => PromptHelper.Get("Handler_SkillList_Description",
            VPetLLM.Instance.Settings?.PromptLanguage ?? "zh");

        public SkillListHandler(SkillManager skillManager)
        {
            _skillManager = skillManager;
        }

        public async Task Execute(string commandValue, IMainWindow mainWindow)
        {
            try
            {
                var skills = _skillManager.GetAllSkills();

                if (skills.Count == 0)
                {
                    Logger.Log("SkillListHandler: No skills defined");
                    ResultAggregator.Enqueue("[SYSTEM] No skills defined yet. Use <|skill_create_begin|> to create a skill with key:value format: name: MySkill, description: ..., action: ... <|skill_create_end|>");
                    await Task.CompletedTask;
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[SKILLS LIST]");
                foreach (var skill in skills)
                {
                    var status = skill.Enabled ? "✓" : "✗";
                    sb.AppendLine($"{status} {skill.Name} (used {skill.UseCount}x): {skill.Description}");
                }
                sb.Append("[END SKILLS LIST]");

                var result = sb.ToString();
                Logger.Log($"SkillListHandler: Listed {skills.Count} skills");
                ResultAggregator.Enqueue(result);
            }
            catch (Exception ex)
            {
                Logger.Log($"SkillListHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[SYSTEM] Skill list error: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }
}
