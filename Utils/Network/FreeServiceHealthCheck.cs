using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace VPetLLM.Utils.Network
{
    /// <summary>
    /// Free 服务可用性预检：在发起真正的请求前先探一次 <c>/health</c>，结果按 6 秒 TTL 缓存。
    ///
    /// 动机：业务请求的超时是 30 秒。服务器下线时每次说话都要干等满 30 秒，期间气泡与动画全被拖住；
    /// 预检链路通畅时 1 秒内返回，短 TTL 内的连续请求（同一句话拆成的多段）复用同一结论。
    ///
    /// TTL 取 6 秒是刻意压短的：宁可多探几次，也要让服务器恢复/掉线能在几秒内反映出来，
    /// 而不是被一个长缓存钉死在过期结论上。代价是探测频次上升，但 /health 本身极轻。
    ///
    /// 缓存以**主机（scheme+host+port）为键**：TTS、ASR、Chat 走的是同一个 standalone 网关，
    /// 谁先探到结果谁就把 TTL 刷新，其余服务直接复用；任何一次真实请求的成败也会回填同一条目。
    ///
    /// 探测优先打**穿透到后端**的路径（如 <c>/tts/health</c> → GPT-SoVITS 的 <c>/health</c>），
    /// 这样后端挂掉而网关还活着的情况也能被识别；该路径不存在时退回网关自身的 <c>/health</c>。
    ///
    /// 安全默认：只有在**明确判定服务不可用**时才拦截请求（连不上、超时、5xx）。
    /// 探测本身出意外、或两个候选路径都没有 /health 时一律放行，
    /// 绝不能因为预检机制本身把可用的服务判死。
    /// </summary>
    public static class FreeServiceHealthCheck
    {
        /// <summary>探测结论的有效期。短 TTL 换取对服务器状态变化的快速跟进。</summary>
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(6);

        /// <summary>心跳超时：链路正常时 /health 应在 1 秒内返回，2 秒还没动静就按服务异常处理。</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

        /// <summary>键为主机（scheme://host:port），TTS/ASR/Chat 共用。</summary>
        private static readonly ConcurrentDictionary<string, HealthEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已确认可用的健康检查地址，避免每次都重试那个不存在的候选。</summary>
        private static readonly ConcurrentDictionary<string, string> _resolvedProbeUrls = new(StringComparer.OrdinalIgnoreCase);

        private sealed class HealthEntry
        {
            public bool Healthy;
            public string Reason = "";
            public DateTime CheckedAt;
        }

        /// <summary>
        /// 判断 <paramref name="apiUrl"/> 所属服务当前是否可用。
        /// 返回 false 表示调用方应立即失败，不要发起那次会干等到超时的请求。
        /// </summary>
        /// <param name="apiUrl">业务接口地址，健康检查地址由它推导。</param>
        /// <param name="proxy">与业务请求一致的代理设置，保证探测走的是同一条链路。</param>
        /// <param name="log">日志回调。</param>
        public static async Task<(bool healthy, string reason)> IsHealthyAsync(
            string apiUrl, IWebProxy? proxy, Action<string>? log = null)
        {
            var host = GetHostKey(apiUrl);
            if (host is null) return (true, "无法推导健康检查地址，放行");

            if (TryGetCached(host, out var cached))
                return (cached.Healthy, cached.Reason);

            // 单飞：同一主机同时只探一次，避免一句话拆成多段、或 TTS 与 ASR 同时启动时并发探测。
            var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryGetCached(host, out cached))
                    return (cached.Healthy, cached.Reason);

                var result = await ProbeAsync(host, apiUrl, proxy, log).ConfigureAwait(false);
                Store(host, result.healthy, result.reason);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 业务请求自己失败时回填结论，让紧随其后的请求（含其它服务）不必重复踩坑；
        /// 成功时同样回填，可立刻抹掉一次误判并刷新 TTL。
        /// </summary>
        public static void ReportOutcome(string apiUrl, bool succeeded, string reason)
        {
            var host = GetHostKey(apiUrl);
            if (host is null) return;
            Store(host, succeeded, reason);
        }

        /// <summary>清空缓存，让下一次调用立即重新探测（切换服务器或用户手动重试时使用）。</summary>
        public static void Invalidate()
        {
            _cache.Clear();
            _resolvedProbeUrls.Clear();
        }

        private static void Store(string host, bool healthy, string reason)
            => _cache[host] = new HealthEntry { Healthy = healthy, Reason = reason, CheckedAt = DateTime.UtcNow };

        private static bool TryGetCached(string host, out HealthEntry entry)
        {
            if (_cache.TryGetValue(host, out entry!))
            {
                var age = DateTime.UtcNow - entry.CheckedAt;
                if (age >= TimeSpan.Zero && age < Ttl)
                    return true;
            }
            entry = null!;
            return false;
        }

        private static async Task<(bool healthy, string reason)> ProbeAsync(
            string host, string apiUrl, IWebProxy? proxy, Action<string>? log)
        {
            using var handler = new HttpClientHandler();
            if (proxy is not null) { handler.Proxy = proxy; handler.UseProxy = true; }
            else { handler.Proxy = null; handler.UseProxy = false; }

            using var client = new HttpClient(handler) { Timeout = ProbeTimeout };

            foreach (var probeUrl in GetProbeUrls(host, apiUrl))
            {
                var (verdict, reason) = await ProbeOnceAsync(client, probeUrl, log).ConfigureAwait(false);
                switch (verdict)
                {
                    case ProbeVerdict.Healthy:
                        _resolvedProbeUrls[host] = probeUrl;
                        return (true, reason);
                    case ProbeVerdict.Unhealthy:
                        // 连不上/超时/5xx 是确定的坏消息，换个路径也没意义。
                        return (false, reason);
                    case ProbeVerdict.NoSuchEndpoint:
                        // 这个候选没有 /health，试下一个。
                        _resolvedProbeUrls.TryRemove(host, out _);
                        continue;
                }
            }

            log?.Invoke("未找到可用的健康检查端点，跳过预检");
            return (true, "无健康检查端点");
        }

        private enum ProbeVerdict { Healthy, Unhealthy, NoSuchEndpoint }

        private static async Task<(ProbeVerdict verdict, string reason)> ProbeOnceAsync(
            HttpClient client, string probeUrl, Action<string>? log)
        {
            try
            {
                var started = DateTime.UtcNow;
                // HEAD 更省，但不少后端只实现了 GET；/health 的响应体本来就极小。
                using var response = await client.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);

                var status = (int)response.StatusCode;
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                if (response.IsSuccessStatusCode)
                {
                    log?.Invoke($"健康检查通过 ({status}, {elapsed:F0}ms) via {probeUrl}，结论缓存 {Ttl.TotalSeconds:0} 秒");
                    return (ProbeVerdict.Healthy, "服务正常");
                }

                // 该路径没有 /health 路由——换下一个候选，不能据此判定服务不可用。
                if (status is 404 or 405 or 501)
                {
                    log?.Invoke($"{probeUrl} 无健康检查端点 ({status})");
                    return (ProbeVerdict.NoSuchEndpoint, "无健康检查端点");
                }

                // 5xx 是服务端明确的不可用信号（网关探不到后端时也会走这里）。
                if (status >= 500)
                {
                    log?.Invoke($"健康检查失败：{probeUrl} 返回 {status}");
                    return (ProbeVerdict.Unhealthy, $"服务暂时不可用 ({status})");
                }

                // 其余 4xx（鉴权等）说明服务器是活的，业务请求该发还得发。
                log?.Invoke($"健康检查返回 {status}，视为服务在线");
                return (ProbeVerdict.Healthy, $"服务在线 ({status})");
            }
            catch (TaskCanceledException)
            {
                log?.Invoke($"健康检查超时（{ProbeTimeout.TotalSeconds:0} 秒），判定服务不可用");
                return (ProbeVerdict.Unhealthy, "服务无响应");
            }
            catch (HttpRequestException ex)
            {
                log?.Invoke($"健康检查网络错误：{ex.Message}");
                return (ProbeVerdict.Unhealthy, "无法连接到服务");
            }
            catch (Exception ex)
            {
                // 预检自身的意外不应该拖累业务请求。
                log?.Invoke($"健康检查异常，跳过预检：{ex.Message}");
                return (ProbeVerdict.Healthy, "预检异常，已放行");
            }
        }

        /// <summary>
        /// 候选健康检查地址，按优先级：
        /// 1. 已确认可用的那个（命中后不再试其它）；
        /// 2. 与业务接口同级的 <c>…/health</c>——网关会把它转发到后端（<c>/tts/health</c> → GPT-SoVITS 的 <c>/health</c>），
        ///    能一并覆盖"网关活着但后端挂了"；
        /// 3. 网关根部的 <c>/health</c>。
        /// </summary>
        private static IEnumerable<string> GetProbeUrls(string host, string apiUrl)
        {
            var candidates = new List<string>(3);

            if (_resolvedProbeUrls.TryGetValue(host, out var known) && !string.IsNullOrEmpty(known))
                candidates.Add(known);

            if (Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                var lastSlash = path.LastIndexOf('/');
                if (lastSlash > 0)
                    candidates.Add($"{host}{path[..lastSlash]}/health");
            }

            candidates.Add($"{host}/health");

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>取业务接口地址的 <c>{scheme}://{host}:{port}</c> 作为缓存键。</summary>
        private static string? GetHostKey(string apiUrl)
        {
            if (string.IsNullOrWhiteSpace(apiUrl)) return null;
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }
}
