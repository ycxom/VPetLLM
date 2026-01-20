using System.Net.Http;

namespace VPetLLM.Core.Providers.TTS
{
    /// <summary>
    /// OpenAI TTS 实现
    /// </summary>
    public class OpenAITTSCore : TTSCoreBase
    {
        public override string Name => "OpenAI";
        private readonly Setting.OpenAITTSSetting _openAISetting;

        public OpenAITTSCore(Setting settings) : base(settings)
        {
            _openAISetting = settings?.TTS?.OpenAI ?? new Setting.OpenAITTSSetting();
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                Logger.Log($"TTS (OpenAI): 开始构建请求");
                Logger.Log($"TTS (OpenAI): 目标URL: {_openAISetting.BaseUrl}");
                Logger.Log($"TTS (OpenAI): 模型: {_openAISetting.Model}");
                Logger.Log($"TTS (OpenAI): 语音ID: {_openAISetting.Voice}");
                Logger.Log($"TTS (OpenAI): 音频格式: {_openAISetting.Format}");
                Logger.Log($"TTS (OpenAI): 文本长度: {text.Length} 字符");

                var request = new HttpRequestMessage(HttpMethod.Post, _openAISetting.BaseUrl);

                // 设置请求头
                if (!string.IsNullOrWhiteSpace(_openAISetting.ApiKey))
                {
                    request.Headers.Add("Authorization", $"Bearer {_openAISetting.ApiKey}");
                    Logger.Log($"TTS (OpenAI): 已添加Authorization头");
                }
                else
                {
                    Logger.Log($"TTS (OpenAI): 警告 - API Key为空");
                }

                request.Headers.Add("model", _openAISetting.Model);
                request.Headers.Add("User-Agent", "VPetLLM/1.0");

                // 构建请求体 - 使用fish.audio格式
                var requestBody = new
                {
                    text = text,
                    reference_id = _openAISetting.Voice,
                    format = _openAISetting.Format
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                Logger.Log($"TTS (OpenAI): 完整请求信息:");
                Logger.Log($"TTS (OpenAI): Method: {request.Method}");
                Logger.Log($"TTS (OpenAI): URL: {request.RequestUri}");
                Logger.Log($"TTS (OpenAI): Request Body:");
                Logger.Log($"TTS (OpenAI): {json}");

                // 发送请求
                Logger.Log($"TTS (OpenAI): 发送请求..");
                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);

                Logger.Log($"TTS (OpenAI): 响应状态码: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"TTS (OpenAI): 错误响应内容: {errorContent}");
                    throw new Exception($"OpenAI TTS API 错误: {response.StatusCode}");
                }

                var responseData = await response.Content.ReadAsByteArrayAsync();
                Logger.Log($"TTS (OpenAI): 成功获取响应数据，大小 {responseData.Length} 字节");

                return responseData;
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"TTS (OpenAI): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接");
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"TTS (OpenAI): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (OpenAI): 生成音频错误: {ex.Message}");
                throw;
            }
        }

        public override string GetAudioFormat()
        {
            return _openAISetting.Format;
        }
    }
}