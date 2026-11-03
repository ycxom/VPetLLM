using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;

namespace VPetLLM.Core.Services
{
    public enum OllamaFailure
    {
        VersionLookup,
        Download,
        DiskSpace,
        Signature,
        Setup,
        NotInstalled,
        ServerStart,
        Api
    }

    /// <summary>可以告诉用户怎么办的失败。界面按 <see cref="Failure"/> 选文案。</summary>
    public sealed class OllamaOperationException : Exception
    {
        public OllamaOperationException(OllamaFailure failure, string? detail = null, Exception? inner = null)
            : base(detail ?? failure.ToString(), inner)
        {
            Failure = failure;
        }

        public OllamaFailure Failure { get; }

        /// <summary>仅 <see cref="OllamaFailure.DiskSpace"/>：需要的字节数。</summary>
        public long Required { get; init; }

        /// <summary>仅 <see cref="OllamaFailure.DiskSpace"/>：现有的字节数。</summary>
        public long Available { get; init; }
    }

    /// <summary>一条下载线路：改写后的地址和这条线路要用的连接。</summary>
    public sealed record DownloadRoute(string Url, Func<HttpMessageHandler> CreateHandler);

    public sealed record OllamaInstallation(string Directory, string? Version)
    {
        public string ServerPath => Path.Combine(Directory, "ollama.exe");
    }

    /// <summary>
    /// 安装 Ollama 这台电脑上的部分：找到已有安装、查最新版、下载、验签、静默安装、启动服务。
    /// 安装包有 1.7 GB，下载全程落盘，换线路和取消都不丢进度。
    /// </summary>
    public class OllamaInstaller
    {
        private const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{44E83376-CE68-45EB-8FC1-393500EB558C}_is1";

        private const string LatestReleasePage = "https://github.com/ollama/ollama/releases/latest";

        // 只认正式版：rc 标签后面跟着 -rc1，不能当成 0.41.0
        private static readonly Regex ReleaseVersion = new(
            @"(?:/download/|/tag/|""tag_name""\s*:\s*"")v(\d+\.\d+\.\d+)(?![-.\w])", RegexOptions.Compiled);

        private static readonly Regex VersionText = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

        // 安装包本身加解压后的程序文件，放在同一个盘上的常见情况
        private const long RequiredInstallSpace = 8L * 1024 * 1024 * 1024;

        private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

        private static readonly string[] ProxySchemes = { "http", "https", "socks4", "socks4a", "socks5" };

        private readonly Func<string, Task<IReadOnlyList<DownloadRoute>>> _routes;
        private readonly Func<HttpMessageHandler> _directHandler;
        private readonly Func<Uri, HttpMessageHandler> _proxyHandler;
        private readonly string _cacheDirectory;

        private Uri? _downloadProxy;
        private CancellationTokenSource? _attempt;

        public OllamaInstaller(
            Func<string, Task<IReadOnlyList<DownloadRoute>>> routes,
            Func<HttpMessageHandler>? directHandler = null,
            string? cacheDirectory = null,
            Func<Uri, HttpMessageHandler>? proxyHandler = null)
        {
            _routes = routes;
            _directHandler = directHandler ?? (() => new SocketsHttpHandler());
            _proxyHandler = proxyHandler ?? (proxy => new SocketsHttpHandler { Proxy = new WebProxy(proxy), UseProxy = true });
            // 放临时目录：卸载 VPetLLM 不留残余，也不放进会被宿主清理的 VPet\cache
            _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "VPetLLM", "ollama");
        }

        /// <summary>用户指定的下载代理，null 表示不用。</summary>
        public Uri? DownloadProxy => Volatile.Read(ref _downloadProxy);

        /// <summary>
        /// 改用（或停用）下载代理。正在下的那条线路立刻断开，从 .part 已有的位置经代理接着下，
        /// 这次断开不算线路失败。代理只是多一条去 GitHub 原地址的线路，安装前照样验签。
        /// </summary>
        public void UseDownloadProxy(Uri? proxy)
        {
            Interlocked.Exchange(ref _downloadProxy, proxy);
            try
            {
                Volatile.Read(ref _attempt)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 这条线路刚好结束，下一条会读到新代理
            }
        }

        /// <summary>解析用户填的代理地址，没写协议当 http。填得不对返回 null。</summary>
        public static Uri? ParseProxy(string? text)
        {
            var value = text?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
            if (!ProxySchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) return null;
            return string.IsNullOrEmpty(uri.Host) ? null : uri;
        }

        /// <summary>连接和收到响应头的上限。</summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

        /// <summary>这么久一个字节都没收到就换线路。</summary>
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>每个窗口期至少要收到 <see cref="MinWindowBytes"/>，否则换线路：1.7 GB 不能靠一条龟速线路硬等。</summary>
        public TimeSpan SlowWindow { get; init; } = TimeSpan.FromSeconds(30);

        public long MinWindowBytes { get; init; } = 3 * 1024 * 1024;

        /// <summary>安装程序写下的卸载信息最准，其次默认位置，最后是 PATH（手动解压版）。</summary>
        public virtual OllamaInstallation? Detect()
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is not string directory) continue;

                var installation = new OllamaInstallation(directory.TrimEnd('\\'), key.GetValue("DisplayVersion") as string);
                if (File.Exists(installation.ServerPath)) return installation;
            }

            var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama") }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return candidates
                .Where(directory => File.Exists(Path.Combine(directory, "ollama.exe")))
                .Select(directory => new OllamaInstallation(directory.TrimEnd('\\'), null))
                .FirstOrDefault();
        }

        /// <summary>
        /// 查最新版本号，查不到返回 null。版本号只用来拼下载地址和提示升级，
        /// 假的版本号最多让人下到另一个官方签名的版本，下不到能运行的东西，所以经代理查也无妨。
        /// </summary>
        public virtual async Task<string?> GetLatestVersionAsync(CancellationToken token)
        {
            var direct = new[]
            {
                "https://ollama.com/api/update?os=windows&arch=amd64&version=0.0.0&ts=1",
                "https://api.github.com/repos/ollama/ollama/releases/latest"
            };
            var sources = new List<DownloadRoute>();
            if (DownloadProxy is { } proxy)
                sources.AddRange(direct.Select(url => new DownloadRoute(url, () => _proxyHandler(proxy))));
            sources.AddRange(direct.Select(url => new DownloadRoute(url, _directHandler)));
            sources.AddRange(await TryGetRoutesAsync(LatestReleasePage).ConfigureAwait(false));

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var version = await ReadVersionAsync(source, token).ConfigureAwait(false);
                    if (version is not null) return version;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    Logger.Log($"查询 Ollama 最新版本失败: {ex.Message}");
                }
            }
            return null;
        }

        private async Task<IReadOnlyList<DownloadRoute>> TryGetRoutesAsync(string url)
        {
            try
            {
                return await _routes(url).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                Logger.Log($"获取下载线路失败，改用直连: {ex.Message}");
                return new[] { new DownloadRoute(url, _directHandler) };
            }
        }

        private async Task<string?> ReadVersionAsync(DownloadRoute source, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(LookupTimeout);

            using var client = CreateClient(source);
            using var response = await client.GetAsync(source.Url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            // 跳转后的地址里就有版本号，代理不透传跳转时才需要翻页面
            var fromUrl = ReleaseVersion.Match(response.RequestMessage?.RequestUri?.ToString() ?? "");
            if (fromUrl.Success) return fromUrl.Groups[1].Value;

            var fromBody = ReleaseVersion.Match(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return fromBody.Success ? fromBody.Groups[1].Value : null;
        }

        private static HttpClient CreateClient(DownloadRoute route)
        {
            var client = new HttpClient(route.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VPetLLM");
            return client;
        }

        /// <summary>
        /// 下载指定版本的安装包并返回路径。这里不验签，验签在 <see cref="InstallAsync"/> 里贴着运行做。
        /// 线路逐条试，进度留在 .part 文件里，一整轮都没有新增才算失败。
        /// </summary>
        public virtual async Task<string> DownloadAsync(string version, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            // 版本号来自网络，下面要拼进路径和地址
            if (!VersionText.IsMatch(version))
                throw new OllamaOperationException(OllamaFailure.VersionLookup, version);

            Directory.CreateDirectory(_cacheDirectory);
            var target = Path.Combine(_cacheDirectory, $"OllamaSetup-{version}.exe");
            var part = target + ".part";
            DeleteOtherVersions(target, part);
            if (File.Exists(target)) return target;

            EnsureFreeSpace(LengthOf(part));

            var url =$"https://github.com/ollama/ollama/releases/download/v{version}/OllamaSetup.exe";
            var routes = await TryGetRoutesAsync(url).ConfigureAwait(false);

            Exception? lastError = null;
            var restartAllowed = false;
            while (true)
            {
                var before = LengthOf(part);
                var rangeIgnored = false;
                var proxy = DownloadProxy;
                var round = proxy is null ? routes : routes.Prepend(new DownloadRoute(url, () => _proxyHandler(proxy))).ToList();

                foreach (var route in round)
                {
                    token.ThrowIfCancellationRequested();
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    Interlocked.Exchange(ref _attempt, attempt);
                    try
                    {
                        // 先登记再看：代理恰好在登记前换掉时，这里能看到
                        if (!ReferenceEquals(DownloadProxy, proxy)) break;
                        await DownloadFromRouteAsync(route, part, restartAllowed, progress, attempt.Token).ConfigureAwait(false);
                        File.Move(part, target, overwrite: true);
                        return target;
                    }
                    catch (RangeIgnoredException)
                    {
                        rangeIgnored = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                    {
                        // 用户换了代理才断的，不算这条线路的错
                        if (!ReferenceEquals(DownloadProxy, proxy)) break;
                        lastError = ex;
                        Logger.Log($"Ollama 安装包下载中断，换线路: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _attempt, null, attempt);
                    }
                }

                if (!ReferenceEquals(DownloadProxy, proxy))
                {
                    Logger.Log(DownloadProxy is null ? "Ollama 安装包停用代理，接着下载" : "Ollama 安装包改用代理，接着下载");
                    continue;
                }

                // 变长了是有进展，变没了是坏文件被丢弃，都值得再来一轮
                var length = LengthOf(part);
                if (length > before || (length == 0 && before > 0)) continue;

                // 所有线路都不支持续传时只能重头来，放到最后才走这步，别白白丢掉已下载的部分
                if (rangeIgnored && !restartAllowed)
                {
                    restartAllowed = true;
                    continue;
                }

                throw new OllamaOperationException(OllamaFailure.Download, lastError?.Message, lastError);
            }
        }

        private async Task DownloadFromRouteAsync(
            DownloadRoute route, string part, bool restartAllowed, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            var offset = LengthOf(part);

            using var client = CreateClient(route);
            using var request = new HttpRequestMessage(HttpMethod.Get, route.Url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
            watchdog.CancelAfter(ConnectTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 上次下完没来得及改名时，.part 正好是完整文件
                if (response.Content.Headers.ContentRange?.Length == offset) return;
                File.Delete(part);
                throw new IOException("range not satisfiable");
            }
            response.EnsureSuccessStatusCode();

            long total;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset) throw new IOException($"unexpected range {range}");
                total = range.Length ?? -1;
            }
            else
            {
                if (offset > 0 && !restartAllowed) throw new RangeIgnoredException();
                offset = 0;
                total = response.Content.Headers.ContentLength ?? -1;
            }

            using var body = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
            using var file = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            var buffer = new byte[256 * 1024];
            var received = offset;
            var windowStart = Stopwatch.StartNew();
            var windowBytes = 0L;
            var lastReport = Stopwatch.StartNew();

            while (true)
            {
                watchdog.CancelAfter(StallTimeout);
                var count = await body.ReadAsync(buffer, watchdog.Token).ConfigureAwait(false);
                if (count == 0) break;

                // 代理出错时常回 200 加一段网页，尽早发现，别当成安装包写满磁盘
                if (received == 0 && !(count >= 2 && buffer[0] == 'M' && buffer[1] == 'Z'))
                    throw new IOException("response is not an executable");

                await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                received += count;
                windowBytes += count;

                if (lastReport.Elapsed >= ProgressInterval)
                {
                    lastReport.Restart();
                    progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.DownloadingSetup, received, total));
                }

                if (windowStart.Elapsed >= SlowWindow)
                {
                    if (windowBytes < MinWindowBytes) throw new IOException("route too slow");
                    windowStart.Restart();
                    windowBytes = 0;
                }
            }

            progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.DownloadingSetup, received, total));
            if (total >= 0 && received != total)
                throw new IOException($"connection closed at {received}/{total}");
        }

        private void DeleteOtherVersions(string target, string part)
        {
            foreach (var file in Directory.GetFiles(_cacheDirectory, "OllamaSetup-*"))
            {
                if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file, part, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }

        private void EnsureFreeSpace(long alreadyDownloaded)
        {
            var required = RequiredInstallSpace - alreadyDownloaded;
            var available = GetFreeSpace(_cacheDirectory);
            if (available < required)
            {
                throw new OllamaOperationException(OllamaFailure.DiskSpace)
                {
                    Required = required,
                    Available = available
                };
            }
        }

        /// <summary>路径所在磁盘的可用空间。网络路径查不出来，当作够用，真不够时由下载或安装报错。</summary>
        public virtual long GetFreeSpace(string path)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Logger.Log($"查询磁盘空间失败: {ex.Message}");
                return long.MaxValue;
            }
        }

        /// <summary>
        /// 验签并运行安装包。验签前就占住文件、一直占到安装结束，验过的和运行的才一定是同一份。
        /// 验签失败的文件立刻删掉，没有任何放行的路径。
        /// </summary>
        public virtual async Task InstallAsync(string setupPath, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            int exitCode;
            string? rejected;
            using (var guard = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.VerifyingSetup));
                rejected = await Task.Run(() => AuthenticodeVerifier.CheckOllamaPublisher(setupPath, guard.SafeFileHandle), token).ConfigureAwait(false);

                exitCode = 0;
                if (rejected is null)
                {
                    progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.InstallingSetup));
                    exitCode = await RunSetupAsync(setupPath, token).ConfigureAwait(false);
                }
            }

            if (rejected is not null)
            {
                Logger.Log($"Ollama 安装包验签失败: {rejected}");
                TryDelete(setupPath);
                throw new OllamaOperationException(OllamaFailure.Signature, rejected);
            }

            // 失败时留着安装包：多半是环境问题，重试不用再下 1.7 GB。下次下载新版本时会清掉
            if (exitCode != 0)
                throw new OllamaOperationException(OllamaFailure.Setup, exitCode.ToString());

            TryDelete(setupPath);
        }

        /// <summary>
        /// 官方安装包按用户安装，不需要管理员权限。升级时它会自己结束正在运行的 Ollama，
        /// 装完也会自己拉起托盘程序。
        /// </summary>
        protected virtual async Task<int> RunSetupAsync(string setupPath, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SetupTimeout);

            using var process = Process.Start(new ProcessStartInfo(setupPath, "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new OllamaOperationException(OllamaFailure.Setup, "process not started");

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new OllamaOperationException(OllamaFailure.Setup, "timeout");
            }
            return process.ExitCode;
        }

        /// <summary>
        /// 后台启动服务。用 serve 而不是托盘程序：没有窗口打扰，也能让服务听在设置填的端口上。
        /// 不等它，也不随桌宠退出，下次打开桌宠时还在就直接用。
        /// </summary>
        public virtual void StartServer(OllamaInstallation installation, Uri endpoint)
            => Process.Start(CreateServerStartInfo(installation, endpoint))?.Dispose();

        public static ProcessStartInfo CreateServerStartInfo(OllamaInstallation installation, Uri endpoint)
        {
            var info = new ProcessStartInfo(installation.ServerPath, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installation.Directory
            };
            info.Environment["OLLAMA_HOST"] = $"{endpoint.Host}:{endpoint.Port}";
            // 和下载前查磁盘空间看的是同一个目录
            info.Environment["OLLAMA_MODELS"] = OllamaManagerService.ModelsDirectory;
            return info;
        }

        private static long LengthOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Log($"删除 Ollama 安装包失败: {ex.Message}");
            }
        }

        private sealed class RangeIgnoredException : Exception
        {
        }
    }
}
