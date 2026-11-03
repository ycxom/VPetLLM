using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace VPetLLM.Core.Services
{
    /// <summary>本机一个能用的代理端口，<see cref="Software"/> 是常用这个端口的软件，只用来给用户看。</summary>
    public sealed record LocalProxyCandidate(int Port, string Software);

    /// <summary>探测结果：找到的代理，以及系统代理（下载默认已经经过它）。</summary>
    public sealed record LocalProxyScan(Uri? Found, string? Software, Uri? SystemProxy);

    /// <summary>
    /// 找本机代理软件开着的端口，给不会填代理的用户预填。端口开着不算数，
    /// 要真的经它握手到 <see cref="ProbeHost"/>：先试 HTTP CONNECT，不行再试 SOCKS5。
    /// 系统代理已经指着的端口跳过，下载本来就经过它，再选一次没有用。
    /// </summary>
    public sealed class LocalProxyDetector
    {
        // 混合端口 HTTP 和 SOCKS5 都收，按 HTTP 用
        public static readonly IReadOnlyList<LocalProxyCandidate> CommonPorts = new[]
        {
            new LocalProxyCandidate(7890, "Clash"),
            new LocalProxyCandidate(7897, "Clash Verge"),
            new LocalProxyCandidate(10809, "v2rayN"),
            new LocalProxyCandidate(10808, "v2rayN"),
            new LocalProxyCandidate(2080, "NekoRay"),
            new LocalProxyCandidate(12334, "Hiddify"),
            new LocalProxyCandidate(8889, "Qv2ray"),
            new LocalProxyCandidate(7891, "Clash"),
            new LocalProxyCandidate(1089, "Qv2ray"),
            new LocalProxyCandidate(1080, "Shadowsocks")
        };

        public string Host { get; init; } = "127.0.0.1";

        public IReadOnlyList<LocalProxyCandidate> Candidates { get; init; } = CommonPorts;

        /// <summary>经代理握手的目标，安装包从 GitHub 下。</summary>
        public string ProbeHost { get; init; } = "github.com";

        /// <summary>每个端口最多等这么久，各端口同时试。</summary>
        public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>系统代理会把这个地址发去哪，不经代理时返回 null。</summary>
        public Func<Uri, Uri?> SystemProxy { get; init; } = target =>
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(target);
            return proxy is null || proxy == target ? null : proxy;
        };

        public async Task<LocalProxyScan> DetectAsync(CancellationToken cancellationToken = default)
        {
            Uri? system = null;
            try
            {
                system = SystemProxy(new Uri($"https://{ProbeHost}/"));
            }
            catch (Exception ex)
            {
                Logger.Log($"读取系统代理失败: {ex.Message}");
            }

            var candidates = Candidates
                .Where(c => !(system is not null && system.Port == c.Port && IsSameHost(system.Host)))
                .ToList();
            var probes = candidates.Select(c => ProbeAsync(c.Port, cancellationToken)).ToList();
            var results = await Task.WhenAll(probes);
            cancellationToken.ThrowIfCancellationRequested();

            // 都试完再按列表顺序挑，常见的排前面
            for (var i = 0; i < candidates.Count; i++)
            {
                if (results[i] is { } found) return new LocalProxyScan(found, candidates[i].Software, system);
            }
            return new LocalProxyScan(null, null, system);
        }

        private bool IsSameHost(string host)
            => string.Equals(host, Host, StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(Host, out var ip) && IPAddress.IsLoopback(ip)
                   && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                       || (IPAddress.TryParse(host, out var other) && IPAddress.IsLoopback(other))));

        private async Task<Uri?> ProbeAsync(int port, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                if (await TryHttpConnectAsync(port, timeout.Token)) return new Uri($"http://{Host}:{port}");
                if (await TrySocks5Async(port, timeout.Token)) return new Uri($"socks5://{Host}:{port}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时，当没有
            }
            catch (SocketException)
            {
                // 端口没开
            }
            catch (IOException)
            {
                // 对方中途断开，不是代理
            }
            return null;
        }

        private async Task<bool> TryHttpConnectAsync(int port, CancellationToken cancellationToken)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(Host, port, cancellationToken);
            var stream = tcp.GetStream();
            var target = $"{ProbeHost}:443";
            var line = new StringBuilder();
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"), cancellationToken);

                // 只看状态行
                var buffer = new byte[1];
                while (line.Length < 128)
                {
                    if (await stream.ReadAsync(buffer, cancellationToken) == 0) return false;
                    if (buffer[0] == '\n') break;
                    line.Append((char)buffer[0]);
                }
            }
            catch (IOException)
            {
                // SOCKS 端口收到这串字会直接重置连接，接着试 SOCKS5
                return false;
            }

            var parts = line.ToString().Split(' ', 3);
            return parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) && parts[1] == "200";
        }

        private async Task<bool> TrySocks5Async(int port, CancellationToken cancellationToken)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(Host, port, cancellationToken);
            var stream = tcp.GetStream();

            // 版本 5，一种认证方式：不认证
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
            var reply = new byte[2];
            await stream.ReadExactlyAsync(reply, cancellationToken);
            if (reply[0] != 5 || reply[1] != 0) return false;

            var host = Encoding.ASCII.GetBytes(ProbeHost);
            var request = new byte[7 + host.Length];
            request[0] = 5;
            request[1] = 1;
            request[3] = 3;
            request[4] = (byte)host.Length;
            host.CopyTo(request, 5);
            request[^2] = 443 >> 8;
            request[^1] = 443 & 0xFF;
            await stream.WriteAsync(request, cancellationToken);

            await stream.ReadExactlyAsync(reply, cancellationToken);
            return reply[0] == 5 && reply[1] == 0;
        }
    }
}
