using Newtonsoft.Json;
using System.IO;

namespace VPetLLM
{
    public class Setting
    {
        public LLMType Provider { get; set; } = LLMType.Ollama;
        public OllamaSetting Ollama { get; set; } = new OllamaSetting();
        public OpenAISetting OpenAI { get; set; } = new OpenAISetting();
        public GeminiSetting Gemini { get; set; } = new GeminiSetting();
        public string AiName { get; set; } = "虚拟宠物";
        public string UserName { get; set; } = "主人";
        public string Role { get; set; } = "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。";
        public bool KeepContext { get; set; } = true;
        public bool EnableChatHistory { get; set; } = true;
        public bool SeparateChatByProvider { get; set; } = false;
        public bool AutoMigrateChatHistory { get; set; } = false;
        public bool LogAutoScroll { get; set; } = true;
        public int MaxLogCount { get; set; } = 1000;
        public bool EnableAction { get; set; } = true;
        public bool EnableBuy { get; set; } = true;
        public bool EnableState { get; set; } = true;
        public bool EnableActionExecution { get; set; } = true;
        public bool EnableMove { get; set; } = true;
        public bool EnableTime { get; set; } = true;

        private readonly string _path;

        public Setting(string path)
        {
            _path = Path.Combine(path, "VPetLLM.json");
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                JsonConvert.PopulateObject(json, this);
            }
        }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(_path, json);
        }

        public class OllamaSetting
        {
            public string Url { get; set; } = "http://localhost:11434";
            public string? Model { get; set; }
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
        }

        public class OpenAISetting
        {
            public string? ApiKey { get; set; }
            public string? Model { get; set; }
            public string Url { get; set; } = "https://api.openai.com/v1";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
        }

        public class GeminiSetting
        {
            public string? ApiKey { get; set; }
            public string Model { get; set; } = "gemini-pro";
            public string Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
        }

        public enum LLMType
        {
            Ollama,
            OpenAI,
            Gemini
        }
    }
}
