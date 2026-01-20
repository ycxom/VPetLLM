using System.Net.Http;

namespace VPetLLM.Core.Providers.TTS
{
    /// <summary>
    /// DIY TTS 实现
    /// </summary>
    public class DIYTTSCore : TTSCoreBase
    {
        public override string Name => "DIY";
        private readonly Setting.DIYTTSSetting _diyConfig;

        public DIYTTSCore(Setting settings) : base(settings)
        {
            _diyConfig = settings.TTS.DIY;
            // 初始化时执行配置回收机制
            PerformConfigCleanup();
        }

        public override async Task<byte[]> GenerateAudioAsync(string text)
        {
            try
            {
                // 检查是否启用（通过TTS主设置）
                if (!Settings.TTS.IsEnabled)
                {
                    Logger.Log("TTS (DIY): DIY TTS 未启用");
                    throw new InvalidOperationException("DIY TTS 未启用");
                }

                // 验证配置
                if (!IsValidConfig())
                {
                    Logger.Log("TTS (DIY): DIY TTS 配置无效");
                    throw new InvalidOperationException("DIY TTS 配置无效");
                }

                var baseUrl = _diyConfig.BaseUrl?.TrimEnd('/');
                var requestMethod = _diyConfig.Method?.ToUpper() ?? "POST";
                var contentType = _diyConfig.ContentType ?? "application/json";
                const int timeout = 30000; // 默认30秒超时

                // 验证URL格式
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                {
                    Logger.Log($"TTS (DIY): 错误 - URL格式无效: {baseUrl}");
                    throw new ArgumentException($"无效的URL格式: {baseUrl}");
                }

                HttpResponseMessage response;

                // 设置超时时间
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));

                if (requestMethod == "POST")
                {
                    // POST请求
                    var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

                    // 设置自定义请求头
                    foreach (var header in _diyConfig.CustomHeaders ?? new List<Setting.CustomHeader>())
                    {
                        if (!header.IsEnabled) continue;

                        if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            // 不强制设置，允许用户自定义User-Agent
                            request.Headers.Add("User-Agent", header.Value);
                            Logger.Log($"TTS (DIY): 已设置User-Agent: {header.Value}");
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
                                Logger.Log($"TTS (DIY): 已设置请求头: {header.Key}: {header.Value}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS (DIY): 设置请求头失败：{header.Key}: {ex.Message}");
                            }
                        }
                    }

                    // 构建请求体，替换文本占位符
                    var requestBody = _diyConfig.RequestBody ?? "";
                    requestBody = requestBody.Replace("{text}", text);

                    request.Content = new StringContent(requestBody, Encoding.UTF8, contentType);

                    Logger.Log($"TTS (DIY): POST请求体：");
                    Logger.Log($"TTS (DIY): {requestBody}");
                    Logger.Log($"TTS (DIY): 发送POST请求到 {baseUrl}");

                    using var client = CreateHttpClient();
                    response = await client.SendAsync(request, cts.Token);
                }
                else
                {
                    // GET请求 - 构建查询参数
                    var queryParams = new List<string>();

                    // 尝试解析JSON格式的RequestBody
                    try
                    {
                        var requestBody = _diyConfig.RequestBody ?? "";
                        if (!string.IsNullOrWhiteSpace(requestBody))
                        {
                            var jsonDoc = JsonDocument.Parse(requestBody);
                            foreach (var property in jsonDoc.RootElement.EnumerateObject())
                            {
                                var value = property.Value.GetString() ?? "";
                                if (value.Contains("{text}"))
                                {
                                    value = value.Replace("{text}", text);
                                }
                                var encodedValue = Uri.EscapeDataString(value);
                                queryParams.Add($"{property.Name}={encodedValue}");
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // 如果解析JSON失败，将RequestBody作为text参数处理
                        var encodedText = Uri.EscapeDataString(text);
                        queryParams.Add($"text={encodedText}");
                    }

                    // 如果没有解析出任何参数，添加默认的text参数
                    if (queryParams.Count == 0 || !queryParams.Any(p => p.StartsWith("text=")))
                    {
                        var encodedText = Uri.EscapeDataString(text);
                        queryParams.Add($"text={encodedText}");
                    }

                    var queryString = string.Join("&", queryParams);
                    var url = $"{baseUrl}?{queryString}";

                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    // 设置自定义请求头（GET请求不设置Content-Type）
                    foreach (var header in _diyConfig.CustomHeaders ?? new List<Setting.CustomHeader>())
                    {
                        if (!header.IsEnabled) continue;

                        if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            // 不强制设置，允许用户自定义User-Agent
                            request.Headers.Add("User-Agent", header.Value);
                            Logger.Log($"TTS (DIY): 已设置User-Agent: {header.Value}");
                        }
                        else if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            // GET请求跳过Content-Type设置
                            Logger.Log($"TTS (DIY): 跳过Content-Type设置 (GET请求不需要)");
                        }
                        else
                        {
                            try
                            {
                                request.Headers.Add(header.Key, header.Value);
                                Logger.Log($"TTS (DIY): 已设置请求头: {header.Key}: {header.Value}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS (DIY): 设置请求头失败：{header.Key}: {ex.Message}");
                            }
                        }
                    }

                    Logger.Log($"TTS (DIY): GET请求URL: {url}");
                    Logger.Log($"TTS (DIY): 发送GET请求到 {url}");

                    using var client = CreateHttpClient();
                    response = await client.SendAsync(request, cts.Token);
                }

                Logger.Log($"TTS (DIY): 响应状态码: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"TTS (DIY): 错误响应内容: {errorContent}");
                    throw new Exception($"DIY TTS API 错误: {response.StatusCode}");
                }

                var responseData = await response.Content.ReadAsByteArrayAsync();
                Logger.Log($"TTS (DIY): 响应数据大小: {responseData.Length} 字节");

                return responseData;
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"TTS (DIY): 请求超时: {ex.Message}");
                throw new Exception("请求超时，请检查网络连接");
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"TTS (DIY): 网络错误: {ex.Message}");
                throw new Exception($"网络错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (DIY): 生成音频错误: {ex.Message}");
                throw;
            }
        }

        public override string GetAudioFormat()
        {
            return _diyConfig.ResponseFormat ?? "mp3";
        }

        /// <summary>
        /// 验证DIY TTS配置是否有效
        /// </summary>
        private bool IsValidConfig()
        {
            if (_diyConfig is null) return false;
            if (string.IsNullOrWhiteSpace(_diyConfig.BaseUrl)) return false;
            if (string.IsNullOrWhiteSpace(_diyConfig.Method)) return false;
            if (!Uri.TryCreate(_diyConfig.BaseUrl, UriKind.Absolute, out _)) return false;

            return true;
        }

        /// <summary>
        /// 执行配置回收机制，清理无效和重复的配置项
        /// </summary>
        private void PerformConfigCleanup()
        {
            try
            {
                Logger.Log("TTS (DIY): 开始执行配置回收机制");

                if (_diyConfig is null)
                {
                    Logger.Log("TTS (DIY): DIY配置为空，跳过回收");
                    return;
                }

                bool hasChanges = false;

                // 清理自定义请求头
                if (_diyConfig.CustomHeaders is not null && _diyConfig.CustomHeaders.Any())
                {
                    var originalCount = _diyConfig.CustomHeaders.Count;

                    // 移除空键名或空值的项
                    _diyConfig.CustomHeaders = _diyConfig.CustomHeaders
                        .Where(h => !string.IsNullOrWhiteSpace(h.Key) && !string.IsNullOrWhiteSpace(h.Value))
                        .ToList();

                    // 移除重复的键（保留第一个）
                    _diyConfig.CustomHeaders = _diyConfig.CustomHeaders
                        .GroupBy(h => h.Key.Trim().ToLowerInvariant())
                        .Select(g => g.First())
                        .ToList();

                    var newCount = _diyConfig.CustomHeaders.Count;
                    if (newCount != originalCount)
                    {
                        hasChanges = true;
                        Logger.Log($"TTS (DIY): 清理了{originalCount - newCount} 个无效/重复的自定义请求头");
                    }
                }

                // 清理无效的URL
                if (!string.IsNullOrWhiteSpace(_diyConfig.BaseUrl))
                {
                    if (!Uri.TryCreate(_diyConfig.BaseUrl, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        Logger.Log($"TTS (DIY): 检测到无效的BaseUrl: {_diyConfig.BaseUrl}，重置为空");
                        _diyConfig.BaseUrl = "";
                        hasChanges = true;
                    }
                }

                // 验证请求方法
                if (!string.IsNullOrWhiteSpace(_diyConfig.Method))
                {
                    var validMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH" };
                    if (!validMethods.Contains(_diyConfig.Method.ToUpperInvariant()))
                    {
                        Logger.Log($"TTS (DIY): 检测到无效的请求方法 {_diyConfig.Method}，重置为POST");
                        _diyConfig.Method = "POST";
                        hasChanges = true;
                    }
                }

                // 清理过长的请求体（防止配置文件过大）
                if (!string.IsNullOrWhiteSpace(_diyConfig.RequestBody) && _diyConfig.RequestBody.Length > 10000)
                {
                    Logger.Log($"TTS (DIY): 检测到过长的请求体 ({_diyConfig.RequestBody.Length} 字符)，截断到10000字符");
                    _diyConfig.RequestBody = _diyConfig.RequestBody.Substring(0, 10000);
                    hasChanges = true;
                }

                // 如果有更改，保存配置
                if (hasChanges)
                {
                    try
                    {
                        // 这里应该调用设置的保存方法，但由于Setting类可能没有公共保存方法，
                        // 我们记录日志，实际的保存由调用者处理
                        Logger.Log("TTS (DIY): 配置回收完成，发现更改，请手动保存设置");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS (DIY): 保存配置时出错 {ex.Message}");
                    }
                }

                Logger.Log("TTS (DIY): 配置回收机制执行完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS (DIY): 配置回收机制执行出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 公共方法，允许外部调用配置回收
        /// </summary>
        public void TriggerConfigCleanup()
        {
            PerformConfigCleanup();
        }
    }
}