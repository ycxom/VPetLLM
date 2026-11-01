using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPetLLM;
using VPetLLM.Core.Abstractions.Interfaces.Plugin;
using VPetLLM.Core.Services;
using VPetLLM.Infrastructure.Configuration;

// 简易设置和新手引导的回归检查。
//
// 简易模式面向不会用完整设置的用户，他们出了问题也不知道该怎么恢复，所以这几条规则最不能被改坏：
//   1. 老用户升级后不被引导打扰，全新安装（默认节点发不出请求）一定弹；
//   2. 保存只调整优先级：不删、不停用用户在完整设置里配的渠道，留空密钥不丢；
//   3. 语音开关不覆盖已配好的服务；
//   4. 引导状态能落盘，否则每次启动都弹；
//   5. 界面用到的词条在四种语言里都有，占位符一致。
//
// 注意：settings.db 固定在"文档\VPetLLM"，不看 Setting 构造参数，这里读到的是本机真实配置。
// 所以 Fresh() 先清成已知状态，且本文件绝不能调用 Setting.Save()。落盘检查见 Test_Persistence。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var dir = Path.Combine(Path.GetTempPath(), $"vpet-quicksetup-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Test_ChannelConfigured();
            Test_ShouldShowOnboarding(dir);
            Test_SettingsMode(dir);
            Test_PrimaryChannel(dir);
            Test_ApplyChannel(dir);
            Test_ApplyVoice(dir);
            Test_Persistence(dir);
            Test_PluginCatalog();
            Test_PluginHashFailure(dir).GetAwaiter().GetResult();
            Test_PluginRoutes(dir).GetAwaiter().GetResult();
            Test_PluginPanelLoads(dir);
            Test_Localization();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 连接池可能还占着临时库，不影响结果 */ }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>全新安装的状态：一个没选模型的 Ollama 节点 + 未启用的 Free 节点，语音都没配过。</summary>
    static Setting Fresh(string dir)
    {
        var s = new Setting(Path.Combine(dir, Guid.NewGuid().ToString("N")));
        s.OpenAI.OpenAINodes.Clear();
        s.Gemini.GeminiNodes.Clear();
        s.Ollama.OllamaNodes.Clear();
        s.LMStudio.LMStudioNodes.Clear();
        s.Free.FreeNodes.Clear();

        s.Ollama.OllamaNodes.Add(new Setting.OllamaNodeSetting { Model = "", Priority = 100 });
        s.Free.FreeNodes.Add(new Setting.FreeNodeSetting { Enabled = false });

        s.OnboardingCompleted = false;
        s.TTS.Provider = "URL";
        s.TTS.URL.BaseUrl = new Setting.URLTTSSetting().BaseUrl;
        s.TTS.IsEnabled = false;
        s.ASR.Provider = "OpenAI";
        s.ASR.OpenAI.ApiKey = "";
        s.ASR.Soniox.ApiKey = "";
        s.ASR.IsEnabled = false;
        return s;
    }

    static void Test_ChannelConfigured()
    {
        Console.WriteLine("\n[渠道是否算配置过]");
        var defaultUrl = new Setting.OpenAINodeSetting().Url;

        Check("Free 不用填写", QuickSetupService.IsChannelConfigured(new Setting.FreeNodeSetting()));
        Check("OpenAI 默认地址无密钥 = 没配", !QuickSetupService.IsChannelConfigured(new Setting.OpenAINodeSetting { Url = defaultUrl, ApiKey = "" }));
        Check("OpenAI 有密钥 = 配过", QuickSetupService.IsChannelConfigured(new Setting.OpenAINodeSetting { Url = defaultUrl, ApiKey = "k" }));
        Check("OpenAI 改过地址 = 配过（本地转发常常不要密钥）",
              QuickSetupService.IsChannelConfigured(new Setting.OpenAINodeSetting { Url = "http://127.0.0.1:3000/v1", ApiKey = "" }));
        Check("OpenAI 地址只差末尾斜杠 = 没配",
              !QuickSetupService.IsChannelConfigured(new Setting.OpenAINodeSetting { Url = defaultUrl + "/", ApiKey = "" }));
        Check("Gemini 无密钥 = 没配", !QuickSetupService.IsChannelConfigured(new Setting.GeminiNodeSetting { ApiKey = "" }));
        Check("Gemini 有密钥 = 配过", QuickSetupService.IsChannelConfigured(new Setting.GeminiNodeSetting { ApiKey = "k" }));
        Check("Ollama 没选模型 = 没配（全新安装迁移出的就是它）",
              !QuickSetupService.IsChannelConfigured(new Setting.OllamaNodeSetting { Model = "" }));
        Check("Ollama 选了模型 = 配过", QuickSetupService.IsChannelConfigured(new Setting.OllamaNodeSetting { Model = "qwen3" }));
        Check("LMStudio 没选模型 = 没配", !QuickSetupService.IsChannelConfigured(new Setting.LMStudioNodeSetting { Model = "" }));
    }

    static void Test_ShouldShowOnboarding(string dir)
    {
        Console.WriteLine("\n[自动弹引导]");

        var s = Fresh(dir);
        Check("全新安装弹", QuickSetupService.ShouldShowOnboarding(s));

        s = Fresh(dir);
        s.Ollama.OllamaNodes[0].Model = "qwen3";
        Check("老用户升级（默认值 false，但渠道配好了）不弹", !QuickSetupService.ShouldShowOnboarding(s));

        s = Fresh(dir);
        s.OnboardingCompleted = true;
        Check("看过引导就不再弹，哪怕还没配", !QuickSetupService.ShouldShowOnboarding(s));

        s = Fresh(dir);
        s.Ollama.OllamaNodes[0].Model = "qwen3";
        s.Ollama.OllamaNodes[0].Enabled = false;
        Check("配过但全停用了 = 现在聊不了，弹", QuickSetupService.ShouldShowOnboarding(s));
    }

    static void Test_SettingsMode(string dir)
    {
        Console.WriteLine("\n[引导里的设置界面]");

        var s = Fresh(dir);
        s.UseSimpleSettings = false;
        Check("首次引导默认选简易界面", QuickSetupService.PreferSimpleSettings(s, firstRun: true));
        Check("从极客界面重开引导，默认保持极客界面", !QuickSetupService.PreferSimpleSettings(s, firstRun: false));

        s.UseSimpleSettings = true;
        Check("从简易界面重开引导，默认保持简易界面", QuickSetupService.PreferSimpleSettings(s, firstRun: false));
    }

    static void Test_PrimaryChannel(string dir)
    {
        Console.WriteLine("\n[主渠道]");

        var s = Fresh(dir);
        var low = new Setting.OpenAINodeSetting { Name = "low", Url = "http://a/v1", Priority = 10 };
        var high = new Setting.OpenAINodeSetting { Name = "high", Url = "http://b/v1", Priority = 500 };
        var off = new Setting.OpenAINodeSetting { Name = "off", Url = "http://c/v1", Priority = 900, Enabled = false };
        s.OpenAI.OpenAINodes.AddRange(new[] { low, high, off });

        Check("优先级最大的启用渠道", ReferenceEquals(QuickSetupService.GetPrimaryChannel(s), high));

        foreach (var c in s.EnumerateChannels()) c.Enabled = false;
        Check("全停用时为 null", QuickSetupService.GetPrimaryChannel(s) is null);
    }

    static void Test_ApplyChannel(string dir)
    {
        Console.WriteLine("\n[写回渠道]");

        // 新增：已有渠道一条不少，新渠道排到最前面
        var s = Fresh(dir);
        s.Ollama.OllamaNodes[0].Model = "qwen3";
        var backup = new Setting.GeminiNodeSetting { Name = "备用", ApiKey = "gem", Priority = 300 };
        s.Gemini.GeminiNodes.Add(backup);
        var before = s.EnumerateChannels().Count();

        var draft = QuickSetupService.BuildDraft(s, Setting.ChannelKind.OpenAIChat, "DeepSeek", "https://api.deepseek.com/v1", "sk-new", "deepseek-chat");
        Check("BuildDraft 不改设置", s.EnumerateChannels().Count() == before);

        var target = QuickSetupService.ApplyChannel(s, draft);
        Check("新增一条，其它不删", s.EnumerateChannels().Count() == before + 1 && s.Gemini.GeminiNodes.Contains(backup));
        Check("其它渠道保持启用", backup.Enabled && s.Ollama.OllamaNodes[0].Enabled);
        Check("新渠道压过所有启用渠道", target.Priority > 300, $"priority={target.Priority}");
        Check("新渠道成为主渠道", ReferenceEquals(QuickSetupService.GetPrimaryChannel(s), target));
        Check("名字用用户选的服务商", target.Name == "DeepSeek");

        // 再保存一次：换了模型、密钥留空
        var firstId = target.Id;
        var count = s.EnumerateChannels().Count();
        var again = QuickSetupService.BuildDraft(s, Setting.ChannelKind.OpenAIChat, "别的名字", "https://api.deepseek.com/v1/", "", "deepseek-reasoner");
        var reused = QuickSetupService.ApplyChannel(s, again);
        Check("同地址复用节点，不会越存越多", s.EnumerateChannels().Count() == count && ReferenceEquals(reused, target));
        Check("复用时 Id 不变", reused.Id == firstId);
        Check("密钥留空沿用旧密钥", ((Setting.OpenAINodeSetting)reused).ApiKey == "sk-new");
        Check("模型更新了", reused.Model == "deepseek-reasoner");
        Check("复用时不改名（用户可能在完整设置里改过）", reused.Name == "DeepSeek");

        // Free：启用已有节点，不新增
        s = Fresh(dir);
        s.Ollama.OllamaNodes[0].Model = "qwen3";
        var free = s.Free.FreeNodes[0];
        var freeTarget = QuickSetupService.ApplyChannel(s, QuickSetupService.BuildDraft(s, Setting.ChannelKind.Free, null, null, null, null));
        Check("Free 复用已有节点", s.Free.FreeNodes.Count == 1 && ReferenceEquals(freeTarget, free));
        Check("Free 被启用并成为主渠道", free.Enabled && ReferenceEquals(QuickSetupService.GetPrimaryChannel(s), free));

        // 已停用的渠道优先级再高也不参与选路，不该把新渠道的优先级顶上去
        s = Fresh(dir);
        s.Ollama.OllamaNodes[0].Enabled = false;
        s.Ollama.OllamaNodes[0].Priority = 9000;
        var keep = new Setting.OpenAINodeSetting().Priority;
        var only = QuickSetupService.ApplyChannel(s, QuickSetupService.BuildDraft(s, Setting.ChannelKind.OpenAIChat, "x", "http://x/v1", "k", "m"));
        Check("停用渠道不抬高优先级", only.Priority == keep, $"priority={only.Priority}");
    }

    static void Test_ApplyVoice(string dir)
    {
        Console.WriteLine("\n[语音开关]");

        var s = Fresh(dir);
        QuickSetupService.ApplyVoice(s, readAloud: true, voiceInput: true);
        Check("TTS 没配过 → 免费服务", s.TTS.IsEnabled && s.TTS.Provider == "Free");
        Check("ASR 没配过 → 免费服务", s.ASR.IsEnabled && s.ASR.Provider == "Free");

        s = Fresh(dir);
        s.TTS.URL.BaseUrl = "http://my-tts/";
        s.ASR.OpenAI.ApiKey = "k";
        QuickSetupService.ApplyVoice(s, readAloud: true, voiceInput: true);
        Check("TTS 配过的保留", s.TTS.IsEnabled && s.TTS.Provider == "URL");
        Check("ASR 配过的保留", s.ASR.IsEnabled && s.ASR.Provider == "OpenAI");

        s = Fresh(dir);
        s.ASR.Provider = "Soniox";
        s.ASR.Soniox.ApiKey = "k";
        QuickSetupService.ApplyVoice(s, readAloud: false, voiceInput: true);
        Check("Soniox 配过的保留", s.ASR.Provider == "Soniox");

        s = Fresh(dir);
        s.TTS.Provider = "GPT-SoVITS";
        s.ASR.Provider = "Soniox";
        QuickSetupService.ApplyVoice(s, readAloud: true, voiceInput: false);
        Check("其它 TTS 服务不被改成 Free", s.TTS.Provider == "GPT-SoVITS");
        Check("关闭语音输入时不动服务", !s.ASR.IsEnabled && s.ASR.Provider == "Soniox");

        s = Fresh(dir);
        QuickSetupService.ApplyVoice(s, readAloud: false, voiceInput: false);
        Check("关闭时不动服务，也不替用户开启", !s.TTS.IsEnabled && s.TTS.Provider == "URL" && !s.ASR.IsEnabled && s.ASR.Provider == "OpenAI");
    }

    static void Test_Persistence(string dir)
    {
        Console.WriteLine("\n[引导状态落盘]");

        // 把库和备份都改到临时目录：Save 前的备份会轮转删除旧备份，不换的话动的是用户真实的备份
        var db = Path.Combine(dir, "settings.db");
        var storage = new SQLiteSettingStorage(dir);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(SQLiteSettingStorage).GetField("_databasePath", flags)!.SetValue(storage, db);
        typeof(SQLiteSettingStorage).GetField("_backupManager", flags)!.SetValue(storage, new BackupManager(db));
        try
        {
            Check("确实用的是临时库", storage.GetStorageLocation() == db, storage.GetStorageLocation());
            if (storage.GetStorageLocation() != db) return;
            Check("临时库初始化成功", storage.Initialize());

            var s = Fresh(dir);
            s.OnboardingCompleted = true;
            s.UseSimpleSettings = true;
            s.ShowOnboardingOnStartup = true;
            storage.Save(s);

            // 和 Setting.LoadSettings 同一条路
            var back = Fresh(dir);
            back.OnboardingCompleted = false;
            back.UseSimpleSettings = false;
            back.ShowOnboardingOnStartup = false;
            JsonConvert.PopulateObject(storage.LoadAsJson()!, back);
            Check("OnboardingCompleted 往返", back.OnboardingCompleted);
            Check("UseSimpleSettings 往返", back.UseSimpleSettings);
            Check("ShowOnboardingOnStartup 往返", back.ShowOnboardingOnStartup);
        }
        finally
        {
            storage.Dispose();
        }
    }

    static UnifiedPluginItem Installed(string name, string sha) => new()
    {
        IsLocal = true, Id = name, Name = name, OriginalName = name, Version = sha, StatusText = "运行中"
    };

    static void Test_PluginCatalog()
    {
        Console.WriteLine("\n[插件商店清单合并]");

        var remote = JsonConvert.DeserializeObject<Dictionary<string, JObject>>("""
            {
              "new_id": { "Name": "new_plugin", "Author": "a", "File": "https://x/new.dll", "SHA256": "aa",
                          "Description": { "en": "english", "zh-hans": "中文" },
                          "Extra": [ { "Name": "rules.json", "File": "https://x/rules.json", "SHA256": "bb" } ] },
              "stale_id": { "Name": "stale", "File": "https://x/stale.dll", "SHA256": "new-hash" },
              "same_id": { "Name": "same", "File": "https://x/same.dll", "SHA256": "ABCD" },
              "broken_id": { "Name": "broken", "File": "https://x/broken.dll", "SHA256": "abcd" },
              "draft_id": { "Name": "draft", "File": "https://x/draft.dll", "SHA256": "cc", "Published": false },
              "draft_installed_id": { "Name": "draft_installed", "File": "https://x/d.dll", "SHA256": "dd", "Published": "false" }
            }
            """)!;

        var items = new Dictionary<string, UnifiedPluginItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["stale"] = Installed("stale", "old-hash"),
            ["same"] = Installed("same", "abcd"),
            ["broken"] = Installed("broken", "abcd"),
            ["draft_installed"] = Installed("draft_installed", "old-hash"),
            ["local_only"] = Installed("local_only", "ffff")
        };
        items["broken"].IsFailed = true;

        var hidden = PluginStoreService.MergeCatalog(items, remote, "zh-hans");
        Check("未上线条目计入跳过数", hidden == 2, $"hidden={hidden}");
        Check("未上线条目不出现在列表里", !items.ContainsKey("draft_id") && !items.Values.Any(p => p.Name == "draft"));

        var fresh = items["new_id"];
        Check("没装的可以安装、不能卸载", fresh.CanInstallOrUpdate && !fresh.CanUninstall);
        Check("描述按当前语言取、不带来源前缀", fresh.OriginalDescription == "中文", fresh.OriginalDescription);
        Check("附属文件带着哈希解析出来",
            fresh.Extra.Count == 1 && fresh.Extra[0].Name == "rules.json" && fresh.Extra[0].SHA256 == "bb");

        Check("哈希不同可以更新", items["stale"].CanInstallOrUpdate && items["stale"].IsUpdatable);
        Check("哈希相同（大小写不同）不提示更新", !items["same"].IsUpdatable && !items["same"].CanInstallOrUpdate);
        Check("已是最新的仍然能卸载", items["same"].CanUninstall);
        Check("加载失败的即使哈希相同也能更新", items["broken"].CanInstallOrUpdate);
        Check("未上线版本不推给已安装的人（字符串 false 也算）", !items["draft_installed"].CanInstallOrUpdate);
        Check("商店没有的本地插件保留、只能卸载",
            items["local_only"].CanUninstall && !items["local_only"].CanInstallOrUpdate);

        Check("有更新时状态显示更新提示", items["stale"].DisplayStatus == items["stale"].UpdateAvailableText);
        Check("没更新时状态显示运行状态", items["same"].DisplayStatus == "运行中");
    }

    /// <summary>
    /// 哈希对不上时的走向是这次抽服务最不能退化的地方：简易界面不给强装。
    /// 用回环地址上的假下载源跑真实的下载校验流程，不碰外网。
    /// </summary>
    static async Task Test_PluginHashFailure(string dir)
    {
        Console.WriteLine("\n[插件校验失败]");

        using var cts = new CancellationTokenSource();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\ngarbage");
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096], cts.Token);
                    await stream.WriteAsync(response, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        var plugin = (VPetLLM.VPetLLM)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VPetLLM.VPetLLM));
        var settings = Fresh(dir);
        settings.PluginStore.UseProxy = false;
        plugin.Settings = settings;
        var store = new PluginStoreService(plugin);

        var id = $"quicksetup-check-{Guid.NewGuid():N}";
        var target = Path.Combine(VPetLLM.Utils.Plugin.PluginManager.PluginPath, $"{id}.dll");
        var item = new UnifiedPluginItem
        {
            Id = id, Name = id, FileUrl = $"http://127.0.0.1:{port}/plugin.dll", SHA256 = "expected"
        };

        try
        {
            var refused = await store.InstallOrUpdateAsync(item);
            Check("简易界面：校验失败判为失败", !refused.Success && !refused.Silent);
            Check("简易界面：给出能看懂的原因", !string.IsNullOrWhiteSpace(refused.Reason));
            Check("简易界面：文件没有写进插件目录", !File.Exists(target));

            string? asked = null;
            var declined = await store.InstallOrUpdateAsync(item, message => { asked = message; return false; });
            Check("极客界面：校验失败时问一次", asked?.Contains(id) == true);
            Check("极客界面：选否算取消、不算失败", !declined.Success && declined.Silent);
            Check("极客界面：选否时文件没有写进插件目录", !File.Exists(target));

            var forced = await store.InstallOrUpdateAsync(item, _ => true);
            Check("极客界面：强装时不是程序集照样拒绝", !forced.Success && !forced.Silent);
            Check("极客界面：拒绝后文件没有写进插件目录", !File.Exists(target));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            File.Delete(target);
        }
    }

    /// <summary>
    /// 代理池接进商店后的走向：哪条线路先试、坏线路换下一条、哈希校验一点不松。
    /// 故意留一个所有线路都校验不过的附属文件，让流程停在加载插件之前，不去动真实的插件目录。
    /// </summary>
    static async Task Test_PluginRoutes(string dir)
    {
        Console.WriteLine("\n[插件下载线路]");

        const string catalogUrl = "https://raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json";
        const string catalogPath = "raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json";
        const string dllPath = "github.com/o/r/raw/a.dll";
        const string goodExtraPath = "github.com/o/r/raw/good.dat";
        const string badExtraPath = "github.com/o/r/raw/bad.dat";

        static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var catalog = Encoding.UTF8.GetBytes("{\"A\":{\"Name\":\"a\",\"File\":\"https://github.com/o/r/raw/a.dll\"}}");
        var goodDll = Encoding.UTF8.GetBytes("good dll");
        var badDll = Encoding.UTF8.GetBytes("tampered dll");
        var goodExtra = Encoding.UTF8.GetBytes("good extra");

        var fetched = new List<string>();
        Func<string, CancellationToken, Task<byte[]>> fetch = async (url, token) =>
        {
            lock (fetched) fetched.Add(url);
            if (url.EndsWith("GitHub_Proxy.json"))
            {
                return Encoding.UTF8.GetBytes("{\"proxies\":[\"https://slow.test\",\"https://fast.test\"]}");
            }
            if (url == $"https://slow.test/{catalogPath}") { await Task.Delay(300, token); return catalog; }
            if (url == $"https://fast.test/{catalogPath}") return catalog;
            throw new HttpRequestException("unreachable");
        };

        var pool = new GitHubProxyPool(Path.Combine(dir, $"routes-{Guid.NewGuid():N}.db"), catalogUrl,
            new[] { "https://list.test/GitHub_Proxy.json" }, fetch, () => DateTime.UtcNow);

        var requested = new List<string>();
        var downloads = new FakeDownloads(requested, new Dictionary<string, byte[]>
        {
            [$"https://fast.test/{dllPath}"] = badDll,
            [$"https://slow.test/{dllPath}"] = goodDll,
            [$"https://slow.test/{goodExtraPath}"] = goodExtra,
            [$"https://fast.test/{badExtraPath}"] = badDll,
            [$"https://slow.test/{badExtraPath}"] = badDll,
            [$"https://ghfast.top/{badExtraPath}"] = badDll,
            [$"https://{badExtraPath}"] = badDll
        });

        var plugin = (VPetLLM.VPetLLM)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VPetLLM.VPetLLM));
        var settings = Fresh(dir);
        settings.PluginStore.UseProxy = true;
        settings.PluginStore.ProxyUrl = "https://ghfast.top";
        plugin.Settings = settings;
        var store = new PluginStoreService(plugin, pool, () => downloads);

        var pluginDir = Path.Combine(dir, $"routes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginDir);
        var dllFile = Path.Combine(pluginDir, "a.dll");
        var item = new UnifiedPluginItem
        {
            IsLocal = true,
            LocalPlugin = new FakePlugin { FilePath = dllFile },
            Id = "a",
            Name = "a",
            FileUrl = $"https://{dllPath}",
            SHA256 = Hash(goodDll),
            Extra = new List<PluginExtraFile>
            {
                new() { Name = "good.dat", FileUrl = $"https://{goodExtraPath}", SHA256 = Hash(goodExtra) },
                new() { Name = "bad.dat", FileUrl = $"https://{badExtraPath}", SHA256 = Hash(goodExtra) }
            }
        };

        var result = await store.InstallOrUpdateAsync(item);

        var dllRequests = requested.Where(r => r.EndsWith("a.dll")).ToList();
        Check("先试最快的线路", dllRequests.FirstOrDefault() == $"https://fast.test/{dllPath}", string.Join(", ", dllRequests));
        Check("哈希对不上就换下一条线路", dllRequests.Count == 2 && dllRequests[1] == $"https://slow.test/{dllPath}", string.Join(", ", dllRequests));
        Check("写进去的是校验通过的那份", File.Exists(dllFile) && File.ReadAllBytes(dllFile).SequenceEqual(goodDll));
        Check("附属文件一条线路失败就换下一条", File.Exists(Path.Combine(pluginDir, "good.dat"))
            && File.ReadAllBytes(Path.Combine(pluginDir, "good.dat")).SequenceEqual(goodExtra));
        Check("附属文件所有线路都校验不过就判失败", !result.Success && result.Reason.Contains("bad.dat"), result.Reason);
        Check("校验不过的附属文件不落盘", !File.Exists(Path.Combine(pluginDir, "bad.dat")));
        Check("所有线路都试过才放弃", requested.Count(r => r.EndsWith("bad.dat")) == 4, string.Join(", ", requested));

        var id = $"routes-check-{Guid.NewGuid():N}";
        var target = Path.Combine(VPetLLM.Utils.Plugin.PluginManager.PluginPath, $"{id}.dll");
        var refused = await store.InstallOrUpdateAsync(new UnifiedPluginItem
        {
            Id = id, Name = id, FileUrl = $"https://{dllPath}", SHA256 = "expected"
        });
        Check("多线路下校验不过仍然拒绝", !refused.Success && !refused.Silent);
        Check("多线路下校验不过文件不落地", !File.Exists(target));

        await pool.WaitForIdleAsync();

        var fetchCount = fetched.Count;
        requested.Clear();
        downloads.Files[catalogUrl] = catalog;

        settings.PluginStore.ProxyUrl = "127.0.0.1:7890";
        Check("本地 HTTP 代理照常拉清单", (await store.FetchCatalogAsync()).ContainsKey("A"));
        Check("本地 HTTP 代理不走代理池", fetched.Count == fetchCount && requested.SequenceEqual(new[] { catalogUrl }));

        requested.Clear();
        await store.InstallOrUpdateAsync(new UnifiedPluginItem
        {
            Id = id, Name = id, FileUrl = $"https://{badExtraPath}", SHA256 = "expected"
        });
        Check("本地 HTTP 代理下载不换线路", requested.Count == 3 && requested.All(r => r == $"https://{badExtraPath}"), string.Join(", ", requested));
        requested.Clear();

        settings.PluginStore.UseProxy = false;
        settings.PluginStore.ProxyUrl = "https://ghfast.top";
        Check("关掉代理照常拉清单", (await store.FetchCatalogAsync()).ContainsKey("A"));
        Check("关掉代理不走代理池", fetched.Count == fetchCount && requested.SequenceEqual(new[] { catalogUrl }));

        settings.PluginStore.UseProxy = true;
        requested.Clear();
        Check("镜像模式经代理池拉清单", (await store.FetchCatalogAsync()).ContainsKey("A"));
        Check("镜像模式不绕过代理池直接请求", fetched.Count > fetchCount && requested.Count == 0);
        await pool.WaitForIdleAsync();
    }

    /// <summary>XAML 里的样式键写错只有运行时才炸，这里真的加载并排版一次，顺带核对每行露出的按钮。</summary>
    static void Test_PluginPanelLoads(string dir)
    {
        Console.WriteLine("\n[插件面板界面]");

        string? error = null;
        var buttons = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                // pack:// 地址要等它注册。关掉最后一个窗口时不能让它退出，否则 Dispatcher 跟着停了，后面的窗口开不了
                _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                var panel = new VPetLLM.UI.Controls.QuickPluginPanel();
                var list = (System.Windows.Controls.ItemsControl)panel.FindName("List_Plugins");
                list.ItemsSource = new List<UnifiedPluginItem>
                {
                    new() { IsLocal = true, IsUpdatable = true, Name = "money_manager", UpdateAvailableText = "可更新", OriginalDescription = "帮桌宠管理钱包，这段说明故意写长一点，用来看换行之后按钮还在不在原来的位置上。", ActionText = "更新", UninstallActionText = "卸载", LocalPlugin = new FakePlugin(), HasSettingAction = true, SettingActionText = "设置" },
                    new() { IsLocal = true, Name = "system_info", StatusText = "运行中", OriginalDescription = "查询电脑的系统信息。", ActionText = "卸载插件", UninstallActionText = "卸载" },
                    new() { Name = "wait", StatusText = "未下载", OriginalDescription = "让桌宠等待一段时间。", ActionText = "安装" }
                };

                var size = new System.Windows.Size(560, double.PositiveInfinity);
                panel.Measure(size);
                panel.Arrange(new System.Windows.Rect(new System.Windows.Size(560, panel.DesiredSize.Height)));
                panel.UpdateLayout();

                for (var i = 0; i < list.Items.Count; i++)
                {
                    var row = (System.Windows.DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(i);
                    buttons.Add(string.Join("+", VisibleButtons(row).Select(b => (string)b.Content)));
                }

                CheckSettingsButton(dir);
                CheckSimpleSettingTabs(dir);
                CheckGeekWindowLoads();
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Check("面板 XAML 能加载、能排版", error is null, error?.Split('\n')[0] ?? "");
        Check("有更新且有设置的行：更新 + 设置 + 卸载", buttons.ElementAtOrDefault(0) == "更新+设置+卸载", buttons.ElementAtOrDefault(0) ?? "");
        Check("已是最新的行：只有卸载", buttons.ElementAtOrDefault(1) == "卸载", buttons.ElementAtOrDefault(1) ?? "");
        Check("没装的行：只有安装", buttons.ElementAtOrDefault(2) == "安装", buttons.ElementAtOrDefault(2) ?? "");
    }

    /// <summary>
    /// 设置按钮的每条路都真的点一遍。列表走 BuildLocalItems，按钮文字和判据都是真实的；
    /// PluginManager.Plugins 是进程内的静态表，不落盘，用完清掉。
    /// </summary>
    static void CheckSettingsButton(string dir)
    {
        var host = (VPetLLM.VPetLLM)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VPetLLM.VPetLLM));
        host.Settings = Fresh(dir);

        var action = new FakeActionPlugin { Name = "fake_action", Parameters = "action(setting)" };
        var guarded = new FakeTabPlugin { Name = "fake_guarded", Parameters = "action(setting)" };
        var tab = new FakeTabPlugin { Name = "fake_tab", Parameters = "action(status)" };
        var noSetting = new FakeActionPlugin { Name = "fake_nosetting", Parameters = "amount(double)" };
        var disabled = new FakeTabPlugin { Name = "fake_disabled", Parameters = "action(setting)", Enabled = false };
        var faulted = new FakeActionPlugin { Name = "fake_faulted", Parameters = "action(setting)", Failure = new InvalidOperationException("缺资源") };
        var brokenTab = new FakeTabPlugin { Name = "fake_brokentab", Failure = new InvalidOperationException("面板坏了") };
        var opensFine = new FakeActionPlugin { Name = "fake_ok", Parameters = "action(setting)", Reply = "设置窗口已打开。" };
        var emptyReply = new FakeActionPlugin { Name = "fake_empty", Parameters = "action(setting)" };
        // 下面这几个是真实插件的写法：自己 catch 后把失败写进返回值，不抛异常
        var replyFailed = new FakeActionPlugin { Name = "fake_replyfailed", Parameters = "action(setting)", Reply = "打开设置窗口失败: 缺文件" };
        var replyError = new FakeActionPlugin { Name = "fake_replyerror", Parameters = "action(setting)", Reply = "Error: 配置坏了" };
        var resourceZh = new FakeActionPlugin { Name = "fake_reszh", Parameters = "action(setting)", Failure = new InvalidOperationException("组件“X”不具有由 URI“/X;component/a.xaml”识别的资源。") };
        var resourceEn = new FakeActionPlugin { Name = "fake_resen", Parameters = "action(setting)", Failure = new InvalidOperationException("The component 'X' does not have a resource identified by the URI '/X;component/a.xaml'.") };
        var resourceReply = new FakeActionPlugin { Name = "fake_resreply", Parameters = "action(setting)", Reply = "打开设置窗口失败: 组件“X”不具有由 URI“/a.xaml”识别的资源。" };

        var plugins = VPetLLM.Utils.Plugin.PluginManager.Plugins;
        var backup = plugins.ToList();
        plugins.Clear();
        plugins.AddRange(new IVPetLLMPlugin[] { action, guarded, tab, noSetting, disabled, faulted, brokenTab, opensFine, emptyReply, replyFailed, replyError, resourceZh, resourceEn, resourceReply });

        System.Windows.Window? window = null;
        try
        {
            var items = new PluginStoreService(host).BuildLocalItems("zh-hans");
            // 极客界面的设置按钮就看这个字段。曾因 IActionPlugin 解析到 VPetLLM.Core 下的旧接口恒为 false
            Check("新版插件声明了 setting 就有设置入口", items["fake_action"].HasSettingAction);
            Check("只有内嵌面板的插件也算有设置", items["fake_tab"].CanOpenSettings);
            Check("只有内嵌面板的插件按钮有文字", !string.IsNullOrEmpty(items["fake_tab"].SettingActionText));
            Check("没有设置的插件不算", !items["fake_nosetting"].CanOpenSettings);
            Check("没启用的插件不算（没初始化，开了也是空的）", !items["fake_disabled"].CanOpenSettings);

            // 极客界面的按钮看 CanRunSettingAction
            Check("极客界面：有入口的插件显示设置按钮", items["fake_action"].CanRunSettingAction);
            Check("极客界面：没启用的插件不显示", !items["fake_disabled"].CanRunSettingAction);
            Check("极客界面：只有面板的插件不显示（面板在插件面板页，点了也没事做）", !items["fake_tab"].CanRunSettingAction);

            // 极客界面和简易界面都走这个方法。假插件的 Task 都已完成，这里等不会卡住 UI 线程
            var store = new PluginStoreService(host);
            string? Run(string name) => store.RunSettingActionAsync(items[name]).GetAwaiter().GetResult();

            Check("打开成功不报错（成功文本不算失败）", Run("fake_ok") is null, Run("fake_ok") ?? "");
            Check("返回空串不报错", Run("fake_empty") is null);
            var thrown = Run("fake_faulted");
            Check("异常不被吞", thrown?.Contains("fake_faulted") == true && thrown.Contains("缺资源"), thrown ?? "");
            var replied = Run("fake_replyfailed");
            Check("写在返回值里的失败不被吞", replied?.Contains("fake_replyfailed") == true && replied.Contains("缺文件"), replied ?? "");
            var repliedError = Run("fake_replyerror");
            Check("Error: 开头的返回值算失败", repliedError?.Contains("配置坏了") == true, repliedError ?? "");
            var missingZh = Run("fake_reszh");
            var missingEn = Run("fake_resen");
            var missingReply = Run("fake_resreply");
            Check("资源缺失有专门提示（中文系统）", missingZh?.Contains("重新安装") == true && !missingZh.Contains("URI"), missingZh ?? "");
            Check("资源缺失有专门提示（英文系统）", missingEn?.Contains("重新安装") == true && !missingEn.Contains("URI"), missingEn ?? "");
            Check("资源缺失写在返回值里也能认出来", missingReply?.Contains("重新安装") == true, missingReply ?? "");

            var panel = new VPetLLM.UI.Controls.QuickPluginPanel();
            panel.Attach(host);
            var list = (System.Windows.Controls.ItemsControl)panel.FindName("List_Plugins");
            var state = (System.Windows.Controls.TextBlock)panel.FindName("Tb_State");
            list.ItemsSource = items.Values.ToList();

            // 要真窗口：插件面板的窗口以它为父窗口
            window = new System.Windows.Window
            {
                Content = panel, Width = 640, Height = 900, Left = -20000, Top = -20000,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false
            };
            window.Show();
            window.UpdateLayout();

            var label = items["fake_action"].SettingActionText;
            System.Windows.Controls.Button? SettingsButton(string name)
            {
                var row = (System.Windows.DependencyObject)list.ItemContainerGenerator.ContainerFromItem(items[name]);
                return VisibleButtons(row).FirstOrDefault(b => (string)b.Content == label);
            }
            void Click(string name) => SettingsButton(name)!.RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Check("有设置的行显示按钮", SettingsButton("fake_action") is not null);
            Check("没有设置的行不显示按钮", SettingsButton("fake_nosetting") is null);
            Check("没启用的行不显示按钮", SettingsButton("fake_disabled") is null);

            Click("fake_action");
            Check("有入口的插件走 action(setting)", action.Calls.SequenceEqual(new[] { "action(setting)" }), string.Join(",", action.Calls));

            Click("fake_guarded");
            Check("入口和面板都有时走入口（Terminal 的风险确认在这条路上）", guarded.Calls.Count == 1 && guarded.PanelsCreated == 0);
            Check("走入口时不另开窗口", window.OwnedWindows.Count == 0);

            Click("fake_tab");
            var opened = window.OwnedWindows.OfType<System.Windows.Window>().FirstOrDefault();
            Check("只有面板的插件套窗口打开", tab.PanelsCreated == 1 && opened?.Content is System.Windows.Controls.TextBlock);
            Check("窗口标题用插件的标题", opened?.Title == tab.TabTitle, opened?.Title ?? "");

            Click("fake_tab");
            Check("再点不重复开窗口", window.OwnedWindows.Count == 1 && tab.PanelsCreated == 1);

            Click("fake_ok");
            Check("打开成功时状态行不显示失败", string.IsNullOrEmpty(state.Text), state.Text);

            Click("fake_faulted");
            Check("异步失败能显示出来，不被吞掉", state.Text.Contains("fake_faulted") && state.Text.Contains("缺资源"), state.Text);

            Click("fake_replyfailed");
            Check("写在返回值里的失败也显示出来", state.Text.Contains("fake_replyfailed") && state.Text.Contains("缺文件"), state.Text);

            Click("fake_brokentab");
            Check("面板创建失败不崩、显示原因", state.Text.Contains("fake_brokentab") && state.Text.Contains("面板坏了"), state.Text);

            var closed = false;
            var unloaded = false;
            if (opened is not null)
            {
                opened.Closed += (_, _) => closed = true;
                ((System.Windows.FrameworkElement)opened.Content).Unloaded += (_, _) => unloaded = true;
            }
            window.Close();
            Check("简易界面关闭时带走面板窗口", closed);

            // RemoteChat 的面板靠 Unloaded 停定时器。这个事件排在 Dispatcher 里，要让它跑一轮
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check("面板窗口关掉后面板收到 Unloaded（定时器才会停）", unloaded);
        }
        finally
        {
            window?.Close();
            plugins.Clear();
            plugins.AddRange(backup);
        }
    }

    /// <summary>
    /// 简易设置的分页，用真实窗口点一遍。保存只走校验失败的分支：那条路在写入任何设置之前就返回，
    /// 所以按保存前要先确认草稿确实组不出来，否则就成了往真实 settings.db 里写。
    /// </summary>
    static void CheckSimpleSettingTabs(string dir)
    {
        Console.WriteLine("\n[简易设置分页]");

        // 真实窗口的事件都跑在 Dispatcher 里，await 回得到 UI 线程；这里是测试直接调的，要自己装上
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());

        var settings = Fresh(dir);
        settings.Ollama.OllamaNodes.Clear();
        settings.Gemini.GeminiNodes.Add(new Setting.GeminiNodeSetting { Name = "主渠道", ApiKey = "k", Model = "gemini-x", Priority = 1000 });
        // 插件页的联网部分走一个连不上的本地代理：很快失败，也不碰用真实库的代理池
        settings.PluginStore.UseProxy = true;
        settings.PluginStore.ProxyUrl = "127.0.0.1:1";

        var host = (VPetLLM.VPetLLM)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VPetLLM.VPetLLM));
        host.Settings = settings;

        var plugins = VPetLLM.Utils.Plugin.PluginManager.Plugins;
        var backup = plugins.ToList();
        plugins.Clear();

        VPetLLM.UI.Windows.winSimpleSetting? window = null;
        System.Windows.Window Open(Setting s)
        {
            host.Settings = s;
            window?.Close();
            window = new VPetLLM.UI.Windows.winSimpleSetting(host)
            {
                Left = -20000, Top = -20000,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                ShowInTaskbar = false, ShowActivated = false
            };
            window.Show();
            window.UpdateLayout();
            return window;
        }

        try
        {
            var win = Open(settings);
            T Named<T>(string name) => (T)win.FindName(name);
            var tabs = Named<System.Windows.Controls.TabControl>("Tabs");
            var tabChannel = Named<System.Windows.Controls.TabItem>("Tab_Channel");
            var channelHost = Named<System.Windows.Controls.Border>("Host_Channel");
            var channel = (VPetLLM.UI.Controls.QuickChannelPanel)channelHost.Child;
            var profile = (VPetLLM.UI.Controls.QuickProfilePanel)Named<System.Windows.Controls.Border>("Host_Profile").Child;
            var pluginPanel = (VPetLLM.UI.Controls.QuickPluginPanel)Named<System.Windows.Controls.Border>("Host_Plugins").Child;
            var pluginList = (System.Windows.Controls.ItemsControl)pluginPanel.FindName("List_Plugins");
            var pluginState = (System.Windows.Controls.TextBlock)pluginPanel.FindName("Tb_State");
            var pluginRefresh = (System.Windows.Controls.Button)pluginPanel.FindName("Btn_Refresh");
            var change = Named<System.Windows.Controls.Button>("Btn_ChangeChannel");
            var enableBuy = Named<System.Windows.Controls.CheckBox>("Cb_EnableBuy");
            var aiName = (System.Windows.Controls.TextBox)profile.FindName("Tb_AiName");

            void Select(string name)
            {
                tabs.SelectedItem = win.FindName(name);
                win.UpdateLayout();
            }
            void Tour()
            {
                foreach (var name in new[] { "Tab_Profile", "Tab_Features", "Tab_Plugins", "Tab_Help", "Tab_Channel" })
                    Select(name);
            }
            void Click(System.Windows.Controls.Button button)
            {
                button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                win.UpdateLayout();
            }

            Check("分成五页，打开时在 AI 服务", tabs.Items.Count == 5 && ReferenceEquals(tabs.SelectedItem, tabChannel), $"{tabs.Items.Count} {tabs.SelectedIndex}");
            Check("每页都有标题", tabs.Items.OfType<System.Windows.Controls.TabItem>().All(t => !string.IsNullOrEmpty(t.Header as string)));
            Check("别的页不占地方", !enableBuy.IsVisible && !aiName.IsVisible && !pluginRefresh.IsVisible);
            Check("主渠道能用时 AI 服务默认折叠", channelHost.Visibility == System.Windows.Visibility.Collapsed);
            Check("打开窗口时不加载插件", pluginList.ItemsSource is null && string.IsNullOrEmpty(pluginState.Text));

            Select("Tab_Profile");
            Check("切页后内容显示出来", aiName.IsVisible);
            var personality = (System.Windows.Controls.ComboBox)profile.FindName("Cb_Personality");
            var bubbled = false;
            tabs.SelectionChanged += (_, e) => bubbled |= ReferenceEquals(e.OriginalSource, personality);
            personality.SelectedIndex = personality.SelectedIndex == 0 ? 1 : 0;
            Check("页内下拉框的事件确实冒泡到 TabControl", bubbled);
            Check("页内下拉框不触发插件加载", pluginList.ItemsSource is null);
            Select("Tab_Features");
            Select("Tab_Help");
            Check("切到别的页不加载插件", pluginList.ItemsSource is null);

            Select("Tab_Plugins");
            Check("切到插件页才加载", pluginList.ItemsSource is not null && !pluginRefresh.IsEnabled);
            Check("切到插件页直接看得到列表，不用再点一下", pluginRefresh.IsVisible && pluginList.IsVisible);
            Pump(() => pluginRefresh.IsEnabled);
            Check("连不上商店时显示原因，能重试", pluginRefresh.IsEnabled && !string.IsNullOrEmpty(pluginState.Text), pluginState.Text);

            var loaded = new List<UnifiedPluginItem>();
            pluginList.ItemsSource = loaded;
            Select("Tab_Channel");
            Select("Tab_Plugins");
            Check("切走再切回不重复加载", ReferenceEquals(pluginList.ItemsSource, loaded));

            // 用户最常见的动作：点开看一眼，逛一圈，什么都没改。面板表达不了 Gemini，这时不能算改过
            Select("Tab_Channel");
            Click(change);
            Check("点更换能展开", channel.IsVisible);
            Tour();
            Check("只是逛一圈，Gemini 主渠道不会被写回", !channel.NeedsApply);

            var useKey = (System.Windows.Controls.RadioButton)channel.FindName("Rb_ApiKey");
            var keyBox = (System.Windows.Controls.PasswordBox)channel.FindName("Pb_ApiKey");
            useKey.IsChecked = true;
            keyBox.Password = "sk-typed";
            aiName.Text = "刚填的名字";
            var buy = enableBuy.IsChecked != true;
            enableBuy.IsChecked = buy;
            Tour();
            Check("切页不丢没保存的输入", useKey.IsChecked == true && keyBox.Password == "sk-typed" && aiName.Text == "刚填的名字" && enableBuy.IsChecked == buy);

            var useLocal = (System.Windows.Controls.RadioButton)channel.FindName("Rb_Local");
            useLocal.IsChecked = true;
            ((System.Windows.Controls.TextBox)channel.FindName("Tb_LocalUrl")).Text = "not-a-url";
            Click(change);
            Check("保存前 AI 服务已折叠", channelHost.Visibility == System.Windows.Visibility.Collapsed);
            Select("Tab_Help");

            var rejected = channel.NeedsApply && channel.BuildDraft() is null;
            Check("草稿确实组不出来（否则保存会写真实库，下面不点）", rejected);
            var save = VisibleButtons(win).FirstOrDefault(b => (b.Content as string) == "保存");
            Check("保存按钮在别的页也看得到", save is not null);
            if (rejected && save is not null)
            {
                var savedBuy = settings.EnableBuy;
                Click(save);
                Check("校验失败跳回 AI 服务页", ReferenceEquals(tabs.SelectedItem, tabChannel));
                Check("校验失败展开表单", channel.IsVisible);
                Check("校验失败给出提示", !string.IsNullOrEmpty(Named<System.Windows.Controls.TextBlock>("Tb_Status").Text));
                Check("校验失败什么都不写", settings.EnableBuy == savedBuy && settings.Gemini.GeminiNodes.Count == 1);
            }

            win = Open(Fresh(dir));
            var fresh = (VPetLLM.UI.Controls.QuickChannelPanel)((System.Windows.Controls.Border)win.FindName("Host_Channel")).Child;
            Check("还没有服务时直接摊开表单", fresh.IsVisible && fresh.NeedsApply);
        }
        finally
        {
            window?.Close();
            plugins.Clear();
            plugins.AddRange(backup);
        }
    }

    /// <summary>
    /// Tab 样式放在公共样式里，两个窗口共用，键写错只有运行时才炸。极客界面的构造函数第一句就是
    /// InitializeComponent，传 null 会在 XAML 全部加载之后、碰到设置之前抛异常，正好只验证 XAML。
    /// </summary>
    static void CheckGeekWindowLoads()
    {
        string? xamlError = null;
        try
        {
            _ = new VPetLLM.UI.Windows.winSettingNew(null!);
        }
        catch (System.Windows.Markup.XamlParseException ex)
        {
            xamlError = ex.Message;
        }
        catch (NullReferenceException)
        {
            // 预期：XAML 已经加载完
        }
        Check("极客界面 XAML 能加载", xamlError is null, xamlError ?? "");
    }

    /// <summary>让 Dispatcher 转起来，直到条件成立或超时。</summary>
    static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    static IEnumerable<System.Windows.Controls.Button> VisibleButtons(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Button { Visibility: System.Windows.Visibility.Visible } button)
                yield return button;
            foreach (var nested in VisibleButtons(child))
                yield return nested;
        }
    }

    static void Test_Localization()
    {
        Console.WriteLine("\n[词条覆盖]");

        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "VPetLLM.csproj")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        if (root is null)
        {
            Check("找到仓库根目录", false);
            return;
        }

        var lang = JObject.Parse(File.ReadAllText(Path.Combine(root, "3000_VPetLLM", "plugin", "VPetLLM_lang", "Language.json")));
        var section = (JObject)lang["QuickSetup"]!;

        var keys = new HashSet<string>();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "UI"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml") || f.EndsWith(".cs"))
            .Append(Path.Combine(root, "Core", "Services", "PluginStoreService.cs"));
        foreach (var file in sources)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"KeyPath=QuickSetup\.(\w+)|""QuickSetup\.(\w+)"""))
                keys.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        Check("扫到了界面词条", keys.Count > 50, $"count={keys.Count}");

        var missing = keys.Where(k => section[k] is null).ToList();
        Check("界面用到的键都有词条", missing.Count == 0, string.Join(",", missing));

        var incomplete = new List<string>();
        var placeholders = new List<string>();
        foreach (var entry in section.Properties())
        {
            var texts = new[] { "en", "zh-hans", "zh-hant", "jp" }.Select(l => (string?)entry.Value[l]).ToList();
            if (texts.Any(string.IsNullOrWhiteSpace))
            {
                incomplete.Add(entry.Name);
                continue;
            }
            var shapes = texts.Select(t => string.Join("", Regex.Matches(t!, @"\{\d\}").Select(m => m.Value).Order())).Distinct();
            if (shapes.Count() != 1)
                placeholders.Add(entry.Name);
        }
        Check("四种语言齐全", incomplete.Count == 0, string.Join(",", incomplete));
        Check("占位符一致（string.Format 不会抛异常）", placeholders.Count == 0, string.Join(",", placeholders));

        // 这几条不在 QuickSetup 段，上面扫不到
        foreach (var (key, placeholderCount) in new[] { ("Plugin.ResourceError", 1), ("Plugin.SettingError", 2), ("Error", 0) })
        {
            var texts = new[] { "en", "zh-hans", "zh-hant", "jp" }.Select(l => (string?)lang.SelectToken(key)?[l]).ToList();
            Check($"{key} 四种语言齐全", texts.All(t => !string.IsNullOrWhiteSpace(t)));
            Check($"{key} 占位符数量对", texts.All(t => t is not null && Regex.Matches(t, @"\{\d\}").Count == placeholderCount));
        }

        // 极客窗口起不来（要真实宿主），这里只能看源码：按钮绑的属性存在，点击处理确实等了插件
        var xaml = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winSettingNew.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "UI", "Windows", "winSettingNew.xaml.cs"));
        Check("极客界面的设置按钮绑定 CanRunSettingAction", xaml.Contains($"{{Binding {nameof(UnifiedPluginItem.CanRunSettingAction)}}}"));
        Check("极客界面的点击处理等了插件", code.Contains($"await _pluginStore.{nameof(PluginStoreService.RunSettingActionAsync)}(plugin)"));
    }
}

class FakeDownloads : HttpMessageHandler
{
    private readonly List<string> _requested;

    public FakeDownloads(List<string> requested, Dictionary<string, byte[]> files)
    {
        _requested = requested;
        Files = files;
    }

    public Dictionary<string, byte[]> Files { get; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (_requested) _requested.Add(url);
        return Task.FromResult(Files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    protected override void Dispose(bool disposing) { }
}

class FakePlugin : IVPetLLMPlugin
{
    public string Name { get; init; } = "fake";
    public string Author => string.Empty;
    public string Description => string.Empty;
    public string Parameters { get; init; } = string.Empty;
    public string Examples => string.Empty;
    public bool Enabled { get; set; } = true;
    public string FilePath { get; set; } = string.Empty;
    public void Initialize(VPetLLM.VPetLLM plugin) { }
    public void Unload() { }
}

class FakeActionPlugin : FakePlugin, IActionPlugin
{
    public List<string> Calls { get; } = new();
    public Exception? Failure { get; init; }
    public string Reply { get; init; } = string.Empty;

    // 真插件的 Function 是 async，失败落在 Task 里，不 await 就看不见
    public async Task<string> Function(string arguments)
    {
        Calls.Add(arguments);
        await Task.CompletedTask;
        if (Failure is not null) throw Failure;
        return Reply;
    }
}

/// <summary>Parameters 不含 setting 时就是 RemoteChat 的形态：能调用，但设置只在面板里。</summary>
class FakeTabPlugin : FakeActionPlugin, IPluginTab
{
    public int PanelsCreated { get; private set; }
    public string TabTitle => "假面板";

    public System.Windows.FrameworkElement CreatePanel()
    {
        if (Failure is not null) throw Failure;
        PanelsCreated++;
        return new System.Windows.Controls.TextBlock();
    }
}
