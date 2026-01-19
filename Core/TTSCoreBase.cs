using System.Net;
using System.Net.Http;
using VPetLLM.Utils.System;

namespace VPetLLM.Core
{
    /// <summary>
    /// TTS服务核心基类
    /// </summary>
    public abstract class TTSCoreBase
    {
        public abstract string Name { get; }
        protected Setting? Settings { get; }

        public event EventHandler<byte[]>? AudioGenerated;
        public event EventHandler<string>? AudioGenerationError;

        protected TTSCoreBase(Setting? settings)
        {
            Settings = settings;
        }

        /// <summary>
        /// 生成语音音频数据
        /// </summary>
        /// <param name="text">要转换的文本</param>
        /// <returns>音频字节数据</returns>
        public abstract Task<byte[]> GenerateAudioAsync(string text);

        /// <summary>
        /// 获取音频格式扩展名
        /// </summary>
        public abstract string GetAudioFormat();

        /// <summary>
        /// 触发音频生成完成事件
        /// </summary>
        protected void OnAudioGenerated(byte[] audioData)
        {
            AudioGenerated?.Invoke(this, audioData);
        }

        /// <summary>
        /// 触发音频生成错误事件
        /// </summary>
        protected void OnAudioGenerationError(string error)
        {
            AudioGenerationError?.Invoke(this, error);
        }

        /// <summary>
        /// 获取代理配置
        /// </summary>
        protected IWebProxy? GetProxy()
        {
            if (Settings?.Proxy == null || !Settings.Proxy.IsEnabled)
            {
                Logger.Log($"TTS ({Name}): 代理未启用");
                return null;
            }

            bool useProxy = Settings.Proxy.ForAllAPI || Settings.Proxy.ForTTS;

            if (!useProxy)
            {
                Logger.Log($"TTS ({Name}): 不使用代理");
                return null;
            }

            if (Settings.Proxy.FollowSystemProxy)
            {
                Logger.Log($"TTS ({Name}): 使用系统代理");
                return WebRequest.GetSystemWebProxy();
            }
            else if (!string.IsNullOrWhiteSpace(Settings.Proxy.Address))
            {
                try
                {
                    var protocol = Settings.Proxy.Protocol?.ToLower() ?? "http";
                    if (protocol != "http" && protocol != "https" &&
                        protocol != "socks4" && protocol != "socks4a" && protocol != "socks5")
                    {
                        protocol = "http";
                    }

                    var proxyUri = new Uri($"{protocol}://{Settings.Proxy.Address}");
                    Logger.Log($"TTS ({Name}): 使用自定义代理 {proxyUri}");
                    return new WebProxy(proxyUri);
                }
                catch (Exception ex)
                {
                    Logger.Log($"TTS ({Name}): 代理配置错误: {ex.Message}");
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// 创建 HttpClient
        /// </summary>
        protected HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            var proxy = GetProxy();

            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }
    }
}