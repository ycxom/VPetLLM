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
                Logger.Log($"Prompts reloaded successfully. Found {_prompts.Count} prompt keys.");
            }
            else
            {
                _prompts = new JObject();
                Logger.Log($"Prompt file not found at: {_promptFilePath}, initialized with empty prompts.");
            }
        }

        public static string Get(string key, string lang)
        {
            if (_prompts == null) 
            {
                Logger.Log("PromptHelper: _prompts is null");
                return $"[Prompt Not Loaded: {key}]";
            }

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
            else
            {
                Logger.Log($"PromptHelper: Key {key} not found in prompts");
            }
            return $"[Prompt Missing: {key}]";
        }
    }
}