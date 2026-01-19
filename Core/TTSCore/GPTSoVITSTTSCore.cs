using System.Net.Http;
using System.Text;
using System.Text.Json;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// GPT-SoVITS TTS 实现
    /// 支持两种 API 模式：
    /// - 整合包网页模式：使用 /infer_single 端点，返回音频 URL 后二次下载
    /// - API v2 模式：使用 /tts 端点，直接返回音频流
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
        /// 根据当前设置获取对应的 API 策略
        /// </summary>
        private IGPTSoVITSApiStrategy GetStrategy()
        {
            return _gptSoVITSSetting.ApiMode switch
            {
                Setting.GPTSoVITSApiMode.ApiV2 => new GPTSoVITSApiV2Strategy(),
                _ => new GPTSoVITSWebUIStrategy()
            };
        }

        /// <summary>
        /// 版本信息响应模型（WebUI 模式专用）
        /// </summary>
        private class VersionResponse
        {
            public string msg { get; set; }
            public List<string> support_versions { get; set; }
        }

        /// <summary>
        /// 模型列表响应模型（WebUI 模式专用）
        /// </summary>
        private class ModelsResponse
        {
            public string msg { get; set; }
            public Dictionary<string, Dictionary<string, List<string>>> models { get; set; }
        }

        /// <summary>
        /// 获取支持的版本列表（仅 WebUI 模式）
        /// </summary>
        public async Task<List<string>> GetSupportedVersionsAsync()
        {
            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/version";

                Logger.Log($"TTS (GPT-SoVITS): 获取版本信息: {endpoint}");

                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"TTS (GPT-SoVITS): 获取版本失败: {response.StatusCode}");
                    return new List<string>();
                }

                var responseText = await response.Content.ReadAsStringAsync();
                Logger.Log($"TTS (GPT-SoVITS): 版本响应: {responseText}");

                var versionResponse = JsonSerializer.Deserialize<VersionResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return versionResponse?.support_versions ?? new List<string>();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (GPT-SoVITS): 获取版本错误: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 获取指定版本的模型列表（仅 WebUI 模式）
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, List<string>>>> GetModelsAsync(string version)
        {
            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/models/{version}";

                Logger.Log($"TTS (GPT-SoVITS): 获取模型列表: {endpoint}");

                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"TTS (GPT-SoVITS): 获取模型失败: {response.StatusCode}");
                    return new Dictionary<string, Dictionary<string, List<string>>>();
                }

                var responseText = await response.Content.ReadAsStringAsync();
                Logger.Log($"TTS (GPT-SoVITS): 模型响应: {responseText}");

                var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return modelsResponse?.models ?? new Dictionary<string, Dictionary<string, List<string>>>();
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (GPT-SoVITS): 获取模型错误: {ex.Message}");
                return new Dictionary<string, Dictionary<string, List<string>>>();
            }
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            var strategy = GetStrategy();

            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');

                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 开始生成音频");
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 服务器地址: {baseUrl}");
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 文本内容: {text}");
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): API 模式: {_gptSoVITSSetting.ApiMode}");

                // 验证设置
                strategy.ValidateSettings(_gptSoVITSSetting);

                // 构建请求体
                var requestBody = strategy.BuildRequestBody(text, _gptSoVITSSetting);
                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 请求参数: {json}");

                // 获取端点并发送请求
                var endpoint = strategy.GetEndpoint(baseUrl);
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): POST {endpoint}");

                using var client = CreateHttpClient();
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content);

                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 响应状态: {response.StatusCode}");

                // 解析响应
                var audioData = await strategy.ParseResponseAsync(response, client);

                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 音频生成完成，大小: {audioData.Length} 字节");
                return audioData;
            }
            catch (ArgumentException ex)
            {
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 参数错误: {ex.Message}");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查 GPT-SoVITS 服务是否正常运行");
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): JSON 解析错误: {ex.Message}");
                throw new Exception($"响应解析失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (GPT-SoVITS {strategy.ModeName}): 错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 切换 GPT 模型权重（API v2 模式）
        /// </summary>
        /// <param name="weightsPath">GPT 模型权重文件路径</param>
        /// <returns>是否成功</returns>
        public async Task<bool> SetGptWeightsAsync(string weightsPath)
        {
            if (string.IsNullOrWhiteSpace(weightsPath))
            {
                Logger.Log("TTS (GPT-SoVITS): GPT 权重路径为空");
                return false;
            }

            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/set_gpt_weights?weights_path={Uri.EscapeDataString(weightsPath)}";

                Logger.Log($"TTS (GPT-SoVITS): 切换 GPT 模型: {endpoint}");

                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log("TTS (GPT-SoVITS): GPT 模型切换成功");
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"TTS (GPT-SoVITS): GPT 模型切换失败: {response.StatusCode} - {errorContent}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (GPT-SoVITS): GPT 模型切换错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 切换 SoVITS 模型权重（API v2 模式）
        /// </summary>
        /// <param name="weightsPath">SoVITS 模型权重文件路径</param>
        /// <returns>是否成功</returns>
        public async Task<bool> SetSovitsWeightsAsync(string weightsPath)
        {
            if (string.IsNullOrWhiteSpace(weightsPath))
            {
                Logger.Log("TTS (GPT-SoVITS): SoVITS 权重路径为空");
                return false;
            }

            try
            {
                var baseUrl = _gptSoVITSSetting.BaseUrl?.TrimEnd('/');
                var endpoint = $"{baseUrl}/set_sovits_weights?weights_path={Uri.EscapeDataString(weightsPath)}";

                Logger.Log($"TTS (GPT-SoVITS): 切换 SoVITS 模型: {endpoint}");

                using var client = CreateHttpClient();
                var response = await client.GetAsync(endpoint);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log("TTS (GPT-SoVITS): SoVITS 模型切换成功");
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"TTS (GPT-SoVITS): SoVITS 模型切换失败: {response.StatusCode} - {errorContent}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (GPT-SoVITS): SoVITS 模型切换错误: {ex.Message}");
                return false;
            }
        }

        public override string GetAudioFormat()
        {
            // API v2 模式支持自定义格式
            if (_gptSoVITSSetting.ApiMode == Setting.GPTSoVITSApiMode.ApiV2
                && !string.IsNullOrWhiteSpace(_gptSoVITSSetting.MediaType))
            {
                return _gptSoVITSSetting.MediaType;
            }
            // 默认返回 wav 格式
            return "wav";
        }
    }
}
