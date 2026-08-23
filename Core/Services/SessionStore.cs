using System;
using System.Collections.Generic;
using System.Linq;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 会话草稿纸：一个带上限和过期的键值表，供模型在多轮之间暂存中间结果。
    ///
    /// 取自 codex code-mode 里的 <c>store(key, value)</c> / <c>load(key)</c>。它填的是
    /// VPetLLM 一直缺的那块：插件的输出除了被塞回对话上下文之外无处可去，
    /// 想把 A 插件的结果喂给 B 插件就只能靠模型自己在上下文里转述一遍。
    ///
    /// 和 <c>Memory</c> 的分工：Memory 是长期的、语义检索的；这里是短期的、精确按键取的，
    /// 会过期、会被挤掉，不进数据库。别拿它存需要长期留存的东西。
    /// </summary>
    public static class SessionStore
    {
        private sealed class Entry
        {
            public string Value { get; init; } = "";
            public DateTime WrittenUtc { get; init; } = DateTime.UtcNow;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>最多存多少个键，超了淘汰最旧的。</summary>
        public const int MaxKeys = 32;

        /// <summary>单个值的字符上限，防止模型把整篇网页塞进来。</summary>
        public const int MaxValueChars = 4000;

        /// <summary>条目存活时长，过期即失效。</summary>
        public static TimeSpan Ttl { get; set; } = TimeSpan.FromHours(6);

        /// <summary>写入一个键。返回给模型看的回执。</summary>
        public static string Store(string key, string value)
        {
            key = (key ?? "").Trim();
            value = value ?? "";

            if (string.IsNullOrEmpty(key))
            {
                return "[SYSTEM] store failed: key must not be empty.";
            }

            var truncated = false;
            if (value.Length > MaxValueChars)
            {
                value = value.Substring(0, MaxValueChars);
                truncated = true;
            }

            lock (_lock)
            {
                PruneLocked();

                _entries[key] = new Entry { Value = value };

                // 超额时淘汰最旧的，保证这张草稿纸不会无限长大
                while (_entries.Count > MaxKeys)
                {
                    var oldest = _entries.OrderBy(kv => kv.Value.WrittenUtc).First().Key;
                    _entries.Remove(oldest);
                }
            }

            Logger.Log($"SessionStore: stored '{key}' ({value.Length} chars{(truncated ? ", truncated" : "")})");
            return truncated
                ? $"[SYSTEM] stored '{key}' (truncated to {MaxValueChars} chars)."
                : $"[SYSTEM] stored '{key}'.";
        }

        /// <summary>读取一个键；不存在或已过期返回 null。</summary>
        public static string? Load(string key)
        {
            key = (key ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return null;

            lock (_lock)
            {
                PruneLocked();
                return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
            }
        }

        public static bool Remove(string key)
        {
            lock (_lock)
            {
                return _entries.Remove((key ?? "").Trim());
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        /// <summary>当前有哪些键，用于在提示词里告诉模型草稿纸上有什么。</summary>
        public static IReadOnlyList<string> Keys
        {
            get
            {
                lock (_lock)
                {
                    PruneLocked();
                    return _entries.Keys.ToList();
                }
            }
        }

        /// <summary>
        /// 把文本里的 <c>{{key}}</c> 替换成草稿纸上的值。
        /// 技能模板在交给专家模型之前会走一遍，这样模板就能引用先前存下的中间结果。
        /// 找不到的键原样保留 —— 悄悄替换成空字符串只会让人摸不着头脑。
        /// </summary>
        public static string Interpolate(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{{")) return text ?? "";

            return System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\{\{\s*([\w\-\.]+)\s*\}\}",
                match =>
                {
                    var value = Load(match.Groups[1].Value);
                    return value ?? match.Value;
                });
        }

        /// <summary>提示词片段：告诉模型草稿纸上现在有哪些键。</summary>
        public static string Describe(string language)
        {
            var keys = Keys;
            if (keys.Count == 0) return "";

            var joined = string.Join(", ", keys);
            return language switch
            {
                "zh-hans" => $"当前暂存的键：{joined}。用 load 读取，技能模板里可以写 {{{{键名}}}} 直接引用。",
                "zh-hant" => $"當前暫存的鍵：{joined}。用 load 讀取，技能模板裡可以寫 {{{{鍵名}}}} 直接引用。",
                "ja" => $"一時保存中のキー：{joined}。load で読み出せます。スキルテンプレート内では {{{{キー名}}}} で参照できます。",
                _ => $"Keys currently stored: {joined}. Read them with load; skill templates can reference them as {{{{key}}}}."
            };
        }

        private static void PruneLocked()
        {
            if (_entries.Count == 0) return;

            var cutoff = DateTime.UtcNow - Ttl;
            var expired = _entries.Where(kv => kv.Value.WrittenUtc < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }
        }
    }
}
