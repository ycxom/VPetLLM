using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace VPetLLM.Utils
{
    public static class PromptHelper
    {
        private static JObject _prompts;

        public static void LoadPrompts(string path)
        {
            var promptPath = Path.Combine(Path.GetDirectoryName(path), "Prompt.json");
            if (File.Exists(promptPath))
            {
                var json = File.ReadAllText(promptPath);
                _prompts = JObject.Parse(json);
            }
            else
            {
                _prompts = new JObject();
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