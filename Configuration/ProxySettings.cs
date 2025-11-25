namespace VPetLLM.Configuration
{
    /// <summary>
    /// 代理相关配置模块
    /// </summary>
    public class ProxySettings : ISettings
    {
        /// <summary>
        /// 是否启用代理
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 是否跟随系统代理
        /// </summary>
        public bool FollowSystemProxy { get; set; } = false;

        /// <summary>
        /// 代理协议 (http, https, socks4, socks5)
        /// </summary>
        public string Protocol { get; set; } = "http";

        /// <summary>
        /// 代理地址 (host:port)
        /// </summary>
        public string Address { get; set; } = "127.0.0.1:8080";

        /// <summary>
        /// 是否对所有 API 使用代理
        /// </summary>
        public bool ForAllAPI { get; set; } = false;

        /// <summary>
        /// 是否对 Ollama 使用代理
        /// </summary>
        public bool ForOllama { get; set; } = false;

        /// <summary>
        /// 是否对 OpenAI 使用代理
        /// </summary>
        public bool ForOpenAI { get; set; } = false;

        /// <summary>
        /// 是否对 Gemini 使用代理
        /// </summary>
        public bool ForGemini { get; set; } = false;

        /// <summary>
        /// 是否对 Free 使用代理
        /// </summary>
        public bool ForFree { get; set; } = false;

        /// <summary>
        /// 是否对 TTS 使用代理
        /// </summary>
        public bool ForTTS { get; set; } = false;

        /// <summary>
        /// 是否对 ASR 使用代理
        /// </summary>
        public bool ForASR { get; set; } = false;

        /// <summary>
        /// 是否对 MCP 使用代理
        /// </summary>
        public bool ForMcp { get; set; } = false;

        /// <summary>
        /// 是否对插件使用代理
        /// </summary>
        public bool ForPlugin { get; set; } = false;

        /// <inheritdoc/>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            if (!IsEnabled)
            {
                return result; // 代理未启用，无需验证
            }

            // 如果不跟随系统代理，则需要验证地址
            if (!FollowSystemProxy)
            {
                if (string.IsNullOrWhiteSpace(Address))
                {
                    result.AddError("Proxy address is required when not following system proxy");
                }
                else
                {
                    // 验证地址格式
                    var parts = Address.Split(':');
                    if (parts.Length != 2)
                    {
                        result.AddError("Proxy address must be in format 'host:port'");
                    }
                    else if (!int.TryParse(parts[1], out int port) || port < 1 || port > 65535)
                    {
                        result.AddError("Proxy port must be a valid number between 1 and 65535");
                    }
                }

                // 验证协议
                var validProtocols = new[] { "http", "https", "socks4", "socks4a", "socks5" };
                if (!validProtocols.Contains(Protocol?.ToLower()))
                {
                    result.AddWarning($"Unknown proxy protocol: {Protocol}. Valid protocols are: {string.Join(", ", validProtocols)}");
                }
            }

            return result;
        }
    }
}
