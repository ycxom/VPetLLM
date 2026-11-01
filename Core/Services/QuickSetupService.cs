namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 简易设置和新手引导共用的规则。只改 <see cref="Setting"/>，不碰窗口和宿主，
    /// 回归检查可以直接钉住。保存时的副作用见 <c>VPetLLM.ApplyQuickSettings</c>。
    /// </summary>
    public static class QuickSetupService
    {
        /// <summary>
        /// 渠道是否填到了能发请求的程度。不探测服务是否真的活着：这里只用来判断要不要打扰用户。
        /// </summary>
        public static bool IsChannelConfigured(Setting.ChannelNodeBase node) => node switch
        {
            Setting.FreeNodeSetting => true,
            // 本地转发服务常常不要密钥，改过地址也算配置过
            Setting.OpenAINodeSetting o => HasText(o.ApiKey)
                                           || NormalizeUrl(o.Url) != NormalizeUrl(new Setting.OpenAINodeSetting().Url),
            Setting.GeminiNodeSetting g => HasText(g.ApiKey),
            // 全新安装会迁移出一个没有模型的 Ollama 节点，发出去必失败
            _ => HasText(node.Model)
        };

        /// <summary>
        /// 是否自动弹新手引导。不看"是不是新装"，看"现在能不能聊天"：
        /// 老用户配好了渠道，升级后读到 <see cref="Setting.OnboardingCompleted"/> 的默认值也不会被打扰。
        /// </summary>
        public static bool ShouldShowOnboarding(Setting settings)
            => !settings.OnboardingCompleted
               && !settings.EnumerateChannels().Any(c => c.Enabled && IsChannelConfigured(c));

        /// <summary>
        /// 引导里默认选中哪个设置界面。首次用简易界面；手动重开时保持现状，
        /// 否则极客界面的用户看一眼引导再关掉，下次打开设置就被换了界面。
        /// </summary>
        public static bool PreferSimpleSettings(Setting settings, bool firstRun)
            => firstRun || settings.UseSimpleSettings;

        /// <summary>和路由选路同一顺序取主渠道；没有启用的渠道时返回 null。</summary>
        public static Setting.ChannelNodeBase? GetPrimaryChannel(Setting settings)
            => settings.EnumerateChannels()
                .Where(c => c.Enabled)
                .OrderByDescending(c => c.Priority)
                .ThenByDescending(c => c.Weight)
                .ThenBy(c => c.Id)
                .FirstOrDefault();

        /// <summary>同类型、同地址的现有渠道。简易模式复用它，免得每保存一次就多一条渠道。</summary>
        public static Setting.ChannelNodeBase? FindExisting(Setting settings, Setting.ChannelKind kind, string? url)
            => settings.EnumerateChannels().FirstOrDefault(c =>
                c.Kind == kind && NormalizeUrl(c.Endpoint) == NormalizeUrl(url));

        /// <summary>
        /// 组装一个待测试、待保存的渠道副本，不改动设置。
        /// 密钥留空表示沿用现有渠道的密钥，界面不回显已保存的密钥。
        /// </summary>
        public static Setting.ChannelNodeBase BuildDraft(
            Setting settings, Setting.ChannelKind kind, string? name, string? url, string? apiKey, string? model)
        {
            var existing = FindExisting(settings, kind, url);
            var draft = existing is not null ? Setting.CloneChannel(existing) : Setting.CreateChannel(kind);

            if (HasText(name) && existing is null)
                draft.Name = name!;
            if (HasText(model))
                draft.Model = model!.Trim();

            switch (draft)
            {
                case Setting.OpenAINodeSetting o:
                    o.Url = url!.Trim();
                    if (HasText(apiKey)) o.ApiKey = apiKey!.Trim();
                    break;
                case Setting.OllamaNodeSetting o:
                    o.Url = url!.Trim();
                    break;
                case Setting.LMStudioNodeSetting l:
                    l.Url = url!.Trim();
                    break;
            }
            return draft;
        }

        /// <summary>
        /// 让选中的服务成为主渠道。其它渠道一条不删、也不停用：
        /// 用户可能在完整设置里配过备用渠道，这里只把选中的排到最前面。
        /// </summary>
        public static Setting.ChannelNodeBase ApplyChannel(Setting settings, Setting.ChannelNodeBase draft)
        {
            var target = FindExisting(settings, draft.Kind, draft.Endpoint);
            if (target is null)
            {
                settings.AddChannel(draft);
                target = draft;
            }
            else
            {
                Setting.CopyChannelInto(draft, target);
            }
            target.Enabled = true;

            var others = settings.EnumerateChannels()
                .Where(c => c.Enabled && !ReferenceEquals(c, target))
                .Select(c => c.Priority)
                .ToList();
            if (others.Count > 0 && others.Max() >= target.Priority)
                target.Priority = others.Max() + 1;

            settings.SyncFreeContainer();
            return target;
        }

        /// <summary>
        /// 语音开关。没配过服务时用免费服务；配过的保留原来的服务，简易模式只管开和关。
        /// </summary>
        public static void ApplyVoice(Setting settings, bool readAloud, bool voiceInput)
        {
            if (readAloud && settings.TTS.Provider == "URL"
                          && settings.TTS.URL.BaseUrl == new Setting.URLTTSSetting().BaseUrl)
            {
                settings.TTS.Provider = "Free";
            }
            settings.TTS.IsEnabled = readAloud;

            if (voiceInput && !IsAsrProviderConfigured(settings.ASR))
                settings.ASR.Provider = "Free";
            settings.ASR.IsEnabled = voiceInput;
        }

        private static bool IsAsrProviderConfigured(Setting.ASRSetting asr) => asr.Provider switch
        {
            "OpenAI" => HasText(asr.OpenAI.ApiKey),
            "Soniox" => HasText(asr.Soniox.ApiKey),
            _ => true
        };

        private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

        private static string NormalizeUrl(string? url) => (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
    }
}
