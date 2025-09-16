using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Text;
using System.Text.Json;
using VPetLLM.Utils;

namespace VPetLLM.Utils
{
    public class TTSService
    {
        private HttpClient _httpClient;
        private MediaPlayer? _mediaPlayer;
        private Setting.TTSSetting _settings;
        private Setting.ProxySetting? _proxySettings;

        public TTSService(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            _settings = settings;
            _proxySettings = proxySettings;
            _httpClient = CreateHttpClient();
            _mediaPlayer = new MediaPlayer();
        }

        private HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            
            // 配置代理 - 检查是否启用了TTS代理
            if (_proxySettings != null && _proxySettings.IsEnabled && 
                (_proxySettings.ForAllAPI || _proxySettings.ForTTS))
            {
                Logger.Log($"TTS: 配置代理设置 (ForAllAPI: {_proxySettings.ForAllAPI}, ForTTS: {_proxySettings.ForTTS})");
                
                if (_proxySettings.FollowSystemProxy)
                {
                    handler.UseProxy = true;
                    handler.UseDefaultCredentials = true;
                    Logger.Log($"TTS: 使用系统代理");
                }
                else if (!string.IsNullOrEmpty(_proxySettings.Address))
                {
                    try
                    {
                        // 确保协议格式正确
                        var protocol = _proxySettings.Protocol?.ToLower() ?? "http";
                        if (protocol != "http" && protocol != "https" && 
                            protocol != "socks4" && protocol != "socks4a" && protocol != "socks5")
                        {
                            protocol = "http"; // 默认使用http
                        }
                        
                        var proxyUri = new Uri($"{protocol}://{_proxySettings.Address}");
                        handler.Proxy = new System.Net.WebProxy(proxyUri);
                        handler.UseProxy = true;
                        Logger.Log($"TTS: 使用自定义代理: {proxyUri} (协议: {protocol})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: 代理配置错误: {ex.Message}，将不使用代理");
                        handler.UseProxy = false;
                    }
                }
            }
            else
            {
                Logger.Log($"TTS: 不使用代理 (代理未启用或TTS代理被禁用)");
            }

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        /// <summary>
        /// 播放文本转语音
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns></returns>
        public async Task<bool> PlayTextAsync(string text)
        {
            if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");
                
                byte[] audioData;
                string fileExtension;

                // 根据提供商获取音频数据
                switch (_settings.Provider)
                {
                    case "OpenAI":
                        audioData = await GetOpenAITTSAsync(text);
                        fileExtension = _settings.OpenAI.Format;
                        break;
                    case "URL":
                    default:
                        audioData = await GetURLTTSAsync(text);
                        fileExtension = "mp3";
                        break;
                }

                if (audioData == null || audioData.Length == 0)
                {
                    Logger.Log("TTS: 未获取到音频数据");
                    return false;
                }

                Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 播放音频
                await PlayAudioFileAsync(tempFile);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取URL TTS音频数据
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>音频数据</returns>
        private async Task<byte[]> GetURLTTSAsync(string text)
        {
            var baseUrl = _settings.URL.BaseUrl?.TrimEnd('/');
            var voice = _settings.URL.Voice;
            var requestMethod = _settings.URL.Method?.ToUpper() ?? "GET";
            
            Logger.Log($"TTS: URL请求构建信息:");
            Logger.Log($"TTS: 原始BaseUrl: '{_settings.URL.BaseUrl}'");
            Logger.Log($"TTS: 处理后BaseUrl: '{baseUrl}'");
            Logger.Log($"TTS: 语音ID: '{voice}'");
            Logger.Log($"TTS: 请求方法: '{requestMethod}'");
            Logger.Log($"TTS: 原始文本: '{text}'");
            
            // 验证URL格式
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                Logger.Log($"TTS: 错误 - URL格式无效: {baseUrl}");
                throw new ArgumentException($"无效的URL格式: {baseUrl}");
            }
            
            HttpResponseMessage response;
            
            if (requestMethod == "POST")
            {
                // POST请求
                var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                
                // 设置请求头
                request.Headers.Add("Connection", "keep-alive");
                request.Headers.Add("Accept", "*/*");
                request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
                
                Logger.Log($"TTS: 已设置POST请求头:");
                Logger.Log($"TTS:   Connection: keep-alive");
                Logger.Log($"TTS:   Accept: */*");
                Logger.Log($"TTS:   Accept-Encoding: gzip, deflate, br");
                
                // 构建请求体参数
                var requestBody = new
                {
                    text = text,
                    @void = voice  // 使用voice作为void参数
                };
                
                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                
                Logger.Log($"TTS: POST请求体参数:");
                Logger.Log($"TTS: {json}");
                Logger.Log($"TTS: 发送POST请求到: {baseUrl}");
                
                response = await _httpClient.SendAsync(request);
            }
            else
            {
                // GET请求
                var encodedText = Uri.EscapeDataString(text);
                var url = $"{baseUrl}/?text={encodedText}&voice={voice}";
                
                Logger.Log($"TTS: GET请求URL: {url}");
                Logger.Log($"TTS: 编码后文本: '{encodedText}'");
                Logger.Log($"TTS: 发送GET请求到: {url}");
                
                response = await _httpClient.GetAsync(url);
            }
            
            Logger.Log($"TTS: URL响应状态码: {response.StatusCode}");
            Logger.Log($"TTS: URL响应头:");
            foreach (var header in response.Headers)
            {
                Logger.Log($"TTS:   {header.Key}: {string.Join(", ", header.Value)}");
            }
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Log($"TTS: 错误响应内容: {errorContent}");
            }
            
            response.EnsureSuccessStatusCode();
            
            var responseData = await response.Content.ReadAsByteArrayAsync();
            Logger.Log($"TTS: URL响应数据大小: {responseData.Length} 字节");
            
            return responseData;
        }

        /// <summary>
        /// 获取OpenAI格式TTS音频数据
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>音频数据</returns>
        private async Task<byte[]> GetOpenAITTSAsync(string text)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.OpenAI.BaseUrl);
            
            Logger.Log($"TTS: 开始构建OpenAI请求");
            Logger.Log($"TTS: 目标URL: {_settings.OpenAI.BaseUrl}");
            Logger.Log($"TTS: API Key长度: {(_settings.OpenAI.ApiKey?.Length ?? 0)} 字符");
            Logger.Log($"TTS: 模型: {_settings.OpenAI.Model}");
            Logger.Log($"TTS: 语音ID: {_settings.OpenAI.Voice}");
            Logger.Log($"TTS: 音频格式: {_settings.OpenAI.Format}");
            Logger.Log($"TTS: 文本长度: {text.Length} 字符");
            
            // 设置请求头
            if (!string.IsNullOrWhiteSpace(_settings.OpenAI.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_settings.OpenAI.ApiKey}");
                Logger.Log($"TTS: 已添加Authorization头");
            }
            else
            {
                Logger.Log($"TTS: 警告 - API Key为空");
            }
            
            request.Headers.Add("model", _settings.OpenAI.Model);
            request.Headers.Add("User-Agent", "VPetLLM/1.0");
            
            Logger.Log($"TTS: 已添加model头: {_settings.OpenAI.Model}");
            Logger.Log($"TTS: 已添加User-Agent头: VPetLLM/1.0");
            
            // 构建请求体 - 使用fish.audio格式
            var requestBody = new
            {
                text = text,
                reference_id = _settings.OpenAI.Voice,
                format = _settings.OpenAI.Format
            };
            
            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            
            Logger.Log($"TTS: 完整请求信息:");
            Logger.Log($"TTS: Method: {request.Method}");
            Logger.Log($"TTS: URL: {request.RequestUri}");
            Logger.Log($"TTS: Headers:");
            foreach (var header in request.Headers)
            {
                Logger.Log($"TTS:   {header.Key}: {string.Join(", ", header.Value)}");
            }
            if (request.Content?.Headers != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    Logger.Log($"TTS:   {header.Key}: {string.Join(", ", header.Value)}");
                }
            }
            Logger.Log($"TTS: Request Body:");
            Logger.Log($"TTS: {json}");
            
            // 发送请求
            Logger.Log($"TTS: 发送请求...");
            var response = await _httpClient.SendAsync(request);
            
            Logger.Log($"TTS: 响应状态码: {response.StatusCode}");
            Logger.Log($"TTS: 响应头:");
            foreach (var header in response.Headers)
            {
                Logger.Log($"TTS:   {header.Key}: {string.Join(", ", header.Value)}");
            }
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Log($"TTS: 错误响应内容: {errorContent}");
            }
            
            response.EnsureSuccessStatusCode();
            
            var responseData = await response.Content.ReadAsByteArrayAsync();
            Logger.Log($"TTS: 成功获取响应数据，大小: {responseData.Length} 字节");
            
            return responseData;
        }

        /// <summary>
        /// 播放音频文件
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        private async Task PlayAudioFileAsync(string filePath)
        {
            try
            {
                var playbackCompleted = new TaskCompletionSource<bool>();

                // 确保在UI线程上执行所有MediaPlayer操作
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 确保MediaPlayer在UI线程上创建
                        if (_mediaPlayer == null)
                        {
                            _mediaPlayer = new MediaPlayer();
                            Logger.Log($"TTS: MediaPlayer已在UI线程上创建");
                        }

                        // 清除之前的事件处理器
                        _mediaPlayer.MediaEnded -= OnMediaEnded;
                        _mediaPlayer.MediaFailed -= OnMediaFailed;

                        // 停止当前播放
                        _mediaPlayer.Stop();

                        // 设置音量和播放速度
                        _mediaPlayer.Volume = _settings.Volume;
                        _mediaPlayer.SpeedRatio = _settings.Speed;

                        // 添加事件处理器
                        void OnMediaEnded(object sender, EventArgs e)
                        {
                            Logger.Log($"TTS: 音频播放完成: {filePath}");
                            playbackCompleted.TrySetResult(true);
                        }

                        void OnMediaFailed(object sender, System.Windows.Media.ExceptionEventArgs e)
                        {
                            Logger.Log($"TTS: 音频播放失败: {e.ErrorException?.Message}");
                            playbackCompleted.TrySetResult(false);
                        }

                        _mediaPlayer.MediaEnded += OnMediaEnded;
                        _mediaPlayer.MediaFailed += OnMediaFailed;

                        // 打开并播放文件
                        _mediaPlayer.Open(new Uri(filePath));
                        _mediaPlayer.Play();

                        Logger.Log($"TTS: 开始播放音频文件: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: UI线程播放设置失败: {ex.Message}");
                        playbackCompleted.TrySetResult(false);
                    }
                });

                // 等待播放完成或超时（最多30秒）
                var timeoutTask = Task.Delay(30000);
                var completedTask = await Task.WhenAny(playbackCompleted.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Logger.Log($"TTS: 播放超时，停止播放: {filePath}");
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            _mediaPlayer?.Stop();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"TTS: 停止播放失败: {ex.Message}");
                        }
                    });
                }

                // 清理临时文件（延迟删除）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // 等待2秒后删除，确保播放完成
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            Logger.Log($"TTS: 已删除临时文件: {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: 删除临时文件失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止当前播放
        /// </summary>
        public void Stop()
        {
            try
            {
                _mediaPlayer?.Stop();
                Logger.Log("TTS: 已停止播放");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS停止播放错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新TTS设置
        /// </summary>
        /// <param name="settings">新的设置</param>
        /// <param name="proxySettings">代理设置</param>
        public void UpdateSettings(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            _settings = settings;
            
            // 如果代理设置发生变化，重新创建HttpClient
            if (proxySettings != null && !ProxySettingsEqual(_proxySettings, proxySettings))
            {
                _proxySettings = proxySettings;
                _httpClient?.Dispose();
                _httpClient = CreateHttpClient();
                Logger.Log("TTS: 代理设置已更新，重新创建HttpClient");
            }
            
            Logger.Log("TTS: 设置已更新");
        }

        /// <summary>
        /// 比较两个代理设置是否相等
        /// </summary>
        private bool ProxySettingsEqual(Setting.ProxySetting? settings1, Setting.ProxySetting? settings2)
        {
            if (settings1 == null && settings2 == null) return true;
            if (settings1 == null || settings2 == null) return false;
            
            return settings1.IsEnabled == settings2.IsEnabled &&
                   settings1.FollowSystemProxy == settings2.FollowSystemProxy &&
                   settings1.Protocol == settings2.Protocol &&
                   settings1.Address == settings2.Address &&
                   settings1.ForAllAPI == settings2.ForAllAPI &&
                   settings1.ForTTS == settings2.ForTTS;
        }

        /// <summary>
        /// 测试TTS功能
        /// </summary>
        /// <returns></returns>
        public async Task<bool> TestTTSAsync()
        {
            var testText = "这是一个TTS测试，Hello World!";
            Logger.Log("TTS: 开始测试TTS功能");
            return await PlayTextAsync(testText);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Close();
                _mediaPlayer = null;
                _httpClient?.Dispose();
                Logger.Log("TTS: 资源已释放");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS释放资源错误: {ex.Message}");
            }
        }
    }
}