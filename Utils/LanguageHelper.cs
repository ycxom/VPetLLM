using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Utils
{
    public static class LanguageHelper
    {
        private static JObject _languageData = new JObject();
        private static JObject _errorData = new JObject();
        public static Dictionary<string, string> LanguageDisplayMap { get; private set; } = new Dictionary<string, string>();

        public static void LoadLanguages(string path)
        {
            var langDir = Path.GetDirectoryName(path);
            if (!Directory.Exists(langDir)) return;

            var langFile = Path.Combine(langDir, "Language.json");
            if (File.Exists(langFile))
            {
                var json = File.ReadAllText(langFile);
                _languageData = JObject.Parse(json);
                LanguageDisplayMap = _languageData["Language"]["Select"].ToObject<Dictionary<string, string>>();
            }

            var errorFile = Path.Combine(langDir, "error.json");
            if (File.Exists(errorFile))
            {
                var json = File.ReadAllText(errorFile);
                _errorData = JObject.Parse(json);
            }
        }

        public static string Get(string path, string langCode)
        {
            if (_languageData == null || string.IsNullOrEmpty(langCode))
            {
                return $"[{path}]";
            }

            var token = _languageData.SelectToken(path);

            return token?[langCode]?.ToString() ?? $"[{path}]";
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