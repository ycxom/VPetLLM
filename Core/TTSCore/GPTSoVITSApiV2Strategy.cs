using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// GPT-SoVITS API v2 模式策略
    /// 使用 /tts 端点，直接返回音频流
    /// </summary>
    internal class GPTSoVITSApiV2Strategy : IGPTSoVITSApiStrategy
    {
        public string ModeName => "API v2";

        /// <summary>
        /// API v2 错误响应模型
        /// </summary>
        private class ApiV2ErrorResponse
        {
            public string message { get; set; }
            public string Exception { get; set; }
        }

        public string GetEndpoint(string baseUrl)
        {
            baseUrl = baseUrl?.TrimEnd('/');
            return $"{baseUrl}/tts";
        }

        public object BuildRequestBody(string text, Setting.GPTSoVITSTTSSetting settings)
        {
            // 获取 prompt_text，如果为空则尝试从参考音频文件名提取
            var promptText = settings.PromptTextV2;
            if (string.IsNullOrWhiteSpace(promptText))
            {
                promptText = ExtractPromptTextFromPath(settings.RefAudioPath);
            }

            return new
            {
                text = text,
                text_lang = string.IsNullOrWhiteSpace(settings.TextLangV2) ? "zh" : settings.TextLangV2.ToLower(),
                ref_audio_path = settings.RefAudioPath,
                prompt_text = promptText ?? "",
                prompt_lang = string.IsNullOrWhiteSpace(settings.PromptLangV2) ? "zh" : settings.PromptLangV2.ToLower(),
                top_k = settings.TopK,
                top_p = settings.TopP,
                temperature = settings.Temperature,
                text_split_method = string.IsNullOrWhiteSpace(settings.TextSplitMethodV2) ? "cut5" : settings.TextSplitMethodV2,
                batch_size = settings.BatchSize > 0 ? settings.BatchSize : 1,
                batch_threshold = 0.75,
                split_bucket = false,  // V3/V4 模型不支持分桶处理
                speed_factor = settings.Speed,
                fragment_interval = 0.3,
                seed = -1,
                media_type = string.IsNullOrWhiteSpace(settings.MediaType) ? "wav" : settings.MediaType,
                streaming_mode = settings.StreamingMode,
                parallel_infer = true,
                repetition_penalty = settings.RepetitionPenalty > 0 ? settings.RepetitionPenalty : 1.35,
                sample_steps = settings.SampleSteps > 0 ? settings.SampleSteps : 32,
                super_sampling = settings.SuperSampling
            };
        }

        public async Task<byte[]> ParseResponseAsync(HttpResponseMessage response, HttpClient httpClient)
        {
            // API v2 成功时直接返回音频流
            if (response.IsSuccessStatusCode)
            {
                var audioData = await response.Content.ReadAsByteArrayAsync();
                Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 接收音频数据，大小: {audioData.Length} 字节");
                return audioData;
            }

            // 失败时解析 JSON 错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 错误响应: {errorContent}");

            try
            {
                var errorResponse = JsonSerializer.Deserialize<ApiV2ErrorResponse>(errorContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var errorMessage = errorResponse?.message ?? errorResponse?.Exception ?? errorContent;
                throw new Exception($"API v2 请求失败: {errorMessage}");
            }
            catch (JsonException)
            {
                throw new Exception($"API v2 请求失败: {response.StatusCode} - {errorContent}");
            }
        }

        public void ValidateSettings(Setting.GPTSoVITSTTSSetting settings)
        {
            // API v2 模式必须提供参考音频路径
            if (string.IsNullOrWhiteSpace(settings.RefAudioPath))
            {
                throw new ArgumentException("API v2 模式需要提供参考音频路径 (ref_audio_path)");
            }

            // 验证语言代码格式
            var validLangs = new[] { "zh", "en", "ja", "ko", "yue", "auto", "all_zh", "all_ja", "all_ko", "all_yue" };
            var textLang = settings.TextLangV2?.ToLower() ?? "zh";
            var promptLang = settings.PromptLangV2?.ToLower() ?? "zh";

            // 只记录警告，不阻止请求（服务端会验证）
            if (!Array.Exists(validLangs, l => l == textLang))
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 警告 - 文本语言 '{textLang}' 可能不受支持");
            }
            if (!Array.Exists(validLangs, l => l == promptLang))
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 警告 - 提示语言 '{promptLang}' 可能不受支持");
            }
        }

        /// <summary>
        /// 从参考音频路径中提取 prompt_text
        /// 支持格式: 【情感】文本内容.wav 或 文本内容.wav
        /// </summary>
        private string ExtractPromptTextFromPath(string refAudioPath)
        {
            if (string.IsNullOrWhiteSpace(refAudioPath))
                return "";

            try
            {
                // 获取文件名（不含扩展名）
                var fileName = System.IO.Path.GetFileNameWithoutExtension(refAudioPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    return "";

                // 尝试移除开头的【xxx】格式的情感标签
                var text = fileName;
                if (text.StartsWith("【"))
                {
                    var endIndex = text.IndexOf('】');
                    if (endIndex > 0 && endIndex < text.Length - 1)
                    {
                        text = text.Substring(endIndex + 1);
                    }
                }

                // 移除可能的下划线或其他分隔符
                text = text.TrimStart('_', '-', ' ');

                Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 从文件名提取 prompt_text: {text}");
                return text;
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (GPT-SoVITS API v2): 提取 prompt_text 失败: {ex.Message}");
                return "";
            }
        }
    }
}
