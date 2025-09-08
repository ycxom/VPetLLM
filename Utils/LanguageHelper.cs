using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Utils
{
    public static class LanguageHelper
    {
        private static JObject _languageData;
        public static Dictionary<string, string> LanguageDisplayMap { get; private set; } = new Dictionary<string, string>();

        public static void LoadLanguages(string path)
        {
            if (!File.Exists(path))
            {
                // Fallback in case the file doesn't exist
                LanguageDisplayMap = new Dictionary<string, string> { { "en", "English" } };
                _languageData = new JObject();
                return;
            }
            var json = File.ReadAllText(path);
            _languageData = JObject.Parse(json);
            LanguageDisplayMap = _languageData["Language"]["Tab"].ToObject<Dictionary<string, string>>();
        }

        public static string Get(string path, string langCode)
        {
            if (_languageData == null || string.IsNullOrEmpty(langCode))
            {
                return $"[{path}]";
            }

            JToken token = _languageData;
            foreach (var part in path.Split('.'))
            {
                token = token?[part];
            }

            return token?[langCode]?.ToString() ?? $"[{path}]";
        }
    }
}