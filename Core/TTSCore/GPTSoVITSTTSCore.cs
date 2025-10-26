using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// GPT-SoVITS TTS 实现
    /// 完全基于 GPT-SoVITS-API.md 标准接口
    /// 使用 /infer_single 端点，返回音频 URL 后二次下载
    /// 支持版本检测和模型选择
    /// </summary>
    public class GPTSoVITSTTSCore : TTSCoreBase
    {
        public override string Name => "GPT-SoVITS";
        private readonly Setting.GPTSoVITSTTSSetting _gptSoVITSSetting;

        public GPTSoVITSTTSCore(Setting settings) : base(settings)
        {
            _gptSoVITSSetting = settings?.TTS?.GPTSoVITS ?? new Setting.GPTSoVITSTTSSetting();
        }

        /// <summary>
        /// 版本信息响应模型
        /// </summary>
        private class VersionResponse
        {
            public string msg { get; set; }
            public List<string> support_versions { get; set; }
        }

        /// <summary>
        /// 模型列表响应模型
        /// </summary>
        private class ModelsResponse
        {
            public string msg { get; set; }
            public Dictionary<string, Dictionary<string, List<string>>> models { get; set; }
        }

        /// <summary>
        /// API 响应模型
        /// </summary>
        private class InferSingleResponse
        {
            public string msg { get; set; }
            public string audio_url { get; set; }
        }

        /// <summary>
        /// 获取支持的版本列表
        /// </summary>
        public async Task<List<string>> GetSupportedVersionsAsync()
        {
            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/version";
                
                Utils.Logger.Log($"TTS (GPT-SoVITS): 获取版本信息: {endpoint}");
                
                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);
                
                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"TTS (GPT-SoVITS): 获取版本失败: {response.StatusCode}");
                    return new List<string>();
                }
                
                var responseText = await response.Content.ReadAsStringAsync();
                Utils.Logger.Log($"TTS (GPT-SoVITS): 版本响应: {responseText}");
                
                var versionResponse = JsonSerializer.Deserialize<VersionResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                return versionResponse?.support_versions ?? new List<string>();
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): 获取版本错误: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 获取指定版本的模型列表
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, List<string>>>> GetModelsAsync(string version)
        {
            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/models/{version}";
                
                Utils.Logger.Log($"TTS (GPT-SoVITS): 获取模型列表: {endpoint}");
                
                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);
                
                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"TTS (GPT-SoVITS): 获取模型失败: {response.StatusCode}");
                    return new Dictionary<string, Dictionary<string, List<string>>>();
                }
                
                var responseText = await response.Content.ReadAsStringAsync();
                Utils.Logger.Log($"TTS (GPT-SoVITS): 模型响应: {responseText}");
                
                var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                return modelsResponse?.models ?? new Dictionary<string, Dictionary<string, List<string>>>();
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): 获取模型错误: {ex.Message}");
                return new Dictionary<string, Dictionary<string, List<string>>>();
            }
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                
                Utils.Logger.Log($"TTS (GPT-SoVITS): 开始生成音频");
                Utils.Logger.Log($"TTS (GPT-SoVITS): 服务器地址: {baseUrl}");
                Utils.Logger.Log($"TTS (GPT-SoVITS): 文本内容: {text}");
                Utils.Logger.Log($"TTS (GPT-SoVITS): 使用版本: {_gptSoVITSSetting.Version}");
                Utils.Logger.Log($"TTS (GPT-SoVITS): 使用模型: {_gptSoVITSSetting.ModelName}");

                // 步骤1: 构建请求体 - 完全按照 GPT-SoVITS-API.md 标准
                var requestBody = new
                {
                    dl_url = baseUrl,
                    version = string.IsNullOrWhiteSpace(_gptSoVITSSetting.Version) ? "v4" : _gptSoVITSSetting.Version,
                    model_name = string.IsNullOrWhiteSpace(_gptSoVITSSetting.ModelName) 
                        ? "默认模型" 
                        : _gptSoVITSSetting.ModelName,
                    prompt_text_lang = string.IsNullOrWhiteSpace(_gptSoVITSSetting.PromptLanguage) 
                        ? "中文" 
                        : _gptSoVITSSetting.PromptLanguage, // 从模型数据中获取的语言
                    emotion = string.IsNullOrWhiteSpace(_gptSoVITSSetting.Emotion) ? "默认" : _gptSoVITSSetting.Emotion,
                    text = text,
                    text_lang = _gptSoVITSSetting.TextLanguage,
                    top_k = _gptSoVITSSetting.TopK,
                    top_p = _gptSoVITSSetting.TopP,
                    temperature = _gptSoVITSSetting.Temperature,
                    text_split_method = string.IsNullOrWhiteSpace(_gptSoVITSSetting.TextSplitMethod) 
                        ? "按标点符号切" 
                        : _gptSoVITSSetting.TextSplitMethod,
                    batch_size = 10,
                    batch_threshold = 0.75,
                    split_bucket = true,
                    speed_facter = _gptSoVITSSetting.Speed,
                    fragment_interval = 0.3,
                    media_type = "wav",
                    parallel_infer = true,
                    repetition_penalty = 1.35,
                    seed = -1,
                    sample_steps = 16,
                    if_sr = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                
                Utils.Logger.Log($"TTS (GPT-SoVITS): 请求参数: {json}");

                // 步骤2: 发送 POST 请求到 /infer_single
                var endpoint = baseUrl.Contains("/infer_") ? baseUrl : $"{baseUrl}/infer_single";
                Utils.Logger.Log($"TTS (GPT-SoVITS): POST {endpoint}");
                
                using var client = CreateHttpClient();
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content);

                Utils.Logger.Log($"TTS (GPT-SoVITS): 响应状态: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Utils.Logger.Log($"TTS (GPT-SoVITS): 错误: {errorContent}");
                    throw new Exception($"API 请求失败: {response.StatusCode} - {errorContent}");
                }

                // 步骤3: 解析响应获取 audio_url
                var responseText = await response.Content.ReadAsStringAsync();
                Utils.Logger.Log($"TTS (GPT-SoVITS): 响应: {responseText}");

                var apiResponse = JsonSerializer.Deserialize<InferSingleResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (apiResponse == null || string.IsNullOrWhiteSpace(apiResponse.audio_url))
                {
                    throw new Exception($"响应格式错误: {responseText}");
                }

                Utils.Logger.Log($"TTS (GPT-SoVITS): {apiResponse.msg}");
                Utils.Logger.Log($"TTS (GPT-SoVITS): 音频URL: {apiResponse.audio_url}");

                // 步骤4: 下载音频文件
                Utils.Logger.Log($"TTS (GPT-SoVITS): 开始下载音频");
                var audioResponse = await client.GetAsync(apiResponse.audio_url);
                
                if (!audioResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"下载音频失败: {audioResponse.StatusCode}");
                }

                var audioData = await audioResponse.Content.ReadAsByteArrayAsync();
                Utils.Logger.Log($"TTS (GPT-SoVITS): 下载完成，大小: {audioData.Length} 字节");

                return audioData;
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查 GPT-SoVITS 服务是否正常运行");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): JSON 解析错误: {ex.Message}");
                throw new Exception($"响应解析失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS): 错误: {ex.Message}");
                throw;
            }
        }

        public override string GetAudioFormat()
        {
            // GPT-SoVITS 默认返回 wav 格式
            return "wav";
        }
    }
}