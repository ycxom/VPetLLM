using Newtonsoft.Json;
using System.IO;

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
        public FreeSetting Free { get; set; } = new FreeSetting();
        public string AiName { get; set; } = "虚拟宠物";
        public string UserName { get; set; } = "主人";
        public string Role { get; set; } = "你是一个可爱的虚拟宠物助手，请用友好、可爱的语气回应我。";
        public bool FollowVPetName { get; set; } = true;
        public bool KeepContext { get; set; } = true;
        public bool EnableChatHistory { get; set; } = true;
        public bool SeparateChatByProvider { get; set; } = false;
        public bool LogAutoScroll { get; set; } = true;
        public int MaxLogCount { get; set; } = 1000;
        public bool EnableAction { get; set; } = true;
        public bool EnableBuy { get; set; } = true;
        public bool EnableState { get; set; } = true;
        public bool EnableActionExecution { get; set; } = true;
        public int SayTimeMultiplier { get; set; } = 200;
        public int SayTimeMin { get; set; } = 2000;
        public bool EnableMove { get; set; } = true;
        public bool EnableTime { get; set; } = true;
        public bool EnableHistoryCompression { get; set; } = false;
        public CompressionTriggerMode CompressionMode { get; set; } = CompressionTriggerMode.MessageCount;
        public int HistoryCompressionThreshold { get; set; } = 20;
        public int HistoryCompressionTokenThreshold { get; set; } = 4000;
        public bool EnablePlugin { get; set; } = true;
        public List<ToolSetting> Tools { get; set; } = new List<ToolSetting>();
        public bool ShowUninstallWarning { get; set; } = true;
        public TTSSetting TTS { get; set; } = new TTSSetting();
        public ProxySetting Proxy { get; set; } = new ProxySetting();
        public PluginStoreSetting PluginStore { get; set; } = new PluginStoreSetting();
        public Handlers.TouchFeedbackSettings TouchFeedback { get; set; } = new Handlers.TouchFeedbackSettings();
        public bool EnableBuyFeedback { get; set; } = true;
        public bool EnableLiveMode { get; set; } = false;
        public RateLimiterSetting RateLimiter { get; set; } = new RateLimiterSetting();
        public ASRSetting ASR { get; set; } = new ASRSetting();
        private readonly string _path;

        public Setting(string path)
        {
            _path = Path.Combine(path, "VPetLLM.json");
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                JsonConvert.PopulateObject(json, this);
            }
            
            // 确保所有属性都有默认值
            if (Ollama == null)
            {
                Ollama = new OllamaSetting();
            }
            if (OpenAI == null)
            {
                OpenAI = new OpenAISetting();
            }
            if (Gemini == null)
            {
                Gemini = new GeminiSetting();
            }
            if (Free == null)
            {
                Free = new FreeSetting();
            }
            if (Proxy == null)
            {
                Proxy = new ProxySetting();
            }
            if (PluginStore == null)
            {
                PluginStore = new PluginStoreSetting();
            }
            if (TTS == null)
            {
                TTS = new TTSSetting();
            }
            if (TouchFeedback == null)
            {
                TouchFeedback = new Handlers.TouchFeedbackSettings();
            }
            if (Tools == null)
            {
                Tools = new List<ToolSetting>();
            }
            if (RateLimiter == null)
            {
                RateLimiter = new RateLimiterSetting();
            }
            if (ASR == null)
            {
                ASR = new ASRSetting();
            }

            // 旧版OpenAI单节点配置迁移到多节点结构，避免用户配置丢失
            if (OpenAI == null)
            {
                OpenAI = new OpenAISetting();
            }
            if (OpenAI.OpenAINodes == null)
            {
                OpenAI.OpenAINodes = new List<OpenAINodeSetting>();
            }
            // 若无多节点但旧字段存在，则创建默认节点承载旧配置
            bool hasLegacyOpenAI =
                !string.IsNullOrWhiteSpace(OpenAI.ApiKey) ||
                !string.IsNullOrWhiteSpace(OpenAI.Model) ||
                !string.IsNullOrWhiteSpace(OpenAI.Url) ||
                OpenAI.Temperature != 0.7 ||
                OpenAI.MaxTokens != 2048 ||
                OpenAI.EnableAdvanced ||
                !OpenAI.Enabled ||
                (OpenAI.Name != null && OpenAI.Name != "OpenAI节点");

            if (OpenAI.OpenAINodes.Count == 0 && hasLegacyOpenAI)
            {
                OpenAI.OpenAINodes.Add(new OpenAINodeSetting
                {
                    ApiKey = OpenAI.ApiKey,
                    Model = OpenAI.Model,
                    Url = OpenAI.Url,
                    Temperature = OpenAI.Temperature,
                    MaxTokens = OpenAI.MaxTokens,
                    EnableAdvanced = OpenAI.EnableAdvanced,
                    Enabled = OpenAI.Enabled,
                    Name = string.IsNullOrWhiteSpace(OpenAI.Name) ? "OpenAI节点" : OpenAI.Name
                });
            }

            // 修复索引越界
            if (OpenAI.OpenAINodes.Count == 0)
            {
                OpenAI.CurrentNodeIndex = 0;
            }
            else if (OpenAI.CurrentNodeIndex < 0 || OpenAI.CurrentNodeIndex >= OpenAI.OpenAINodes.Count)
            {
                OpenAI.CurrentNodeIndex = 0;
            }

            // 旧版Gemini单节点配置迁移到多节点结构，避免用户配置丢失
            if (Gemini == null)
            {
                Gemini = new GeminiSetting();
            }
            if (Gemini.GeminiNodes == null)
            {
                Gemini.GeminiNodes = new List<GeminiNodeSetting>();
            }
            bool hasLegacyGemini =
                !string.IsNullOrWhiteSpace(Gemini.ApiKey) ||
                !string.IsNullOrWhiteSpace(Gemini.Model) ||
                !string.IsNullOrWhiteSpace(Gemini.Url) ||
                Gemini.Temperature != 0.7 ||
                Gemini.MaxTokens != 2048 ||
                Gemini.EnableAdvanced;

            if (Gemini.GeminiNodes.Count == 0 && hasLegacyGemini)
            {
                Gemini.GeminiNodes.Add(new GeminiNodeSetting
                {
                    ApiKey = Gemini.ApiKey,
                    Model = Gemini.Model,
                    Url = Gemini.Url,
                    Temperature = Gemini.Temperature,
                    MaxTokens = Gemini.MaxTokens,
                    EnableAdvanced = Gemini.EnableAdvanced,
                    EnableStreaming = Gemini.EnableStreaming,
                    Enabled = true,
                    Name = "Gemini节点"
                });
            }

            if (Gemini.GeminiNodes.Count == 0)
            {
                Gemini.CurrentNodeIndex = 0;
            }
            else if (Gemini.CurrentNodeIndex < 0 || Gemini.CurrentNodeIndex >= Gemini.GeminiNodes.Count)
            {
                Gemini.CurrentNodeIndex = 0;
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
            public bool EnableStreaming { get; set; } = false;
        }

        public class OpenAINodeSetting
        {
            public string? ApiKey { get; set; }
            public string? Model { get; set; }
            public string Url { get; set; } = "https://api.openai.com/v1";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;
            public bool Enabled { get; set; } = true;
            public string Name { get; set; } = "OpenAI节点";
            
            public OpenAISetting GetCurrentOpenAISetting()
            {
                return new OpenAISetting
                {
                    ApiKey = this.ApiKey,
                    Model = this.Model,
                    Url = this.Url,
                    Temperature = this.Temperature,
                    MaxTokens = this.MaxTokens,
                    EnableAdvanced = this.EnableAdvanced,
                    EnableStreaming = this.EnableStreaming,
                    Enabled = this.Enabled,
                    Name = this.Name
                };
            }
        }

        public class OpenAISetting
        {
            public List<OpenAINodeSetting> OpenAINodes { get; set; } = new List<OpenAINodeSetting>();
            public int CurrentNodeIndex { get; set; } = 0;
            public bool EnableLoadBalancing { get; set; } = true;
            
            // 向后兼容的属性
            public string? ApiKey { get; set; }
            public string? Model { get; set; }
            public string Url { get; set; } = "https://api.openai.com/v1";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;
            public bool Enabled { get; set; } = true;
            public string Name { get; set; } = "OpenAI节点";

            public OpenAINodeSetting GetCurrentOpenAISetting()
            {
                // 无节点时回退到兼容配置生成的默认节点
                if (OpenAINodes.Count == 0)
                {
                    return new OpenAINodeSetting
                    {
                        ApiKey = ApiKey,
                        Model = Model,
                        Url = Url,
                        Temperature = Temperature,
                        MaxTokens = MaxTokens,
                        EnableAdvanced = EnableAdvanced,
                        EnableStreaming = EnableStreaming,
                        Enabled = Enabled,
                        Name = Name
                    };
                }

                // 仅在启用的节点间进行选择/轮换
                var enabledNodes = OpenAINodes.Where(n => n.Enabled).ToList();
                if (enabledNodes.Count == 0)
                {
                    // 若没有启用的节点，回退到第一个节点（保持兼容）
                    if (CurrentNodeIndex < 0 || CurrentNodeIndex >= OpenAINodes.Count)
                        CurrentNodeIndex = 0;
                    return OpenAINodes[CurrentNodeIndex];
                }

                if (EnableLoadBalancing)
                {
                    // 轮换到下一个启用的节点
                    CurrentNodeIndex = (CurrentNodeIndex + 1) % enabledNodes.Count;
                    return enabledNodes[CurrentNodeIndex];
                }

                // 非负载均衡：如果索引越界或指向未启用节点，则回退到第一个启用的节点
                if (CurrentNodeIndex < 0 || CurrentNodeIndex >= enabledNodes.Count)
                    CurrentNodeIndex = 0;
                return enabledNodes[CurrentNodeIndex];
            }
        }

        public class GeminiNodeSetting
        {
            public string? ApiKey { get; set; }
            public string Model { get; set; } = "gemini-pro";
            public string Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;
            public bool Enabled { get; set; } = true;
            public string Name { get; set; } = "Gemini节点";
        }

        public class GeminiSetting
        {
            public List<GeminiNodeSetting> GeminiNodes { get; set; } = new List<GeminiNodeSetting>();
            public int CurrentNodeIndex { get; set; } = 0;
            public bool EnableLoadBalancing { get; set; } = true;

            // 向后兼容的属性（用于旧版自动迁移/回退）
            public string? ApiKey { get; set; }
            public string Model { get; set; } = "gemini-pro";
            public string Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;

            public GeminiNodeSetting GetCurrentGeminiSetting()
            {
                // 无节点时回退到兼容配置生成的默认节点
                if (GeminiNodes.Count == 0)
                {
                    return new GeminiNodeSetting
                    {
                        ApiKey = ApiKey,
                        Model = Model,
                        Url = Url,
                        Temperature = Temperature,
                        MaxTokens = MaxTokens,
                        EnableAdvanced = EnableAdvanced,
                        EnableStreaming = EnableStreaming,
                        Enabled = true,
                        Name = "Gemini节点"
                    };
                }

                // 仅在启用的节点间进行选择/轮换
                var enabledNodes = GeminiNodes.Where(n => n.Enabled).ToList();
                if (enabledNodes.Count == 0)
                {
                    // 若没有启用的节点，回退到第一个节点
                    if (CurrentNodeIndex < 0 || CurrentNodeIndex >= GeminiNodes.Count)
                        CurrentNodeIndex = 0;
                    return GeminiNodes[CurrentNodeIndex];
                }

                if (EnableLoadBalancing)
                {
                    // 轮换到下一个启用的节点
                    CurrentNodeIndex = (CurrentNodeIndex + 1) % enabledNodes.Count;
                    return enabledNodes[CurrentNodeIndex];
                }

                // 非负载均衡：如果索引越界或指向未启用节点，则回退到第一个启用的节点
                if (CurrentNodeIndex < 0 || CurrentNodeIndex >= enabledNodes.Count)
                    CurrentNodeIndex = 0;
                return enabledNodes[CurrentNodeIndex];
            }
        }

        public class FreeSetting
        {
            public string? Model { get; set; }
            public double Temperature { get; set; } = 0.7;
            public int MaxTokens { get; set; } = 2048;
            public bool EnableAdvanced { get; set; } = false;
            public bool EnableStreaming { get; set; } = false;
        }

        public class ToolSetting
        {
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public string ApiKey { get; set; } = "";
            public string Description { get; set; } = "";
            public bool IsEnabled { get; set; } = true;
        }
        public enum LLMType
        {
            Ollama,
            OpenAI,
            Gemini,
            Free
        }

        public enum CompressionTriggerMode
        {
            MessageCount,  // 按消息数量触发
            TokenCount,    // 按Token数量触发
            Both           // 两者任一达到阈值即触发
        }
        public class ProxySetting
        {
            public bool IsEnabled { get; set; } = false;
            public bool FollowSystemProxy { get; set; } = false;
            public string Protocol { get; set; } = "http";
            public string Address { get; set; } = "127.0.0.1:8080";
            public bool ForAllAPI { get; set; } = false;
            public bool ForOllama { get; set; } = false;
            public bool ForOpenAI { get; set; } = false;
            public bool ForGemini { get; set; } = false;
            public bool ForFree { get; set; } = false;
            public bool ForTTS { get; set; } = false;
            public bool ForASR { get; set; } = false;
            public bool ForMcp { get; set; } = false;
            public bool ForPlugin { get; set; } = false;
        }

        public class PluginStoreSetting
        {
            public bool UseProxy { get; set; } = true;
            public string ProxyUrl { get; set; } = "https://ghfast.top";
        }

        public class TTSSetting
        {
            public bool IsEnabled { get; set; } = false;
            public string Provider { get; set; } = "URL";
            public bool OnlyPlayAIResponse { get; set; } = true;
            public bool AutoPlay { get; set; } = true;
            public double Volume { get; set; } = 1.0; // 基础音量，范围0到10
            public double Speed { get; set; } = 1.0;
            public double VolumeGain { get; set; } = 0.0; // 音量增益，单位dB，范围-20到+40
            public bool UseQueueDownload { get; set; } = false; // 是否使用队列下载模式（适用于无法并发请求的接口）

            // URL TTS 设置
            public URLTTSSetting URL { get; set; } = new URLTTSSetting();

            // OpenAI TTS 设置 (fish.audio)
            public OpenAITTSSetting OpenAI { get; set; } = new OpenAITTSSetting();

            // DIY TTS 设置
            public DIYTTSSetting DIY { get; set; } = new DIYTTSSetting();

            // GPT-SoVITS TTS 设置
            public GPTSoVITSTTSSetting GPTSoVITS { get; set; } = new GPTSoVITSTTSSetting();
        }

        public class URLTTSSetting
        {
            public string BaseUrl { get; set; } = "https://www.example.com";
            public string Voice { get; set; } = "36";
            public string Method { get; set; } = "GET"; // GET 或 POST
        }

        public class OpenAITTSSetting
        {
            public string ApiKey { get; set; } = "";
            public string BaseUrl { get; set; } = "https://api.fish.audio/v1";
            public string Model { get; set; } = "tts-1";
            public string Voice { get; set; } = "alloy";
            public string Format { get; set; } = "mp3";
        }

        public class DIYTTSSetting
        {
            public string BaseUrl { get; set; } = "https://api.example.com/tts";
            public string Method { get; set; } = "POST"; // GET 或 POST
            public string ContentType { get; set; } = "application/json";
            public string RequestBody { get; set; } = "{\n  \"text\": \"{text}\",\n  \"voice\": \"default\",\n  \"format\": \"mp3\"\n}";
            public List<CustomHeader> CustomHeaders { get; set; } = new List<CustomHeader>
    {
        new CustomHeader { Key = "User-Agent", Value = "VPetLLM", IsEnabled = true },
        new CustomHeader { Key = "Accept", Value = "audio/mpeg", IsEnabled = true }
    };
            public string ResponseFormat { get; set; } = "mp3"; // 响应音频格式
        }

        public class CustomHeader
        {
            public string Key { get; set; } = "";
            public string Value { get; set; } = "";
            public bool IsEnabled { get; set; } = true;
        }

        public class GPTSoVITSTTSSetting
        {
            public string BaseUrl { get; set; } = "http://127.0.0.1:9880";
            public string Version { get; set; } = "v4"; // API版本
            public string ModelName { get; set; } = ""; // 模型名称
            public string Emotion { get; set; } = "默认"; // 情感
            public string ReferWavPath { get; set; } = "";
            public string PromptText { get; set; } = "";
            public string PromptLanguage { get; set; } = "中文"; // 固定为中文（向后兼容保留字段）
            public string TextLanguage { get; set; } = "中文"; // 使用完整语言名称
            public string TextSplitMethod { get; set; } = "按标点符号切"; // 文本切分方法
            public string CutPunc { get; set; } = "";
            public int TopK { get; set; } = 15;
            public double TopP { get; set; } = 1.0;
            public double Temperature { get; set; } = 1.0;
            public double Speed { get; set; } = 1.0;
        }

        public class RateLimiterSetting
        {
            public bool EnableToolRateLimit { get; set; } = true;
            public int ToolMaxCount { get; set; } = 5;
            public int ToolWindowMinutes { get; set; } = 2;

            public bool EnablePluginRateLimit { get; set; } = true;
            public int PluginMaxCount { get; set; } = 5;
            public int PluginWindowMinutes { get; set; } = 2;

            public bool LogRateLimitEvents { get; set; } = true;
        }

        public class ASRSetting
        {
            public bool IsEnabled { get; set; } = false;
            public string Provider { get; set; } = "OpenAI";
            public string HotkeyModifiers { get; set; } = "Win+Alt";
            public string HotkeyKey { get; set; } = "V";
            public string Language { get; set; } = "zh";
            public bool AutoSend { get; set; } = true;
            public bool ShowTranscriptionWindow { get; set; } = true;
            public int RecordingDeviceNumber { get; set; } = 0; // 录音设备编号，0 = 默认设备
            
            // OpenAI Whisper 设置
            public OpenAIASRSetting OpenAI { get; set; } = new OpenAIASRSetting();
            
            // Soniox 设置
            public SonioxASRSetting Soniox { get; set; } = new SonioxASRSetting();
        }

        public class OpenAIASRSetting
        {
            public string ApiKey { get; set; } = "";
            public string BaseUrl { get; set; } = "https://api.openai.com/v1";
            public string Model { get; set; } = "whisper-1";
        }

        public class SonioxASRSetting
        {
            public string ApiKey { get; set; } = "";
            public string BaseUrl { get; set; } = "https://api.soniox.com";
            public string Model { get; set; } = "stt-rt-v3";
            public bool EnablePunctuation { get; set; } = true;
            public bool EnableProfanityFilter { get; set; } = false;
        }

        public class SonioxModelInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string TranscriptionMode { get; set; } = "";
            public List<SonioxLanguageInfo> Languages { get; set; } = new List<SonioxLanguageInfo>();
        }

        public class SonioxLanguageInfo
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
        }

    }
}