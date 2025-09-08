using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Utils
{
    public static class LanguageHelper
    {
        private static JObject _languageData = new JObject();
        public static Dictionary<string, string> LanguageDisplayMap { get; private set; } = new Dictionary<string, string>();

        public static void LoadLanguages(string path)
        {
            var langDir = Path.GetDirectoryName(path);
            if (!Directory.Exists(langDir)) return;

            foreach (var file in Directory.GetFiles(langDir, "*.json"))
            {
                var json = File.ReadAllText(file);
                var jObject = JObject.Parse(json);
                _languageData.Merge(jObject);
            }
            LanguageDisplayMap = _languageData["Language"]["Select"].ToObject<Dictionary<string, string>>();
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
    }
}