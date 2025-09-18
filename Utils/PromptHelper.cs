using Newtonsoft.Json.Linq;
using System.IO;

namespace VPetLLM.Utils
{
    public static class PromptHelper
    {
        private static JObject _prompts;

        private static string _promptFilePath;

        public static void LoadPrompts(string path)
        {
            _promptFilePath = Path.Combine(Path.GetDirectoryName(path), "Prompt.json");
            ReloadPrompts();
        }

        public static void ReloadPrompts()
        {
            if (File.Exists(_promptFilePath))
            {
                var json = File.ReadAllText(_promptFilePath);
                _prompts = JObject.Parse(json);
                Logger.Log("Prompts reloaded successfully.");
            }
            else
            {
                _prompts = new JObject();
                Logger.Log("Prompt file not found, initialized with empty prompts.");
            }
        }

        public static string Get(string key, string lang)
        {
            if (_prompts == null) return $"[Prompt Not Loaded: {key}]";

            if (_prompts.TryGetValue(key, out var token))
            {
                if (token is JObject langObject)
                {
                    if (langObject.TryGetValue(lang, out var value))
                    {
                        return value.ToString();
                    }
                    if (langObject.TryGetValue("en", out var enValue))
                    {
                        return enValue.ToString();
                    }
                }
            }
            return $"[Prompt Missing: {key}]";
        }
    }
}