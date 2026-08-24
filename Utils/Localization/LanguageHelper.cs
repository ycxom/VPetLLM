using Newtonsoft.Json.Linq;

namespace VPetLLM.Utils.Localization
{
    public static class LanguageHelper
    {
        private static JObject _languageData = new JObject();
        private static JObject _errorData = new JObject();
        private static string _langFilePath;
        public static Dictionary<string, string> LanguageDisplayMap { get; private set; } = new Dictionary<string, string>();

        public static void LoadLanguages(string path)
        {
            _langFilePath = path;
            ReloadLanguages();
        }

        public static void ReloadLanguages()
        {
            if (string.IsNullOrEmpty(_langFilePath))
            {
                return;
            }
            var langDir = Path.GetDirectoryName(_langFilePath);
            if (!Directory.Exists(langDir)) return;

            var langFile = Path.Combine(langDir, "Language.json");
            if (File.Exists(langFile))
            {
                var json = File.ReadAllText(langFile);
                _languageData = JObject.Parse(json);
                if (_languageData["Language"]?["Select"] is not null)
                    LanguageDisplayMap = _languageData["Language"]["Select"].ToObject<Dictionary<string, string>>();
            }

            var errorFile = Path.Combine(langDir, "error.json");
            if (File.Exists(errorFile))
            {
                var json = File.ReadAllText(errorFile);
                _errorData = JObject.Parse(json);
            }
        }

        public static string Get(string path, string langCode, string defaultValue = null)
        {
            if (_languageData is null || string.IsNullOrEmpty(langCode))
            {
                return defaultValue ?? $"[{path}]";
            }

            var token = _languageData.SelectToken(path);
            var value = token?[langCode]?.ToString();

            return string.IsNullOrEmpty(value) ? (defaultValue ?? $"[{path}]") : value;
        }

        /// <summary>
        /// 取词条，取不到就返回 null（而不是 <c>[key]</c> 占位串）。
        ///
        /// <see cref="Get"/> 在缺词条时返回的是字符串 <c>"[key]"</c> —— 非 null，
        /// 于是 WPF 绑定认为取值成功，XAML 里写的 <c>Default=</c>（映射到
        /// TargetNullValue/FallbackValue）永远不会触发，界面上就直接显示 <c>[Common.Add]</c> 这种东西。
        /// 绑定路径要的是"缺失即 null"，所以单开这个方法，不动 Get 的既有语义。
        /// </summary>
        public static string? GetOrNull(string path, string langCode)
        {
            if (_languageData is null || string.IsNullOrEmpty(langCode))
            {
                return null;
            }

            var value = _languageData.SelectToken(path)?[langCode]?.ToString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public static string GetError(string key, string langCode)
        {
            if (_errorData is null || string.IsNullOrEmpty(langCode))
            {
                return $"[{key}]";
            }

            // 直接通过键名访问，因为error.json的键包含点号（如 "API.Error.Unauthorized"）
            var token = _errorData[key];

            return token?[langCode]?.ToString() ?? $"[{key}]";
        }
    }
}