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
        }

        public class OpenAISetting
        {
            public string? ApiKey { get; set; }
            public string? Model { get; set; }
            public string Url { get; set; } = "https://api.openai.com/v1/chat/completions";
        }

        public class GeminiSetting
        {
            public string? ApiKey { get; set; }
            public string Model { get; set; } = "gemini-pro";
            public string Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent?key={API_KEY}";
        }

        public enum LLMType
        {
            Ollama,
            OpenAI,
            Gemini
        }
    }
}