using System.Diagnostics;
using System.Net.Http;
using System.Threading;

namespace VPetLLM.Core.Services
{
    public sealed record OllamaStatus(bool IsLocal, OllamaInstallation? Installation, string? RunningVersion)
    {
        public bool IsRunning => RunningVersion is not null;

        /// <summary>运行中的版本比卸载信息准：Ollama 自己的后台更新不一定及时改注册表。</summary>
        public string? Version => RunningVersion ?? Installation?.Version;

        public bool IsUpgradeAvailable(string? latest)
            => System.Version.TryParse(latest, out var newest)
               && System.Version.TryParse(Version, out var current)
               && newest > current;
    }

    /// <summary>
    /// 一键化本地部署的全部操作，窗口只管显示。设置地址指向本机才能装和启动，
    /// 指向别的电脑时只能管那边的模型。这里不碰设置，选定的模型由窗口回传给打开它的面板。
    /// </summary>
    public sealed class OllamaManagerService
    {
        public const string DefaultUrl = "http://localhost:11434";

        // 模型解压合并时临时文件和成品同时存在
        private const double PullSpaceFactor = 1.1;

        private const int MaxReconnectLeftovers = 5;

        private readonly OllamaInstaller _installer;
        private readonly OllamaClient _client;
        private CancellationTokenSource? _pullAttempt;

        public OllamaManagerService(string url, OllamaInstaller installer, HttpMessageHandler? apiHandler = null)
        {
            Url = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim();
            BaseUri = new Uri(Url);
            _installer = installer;
            _client = new OllamaClient(BaseUri, apiHandler);
        }

        public static OllamaManagerService Create(Setting settings, string url)
            => new(url, new OllamaInstaller(routeUrl => PluginStoreService.ResolveFileRoutesAsync(settings.PluginStore, routeUrl)));

        public string Url { get; }

        public Uri BaseUri { get; }

        public bool IsLocal => BaseUri.IsLoopback;

        /// <summary>等服务启动起来的上限。</summary>
        public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>安装程序收尾时会自己拉起服务，先等这么久，免得和它抢端口。</summary>
        public TimeSpan PostInstallGrace { get; init; } = TimeSpan.FromSeconds(8);

        /// <summary>模型存放目录。OLLAMA_MODELS 可能只写在用户环境变量里，桌宠进程启动时没带上。</summary>
        public static string ModelsDirectory =>
            Environment.GetEnvironmentVariable("OLLAMA_MODELS")
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

        public async Task<OllamaStatus> GetStatusAsync(CancellationToken token)
        {
            var version = await _client.GetVersionAsync(token).ConfigureAwait(false);
            return new OllamaStatus(IsLocal, IsLocal ? _installer.Detect() : null, version);
        }

        public Task<string?> GetLatestVersionAsync(CancellationToken token) => _installer.GetLatestVersionAsync(token);

        public Task<IReadOnlyList<OllamaLocalModel>> ListModelsAsync(CancellationToken token) => _client.ListModelsAsync(token);

        public Task DeleteModelAsync(string model, CancellationToken token) => _client.DeleteAsync(model, token);

        public long GetModelsFreeSpace() => _installer.GetFreeSpace(ModelsDirectory);

        public async Task PullModelAsync(string model, long expectedSize, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            // 模型存在对方电脑上时本机的磁盘说明不了什么
            if (IsLocal)
            {
                var required = (long)(expectedSize * PullSpaceFactor);
                var available = _installer.GetFreeSpace(ModelsDirectory);
                if (available < required)
                {
                    throw new OllamaOperationException(OllamaFailure.DiskSpace)
                    {
                        Required = required,
                        Available = available
                    };
                }
            }

            var leftovers = 0;
            var reconnected = false;
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                Interlocked.Exchange(ref _pullAttempt, attempt);
                try
                {
                    await _client.PullAsync(model, expectedSize, progress, attempt.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // 只有 ReconnectPull 会单独取消这一次：Ollama 留着已下的部分，重新请求会重新建立连接
                    Logger.Log($"Ollama 模型 {model} 断开重连");
                    reconnected = true;
                    leftovers = 0;
                }
                catch (OllamaOperationException ex) when (reconnected && ex.Failure == OllamaFailure.Api
                                                          && ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                                                          && ++leftovers <= MaxReconnectLeftovers)
                {
                    // 刚断开时上一轮的下载还没收完，新请求挂上去会跟着报 context canceled，等一下再来
                    Logger.Log($"Ollama 模型 {model} 上一轮下载尚未结束，稍后重试");
                }
                finally
                {
                    Interlocked.CompareExchange(ref _pullAttempt, null, attempt);
                }

                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 断开正在下的模型再接着下。Ollama 单条连接变慢后不会自己换，重新请求能换一批连接，已下的部分不丢。
        /// 没在下载时什么也不做。
        /// </summary>
        public void ReconnectPull()
        {
            try
            {
                Volatile.Read(ref _pullAttempt)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 这次下载刚好结束
            }
        }

        /// <summary>断开后等多久再重新请求，给 Ollama 收拾上一轮的时间。</summary>
        public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>安装包下载代理，只管安装包：模型由 Ollama 自己下载，代理要设在它的环境变量里。</summary>
        public Uri? DownloadProxy => _installer.DownloadProxy;

        public void UseDownloadProxy(Uri? proxy) => _installer.UseDownloadProxy(proxy);

        /// <summary>下载并安装最新版。也用于升级：官方安装包会沿用原来的安装目录。</summary>
        public async Task InstallLatestAsync(IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            var version = await _installer.GetLatestVersionAsync(token).ConfigureAwait(false)
                          ?? throw new OllamaOperationException(OllamaFailure.VersionLookup);
            var setup = await _installer.DownloadAsync(version, progress, token).ConfigureAwait(false);
            await _installer.InstallAsync(setup, progress, token).ConfigureAwait(false);
            await WaitForServerAsync(PostInstallGrace, token).ConfigureAwait(false);
        }

        /// <summary>确保服务在运行，启动不起来时抛异常。</summary>
        public async Task EnsureRunningAsync(IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            if (await _client.GetVersionAsync(token).ConfigureAwait(false) is not null) return;

            if (!IsLocal)
                throw new OllamaOperationException(OllamaFailure.ServerStart, Url);

            var installation = _installer.Detect()
                               ?? throw new OllamaOperationException(OllamaFailure.NotInstalled);

            progress?.Report(new OllamaTaskProgress(OllamaTaskPhase.StartingServer));
            try
            {
                _installer.StartServer(installation, BaseUri);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new OllamaOperationException(OllamaFailure.ServerStart, ex.Message, ex);
            }

            if (!await WaitForServerAsync(StartTimeout, token).ConfigureAwait(false))
                throw new OllamaOperationException(OllamaFailure.ServerStart, "timeout");
        }

        /// <summary>
        /// 一键部署，返回可以使用的模型名：缺什么补什么，已经齐全的步骤都跳过。
        /// 已经有聊天模型时不再下载推荐模型，用户可能是特意装的。这里不主动升级，
        /// 升级会打断正在运行的服务，要用户自己点。
        /// </summary>
        public async Task<string> DeployAsync(OllamaModelInfo recommended, IProgress<OllamaTaskProgress>? progress, CancellationToken token)
        {
            var status = await GetStatusAsync(token).ConfigureAwait(false);
            if (!status.IsRunning && status.IsLocal && status.Installation is null)
                await InstallLatestAsync(progress, token).ConfigureAwait(false);

            await EnsureRunningAsync(progress, token).ConfigureAwait(false);

            var models = await _client.ListModelsAsync(token).ConfigureAwait(false);
            var existing = models.FirstOrDefault(m => m.Name == recommended.Name) ?? models.FirstOrDefault(m => m.IsChatModel);
            if (existing is not null) return existing.Name;

            await PullModelAsync(recommended.Name, recommended.SizeBytes, progress, token).ConfigureAwait(false);
            return recommended.Name;
        }

        private async Task<bool> WaitForServerAsync(TimeSpan limit, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (await _client.GetVersionAsync(token).ConfigureAwait(false) is not null) return true;
                if (clock.Elapsed >= limit) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }
        }
    }
}
