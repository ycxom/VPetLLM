using Newtonsoft.Json;
using System.Globalization;
using System.IO;
using VPetLLM.Utils;

namespace VPetLLM
{
    public partial class Setting
    {
        public LLMType Provider { get; set; } = LLMType.Ollama;
        public string Language { get; set; } = "zh-hans";
        public string PromptLanguage { get; set; } = "zh";
        public OllamaSetting Ollama { get; set; } = new OllamaSetting();
        public OpenAISetting OpenAI { get; set; } = new OpenAISetting();
        public GeminiSetting Gemini { get; set; } = new GeminiSetting();
        public string AiName { get; set; } = "虚拟宠物";
        public string UserName { get; set; } = "主人";
        public string Role { get; set; } = "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。";
        public bool FollowVPetName { get; set; } = true;
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
        public bool EnableHistoryCompression { get; set; } = false;
        public int HistoryCompressionThreshold { get; set; } = 20;
        public bool EnablePlugin { get; set; } = true;
        public List<ToolSetting> Tools { get; set; } = new List<ToolSetting>();
        public bool ShowUninstallWarning { get; set; } = true;
        public ProxySetting Proxy { get; set; } = new ProxySetting();
        private readonly string _path;

        public Setting(string path)
        {
            _path = Path.Combine(path, "VPetLLM.json");
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                JsonConvert.PopulateObject(json, this);
            }
            if (Proxy == null)
            {
                Proxy = new ProxySetting();
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

        public class ToolSetting
        {
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public string ApiKey { get; set; } = "";
            public bool IsEnabled { get; set; } = true;
        }
        public enum LLMType
        {
            Ollama,
            OpenAI,
            Gemini
        }
        public class ProxySetting
        {
            public bool IsEnabled { get; set; } = false;
            public bool FollowSystemProxy { get; set; } = true;
            public string Protocol { get; set; } = "http";
            public string Address { get; set; } = "127.0.0.1:8080";
            public bool ForAllAPI { get; set; } = true;
            public bool ForOllama { get; set; } = true;
            public bool ForOpenAI { get; set; } = true;
            public bool ForGemini { get; set; } = true;
            public bool ForMcp { get; set; } = true;
            public bool ForPlugin { get; set; } = true;
        }
    }
}