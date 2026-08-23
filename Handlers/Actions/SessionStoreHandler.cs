using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// 暂存草稿纸的写入端。
    /// AI 用 <c>&lt;|store_begin|&gt; key, value <|store_end|&gt;</c> 调用。
    ///
    /// 用途是把某一步的中间结果（搜索摘要、命令输出里的关键行、算好的数字）先放一边，
    /// 后面的回合或技能模板再取回来，不必靠在对话里反复转述。
    /// </summary>
    public class StoreHandler : IActionHandler
    {
        public string Keyword => "store";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        public string Description => (VPetLLM.Instance.Settings?.PromptLanguage ?? "zh") switch
        {
            "zh" => "store: 把一段内容暂存起来供后续使用。格式 <|store_begin|> 键名, 内容 <|store_end|>。" +
                    "键名不含逗号；内容可以很长但会截断。适合存中间结果，不适合存需要长期记住的事（那用记忆）。",
            _ => "store: Stash a value for later use. Format: <|store_begin|> key, value <|store_end|>. " +
                 "The key must not contain a comma; long values are truncated. Use it for intermediate results, " +
                 "not for things that must be remembered long-term (use memory for that)."
        };

        public Task Execute(string value, IMainWindow mainWindow)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ResultAggregator.Enqueue("[SYSTEM] store failed: expected `key, value`.");
                return Task.CompletedTask;
            }

            // 只在第一个逗号处切分：值里再出现逗号是正常的
            var separator = value.IndexOf(',');
            if (separator < 0)
            {
                ResultAggregator.Enqueue("[SYSTEM] store failed: expected `key, value`.");
                return Task.CompletedTask;
            }

            var key = value.Substring(0, separator).Trim();
            var payload = value.Substring(separator + 1).Trim();

            ResultAggregator.Enqueue(SessionStore.Store(key, payload));
            return Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }

    /// <summary>
    /// 暂存草稿纸的读取端。
    /// AI 用 <c>&lt;|load_begin|&gt; key <|load_end|&gt;</c> 调用。
    /// </summary>
    public class LoadHandler : IActionHandler
    {
        public string Keyword => "load";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        public string Description => (VPetLLM.Instance.Settings?.PromptLanguage ?? "zh") switch
        {
            "zh" => "load: 取回之前用 store 暂存的内容。格式 <|load_begin|> 键名 <|load_end|>。",
            _ => "load: Retrieve a value previously saved with store. Format: <|load_begin|> key <|load_end|>."
        };

        public Task Execute(string value, IMainWindow mainWindow)
        {
            var key = (value ?? "").Trim();
            if (string.IsNullOrEmpty(key))
            {
                ResultAggregator.Enqueue("[SYSTEM] load failed: no key given.");
                return Task.CompletedTask;
            }

            var stored = SessionStore.Load(key);
            if (stored is null)
            {
                var keys = SessionStore.Keys;
                var available = keys.Count == 0 ? "(none)" : string.Join(", ", keys);
                ResultAggregator.Enqueue($"[SYSTEM] load: '{key}' not found. Stored keys: {available}");
            }
            else
            {
                ResultAggregator.Enqueue($"[SYSTEM] load '{key}': {stored}");
            }

            return Task.CompletedTask;
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }
}
