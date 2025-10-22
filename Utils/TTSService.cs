using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace VPetLLM.Utils
{
    public class TTSService
    {
        private HttpClient _httpClient;
        private MediaPlayer? _mediaPlayer;
        private Setting.TTSSetting _settings;
        private Setting.ProxySetting? _proxySettings;
        private readonly object _playbackLock = new object();
        private bool _isPlaying = false;
        private TaskCompletionSource<bool>? _currentPlaybackTask;

        public TTSService(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            _settings = settings;
            _proxySettings = proxySettings;
            _httpClient = CreateHttpClient();
            // MediaPlayer将在需要时在UI线程上创建
            _mediaPlayer = null;
        }

        private HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();

            // 检查是否应该使用代理
            bool useProxy = false;

            if (_proxySettings != null && _proxySettings.IsEnabled)
            {
                // TTS只根据ForTTS设置决定，不受ForAllAPI影响
                useProxy = _proxySettings.ForTTS;
            }

            if (useProxy)
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
                // 明确禁用代理，防止使用系统默认代理
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        /// <summary>
        /// 仅下载TTS音频文件（不播放）
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns>音频文件路径，失败时返回null</returns>
        public async Task<string> DownloadTTSAudioAsync(string text)
        {
            if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                // Logger.Log($"TTS: 开始下载音频: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                // 根据提供商获取音频数据
                // Logger.Log($"TTS: 当前提供商设置: '{_settings.Provider}'");
                switch (_settings.Provider)
                {
                    case "OpenAI":
                        audioData = await GetOpenAITTSAsync(text);
                        fileExtension = _settings.OpenAI.Format;
                        break;
                    case "DIY":
                        audioData = await GetDIYTTSAsync(text);
                        var diyConfig = DIYTTSConfig.LoadConfig();
                        fileExtension = diyConfig.ResponseFormat;
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
                    return null;
                }

                // Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 应用音量增益
                if (Math.Abs(_settings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _settings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                // Logger.Log($"TTS: 音频文件已下载到: {tempFile}");

                return tempFile;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS下载错误: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 直接播放音频文件
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        public async Task PlayAudioFileDirectAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Log($"TTS: 音频文件不存在: {filePath}");
                return;
            }

            try
            {
                // Logger.Log($"TTS: 直接播放预下载音频: {filePath}");

                // 等待当前播放完成，确保不会被中断
                await WaitForCurrentPlaybackAsync();

                // 播放音频文件
                await PlayAudioFileAsync(filePath);

                // Logger.Log($"TTS: 预下载音频播放完成: {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS直接播放错误: {ex.Message}");
                Logger.Log($"TTS直接播放异常堆栈: {ex.StackTrace}");
            }
        }



        /// <summary>
        /// 等待当前播放完成
        /// </summary>
        private async Task WaitForCurrentPlaybackAsync()
        {
            TaskCompletionSource<bool>? currentTask = null;

            lock (_playbackLock)
            {
                if (_isPlaying && _currentPlaybackTask != null)
                {
                    currentTask = _currentPlaybackTask;
                }
            }

            if (currentTask != null)
            {
                Logger.Log("TTS: 等待当前播放完成...");
                await currentTask.Task;
                Logger.Log("TTS: 当前播放已完成");
            }
        }

        /// <summary>
        /// 开始播放文本转语音（等待当前播放完成后再播放新音频，但立即返回）
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns></returns>
        public async Task<bool> StartPlayTextAsync(string text)
        {
            if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                // 根据提供商获取音频数据
                Logger.Log($"TTS: 当前提供商设置: '{_settings.Provider}'");
                switch (_settings.Provider)
                {
                    case "OpenAI":
                        audioData = await GetOpenAITTSAsync(text);
                        fileExtension = _settings.OpenAI.Format;
                        break;
                    case "DIY":
                        audioData = await GetDIYTTSAsync(text);
                        var diyConfigForTask = DIYTTSConfig.LoadConfig();
                        fileExtension = diyConfigForTask.ResponseFormat;
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

                // 应用音量增益
                if (Math.Abs(_settings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _settings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 开始播放音频（不等待播放完成，但设置播放状态）
                _ = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                Logger.Log($"TTS: 音频开始播放，立即返回");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 开始播放文本转语音并返回可等待的任务
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns>可等待的播放任务</returns>
        public async Task<Task> StartPlayTextAsyncWithTask(string text)
        {
            if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(text))
            {
                return Task.CompletedTask;
            }

            // 等待当前播放完成
            await WaitForCurrentPlaybackAsync();

            try
            {
                Logger.Log($"TTS: 开始转换文本: {text.Substring(0, Math.Min(text.Length, 50))}...");

                byte[] audioData;
                string fileExtension;

                // 根据提供商获取音频数据
                Logger.Log($"TTS: 当前提供商设置: '{_settings.Provider}'");
                switch (_settings.Provider)
                {
                    case "OpenAI":
                        audioData = await GetOpenAITTSAsync(text);
                        fileExtension = _settings.OpenAI.Format;
                        break;
                    case "DIY":
                        audioData = await GetDIYTTSAsync(text);
                        var diyConfig = DIYTTSConfig.LoadConfig();
                        fileExtension = diyConfig.ResponseFormat;
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
                    return Task.CompletedTask;
                }

                Logger.Log($"TTS: 成功获取音频数据，大小: {audioData.Length} 字节");

                // 应用音量增益
                if (Math.Abs(_settings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _settings.VolumeGain);
                }

                // 保存到临时文件
                var tempDir = Path.GetTempPath();
                var tempFileName = $"VPetLLM_TTS_{Guid.NewGuid():N}.{fileExtension}";
                var tempFile = Path.Combine(tempDir, tempFileName);
                await File.WriteAllBytesAsync(tempFile, audioData);
                Logger.Log($"TTS: 音频文件保存到: {tempFile}");

                // 返回播放任务
                var playTask = Task.Run(async () => await PlayAudioFileAsync(tempFile));
                Logger.Log($"TTS: 音频开始播放，返回播放任务");

                return playTask;
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS播放错误: {ex.Message}");
                return Task.CompletedTask;
            }
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
                Logger.Log($"TTS: 当前提供商设置: '{_settings.Provider}'");
                switch (_settings.Provider)
                {
                    case "OpenAI":
                        audioData = await GetOpenAITTSAsync(text);
                        fileExtension = _settings.OpenAI.Format;
                        break;
                    case "DIY":
                        audioData = await GetDIYTTSAsync(text);
                        var diyConfig = DIYTTSConfig.LoadConfig();
                        fileExtension = diyConfig.ResponseFormat;
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

                // 应用音量增益
                if (Math.Abs(_settings.VolumeGain) > 0.01)
                {
                    audioData = AudioProcessor.ApplyVolumeGain(audioData, _settings.VolumeGain);
                }

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

            // Logger.Log($"TTS: URL请求构建信息:");
            // Logger.Log($"TTS: 原始BaseUrl: '{_settings.URL.BaseUrl}'");
            // Logger.Log($"TTS: 处理后BaseUrl: '{baseUrl}'");
            // Logger.Log($"TTS: 语音ID: '{voice}'");
            // Logger.Log($"TTS: 请求方法: '{requestMethod}'");
            // Logger.Log($"TTS: 原始文本: '{text}'");

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

                // Logger.Log($"TTS: 已设置POST请求头:");
                // Logger.Log($"TTS:   Connection: keep-alive");
                // Logger.Log($"TTS:   Accept: */*");
                // Logger.Log($"TTS:   Accept-Encoding: gzip, deflate, br");

                // 构建请求体参数
                var requestBody = new
                {
                    text = text,
                    @void = voice  // 使用voice作为void参数
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // Logger.Log($"TTS: POST请求体参数:");
                // Logger.Log($"TTS: {json}");
                // Logger.Log($"TTS: 发送POST请求到: {baseUrl}");

                response = await _httpClient.SendAsync(request);
            }
            else
            {
                // GET请求
                var encodedText = Uri.EscapeDataString(text);
                var url = $"{baseUrl}/?text={encodedText}&voice={voice}";

                // Logger.Log($"TTS: GET请求URL: {url}");
                // Logger.Log($"TTS: 发送GET请求到: {url}");

                response = await _httpClient.GetAsync(url);
            }

            // Logger.Log($"TTS: URL响应状态码: {response.StatusCode}");
            // Logger.Log($"TTS: URL响应头:");
            // foreach (var header in response.Headers)
            // {
            //     Logger.Log($"TTS:   {header.Key}: {string.Join(", ", header.Value)}");
            // }

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
        /// 优化：使用BeginInvoke异步调度UI操作，减少阻塞
        /// </summary>
        /// <param name="filePath">音频文件路径</param>
        /// <returns></returns>
        private async Task PlayAudioFileAsync(string filePath)
        {
            try
            {
                // Logger.Log($"TTS: 开始播放音频文件: {filePath}");

                TaskCompletionSource<bool> currentTask;
                lock (_playbackLock)
                {
                    _isPlaying = true;
                    _currentPlaybackTask = new TaskCompletionSource<bool>();
                    currentTask = _currentPlaybackTask;
                }

                var tcs = new TaskCompletionSource<bool>();

                // 优化：使用BeginInvoke异步调度，避免阻塞当前线程
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 确保MediaPlayer已初始化
                        if (_mediaPlayer == null)
                        {
                            _mediaPlayer = new MediaPlayer();
                            // Logger.Log("TTS: MediaPlayer已在UI线程上创建");
                        }

                        // 停止当前播放
                        _mediaPlayer.Stop();

                        // 设置音频文件
                        _mediaPlayer.Open(new Uri(filePath, UriKind.Absolute));

                        // 设置音量（基础音量 + 增益转换为线性系数）
                        // MediaPlayer.Volume 可以设置超过1.0，最大到100
                        double baseVolume = _settings.Volume;
                        double gainLinear = Math.Pow(10.0, _settings.VolumeGain / 20.0);
                        double finalVolume = Math.Min(100.0, Math.Max(0.0, baseVolume * gainLinear));
                        _mediaPlayer.Volume = finalVolume;

                        // Logger.Log($"TTS: 设置播放器音量 - 基础音量: {baseVolume:F2}, 增益: {_settings.VolumeGain:F1}dB (线性系数: {gainLinear:F3}), 最终音量: {finalVolume:F2}");

                        // 设置播放结束事件
                        EventHandler mediaEndedHandler = null;
                        EventHandler<ExceptionEventArgs> mediaFailedHandler = null;

                        mediaEndedHandler = (s, e) =>
                        {
                            try
                            {
                                _mediaPlayer.MediaEnded -= mediaEndedHandler;
                                _mediaPlayer.MediaFailed -= mediaFailedHandler;
                                // Logger.Log($"TTS: 音频播放完成: {filePath}");

                                lock (_playbackLock)
                                {
                                    _isPlaying = false;
                                    currentTask?.TrySetResult(true);
                                }

                                tcs.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS: 播放完成事件处理异常: {ex.Message}");
                                tcs.TrySetResult(true); // 即使异常也标记为完成
                            }
                        };

                        mediaFailedHandler = (s, e) =>
                        {
                            try
                            {
                                _mediaPlayer.MediaEnded -= mediaEndedHandler;
                                _mediaPlayer.MediaFailed -= mediaFailedHandler;
                                Logger.Log($"TTS: 音频播放失败: {filePath}, 错误: {e.ErrorException?.Message}");

                                lock (_playbackLock)
                                {
                                    _isPlaying = false;
                                    currentTask?.TrySetResult(false);
                                }

                                tcs.TrySetResult(false);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TTS: 播放失败事件处理异常: {ex.Message}");
                                tcs.TrySetResult(false); // 即使异常也标记为失败
                            }
                        };

                        _mediaPlayer.MediaEnded += mediaEndedHandler;
                        _mediaPlayer.MediaFailed += mediaFailedHandler;

                        // 开始播放
                        _mediaPlayer.Play();
                        Logger.Log($"TTS: 开始播放音频: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TTS: UI线程播放设置失败: {ex.Message}");
                        Logger.Log($"TTS: UI线程异常堆栈: {ex.StackTrace}");

                        lock (_playbackLock)
                        {
                            _isPlaying = false;
                            currentTask?.TrySetResult(false);
                        }

                        tcs.TrySetResult(false);
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                // 等待播放完成（使用ConfigureAwait(false)避免UI线程阻塞）
                Logger.Log($"TTS: 等待音频播放完成: {filePath}");
                var playbackResult = await tcs.Task.ConfigureAwait(false);
                Logger.Log($"TTS: 音频播放结果: {playbackResult}, 文件: {filePath}");

                // 清理临时文件（延迟删除，优化：减少延迟到1秒）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false); // 优化：从2秒减少到1秒
                    try
                    {
                        if (File.Exists(filePath) && filePath.Contains("VPetLLM_TTS_"))
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
                Logger.Log($"TTS播放异常堆栈: {ex.StackTrace}");

                lock (_playbackLock)
                {
                    _isPlaying = false;
                    _currentPlaybackTask?.TrySetResult(false);
                }
            }
        }

        /// <summary>
        /// 停止当前播放
        /// </summary>
        public void Stop()
        {
            try
            {
                lock (_playbackLock)
                {
                    _mediaPlayer?.Stop();
                    _isPlaying = false;
                    _currentPlaybackTask?.TrySetResult(false);
                }
                Logger.Log("TTS: 已停止播放");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTS停止播放错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_playbackLock)
                {
                    return _isPlaying;
                }
            }
        }

        /// <summary>
        /// 更新TTS设置
        /// </summary>
        /// <param name="settings">新的设置</param>
        /// <param name="proxySettings">代理设置</param>
        public void UpdateSettings(Setting.TTSSetting settings, Setting.ProxySetting? proxySettings = null)
        {
            var oldVolume = _settings?.Volume ?? 1.0;
            var oldVolumeGain = _settings?.VolumeGain ?? 0.0;
            
            _settings = settings;

            // 如果代理设置发生变化，重新创建HttpClient
            if (proxySettings != null && !ProxySettingsEqual(_proxySettings, proxySettings))
            {
                _proxySettings = proxySettings;
                _httpClient?.Dispose();
                _httpClient = CreateHttpClient();
                // Logger.Log("TTS: 代理设置已更新，重新创建HttpClient");
            }

            // 如果音量设置发生变化，立即应用到当前播放器
            if (Math.Abs(oldVolume - settings.Volume) > 0.01 || Math.Abs(oldVolumeGain - settings.VolumeGain) > 0.01)
            {
                UpdateCurrentPlayerVolume();
            }

            // Logger.Log("TTS: 设置已更新");
        }

        /// <summary>
        /// 立即更新当前播放器的音量
        /// </summary>
        private void UpdateCurrentPlayerVolume()
        {
            if (_mediaPlayer != null)
            {
                try
                {
                    // 在UI线程上更新音量
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (_mediaPlayer != null)
                        {
                            // 计算最终音量（基础音量 + 增益转换为线性系数）
                            double baseVolume = _settings.Volume;
                            double gainLinear = Math.Pow(10.0, _settings.VolumeGain / 20.0);
                            double finalVolume = Math.Min(100.0, Math.Max(0.0, baseVolume * gainLinear));
                            
                            _mediaPlayer.Volume = finalVolume;
                            // Logger.Log($"TTS: 立即更新播放器音量 - 基础音量: {baseVolume:F2}, 增益: {_settings.VolumeGain:F1}dB, 最终音量: {finalVolume:F2}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTS: 更新播放器音量失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 公共方法：立即更新音量设置并应用到当前播放器
        /// </summary>
        /// <param name="volume">基础音量 (0-10)</param>
        /// <param name="volumeGain">音量增益 (dB, -20到+40)</param>
        public void UpdateVolumeSettings(double volume, double volumeGain)
        {
            _settings.Volume = volume;
            _settings.VolumeGain = volumeGain;
            UpdateCurrentPlayerVolume();
            // Logger.Log($"TTS: 音量设置已更新 - 音量: {volume:F2}, 增益: {volumeGain:F1}dB");
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
        /// 获取DIY TTS音频数据
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>音频数据</returns>
        private async Task<byte[]> GetDIYTTSAsync(string text)
        {
            // 从配置文件加载 DIY TTS 配置
            var diyConfig = DIYTTSConfig.LoadConfig();

            if (!diyConfig.Enabled)
            {
                Logger.Log("TTS: DIY TTS 未启用");
                throw new InvalidOperationException("DIY TTS 未启用");
            }

            if (!DIYTTSConfig.IsValidConfig(diyConfig))
            {
                Logger.Log("TTS: DIY TTS 配置无效");
                throw new InvalidOperationException("DIY TTS 配置无效");
            }

            var baseUrl = diyConfig.BaseUrl?.TrimEnd('/');
            var requestMethod = diyConfig.Method?.ToUpper() ?? "POST";
            var contentType = diyConfig.ContentType ?? "application/json";

            // Logger.Log($"TTS: DIY请求构建信息:");
            // Logger.Log($"TTS: 配置文件路径: {DIYTTSConfig.GetConfigFilePath()}");
            // Logger.Log($"TTS: 目标URL: '{baseUrl}'");
            // Logger.Log($"TTS: 请求方法: '{requestMethod}'");
            // Logger.Log($"TTS: Content-Type: '{contentType}'");
            // Logger.Log($"TTS: 原始文本: '{text}'");

            // 验证URL格式
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                Logger.Log($"TTS: 错误 - URL格式无效: {baseUrl}");
                throw new ArgumentException($"无效的URL格式: {baseUrl}");
            }

            HttpResponseMessage response;

            // 设置超时时间
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(diyConfig.Timeout));

            if (requestMethod == "POST")
            {
                // POST请求
                var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

                // 设置自定义请求头
                foreach (var header in diyConfig.CustomHeaders)
                {
                    if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        // 强制限制User-Agent为VPetLLM
                        request.Headers.Add("User-Agent", "VPetLLM");
                        Logger.Log($"TTS: 已设置User-Agent: VPetLLM (强制限制)");
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
                            Logger.Log($"TTS: 已设置请求头: {header.Key}: {header.Value}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"TTS: 设置请求头失败 {header.Key}: {ex.Message}");
                        }
                    }
                }

                // 构建请求体，处理键值对格式并替换文本占位符
                var requestBodyDict = new Dictionary<string, object>(diyConfig.RequestBody ?? new Dictionary<string, object>());

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

                Logger.Log($"TTS: POST请求体:");
                Logger.Log($"TTS: {requestBodyJson}");
                Logger.Log($"TTS: 发送POST请求到: {baseUrl}");

                response = await _httpClient.SendAsync(request, cts.Token);
            }
            else
            {
                // GET请求 - 构建查询参数
                var queryParams = new List<string>();

                // 处理 requestBody 中的参数，替换 {text} 占位符并添加到查询参数
                foreach (var param in diyConfig.RequestBody ?? new Dictionary<string, object>())
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
                if (!diyConfig.RequestBody.ContainsKey("text"))
                {
                    var encodedText = Uri.EscapeDataString(text);
                    queryParams.Add($"text={encodedText}");
                }

                var queryString = string.Join("&", queryParams);
                var url = $"{baseUrl}?{queryString}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                // 设置自定义请求头（GET请求不设置Content-Type）
                foreach (var header in diyConfig.CustomHeaders)
                {
                    if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        // 强制限制User-Agent为VPetLLM
                        request.Headers.Add("User-Agent", "VPetLLM");
                        Logger.Log($"TTS: 已设置User-Agent: VPetLLM (强制限制)");
                    }
                    else if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        // GET请求跳过Content-Type设置
                        Logger.Log($"TTS: 跳过Content-Type设置 (GET请求不需要)");
                    }
                    else
                    {
                        try
                        {
                            request.Headers.Add(header.Key, header.Value);
                            Logger.Log($"TTS: 已设置请求头: {header.Key}: {header.Value}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"TTS: 设置请求头失败 {header.Key}: {ex.Message}");
                        }
                    }
                }

                Logger.Log($"TTS: GET请求URL: {url}");
                Logger.Log($"TTS: 发送GET请求到: {url}");

                response = await _httpClient.SendAsync(request, cts.Token);
            }

            Logger.Log($"TTS: DIY响应状态码: {response.StatusCode}");
            Logger.Log($"TTS: DIY响应头:");
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
            Logger.Log($"TTS: DIY响应数据大小: {responseData.Length} 字节");

            return responseData;
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