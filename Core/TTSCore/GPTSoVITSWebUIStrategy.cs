using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// GPT-SoVITS 整合包网页模式策略
    /// 使用 /infer_single 端点，返回音频 URL 后二次下载
    /// </summary>
    internal class GPTSoVITSWebUIStrategy : IGPTSoVITSApiStrategy
    {
        public string ModeName => "整合包网页";

        /// <summary>
        /// API 响应模型
        /// </summary>
        private class InferSingleResponse
        {
            public string msg { get; set; }
            public string audio_url { get; set; }
        }

        public string GetEndpoint(string baseUrl)
        {
            baseUrl = baseUrl?.TrimEnd('/');
            return baseUrl.Contains("/infer_") ? baseUrl : $"{baseUrl}/infer_single";
        }

        public object BuildRequestBody(string text, Setting.GPTSoVITSTTSSetting settings)
        {
            return new
            {
                dl_url = settings.BaseUrl?.TrimEnd('/'),
                version = string.IsNullOrWhiteSpace(settings.Version) ? "v4" : settings.Version,
                model_name = string.IsNullOrWhiteSpace(settings.ModelName) 
                    ? "默认模型" 
                    : settings.ModelName,
                prompt_text_lang = string.IsNullOrWhiteSpace(settings.PromptLanguage) 
                    ? "中文" 
                    : settings.PromptLanguage,
                emotion = string.IsNullOrWhiteSpace(settings.Emotion) ? "默认" : settings.Emotion,
                text = text,
                text_lang = settings.TextLanguage,
                top_k = settings.TopK,
                top_p = settings.TopP,
                temperature = settings.Temperature,
                text_split_method = string.IsNullOrWhiteSpace(settings.TextSplitMethod) 
                    ? "按标点符号切" 
                    : settings.TextSplitMethod,
                batch_size = 10,
                batch_threshold = 0.75,
                split_bucket = true,
                speed_facter = settings.Speed,
                fragment_interval = 0.3,
                media_type = "wav",
                parallel_infer = true,
                repetition_penalty = 1.35,
                seed = -1,
                sample_steps = 16,
                if_sr = false
            };
        }

        public async Task<byte[]> ParseResponseAsync(HttpResponseMessage response, HttpClient httpClient)
        {
            var responseText = await response.Content.ReadAsStringAsync();
            Utils.Logger.Log($"TTS (GPT-SoVITS WebUI): 响应: {responseText}");

            var apiResponse = JsonSerializer.Deserialize<InferSingleResponse>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (apiResponse == null || string.IsNullOrWhiteSpace(apiResponse.audio_url))
            {
                throw new Exception($"响应格式错误: {responseText}");
            }

            Utils.Logger.Log($"TTS (GPT-SoVITS WebUI): {apiResponse.msg}");
            Utils.Logger.Log($"TTS (GPT-SoVITS WebUI): 音频URL: {apiResponse.audio_url}");

            // 下载音频文件
            Utils.Logger.Log($"TTS (GPT-SoVITS WebUI): 开始下载音频");
            var audioResponse = await httpClient.GetAsync(apiResponse.audio_url);
            
            if (!audioResponse.IsSuccessStatusCode)
            {
                throw new Exception($"下载音频失败: {audioResponse.StatusCode}");
            }

            var audioData = await audioResponse.Content.ReadAsByteArrayAsync();
            Utils.Logger.Log($"TTS (GPT-SoVITS WebUI): 下载完成，大小: {audioData.Length} 字节");

            return audioData;
        }

        public void ValidateSettings(Setting.GPTSoVITSTTSSetting settings)
        {
            // WebUI 模式没有特殊的必需字段验证
            // 模型名称等可以使用默认值
        }
    }
}
