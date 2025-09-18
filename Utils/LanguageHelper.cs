using Newtonsoft.Json.Linq;
using System.IO;

namespace VPetLLM.Utils
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
                if (_languageData["Language"]?["Select"] != null)
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
            if (_languageData == null || string.IsNullOrEmpty(langCode))
            {
                return defaultValue ?? $"[{path}]";
            }

            var token = _languageData.SelectToken(path);
            var value = token?[langCode]?.ToString();

            return string.IsNullOrEmpty(value) ? (defaultValue ?? $"[{path}]") : value;
        }

        public static string GetError(string key, string langCode)
        {
            if (_errorData == null || string.IsNullOrEmpty(langCode))
            {
                return $"[{key}]";
            }

            var token = _errorData.SelectToken(key);

            return token?[langCode]?.ToString() ?? $"[{key}]";
        }
    }
}