using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPetLLM.Utils;

namespace VPetLLM.Core.TTSCore
{
    /// <summary>
    /// DIY TTS 实现
    /// </summary>
    public class DIYTTSCore : TTSCoreBase
    {
        public override string Name => "DIY";
        private readonly DIYTTSConfig.DIYTTSConfigData _diyConfig;

        public DIYTTSCore(Setting settings) : base(settings)
        {
            _diyConfig = DIYTTSConfig.LoadConfig();
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                if (!_diyConfig.Enabled)
                {
                    Utils.Logger.Log("TTS (DIY): DIY TTS 未启用");
                    throw new InvalidOperationException("DIY TTS 未启用");
                }

                if (!DIYTTSConfig.IsValidConfig(_diyConfig))
                {
                    Utils.Logger.Log("TTS (DIY): DIY TTS 配置无效");
                    throw new InvalidOperationException("DIY TTS 配置无效");
                }

                var baseUrl = _diyConfig.BaseUrl?.TrimEnd('/');
                var requestMethod = _diyConfig.Method?.ToUpper() ?? "POST";
                var contentType = _diyConfig.ContentType ?? "application/json";

                // 验证URL格式
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                {
                    Utils.Logger.Log($"TTS (DIY): 错误 - URL格式无效: {baseUrl}");
                    throw new ArgumentException($"无效的URL格式: {baseUrl}");
                }

                HttpResponseMessage response;

                // 设置超时时间
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_diyConfig.Timeout));

                if (requestMethod == "POST")
                {
                    // POST请求
                    var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

                    // 设置自定义请求头
                    foreach (var header in _diyConfig.CustomHeaders)
                    {
                        if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            // 强制限制User-Agent为VPetLLM
                            request.Headers.Add("User-Agent", "VPetLLM");
                            Utils.Logger.Log($"TTS (DIY): 已设置User-Agent: VPetLLM (强制限制)");
                        }
                        else if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            // Content-Type通过StringContent设置，跳过
                            continue;
                        }
                        else
                        {
                            try
                            {
                                request.Headers.Add(header.Key, header.Value);
                                Utils.Logger.Log($"TTS (DIY): 已设置请求头: {header.Key}: {header.Value}");
                            }
                            catch (Exception ex)
                            {
                                Utils.Logger.Log($"TTS (DIY): 设置请求头失败 {header.Key}: {ex.Message}");
                            }
                        }
                    }

                    // 构建请求体，处理键值对格式并替换文本占位符
                    var requestBodyDict = new Dictionary<string, object>(_diyConfig.RequestBody ?? new Dictionary<string, object>());

                    // 替换所有值中的 {text} 占位符
                    foreach (var key in requestBodyDict.Keys.ToList())
                    {
                        if (requestBodyDict[key] is string stringValue)
                        {
                            requestBodyDict[key] = stringValue.Replace("{text}", text);
                        }
                    }

                    var requestBodyJson = JsonSerializer.Serialize(requestBodyDict, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                    request.Content = new StringContent(requestBodyJson, Encoding.UTF8, contentType);

                    Utils.Logger.Log($"TTS (DIY): POST请求体:");
                    Utils.Logger.Log($"TTS (DIY): {requestBodyJson}");
                    Utils.Logger.Log($"TTS (DIY): 发送POST请求到: {baseUrl}");

                    using var client = CreateHttpClient();
                    response = await client.SendAsync(request, cts.Token);
                }
                else
                {
                    // GET请求 - 构建查询参数
                    var queryParams = new List<string>();

                    // 处理 requestBody 中的参数，替换 {text} 占位符并添加到查询参数
                    foreach (var param in _diyConfig.RequestBody ?? new Dictionary<string, object>())
                    {
                        var value = param.Value?.ToString() ?? "";
                        if (value.Contains("{text}"))
                        {
                            value = value.Replace("{text}", text);
                        }
                        var encodedValue = Uri.EscapeDataString(value);
                        queryParams.Add($"{param.Key}={encodedValue}");
                    }

                    // 如果 requestBody 为空或没有 text 参数，添加默认的 text 参数
                    if (!_diyConfig.RequestBody.ContainsKey("text"))
                    {
                        var encodedText = Uri.EscapeDataString(text);
                        queryParams.Add($"text={encodedText}");
                    }

                    var queryString = string.Join("&", queryParams);
                    var url = $"{baseUrl}?{queryString}";

                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    // 设置自定义请求头（GET请求不设置Content-Type）
                    foreach (var header in _diyConfig.CustomHeaders)
                    {
                        if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            // 强制限制User-Agent为VPetLLM
                            request.Headers.Add("User-Agent", "VPetLLM");
                            Utils.Logger.Log($"TTS (DIY): 已设置User-Agent: VPetLLM (强制限制)");
                        }
                        else if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            // GET请求跳过Content-Type设置
                            Utils.Logger.Log($"TTS (DIY): 跳过Content-Type设置 (GET请求不需要)");
                        }
                        else
                        {
                            try
                            {
                                request.Headers.Add(header.Key, header.Value);
                                Utils.Logger.Log($"TTS (DIY): 已设置请求头: {header.Key}: {header.Value}");
                            }
                            catch (Exception ex)
                            {
                                Utils.Logger.Log($"TTS (DIY): 设置请求头失败 {header.Key}: {ex.Message}");
                            }
                        }
                    }

                    Utils.Logger.Log($"TTS (DIY): GET请求URL: {url}");
                    Utils.Logger.Log($"TTS (DIY): 发送GET请求到: {url}");

                    using var client = CreateHttpClient();
                    response = await client.SendAsync(request, cts.Token);
                }

                Utils.Logger.Log($"TTS (DIY): 响应状态码: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Utils.Logger.Log($"TTS (DIY): 错误响应内容: {errorContent}");
                    throw new Exception($"DIY TTS API 错误: {response.StatusCode}");
                }

                var responseData = await response.Content.ReadAsByteArrayAsync();
                Utils.Logger.Log($"TTS (DIY): 响应数据大小: {responseData.Length} 字节");

                return responseData;
            }
            catch (TaskCanceledException ex)
            {
                Utils.Logger.Log($"TTS (DIY): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接");
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.Log($"TTS (DIY): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"TTS (DIY): 生成音频错误: {ex.Message}");
                throw;
            }
        }

        public override string GetAudioFormat()
        {
            return _diyConfig.ResponseFormat;
        }
    }
}