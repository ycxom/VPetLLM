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
        private readonly HttpClient _httpClient;
        private MediaPlayer? _mediaPlayer;
        private Setting.TTSSetting _settings;

        public TTSService(Setting.TTSSetting settings)
        {
            _settings = settings;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _mediaPlayer = new MediaPlayer();
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
                    case "DouBao":
                    default:
                        audioData = await GetDouBaoTTSAsync(text);
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
                Logger.Log($"TTS错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取豆包TTS音频数据
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>音频数据</returns>
        private async Task<byte[]> GetDouBaoTTSAsync(string text)
        {
            var baseUrl = _settings.DouBao.BaseUrl.TrimEnd('/');
            var encodedText = Uri.EscapeDataString(text);
            var voice = _settings.DouBao.Voice;
            
            var url = $"{baseUrl}/?text={encodedText}&voice={voice}";
            Logger.Log($"TTS: 豆包请求URL: {url}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsByteArrayAsync();
        }

        /// <summary>
        /// 获取OpenAI格式TTS音频数据
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>音频数据</returns>
        private async Task<byte[]> GetOpenAITTSAsync(string text)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, 
                $"{_settings.OpenAI.BaseUrl.TrimEnd('/')}/audio/speech");
            
            // 设置请求头
            if (!string.IsNullOrWhiteSpace(_settings.OpenAI.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_settings.OpenAI.ApiKey}");
            }
            request.Headers.Add("User-Agent", "VPetLLM/1.0");
            
            // 构建请求体
            var requestBody = new
            {
                model = _settings.OpenAI.Model,
                input = text,
                voice = _settings.OpenAI.Voice,
                response_format = _settings.OpenAI.Format,
                speed = _settings.Speed
            };
            
            var json = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            
            Logger.Log($"TTS: OpenAI请求URL: {request.RequestUri}");
            Logger.Log($"TTS: OpenAI请求体: {json}");
            
            // 发送请求
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsByteArrayAsync();
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
                if (_mediaPlayer == null)
                {
                    _mediaPlayer = new MediaPlayer();
                }

                // 停止当前播放
                _mediaPlayer.Stop();

                // 设置音量
                _mediaPlayer.Volume = _settings.Volume;

                // 设置播放速度（如果支持）
                _mediaPlayer.SpeedRatio = _settings.Speed;

                // 打开并播放文件
                _mediaPlayer.Open(new Uri(filePath));
                _mediaPlayer.Play();

                Logger.Log($"TTS: 开始播放音频文件: {filePath}");

                // 等待播放完成（简单实现）
                await Task.Delay(1000); // 给音频播放一些时间

                // 清理临时文件（延迟删除）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000); // 等待10秒后删除
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
        public void UpdateSettings(Setting.TTSSetting settings)
        {
            _settings = settings;
            Logger.Log("TTS: 设置已更新");
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