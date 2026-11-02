using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using VPetLLM.Utils.Localization;
using VPetLLM.Utils.Plugin;
using NewPlugin = VPetLLM.Core.Abstractions.Interfaces.Plugin;
using PE = System.Reflection.PortableExecutable;
using Meta = System.Reflection.Metadata;

namespace VPetLLM.Core.Services
{
    public class UnifiedPluginItem
    {
        public bool IsLocal { get; set; }
        public bool IsFailed { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string OriginalName { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string OriginalDescription { get; set; }
        public bool IsEnabled { get; set; }
        public string ActionText { get; set; }
        public string Icon { get; set; }
        public string FileUrl { get; set; }
        public string SHA256 { get; set; }

        /// <summary>与 DLL 同目录分发的附属数据文件（各自独立校验哈希）。</summary>
        public List<PluginExtraFile> Extra { get; set; } = new();

        public NewPlugin.IVPetLLMPlugin LocalPlugin { get; set; }
        public FailedPlugin FailedPlugin { get; set; }
        public string Version { get; set; }
        public string RemoteVersion { get; set; }
        public bool IsUpdatable { get; set; }
        public string UpdateAvailableText { get; set; }
        public string UninstallActionText { get; set; }
        public string LocalFilePath { get; set; }
        public bool HasSettingAction { get; set; }
        public string StatusText { get; set; }
        public string SettingActionText { get; set; }

        /// <summary>没装的能装，有新版本或加载失败的能更新；已是最新的只能卸载。</summary>
        public bool CanInstallOrUpdate => !IsLocal || IsUpdatable;
        public bool CanUninstall => IsLocal;

        /// <summary>
        /// 没启用的插件没跑过 Initialize，配置还是空的，开不了设置。
        /// </summary>
        public bool CanRunSettingAction => HasSettingAction && LocalPlugin is { Enabled: true };

        /// <summary>
        /// 只有内嵌面板、没声明设置入口的插件（如 RemoteChat）也算有设置。
        /// 极客界面不用这条：那里有插件面板页，这类插件给按钮也没事可做。
        /// </summary>
        public bool CanOpenSettings => CanRunSettingAction || LocalPlugin is { Enabled: true } and IPluginTab;

        public string DisplayStatus => IsUpdatable ? UpdateAvailableText : StatusText;
    }

    /// <summary>
    /// 插件的附属数据文件。
    ///
    /// 有些插件的数据必须以明文文件而不是编进 IL 的形式分发——比如 TerminalPlugin
    /// 的命令安全规则表：那张黑名单一旦成为程序集里的字符串常量，杀软就会把它
    /// 当成勒索软件的载荷清单来打分。
    /// </summary>
    public class PluginExtraFile
    {
        public string Name { get; set; }
        public string FileUrl { get; set; }
        public string SHA256 { get; set; }
    }

    /// <summary>
    /// 插件商店的拉取、下载校验、安装和卸载。完整设置和简易界面共用这一份，
    /// 哈希校验这类安全逻辑不能各写一套。这里只做事、不刷列表，也不自己弹校验确认。
    /// </summary>
    public sealed class PluginStoreService
    {
        private const string CatalogUrl = "https://raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json";

        // 列表只有几 KB，连不上就该尽快告诉用户。下载插件文件不用这个超时，大文件慢是正常的。
        private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(20);

        // 完整设置和简易界面各有一个 PluginStoreService，测速结果要共用
        private static readonly GitHubProxyPool SharedProxyPool = new GitHubProxyPool(CatalogUrl);

        private readonly global::VPetLLM.VPetLLM _plugin;
        private readonly GitHubProxyPool _proxyPool;
        private readonly Func<HttpMessageHandler>? _handlerFactory;

        public PluginStoreService(
            global::VPetLLM.VPetLLM plugin,
            GitHubProxyPool? proxyPool = null,
            Func<HttpMessageHandler>? handlerFactory = null)
        {
            _plugin = plugin;
            _proxyPool = proxyPool ?? SharedProxyPool;
            _handlerFactory = handlerFactory;
        }

        private PluginStoreProxyDecision ResolveProxy()
        {
            var settings = _plugin.Settings.PluginStore;
            return ProxyRoutingPolicy.ResolvePluginStore(settings?.UseProxy == true, settings?.ProxyUrl);
        }

        private string BuildUrl(string originalUrl)
            => ProxyRoutingPolicy.BuildPluginStoreUrl(ResolveProxy(), originalUrl);

        private HttpClient CreateClient()
            => new HttpClient(_handlerFactory?.Invoke() ?? ProxyRoutingPolicy.CreatePluginStoreHandler(ResolveProxy()));

        /// <summary>
        /// 这次下载依次要试的线路。只有用镜像改写地址时才进代理池：关了代理就是不走代理，
        /// 填了本地 HTTP 代理说明用户有自己的线路，都不替他探测。
        /// </summary>
        private Task<IReadOnlyList<PluginStoreProxyDecision>> ResolveDownloadRoutesAsync()
            => ResolveRoutesAsync(ResolveProxy(), _proxyPool);

        private static async Task<IReadOnlyList<PluginStoreProxyDecision>> ResolveRoutesAsync(PluginStoreProxyDecision decision, GitHubProxyPool pool)
        {
            if (decision.Mode != PluginStoreProxyMode.UrlRewrite)
            {
                return new[] { decision };
            }

            var routes = await pool.GetDownloadRoutesAsync(decision.ProxyUrl).ConfigureAwait(false);
            return routes
                .Select(route => route == GitHubProxyPool.DirectRoute
                    ? new PluginStoreProxyDecision(PluginStoreProxyMode.Direct)
                    : new PluginStoreProxyDecision(PluginStoreProxyMode.UrlRewrite, route))
                .ToList();
        }

        /// <summary>
        /// 下载商店以外的 GitHub 文件（Ollama 安装包）要试的线路，和插件下载共用代理设置和测速结果。
        /// 这些文件都有自己的校验，线路本身不需要被信任。
        /// </summary>
        public static async Task<IReadOnlyList<DownloadRoute>> ResolveFileRoutesAsync(Setting.PluginStoreSetting? settings, string url)
        {
            var decision = ProxyRoutingPolicy.ResolvePluginStore(settings?.UseProxy == true, settings?.ProxyUrl);
            var routes = await ResolveRoutesAsync(decision, SharedProxyPool).ConfigureAwait(false);
            return routes
                .Select(route => new DownloadRoute(
                    ProxyRoutingPolicy.BuildPluginStoreUrl(route, url),
                    () => ProxyRoutingPolicy.CreatePluginStoreHandler(route)))
                .ToList();
        }

        /// <summary>
        /// 跑插件的设置入口，打不开时返回给用户看的说明，打开了返回 null。
        /// Function 大多是 async，失败落在 Task 里，必须等；多数插件还会自己 catch，
        /// 把失败写进返回值（本来是给 LLM 看的），所以返回值也要看。
        /// 吞掉后什么都不返回的（Sticker）这里看不出来。
        /// </summary>
        public async Task<string?> RunSettingActionAsync(UnifiedPluginItem item)
        {
            string reply;
            try
            {
                reply = await ((NewPlugin.IActionPlugin)item.LocalPlugin).Function("action(setting)");
            }
            catch (Exception ex)
            {
                Logger.Log($"打开插件设置失败 [{item.Name}]: {ex}");
                return DescribeSettingFailure(item.Name, ex.Message);
            }

            // 返回值没有约定格式，现有插件失败时只有这两种写法；成功的文本四种语言都不含它们
            if (reply is null || !(reply.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) || reply.Contains("失败")))
                return null;

            Logger.Log($"打开插件设置失败 [{item.Name}]: {reply}");
            return DescribeSettingFailure(item.Name, reply);
        }

        public string DescribeSettingFailure(string pluginName, string detail)
        {
            var langCode = _plugin.Settings.Language;

            // WPF 这条报错跟着系统语言走，中英文都要认
            var resourceMissing = detail.Contains("不具有由 URI") || detail.Contains("识别的资源")
                || detail.Contains("identified by the URI") || detail.Contains("Cannot locate resource");

            return resourceMissing
                ? string.Format(LanguageHelper.Get("Plugin.ResourceError", langCode, "{0} 的资源文件缺失或损坏，可以重新安装这个插件，或联系插件开发者。"), pluginName)
                : string.Format(LanguageHelper.Get("Plugin.SettingError", langCode, "{0} 的设置打不开：{1}"), pluginName, detail);
        }

        /// <summary>拉取商店清单。连不上时抛异常，由调用方决定怎么退回离线显示。</summary>
        public async Task<Dictionary<string, JObject>> FetchCatalogAsync()
        {
            var decision = ResolveProxy();
            if (decision.Mode == PluginStoreProxyMode.UrlRewrite)
            {
                return await _proxyPool.FetchCatalogAsync(decision.ProxyUrl).ConfigureAwait(false);
            }

            using var client = CreateClient();
            client.Timeout = CatalogTimeout;
            var json = await client.GetStringAsync(BuildUrl(CatalogUrl)).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<Dictionary<string, JObject>>(json);
        }

        /// <summary>
        /// 取已加载和加载失败的本地插件，不重新加载。
        /// 必须在 UI 线程调用：<see cref="PluginDescriptionProbe"/> 的缓存不是线程安全的。
        /// </summary>
        public Dictionary<string, UnifiedPluginItem> BuildLocalItems(string langCode)
        {
            var pluginItems = new Dictionary<string, UnifiedPluginItem>(StringComparer.OrdinalIgnoreCase);

            // 添加本地成功插件
            foreach (var localPlugin in _plugin.Plugins)
            {
                var sha = PluginManager.GetFileSha256(localPlugin.FilePath);
                var id = localPlugin.Name; // 先用本地名称作为ID
                var localDescription = PluginDescriptionProbe.Get(localPlugin, _plugin, langCode);

                var item = new UnifiedPluginItem
                {
                    IsLocal = true,
                    Id = id,
                    Name = localPlugin.Name,
                    OriginalName = localPlugin.Name,
                    Author = localPlugin.Author,
                    Description = localDescription,
                    OriginalDescription = localDescription,
                    IsEnabled = localPlugin.Enabled,
                    ActionText = LanguageHelper.Get("Plugin.UnloadPlugin", langCode),
                    Icon = "\uE8A5", // Folder icon
                    LocalPlugin = localPlugin,
                    Version = sha,
                    LocalFilePath = localPlugin.FilePath,
                    StatusText = localPlugin.Enabled ?
                        LanguageHelper.Get("Plugin.StatusRunning", langCode) ?? "运行中" :
                        LanguageHelper.Get("Plugin.StatusDisabled", langCode) ?? "已禁用",
                    UninstallActionText = LanguageHelper.Get("Plugin.Uninstall", langCode) ?? "卸载",
                    SettingActionText = LanguageHelper.Get("Plugin.Setting", langCode, "设置")
                };

                // 必须写全：这个命名空间里单写 IActionPlugin 会解析成 VPetLLM.Core 下的旧接口，新插件都不实现它
                if (localPlugin is NewPlugin.IActionPlugin && localPlugin.Parameters.Contains("setting"))
                {
                    item.HasSettingAction = true;
                }
                pluginItems[id] = item;
            }

            // 添加本地失败插件
            Logger.Log($"RefreshPluginList: 开始处理失败插件，数量: {_plugin.FailedPlugins.Count}");
            foreach (var failedPlugin in _plugin.FailedPlugins)
            {
                Logger.Log($"RefreshPluginList: 处理失败插件 - Name: {failedPlugin.Name}, FilePath: {failedPlugin.FilePath}");
                var id = failedPlugin.Name;

                // 尝试计算失败插件的 SHA256，用于版本比较
                string failedPluginSha = null;
                if (!string.IsNullOrEmpty(failedPlugin.FilePath) && File.Exists(failedPlugin.FilePath))
                {
                    failedPluginSha = PluginManager.GetFileSha256(failedPlugin.FilePath);
                    Logger.Log($"RefreshPluginList: 失败插件 {failedPlugin.Name} 的 SHA256: {failedPluginSha}");
                }

                pluginItems[id] = new UnifiedPluginItem
                {
                    IsFailed = true,
                    IsLocal = true,
                    Id = id,
                    Name = $"{failedPlugin.Name} ({LanguageHelper.Get("Plugin.Outdated", langCode)})",
                    OriginalName = failedPlugin.Name,
                    Author = failedPlugin.Author,
                    Description = $"{LanguageHelper.Get("Plugin.LoadFailed", langCode)}: {failedPlugin.Description}",
                    OriginalDescription = failedPlugin.Description,
                    IsEnabled = false,
                    ActionText = LanguageHelper.Get("QuickSetup.PluginNeedsUpdate", langCode, "需要更新"),
                    Icon = "\uE783", // Error icon
                    FailedPlugin = failedPlugin,
                    UninstallActionText = LanguageHelper.Get("Plugin.Uninstall", langCode) ?? "卸载",
                    Version = failedPluginSha ?? LanguageHelper.Get("Plugin.UnknownVersion", langCode),
                    LocalFilePath = failedPlugin.FilePath,
                    StatusText = LanguageHelper.Get("QuickSetup.PluginNeedsUpdate", langCode, "需要更新")
                };
            }

            return pluginItems;
        }

        /// <summary>把商店清单合并进本地列表，返回被跳过的未上线条目数。</summary>
        public static int MergeCatalog(Dictionary<string, UnifiedPluginItem> pluginItems, Dictionary<string, JObject> remotePlugins, string langCode)
        {
            var hiddenCount = 0;

            // 更新本地插件的远程信息
            foreach (var item in remotePlugins)
            {
                var id = item.Key;
                var remoteInfo = item.Value;
                var remoteSha256 = remoteInfo["SHA256"]?.ToString() ?? string.Empty;
                var pluginName = remoteInfo["Name"]?.ToString() ?? string.Empty;

                // Published: false 的条目是"占位不上线"——用来在插件列表里给还没做完的插件
                // 先占住 ID，但不出现在商店里。整条跳过：既不作为在线插件展示，
                // 也不给本地已装的同名插件推更新（半成品不该被推给已经装了的人）。
                // 没写这个字段一律按上线处理，老条目不受影响。
                if (!IsPluginPublished(remoteInfo, id))
                {
                    hiddenCount++;
                    System.Diagnostics.Debug.WriteLine($"[PluginStore] 跳过未上线插件: {pluginName} (ID: {id})");
                    continue;
                }

                // 查找对应的本地插件 - 支持多种匹配方式
                var localItem = pluginItems.Values.FirstOrDefault(p =>
                    p.OriginalName == pluginName ||
                    p.Name == pluginName ||
                    p.Id == id ||
                    (p.LocalPlugin is not null && p.LocalPlugin.Name == pluginName));

                // 如果通过名称没有找到，尝试通过文件路径匹配（处理插件名称变更的情况）
                if (localItem is null)
                {
                    var expectedFilePath = Path.Combine(PluginManager.PluginPath, $"{id}.dll");
                    localItem = pluginItems.Values.FirstOrDefault(p =>
                        p.LocalFilePath is not null &&
                        p.LocalFilePath.Equals(expectedFilePath, StringComparison.OrdinalIgnoreCase));

                    if (localItem is not null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PluginStore] 通过文件路径匹配到插件: {localItem.Name} -> {pluginName}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[PluginStore] 匹配远程插件 '{pluginName}' (ID: {id})");
                System.Diagnostics.Debug.WriteLine($"[PluginStore] 本地插件列表: {string.Join(", ", pluginItems.Values.Select(p => $"{p.Name}({p.OriginalName})"))}");

                if (localItem is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[PluginStore] 找到匹配的本地插件: {localItem.Name}");

                    // 更新本地插件的远程信息
                    localItem.RemoteVersion = remoteSha256;
                    localItem.FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty;
                    localItem.SHA256 = remoteSha256;
                    localItem.Extra = ParseExtraFiles(remoteInfo);

                    // 对于失败的插件，总是标记为可更新（因为它们无法正常加载）
                    bool isUpdatable = localItem.IsFailed ||
                                       (!string.IsNullOrEmpty(localItem.Version) &&
                                       !string.IsNullOrEmpty(remoteSha256) &&
                                       !localItem.Version.Equals(remoteSha256, StringComparison.OrdinalIgnoreCase));

                    System.Diagnostics.Debug.WriteLine($"[PluginStore] 版本比较 - 本地: '{localItem.Version}', 远程: '{remoteSha256}', 失败插件: {localItem.IsFailed}, 可更新: {isUpdatable}");

                    if (isUpdatable)
                    {
                        localItem.IsUpdatable = true;
                        localItem.UpdateAvailableText = LanguageHelper.Get("Plugin.UpdateAvailable", langCode);
                        localItem.ActionText = LanguageHelper.Get("Plugin.Update", langCode);
                        localItem.Icon = "\uE948"; // Sync icon
                    }
                    else
                    {
                        // 确保已更新的插件显示正确的状态
                        localItem.IsUpdatable = false;
                        localItem.UpdateAvailableText = null;
                        localItem.ActionText = LanguageHelper.Get("Plugin.UnloadPlugin", langCode);
                        localItem.Icon = "\uE8A5"; // Folder icon
                    }

                    // 更新描述
                    var des = remoteInfo["Description"]?.ToObject<Dictionary<string, string>>();
                    var remoteDescription = des is not null ? (des.TryGetValue(langCode, out var d) ? d : (des.TryGetValue("en", out var enD) ? enD : string.Empty)) : string.Empty;
                    if (!string.IsNullOrEmpty(remoteDescription))
                    {
                        var localSource = LanguageHelper.Get("Plugin.Source.Local", langCode);
                        localItem.Description = $"({localSource}) {remoteDescription}";
                    }
                }
                else
                {
                    // 添加纯在线插件
                    var des = remoteInfo["Description"]?.ToObject<Dictionary<string, string>>();
                    var description = des is not null ? (des.TryGetValue(langCode, out var d) ? d : (des.TryGetValue("en", out var enD) ? enD : string.Empty)) : string.Empty;

                    pluginItems[id] = new UnifiedPluginItem
                    {
                        IsLocal = false,
                        Id = id,
                        Name = pluginName,
                        Author = remoteInfo["Author"]?.ToString() ?? string.Empty,
                        Description = $"({LanguageHelper.Get("Plugin.Source.Cloud", langCode)}) {description}",
                        OriginalDescription = description,
                        IsEnabled = false,
                        ActionText = LanguageHelper.Get("PluginStore.Install", langCode),
                        Icon = "\uE753", // Cloud download icon
                        FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty,
                        SHA256 = remoteSha256,
                        Extra = ParseExtraFiles(remoteInfo),
                        RemoteVersion = remoteSha256,
                        StatusText = LanguageHelper.Get("Plugin.StatusNotDownloaded", langCode) ?? "未下载"
                    };
                }
            }

            return hiddenCount;
        }

        /// <summary>
        /// 判断插件商店条目是否已上线。<c>Published: false</c> 表示"占位不上线"：
        /// 条目留在 PluginList.json 里占住 ID，但不出现在商店中。
        ///
        /// 没写这个字段的一律算上线——现存条目都没写，不能因为加了这个功能就集体消失。
        /// 写了但不是布尔值（比如手写成字符串 "false"）会记一条日志：这种笔误的后果是
        /// 半成品插件照常上线，比误藏一个成品严重得多，不能让它悄无声息地过去。
        /// </summary>
        private static bool IsPluginPublished(JObject remoteInfo, string id)
        {
            var token = remoteInfo["Published"];
            if (token is null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (bool.TryParse(token.ToString(), out var parsed))
            {
                Logger.Log($"插件商店: 条目 {id} 的 Published 写成了字符串 \"{token}\"，" +
                           $"已按 {parsed} 处理；请改成不带引号的 true/false");
                return parsed;
            }

            Logger.Log($"插件商店: 条目 {id} 的 Published 值 \"{token}\" 无法识别为布尔值，" +
                       $"按已上线处理；如果本意是不上线，请写 \"Published\": false");
            return true;
        }

        /// <summary>
        /// 卸载插件。只做事、不刷列表，刷新和提示留给调用方。
        /// </summary>
        public async Task<PluginOperationResult> UninstallAsync(UnifiedPluginItem plugin)
        {
            // 直接执行卸载，不再显示确认弹窗
            bool uninstalled = false;
            var pluginNameToFind = plugin.OriginalName ?? plugin.Name;
            var localPlugin = _plugin.Plugins.FirstOrDefault(p => p.FilePath == plugin.LocalFilePath);
            var failedPlugin = _plugin.FailedPlugins.FirstOrDefault(p => p.Name == pluginNameToFind);

            // 尝试1: 通过Plugin实例卸载
            if (localPlugin is not null)
            {
                uninstalled = await _plugin.UnloadAndTryDeletePlugin(localPlugin);
            }
            // 尝试2: 通过FailedPlugin卸载
            else if (failedPlugin is not null && !string.IsNullOrEmpty(failedPlugin.FilePath))
            {
                uninstalled = await _plugin.DeletePluginFile(failedPlugin.FilePath);
            }

            // 尝试3: 备用文件定位删除 - 如果前面的方法都失败，尝试直接通过文件路径删除
            if (!uninstalled && !string.IsNullOrEmpty(plugin.LocalFilePath))
            {
                string validFilePath = plugin.LocalFilePath;

                // 检查是否是目录路径
                if (Directory.Exists(validFilePath))
                {
                    Logger.Log($"Fallback: LocalFilePath is a directory, will use DeletePluginByName instead: {validFilePath}");
                    // 如果是目录路径，不要随便删除文件，而是跳到方法4使用插件名称定位
                    validFilePath = null;
                }

                if (!string.IsNullOrEmpty(validFilePath) && File.Exists(validFilePath))
                {
                    Logger.Log($"Fallback: Attempting to delete plugin file directly: {validFilePath}");
                    uninstalled = await _plugin.DeletePluginFile(validFilePath);
                }
            }

            // 尝试4: 最后的备用方案 - 通过插件名称在插件目录中查找并删除
            if (!uninstalled)
            {
                Logger.Log($"Fallback: Attempting to locate and delete plugin by name: {pluginNameToFind}");
                uninstalled = await _plugin.DeletePluginByName(pluginNameToFind);
            }

            // 如果卸载成功，立即从内存中移除插件引用
            if (uninstalled)
            {
                // 从失败插件列表中移除
                if (failedPlugin is not null)
                {
                    _plugin.FailedPlugins.Remove(failedPlugin);
                    Logger.Log($"Removed failed plugin from memory: {pluginNameToFind}");
                }

                // 从已加载插件列表中移除（如果还在的话）
                if (localPlugin is not null && _plugin.Plugins.Contains(localPlugin))
                {
                    _plugin.Plugins.Remove(localPlugin);
                    Logger.Log($"Removed loaded plugin from memory: {pluginNameToFind}");
                }

                DeleteExtraFiles(plugin);
            }

            if (!uninstalled)
            {
                string fileName = !string.IsNullOrEmpty(plugin.LocalFilePath)
                    ? Path.GetFileName(plugin.LocalFilePath)
                    : pluginNameToFind;
                return PluginOperationResult.Fail(
                    ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {fileName}"));
            }

            return PluginOperationResult.Ok();
        }

        /// <summary>解析插件清单里的 Extra 数组（附属数据文件）。</summary>
        private static List<PluginExtraFile> ParseExtraFiles(JToken remoteInfo)
        {
            var list = new List<PluginExtraFile>();
            if (remoteInfo?["Extra"] is not JArray array) return list;

            foreach (var item in array.OfType<JObject>())
            {
                var name = item["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new PluginExtraFile
                {
                    Name = name,
                    FileUrl = item["File"]?.ToString() ?? string.Empty,
                    SHA256 = item["SHA256"]?.ToString() ?? string.Empty
                });
            }
            return list;
        }

        /// <summary>
        /// 下载插件声明的附属数据文件，逐个校验哈希后写进插件目录。
        ///
        /// 任何一个文件所有线路都下载或校验失败，就判整次安装失败。附属文件里装的是安全规则
        /// 这类东西，带着一份来路不明的版本上线，比干脆装不上危险得多——所以这里不做"尽力而为"。
        /// </summary>
        /// <returns>成功返回 null；失败返回给用户看的错误信息。</returns>
        private async Task<string> DownloadExtraFilesAsync(
            HttpClient client, UnifiedPluginItem plugin, string targetDir, IReadOnlyList<PluginStoreProxyDecision> routes)
        {
            if (plugin.Extra is null || plugin.Extra.Count == 0) return null;
            if (string.IsNullOrEmpty(targetDir)) return "无法确定插件目录，附属文件安装失败";

            foreach (var extra in plugin.Extra)
            {
                if (string.IsNullOrWhiteSpace(extra.Name) || string.IsNullOrWhiteSpace(extra.FileUrl))
                {
                    return $"附属文件条目不完整: {extra.Name}";
                }

                // 清单是远端内容，Name 里塞路径分隔符就能把文件写到插件目录之外
                var safeName = Path.GetFileName(extra.Name);
                if (string.IsNullOrEmpty(safeName) || safeName != extra.Name)
                {
                    return $"附属文件名非法: {extra.Name}";
                }

                // 没有哈希就等于没有校验，一律拒绝——附属文件的门槛不能比 DLL 松
                var expected = extra.SHA256?.ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(expected))
                {
                    return $"附属文件缺少 SHA256，拒绝安装: {safeName}";
                }

                string failure = null;
                foreach (var route in routes)
                {
                    try
                    {
                        var url = ProxyRoutingPolicy.BuildPluginStoreUrl(route, extra.FileUrl);
                        Logger.Log($"Downloading extra file: {url}");
                        var bytes = await client.GetByteArrayAsync(url);

                        string actual;
                        using (var sha256 = SHA256.Create())
                        {
                            actual = BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        }

                        if (actual != expected)
                        {
                            Logger.Log($"Extra file hash mismatch for {safeName}: expected {expected}, got {actual}");
                            failure = $"附属文件校验失败: {safeName}（期望 {expected}，实际 {actual}）";
                            continue;
                        }

                        var destination = Path.Combine(targetDir, safeName);
                        File.WriteAllBytes(destination, bytes);
                        Logger.Log($"Extra file written to: {destination}");
                        failure = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to install extra file {safeName}: {ex.Message}");
                        failure = $"附属文件下载失败: {safeName} - {ex.Message}";
                    }
                }

                if (failure is not null)
                {
                    return failure;
                }
            }

            return null;
        }

        /// <summary>
        /// 删除插件的附属数据文件。
        ///
        /// 只删清单里明确列出的名字，不做通配清扫——插件目录里躺的是用户的其他插件，
        /// 猜错一个名字就是误删。清单没加载时残留的数据文件是惰性的，重装会覆盖，无害。
        /// </summary>
        private void DeleteExtraFiles(UnifiedPluginItem plugin)
        {
            if (plugin.Extra is null || plugin.Extra.Count == 0) return;

            var dir = !string.IsNullOrEmpty(plugin.LocalFilePath)
                ? Path.GetDirectoryName(plugin.LocalFilePath)
                : PluginManager.PluginPath;
            if (string.IsNullOrEmpty(dir)) return;

            foreach (var extra in plugin.Extra)
            {
                var safeName = Path.GetFileName(extra.Name ?? "");
                if (string.IsNullOrEmpty(safeName)) continue;

                var path = Path.Combine(dir, safeName);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Logger.Log($"Deleted extra file: {path}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete extra file {path}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 下载并安装/更新一个插件，成败以返回值报告，刷新列表和提示留给调用方。
        /// 哈希对不上时由 <paramref name="confirmForceInstall"/> 决定要不要强装，
        /// 传 null 就是不提供强装：简易界面的用户没有判断文件来源的条件，这种情况一律不装。
        /// </summary>
        public async Task<PluginOperationResult> InstallOrUpdateAsync(UnifiedPluginItem plugin, Func<string, bool>? confirmForceInstall = null)
        {
            try
            {
                using (var client = CreateClient())
                {
                    var routes = await ResolveDownloadRoutesAsync();
                    byte[] data = null;
                    string downloadedFileHash = null;
                    bool downloadSuccess = false;

                    // 重试下载机制：线路够多时每次换一条，不够时在现有线路上重试
                    var attempts = Math.Max(3, routes.Count);
                    for (int attempt = 1; attempt <= attempts; attempt++)
                    {
                        try
                        {
                            var downloadUrl = ProxyRoutingPolicy.BuildPluginStoreUrl(routes[(attempt - 1) % routes.Count], plugin.FileUrl);
                            Logger.Log($"Downloading plugin (attempt {attempt}/{attempts}): {downloadUrl}");
                            data = await client.GetByteArrayAsync(downloadUrl);
                            Logger.Log($"Downloaded {data.Length} bytes");

                            using (var sha256 = SHA256.Create())
                            {
                                var hash = sha256.ComputeHash(data);
                                downloadedFileHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            }

                            var expectedHash = plugin.SHA256?.ToLowerInvariant() ?? "";
                            Logger.Log($"File validation - Expected: {expectedHash}");
                            Logger.Log($"File validation - Downloaded: {downloadedFileHash}");
                            Logger.Log($"File validation - Size: {data.Length} bytes");

                            if (downloadedFileHash == expectedHash)
                            {
                                Logger.Log("File validation successful");
                                downloadSuccess = true;
                                break;
                            }
                            else
                            {
                                Logger.Log($"File validation failed on attempt {attempt}");
                                if (attempt < attempts)
                                {
                                    Logger.Log("Retrying download...");
                                    await Task.Delay(1000); // 等待1秒后重试
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Download attempt {attempt} failed: {ex.Message}");
                            if (attempt < attempts)
                            {
                                await Task.Delay(1000);
                            }
                        }
                    }

                    if (!downloadSuccess)
                    {
                        if (confirmForceInstall is null)
                        {
                            return PluginOperationResult.Fail(LanguageHelper.Get(
                                "QuickSetup.PluginHashFailed", _plugin.Settings.Language,
                                "下载失败或文件校验没通过，为了安全没有安装。请检查网络后重试。"));
                        }

                        var errorMessage = $"文件校验失败！\n" +
                                         $"插件: {plugin.Name}\n" +
                                         $"期望哈希: {plugin.SHA256}\n" +
                                         $"实际哈希: {downloadedFileHash ?? "下载失败"}\n" +
                                         $"文件大小: {data?.Length ?? 0} 字节\n\n" +
                                         $"可能原因:\n" +
                                         $"1. 网络连接不稳定\n" +
                                         $"2. 服务器文件已更新但哈希值未同步\n" +
                                         $"3. 文件在传输过程中损坏\n\n" +
                                         $"是否要强制安装此插件？\n" +
                                         $"(仅在确信文件来源安全时选择'是')";

                        if (!confirmForceInstall(errorMessage))
                        {
                            // 用户自己按的取消，不算失败，也别再进汇总里烦他一次。
                            return PluginOperationResult.Cancelled();
                        }
                        else
                        {
                            Logger.Log($"User chose to force install plugin despite validation failure");
                            // 继续安装，但使用实际下载的文件哈希值作为新的期望值
                            downloadedFileHash = downloadedFileHash ?? "unknown";

                            // 额外的安全检查：验证文件是否为有效的 .NET 程序集。
                            //
                            // 这里绝不能用 Assembly.Load(byte[])：它把程序集永久载入默认
                            // AssemblyLoadContext（卸载不掉，之后插件走可回收 ALC 加载还会撞车），
                            // 并且会执行模块初始化器——等于在"校验"一个哈希已经对不上的下载文件时，
                            // 先把它的代码跑一遍。这同时也是 .NET 恶意软件无文件加载的标志动作，
                            // 杀软的启发式引擎会直接记账。
                            //
                            // 改成只读元数据：PEReader 不按可执行映像映射、不解析引用、不运行任何代码。
                            try
                            {
                                using var ms = new MemoryStream(data);
                                using var peReader = new PE.PEReader(ms);

                                if (!peReader.HasMetadata)
                                    throw new BadImageFormatException("文件不含 CLI 元数据");

                                var metadata = Meta.PEReaderExtensions.GetMetadataReader(peReader);
                                if (!metadata.IsAssembly)
                                    throw new BadImageFormatException("文件不是程序集(可能是 netmodule 或原生 DLL)");

                                var asmDef = metadata.GetAssemblyDefinition();
                                Logger.Log($"Assembly validation successful: {metadata.GetString(asmDef.Name)}, v{asmDef.Version}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Assembly validation failed: {ex.Message}");
                                return PluginOperationResult.Fail($"下载的文件不是有效的 .NET 程序集: {ex.Message}");
                            }
                        }
                    }

                    var pluginDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");
                    if (!Directory.Exists(pluginDir))
                    {
                        Directory.CreateDirectory(pluginDir);
                    }

                    string filePath;

                    // 如果是更新操作，使用现有插件的文件路径，确保替换而不是创建新文件
                    if (plugin.IsLocal && plugin.LocalPlugin is not null && !string.IsNullOrEmpty(plugin.LocalPlugin.FilePath))
                    {
                        filePath = plugin.LocalPlugin.FilePath;
                        Logger.Log($"Updating existing plugin file: {filePath}");
                    }
                    else
                    {
                        // 新安装的插件，使用 plugin.Id 作为文件名
                        filePath = Path.Combine(pluginDir, $"{plugin.Id}.dll");
                        Logger.Log($"Installing new plugin file: {filePath}");
                    }

                    // 写入新文件
                    File.WriteAllBytes(filePath, data);
                    Logger.Log($"Plugin file written to: {filePath}");

                    // 下面两处校验是安全检查，必须真读文件——先把哈希缓存作废，
                    // 不给它任何拿旧值糊弄过去的机会。
                    PluginManager.InvalidateSha256Cache(filePath);

                    // 确保文件写入完成并释放文件句柄
                    await Task.Delay(200);

                    // 验证文件确实已更新（在加载插件之前）
                    string actualFileHash;
                    try
                    {
                        actualFileHash = PluginManager.GetFileSha256(filePath);
                        Logger.Log($"Plugin update verification - Expected: {downloadedFileHash}, Actual: {actualFileHash}");

                        if (actualFileHash != downloadedFileHash)
                        {
                            Logger.Log($"Error: File hash mismatch after update. Expected: {downloadedFileHash}, Got: {actualFileHash}");
                            return PluginOperationResult.Fail($"更新验证失败（期望 {downloadedFileHash}，实际 {actualFileHash}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error verifying updated plugin file: {ex.Message}");
                        return PluginOperationResult.Fail($"无法验证更新后的插件文件: {ex.Message}");
                    }

                    // 附属数据文件必须赶在插件被加载之前就位：插件在 Initialize 里就要读它们，
                    // 晚一步 TerminalPlugin 这类就会直接进 fail-closed 保护模式。
                    var extraError = await DownloadExtraFilesAsync(client, plugin, Path.GetDirectoryName(filePath), routes);
                    if (extraError is not null)
                    {
                        return PluginOperationResult.Fail(extraError);
                    }

                    // 如果是更新操作，使用专门的更新方法
                    if (plugin.IsLocal && plugin.LocalPlugin is not null)
                    {
                        Logger.Log($"Updating existing plugin: {plugin.LocalPlugin.Name}");
                        bool updateSuccess = await _plugin.UpdatePlugin(filePath);
                        if (!updateSuccess)
                        {
                            Logger.Log($"Failed to update plugin using UpdatePlugin method, falling back to full reload");
                            _plugin.LoadPlugins();
                        }
                        else
                        {
                            Logger.Log($"Plugin updated successfully using UpdatePlugin method");
                        }
                    }
                    else
                    {
                        // 新安装的插件，使用常规加载方法
                        Logger.Log($"Installing new plugin: {plugin.Name}");
                        _plugin.LoadPlugins();
                    }
                    Logger.Log($"Plugins reloaded after install/update");

                    // 这里原本还有 800ms 空转 + 立即刷一遍列表 + 1000ms 空转 + 读 DataGrid 自检，
                    // 自检不过再刷一遍 + 再等 1500ms。单个插件就是 3.3 秒纯等待、两次全表重算；
                    // 而 UpdatePlugin/LoadPlugins 本身已经等过文件句柄和 GC 了，这些padding 是叠着加的。
                    // 现在只留一次落盘校验，刷新交给批次结束时统一做一次。
                    var reloadedHash = PluginManager.GetFileSha256(filePath);
                    Logger.Log($"Post-reload verification - Expected: {downloadedFileHash}, Actual: {reloadedHash}");

                    if (!string.IsNullOrEmpty(reloadedHash) &&
                        !string.Equals(reloadedHash, downloadedFileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return PluginOperationResult.Fail($"加载后文件校验不一致（期望 {downloadedFileHash}，实际 {reloadedHash}）");
                    }

                    return PluginOperationResult.Ok();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"安装/更新插件失败 [{plugin.Name}]: {ex.Message}");
                return PluginOperationResult.Fail(ex.Message);
            }
        }
    }
}
