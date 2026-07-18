using System;
using System.Collections.Generic;
using System.Linq;

namespace VPetLLM.Core.Services
{
    internal enum PluginStoreProxyMode
    {
        Direct,
        UrlRewrite,
        HttpProxy
    }

    internal sealed class PluginStoreProxyDecision
    {
        public PluginStoreProxyDecision(PluginStoreProxyMode mode, string? proxyUrl = null)
        {
            Mode = mode;
            ProxyUrl = proxyUrl;
        }

        public PluginStoreProxyMode Mode { get; }
        public string? ProxyUrl { get; }
    }

    /// <summary>
    /// Keeps plugin-store routing dependent only on the plugin-store settings.
    /// Main API proxy settings must never be consulted here.
    /// </summary>
    internal static class ProxyRoutingPolicy
    {
        internal static PluginStoreProxyDecision ResolvePluginStore(bool useProxy, string? proxyUrl)
        {
            if (!useProxy || string.IsNullOrWhiteSpace(proxyUrl))
            {
                return new PluginStoreProxyDecision(PluginStoreProxyMode.Direct);
            }

            var normalizedUrl = proxyUrl.Trim();
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            {
                normalizedUrl = $"http://{normalizedUrl}";
                Uri.TryCreate(normalizedUrl, UriKind.Absolute, out uri);
            }

            if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new PluginStoreProxyDecision(PluginStoreProxyMode.Direct);
            }

            var host = uri.Host;
            var isRewriteService = host.Contains("ghfast", StringComparison.OrdinalIgnoreCase)
                || host.Contains("github", StringComparison.OrdinalIgnoreCase)
                || host.Contains("raw.githubusercontent", StringComparison.OrdinalIgnoreCase);

            return new PluginStoreProxyDecision(
                isRewriteService ? PluginStoreProxyMode.UrlRewrite : PluginStoreProxyMode.HttpProxy,
                normalizedUrl);
        }
    }

    /// <summary>
    /// A proxy is available when at least one independent HTTP probe receives a response.
    /// A single blocked or TLS-incompatible endpoint is not enough evidence to disable it.
    /// </summary>
    internal static class ProxyHealthPolicy
    {
        internal static readonly Uri[] ProbeEndpoints =
        {
            new("https://www.google.com/generate_204"),
            new("https://www.cloudflare.com/cdn-cgi/trace"),
            new("https://www.msftconnecttest.com/connecttest.txt")
        };

        internal static bool IsAvailable(IEnumerable<bool> probeResults)
        {
            return probeResults.Any(success => success);
        }
    }
}
