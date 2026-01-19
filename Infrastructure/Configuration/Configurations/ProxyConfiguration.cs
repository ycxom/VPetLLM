using VPetLLM.Configuration;

namespace VPetLLM.Infrastructure.Configuration.Configurations
{
    /// <summary>
    /// 代理配置
    /// </summary>
    public class ProxyConfiguration : ConfigurationBase
    {
        public override string ConfigurationName => "Proxy";

        /// <summary>
        /// 是否启用代理
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 跟随系统代理
        /// </summary>
        public bool FollowSystemProxy { get; set; } = false;

        /// <summary>
        /// 代理协议
        /// </summary>
        public string Protocol { get; set; } = "http";

        /// <summary>
        /// 代理地址
        /// </summary>
        public string Address { get; set; } = "127.0.0.1:8080";

        /// <summary>
        /// 对所有API使用代理
        /// </summary>
        public bool ForAllAPI { get; set; } = false;

        /// <summary>
        /// 对Ollama使用代理
        /// </summary>
        public bool ForOllama { get; set; } = false;

        /// <summary>
        /// 对OpenAI使用代理
        /// </summary>
        public bool ForOpenAI { get; set; } = false;

        /// <summary>
        /// 对Gemini使用代理
        /// </summary>
        public bool ForGemini { get; set; } = false;

        /// <summary>
        /// 对Free使用代理
        /// </summary>
        public bool ForFree { get; set; } = false;

        /// <summary>
        /// 对TTS使用代理
        /// </summary>
        public bool ForTTS { get; set; } = false;

        /// <summary>
        /// 对ASR使用代理
        /// </summary>
        public bool ForASR { get; set; } = false;

        /// <summary>
        /// 对MCP使用代理
        /// </summary>
        public bool ForMcp { get; set; } = false;

        /// <summary>
        /// 对插件使用代理
        /// </summary>
        public bool ForPlugin { get; set; } = false;

        public override SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (IsEnabled && !FollowSystemProxy)
            {
                if (string.IsNullOrWhiteSpace(Protocol))
                {
                    result.AddError("代理协议不能为空");
                }
                else if (Protocol != "http" && Protocol != "https" && Protocol != "socks4" && Protocol != "socks5")
                {
                    result.AddError("代理协议必须是http、https、socks4或socks5");
                }

                if (string.IsNullOrWhiteSpace(Address))
                {
                    result.AddError("代理地址不能为空");
                }
                else
                {
                    // 验证地址格式 (host:port)
                    var parts = Address.Split(':');
                    if (parts.Length != 2)
                    {
                        result.AddError("代理地址格式应为 host:port");
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(parts[0]))
                        {
                            result.AddError("代理主机不能为空");
                        }

                        if (!int.TryParse(parts[1], out var port) || port <= 0 || port > 65535)
                        {
                            result.AddError("代理端口必须是1-65535之间的有效数字");
                        }
                    }
                }
            }

            return result;
        }

        public override IConfiguration Clone()
        {
            return new ProxyConfiguration
            {
                IsEnabled = IsEnabled,
                FollowSystemProxy = FollowSystemProxy,
                Protocol = Protocol,
                Address = Address,
                ForAllAPI = ForAllAPI,
                ForOllama = ForOllama,
                ForOpenAI = ForOpenAI,
                ForGemini = ForGemini,
                ForFree = ForFree,
                ForTTS = ForTTS,
                ForASR = ForASR,
                ForMcp = ForMcp,
                ForPlugin = ForPlugin,
                LastModified = LastModified,
                IsModified = IsModified
            };
        }

        public override void Merge(IConfiguration other)
        {
            if (other is ProxyConfiguration otherProxy)
            {
                IsEnabled = otherProxy.IsEnabled;
                FollowSystemProxy = otherProxy.FollowSystemProxy;
                Protocol = otherProxy.Protocol;
                Address = otherProxy.Address;
                ForAllAPI = otherProxy.ForAllAPI;
                ForOllama = otherProxy.ForOllama;
                ForOpenAI = otherProxy.ForOpenAI;
                ForGemini = otherProxy.ForGemini;
                ForFree = otherProxy.ForFree;
                ForTTS = otherProxy.ForTTS;
                ForASR = otherProxy.ForASR;
                ForMcp = otherProxy.ForMcp;
                ForPlugin = otherProxy.ForPlugin;

                MarkAsModified();
            }
        }

        public override void ResetToDefaults()
        {
            IsEnabled = false;
            FollowSystemProxy = false;
            Protocol = "http";
            Address = "127.0.0.1:8080";
            ForAllAPI = false;
            ForOllama = false;
            ForOpenAI = false;
            ForGemini = false;
            ForFree = false;
            ForTTS = false;
            ForASR = false;
            ForMcp = false;
            ForPlugin = false;

            MarkAsModified();
        }

        /// <summary>
        /// 检查指定服务是否应该使用代理
        /// </summary>
        /// <param name="service">服务名称</param>
        /// <returns>是否使用代理</returns>
        public bool ShouldUseProxyFor(string service)
        {
            if (!IsEnabled)
                return false;

            if (ForAllAPI)
                return true;

            return service.ToLowerInvariant() switch
            {
                "ollama" => ForOllama,
                "openai" => ForOpenAI,
                "gemini" => ForGemini,
                "free" => ForFree,
                "tts" => ForTTS,
                "asr" => ForASR,
                "mcp" => ForMcp,
                "plugin" => ForPlugin,
                _ => false
            };
        }

        /// <summary>
        /// 获取代理URL
        /// </summary>
        /// <returns>代理URL，如果未启用则返回null</returns>
        public string GetProxyUrl()
        {
            if (!IsEnabled || FollowSystemProxy)
                return null;

            return $"{Protocol}://{Address}";
        }
    }
}