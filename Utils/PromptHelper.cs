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
            Logger.Log($"PromptHelper: Setting prompt file path to: {_promptFilePath}");
            ReloadPrompts();
        }

        public static void ReloadPrompts()
        {
            Logger.Log($"PromptHelper: Attempting to load prompts from: {_promptFilePath}");
            if (File.Exists(_promptFilePath))
            {
                var json = File.ReadAllText(_promptFilePath);
                _prompts = JObject.Parse(json);
                Logger.Log($"Prompts reloaded successfully. Found {_prompts.Count} prompt keys.");
                
                // 调试：检查是否包含BuyFeedback_Batch
                if (_prompts.ContainsKey("BuyFeedback_Batch"))
                {
                    Logger.Log("BuyFeedback_Batch key found in prompts.");
                }
                else
                {
                    Logger.Log("BuyFeedback_Batch key NOT found in prompts.");
                }
            }
            else
            {
                _prompts = new JObject();
                Logger.Log($"Prompt file not found at: {_promptFilePath}, initialized with empty prompts.");
            }
        }

        public static string Get(string key, string lang)
        {
            Logger.Log($"PromptHelper.Get called with key: {key}, lang: {lang}");
            
            if (_prompts == null) 
            {
                Logger.Log("PromptHelper: _prompts is null");
                return $"[Prompt Not Loaded: {key}]";
            }

            if (_prompts.TryGetValue(key, out var token))
            {
                Logger.Log($"PromptHelper: Found key {key}");
                if (token is JObject langObject)
                {
                    Logger.Log($"PromptHelper: Key {key} is a language object with {langObject.Count} languages");
                    if (langObject.TryGetValue(lang, out var value))
                    {
                        Logger.Log($"PromptHelper: Found language {lang} for key {key}");
                        return value.ToString();
                    }
                    if (langObject.TryGetValue("en", out var enValue))
                    {
                        Logger.Log($"PromptHelper: Using fallback English for key {key}");
                        return enValue.ToString();
                    }
                    Logger.Log($"PromptHelper: No suitable language found for key {key}");
                }
                else
                {
                    Logger.Log($"PromptHelper: Key {key} is not a language object");
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