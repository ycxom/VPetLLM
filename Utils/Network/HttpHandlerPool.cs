using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace VPetLLM.Utils.Network
{
    /// <summary>
    /// 按代理配置共享 <see cref="HttpClientHandler"/>，避免每次请求都新建一套连接池。
    ///
    /// 调用方拿到的仍然是独立的 <see cref="HttpClient"/> 实例（可以自由设置 Timeout、
    /// DefaultRequestHeaders，互不影响），但它以 disposeHandler:false 构造，
    /// 所以 using 结束时只回收这个很轻的壳子，底层连接池继续复用。
    /// </summary>
    internal static class HttpHandlerPool
    {
        /// <summary>handler 的轮换周期，用于跟进 DNS / 系统代理变化。</summary>
        private static readonly TimeSpan HandlerLifetime = TimeSpan.FromMinutes(10);

        /// <summary>退休 handler 的宽限期，等在途请求结束后再真正释放。</summary>
        private static readonly TimeSpan DisposeGrace = TimeSpan.FromMinutes(2);

        private sealed class Entry
        {
            public required HttpClientHandler Handler { get; init; }
            public required DateTime CreatedUtc { get; init; }
        }

        private static readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<(HttpClientHandler Handler, DateTime RetiredUtc)> _retired = new();
        private static readonly object _rotateLock = new();

        /// <summary>
        /// 用共享 handler 创建一个 HttpClient。
        /// </summary>
        /// <param name="handlerFactory">
        /// 现有的 handler 构造逻辑。可能被调用但结果被丢弃（此时池里已有等效 handler），
        /// 所以它必须是纯粹的构造、不带副作用。
        /// </param>
        /// <param name="timeout">该 HttpClient 的超时时间，不影响其它调用方。</param>
        public static HttpClient CreateClient(Func<HttpClientHandler> handlerFactory, TimeSpan timeout)
        {
            return new HttpClient(GetHandler(handlerFactory), disposeHandler: false)
            {
                Timeout = timeout
            };
        }

        /// <summary>
        /// 取得与 <paramref name="handlerFactory"/> 产物等效的共享 handler。
        /// </summary>
        public static HttpClientHandler GetHandler(Func<HttpClientHandler> handlerFactory)
        {
            // 先按工厂的产物算出 key。未发起请求的 handler 不持有连接，
            // 这里造出来又丢掉的代价可以忽略，换来的是不必在池里重复一遍代理判断逻辑。
            var candidate = handlerFactory();
            var key = BuildKey(candidate);
            var now = DateTime.UtcNow;

            if (_entries.TryGetValue(key, out var existing) && now - existing.CreatedUtc < HandlerLifetime)
            {
                candidate.Dispose();
                DrainRetired(now);
                return existing.Handler;
            }

            lock (_rotateLock)
            {
                // 双重检查：可能已被其它线程轮换过。
                if (_entries.TryGetValue(key, out var current) && now - current.CreatedUtc < HandlerLifetime)
                {
                    candidate.Dispose();
                    return current.Handler;
                }

                if (current is not null)
                {
                    // 过期的 handler 不能立刻 Dispose，在途请求还挂在上面。
                    _retired.Enqueue((current.Handler, now));
                }

                _entries[key] = new Entry { Handler = candidate, CreatedUtc = now };
            }

            DrainRetired(now);
            return candidate;
        }

        /// <summary>
        /// 代理配置变更后调用，让后续请求换用新 handler。
        /// </summary>
        public static void InvalidateAll()
        {
            var now = DateTime.UtcNow;
            lock (_rotateLock)
            {
                foreach (var key in _entries.Keys.ToList())
                {
                    if (_entries.TryRemove(key, out var entry))
                    {
                        _retired.Enqueue((entry.Handler, now));
                    }
                }
            }
        }

        /// <summary>
        /// 把 key 建立在"实际生效的代理"上，配置等价的 handler 就能共用一套连接池。
        /// </summary>
        private static string BuildKey(HttpClientHandler handler)
        {
            if (!handler.UseProxy || handler.Proxy is null)
            {
                return "noproxy";
            }

            // WebProxy 能读出地址；系统代理读不到，按类型归为一类，靠 HandlerLifetime 轮换跟进变化。
            return handler.Proxy is WebProxy web && web.Address is not null
                ? "proxy:" + web.Address.AbsoluteUri
                : "sysproxy:" + handler.Proxy.GetType().FullName;
        }

        /// <summary>
        /// 释放已过宽限期的退休 handler。
        /// </summary>
        private static void DrainRetired(DateTime now)
        {
            while (_retired.TryPeek(out var item) && now - item.RetiredUtc > DisposeGrace)
            {
                if (!_retired.TryDequeue(out item))
                {
                    break;
                }

                try
                {
                    item.Handler.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"HttpHandlerPool: 释放过期 handler 失败: {ex.Message}");
                }
            }
        }
    }
}
