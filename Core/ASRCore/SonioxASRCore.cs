using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VPetLLM.Core.ASRCore
{
    /// <summary>
    /// Soniox ASR 实现
    /// </summary>
    public class SonioxASRCore : ASRCoreBase
    {
        public override string Name => "Soniox";
        private readonly Setting.SonioxASRSetting _sonioxSetting;

        public SonioxASRCore(Setting settings) : base(settings)
        {
            _sonioxSetting = settings?.ASR?.Soniox ?? new Setting.SonioxASRSetting();
        }

        public override async Task<string> TranscribeAsync(byte[] audioData)
        {
            try
            {
                var baseUrl = _sonioxSetting.BaseUrl.TrimEnd('/');
                var startTime = DateTime.Now;

                Utils.Logger.Log($"ASR (Soniox): 开始转录流程");

                // 步骤 1: 上传文件
                Utils.Logger.Log($"ASR (Soniox): 上传音频文件...");
                var fileId = await UploadFileAsync(audioData, baseUrl);
                Utils.Logger.Log($"ASR (Soniox): 文件上传成功, ID: {fileId}");

                // 步骤 2: 创建转录任务
                Utils.Logger.Log($"ASR (Soniox): 创建转录任务...");
                var transcriptionId = await CreateTranscriptionAsync(fileId, baseUrl);
                Utils.Logger.Log($"ASR (Soniox): 转录任务创建成功, ID: {transcriptionId}");

                // 步骤 3: 等待转录完成
                Utils.Logger.Log($"ASR (Soniox): 等待转录完成...");
                await WaitForTranscriptionAsync(transcriptionId, baseUrl);

                // 步骤 4: 获取转录结果
                Utils.Logger.Log($"ASR (Soniox): 获取转录结果...");
                var transcript = await GetTranscriptAsync(transcriptionId, baseUrl);

                // 步骤 5: 清理资源
                Utils.Logger.Log($"ASR (Soniox): 清理资源...");
                await DeleteTranscriptionAsync(transcriptionId, baseUrl);
                await DeleteFileAsync(fileId, baseUrl);

                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                Utils.Logger.Log($"ASR (Soniox): 转录完成，总耗时 {elapsed:F2} 秒");

                return transcript;
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接或尝试录制更短的音频");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 转录错误: {ex.Message}");
                throw;
            }
        }

        private async Task<string> UploadFileAsync(byte[] audioData, string baseUrl)
        {
            var url = $"{baseUrl}/files";
            
            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(audioData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "audio.wav");

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
            }

            using var client = CreateHttpClient();
            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Utils.Logger.Log($"ASR (Soniox): 文件上传错误: {response.StatusCode} - {responseContent}");
                throw new Exception($"文件上传失败: {response.StatusCode}");
            }

            var result = JObject.Parse(responseContent);
            return result["id"]?.ToString() ?? throw new Exception("未能获取文件 ID");
        }

        private async Task<string> CreateTranscriptionAsync(string fileId, string baseUrl)
        {
            var url = $"{baseUrl}/transcriptions";

            var requestBody = new
            {
                file_id = fileId,
                model = _sonioxSetting.Model,
                language_hints = !string.IsNullOrWhiteSpace(Settings?.ASR?.Language) 
                    ? new[] { Settings.ASR.Language } 
                    : new[] { "en" }
            };

            var jsonContent = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
            }

            Utils.Logger.Log($"ASR (Soniox): 使用模型: {_sonioxSetting.Model}, 语言: {Settings?.ASR?.Language}");

            using var client = CreateHttpClient();
            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Utils.Logger.Log($"ASR (Soniox): 创建转录任务错误: {response.StatusCode} - {responseContent}");
                throw new Exception($"创建转录任务失败: {response.StatusCode}");
            }

            var result = JObject.Parse(responseContent);
            return result["id"]?.ToString() ?? throw new Exception("未能获取转录任务 ID");
        }

        private async Task WaitForTranscriptionAsync(string transcriptionId, string baseUrl)
        {
            var url = $"{baseUrl}/transcriptions/{transcriptionId}";
            var maxAttempts = 60;
            var attempt = 0;

            using var client = CreateHttpClient();
            
            while (attempt < maxAttempts)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
                }

                var response = await client.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"ASR (Soniox): 状态检查错误: {response.StatusCode} - {responseContent}");
                    throw new Exception($"检查转录状态失败: {response.StatusCode}");
                }

                var result = JObject.Parse(responseContent);
                var status = result["status"]?.ToString();

                Utils.Logger.Log($"ASR (Soniox): 转录状态: {status} (尝试 {attempt + 1}/{maxAttempts})");

                if (status == "completed")
                {
                    return;
                }
                else if (status == "error")
                {
                    var errorMessage = result["error_message"]?.ToString() ?? "Unknown error";
                    throw new Exception($"转录失败: {errorMessage}");
                }

                await Task.Delay(1000);
                attempt++;
            }

            throw new Exception("转录超时，请稍后重试");
        }

        private async Task<string> GetTranscriptAsync(string transcriptionId, string baseUrl)
        {
            var url = $"{baseUrl}/transcriptions/{transcriptionId}/transcript";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
            }

            using var client = CreateHttpClient();
            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Utils.Logger.Log($"ASR (Soniox): 获取转录结果错误: {response.StatusCode} - {responseContent}");
                throw new Exception($"获取转录结果失败: {response.StatusCode}");
            }

            Utils.Logger.Log($"ASR (Soniox): 转录响应: {responseContent}");

            var result = JObject.Parse(responseContent);
            var tokens = result["tokens"] as JArray;

            if (tokens == null || tokens.Count == 0)
            {
                Utils.Logger.Log("ASR (Soniox): 未找到转录内容");
                return "";
            }

            Utils.Logger.Log($"ASR (Soniox): 收到 {tokens.Count} 个 token");

            var textBuilder = new StringBuilder();
            foreach (var token in tokens)
            {
                var text = token["text"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    textBuilder.Append(text);
                }
            }

            var finalText = textBuilder.ToString();
            Utils.Logger.Log($"ASR (Soniox): 最终文本长度: {finalText.Length} 字符");

            return finalText;
        }

        private async Task DeleteTranscriptionAsync(string transcriptionId, string baseUrl)
        {
            try
            {
                var url = $"{baseUrl}/transcriptions/{transcriptionId}";
                var request = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
                }

                using var client = CreateHttpClient();
                await client.SendAsync(request);
                Utils.Logger.Log($"ASR (Soniox): 已删除转录任务 {transcriptionId}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 删除转录任务失败: {ex.Message}");
            }
        }

        private async Task DeleteFileAsync(string fileId, string baseUrl)
        {
            try
            {
                var url = $"{baseUrl}/files/{fileId}";
                var request = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
                }

                using var client = CreateHttpClient();
                await client.SendAsync(request);
                Utils.Logger.Log($"ASR (Soniox): 已删除文件 {fileId}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 删除文件失败: {ex.Message}");
            }
        }

        public override async Task<List<string>> GetModelsAsync()
        {
            try
            {
                var url = $"{_sonioxSetting.BaseUrl.TrimEnd('/')}/v1/models";
                Utils.Logger.Log($"ASR (Soniox): 从 {url} 获取模型列表");

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                if (!string.IsNullOrWhiteSpace(_sonioxSetting.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sonioxSetting.ApiKey);
                }

                using var client = CreateHttpClient();
                var response = await client.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Log($"ASR (Soniox): 获取模型列表失败: {response.StatusCode} - {responseContent}");
                    return new List<string>();
                }

                Utils.Logger.Log($"ASR (Soniox): 模型响应: {responseContent}");
                var result = JObject.Parse(responseContent);
                var models = new List<string>();

                if (result["models"] is JArray modelsArray)
                {
                    foreach (var model in modelsArray)
                    {
                        var modelId = model["id"]?.ToString();
                        if (!string.IsNullOrEmpty(modelId))
                        {
                            models.Add(modelId);
                        }
                    }
                }

                Utils.Logger.Log($"ASR (Soniox): 获取到 {models.Count} 个模型");
                return models;
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"ASR (Soniox): 获取模型列表错误: {ex.Message}");
                return new List<string>();
            }
        }
    }
}
