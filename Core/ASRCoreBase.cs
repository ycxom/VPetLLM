using System.Net;
using System.Net.Http;
using VPetLLM.Utils.System;

namespace VPetLLM.Core
{
    /// <summary>
    /// ASR服务核心基类
    /// </summary>
    public abstract class ASRCoreBase
    {
        public abstract string Name { get; }
        protected Setting? Settings { get; }

        public event EventHandler<string>? TranscriptionCompleted;
        public event EventHandler<string>? TranscriptionError;

        protected ASRCoreBase(Setting? settings)
        {
            Settings = settings;
        }

        /// <summary>
        /// 转录音频数据
        /// </summary>
        /// <param name="audioData">音频字节数据</param>
        /// <returns>转录文本</returns>
        public abstract Task<string> TranscribeAsync(byte[] audioData);

        /// <summary>
        /// 获取可用模型列表
        /// </summary>
        public virtual Task<System.Collections.Generic.List<string>> GetModelsAsync()
        {
            return Task.FromResult(new System.Collections.Generic.List<string>());
        }

        /// <summary>
        /// 触发转录完成事件
        /// </summary>
        protected void OnTranscriptionCompleted(string text)
        {
            TranscriptionCompleted?.Invoke(this, text);
        }

        /// <summary>
        /// 触发转录错误事件
        /// </summary>
        protected void OnTranscriptionError(string error)
        {
            TranscriptionError?.Invoke(this, error);
        }

        /// <summary>
        /// 获取代理配置
        /// </summary>
        protected IWebProxy? GetProxy()
        {
            if (Settings?.Proxy == null || !Settings.Proxy.IsEnabled)
            {
                Logger.Log($"ASR ({Name}): 代理未启用");
                return null;
            }

            bool useProxy = Settings.Proxy.ForAllAPI || Settings.Proxy.ForASR;

            if (!useProxy)
            {
                Logger.Log($"ASR ({Name}): 不使用代理");
                return null;
            }

            if (Settings.Proxy.FollowSystemProxy)
            {
                Logger.Log($"ASR ({Name}): 使用系统代理");
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
                    Logger.Log($"ASR ({Name}): 使用自定义代理 {proxyUri}");
                    return new WebProxy(proxyUri);
                }
                catch (Exception ex)
                {
                    Logger.Log($"ASR ({Name}): 代理配置错误: {ex.Message}");
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
                Timeout = TimeSpan.FromSeconds(60)
            };
        }
    }
}
