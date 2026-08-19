using VPet_Simulator.Windows.Interface;
using VPetLLM.Services;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// 「看屏幕」工具：AI 主动截取屏幕并读取内容。
    /// 命令格式：&lt;|see_screen_begin|&gt; 你想看什么 &lt;|see_screen_end|&gt;
    /// 可选前缀 all: 表示抓取全部显示器。
    ///
    /// 与用户手动截图（快捷键 → 选区窗口）的区别：本路径无 UI、无需用户操作，
    /// 结果以文本形式经 ResultAggregator 回灌给 AI。
    /// </summary>
    public class SeeScreenHandler : IActionHandler
    {
        /// <summary>等待用户圈选的上限；超时按取消处理，避免 AI 一直挂着</summary>
        private const int CaptureTimeoutSeconds = 45;

        private readonly Setting _settings;
        private ScreenshotAnalyzer? _analyzer;

        public string Keyword => "see_screen";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        /// <summary>
        /// 能力未开启时返回空串——SystemMessageProvider 会把空描述一并拼进提示词，
        /// 但空串不会让 AI 以为自己有这个能力，避免它调用一个必然失败的工具。
        /// </summary>
        public string Description => IsAvailable
            ? PromptHelper.Get("Handler_SeeScreen_Description", VPetLLM.Instance?.Settings?.PromptLanguage ?? "zh")
            : "";

        /// <summary>
        /// 只要截图功能开着就算可用：具体走视觉模型还是 OCR 在执行时再判断
        /// </summary>
        public bool IsAvailable => _settings?.Screenshot?.IsEnabled == true;

        public SeeScreenHandler(Setting settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private ScreenshotAnalyzer GetAnalyzer()
        {
            // 延迟构造：插件实例在 ActionProcessor 注册期可能尚未就绪
            return _analyzer ??= new ScreenshotAnalyzer(_settings, VPetLLM.Instance);
        }

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            if (!IsAvailable)
            {
                Logger.Log("SeeScreenHandler: 截图能力未启用，忽略命令");
                return;
            }

            if (InterruptManager.IsInterrupted)
            {
                Logger.Log("SeeScreenHandler: 已中断，跳过截屏");
                return;
            }

            // 截屏 + 视觉推理都很贵，限流防止 AI 自我循环
            if (!RateLimiter.TryAcquire("see_screen", 3, TimeSpan.FromMinutes(1)))
            {
                Logger.Log("SeeScreenHandler: 触发限流，拒绝本次截屏");
                ResultAggregator.Enqueue("[屏幕内容] 你刚刚已经看过屏幕了，请先根据已有信息回答，不要连续截屏。");
                return;
            }

            var question = ParseCommand(value);

            try
            {
                var imageData = await CaptureAsync(question);

                if (imageData is null || imageData.Length == 0)
                {
                    // 取消 / 超时 / 抓屏失败对 AI 是同一种结果：这次看不到，别重试
                    ResultAggregator.Enqueue("[屏幕内容] 这次没能看到屏幕（用户取消了截图或未在时限内圈选）。不要重复请求，换个方式回应用户。");
                    return;
                }

                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("SeeScreenHandler: 截屏后检测到中断，放弃分析");
                    return;
                }

                // 处理类型的分发统一交给 ScreenshotAnalyzer —— 与用户手动截图同一份实现。
                var analysis = await GetAnalyzer().AnalyzeAsync(imageData, question);

                // 原生多模态：不做任何描述，把截图原样交给回灌，
                // 由 ResultAggregator 在本回合结束时经 TalkBox.SendChatWithImages 送出——
                // 与手动截图完全同一条链路，主模型看到的是真实像素而不是自己写的描述。
                if (analysis.Kind == ScreenshotAnalysisKind.RawImages)
                {
                    var caption = string.IsNullOrWhiteSpace(question)
                        ? "[屏幕内容] 这是用户当前屏幕的截图，请据此回答。"
                        : $"[屏幕内容] 这是用户当前屏幕的截图，关注点：{question}";

                    Logger.Log("SeeScreenHandler: 原生多模态，截图直接随回灌送入主模型（不预先描述）");
                    ResultAggregator.Enqueue(caption, new[] { imageData });
                    return;
                }

                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("SeeScreenHandler: 分析完成时已中断，不回灌结果");
                    return;
                }

                ResultAggregator.Enqueue(DescribeForAi(analysis));
            }
            catch (Exception ex)
            {
                Logger.Log($"SeeScreenHandler: 执行失败: {ex.Message}");
                ResultAggregator.Enqueue($"[屏幕内容] 查看屏幕时出错：{ex.Message}");
            }
        }

        /// <summary>
        /// 取画面。默认弹选区窗口让用户圈定范围并确认——AI 主动截屏若直接抓全屏，
        /// 会把密码管理器、私信、后台窗口等一并送进模型，这是隐私事故。
        /// 只有用户在设置里显式关掉「显示截图窗口」，才退化为静默主屏截图。
        /// </summary>
        private async Task<byte[]?> CaptureAsync(string question)
        {
            if (_settings.Screenshot?.ShowCaptureWindow != false)
            {
                var reason = string.IsNullOrWhiteSpace(question)
                    ? $"{VPetLLM.Instance?.Settings?.AiName ?? "桌宠"} 想看看你的屏幕，请圈选允许它查看的区域"
                    : $"{VPetLLM.Instance?.Settings?.AiName ?? "桌宠"} 想看：{question}　请圈选允许它查看的区域";

                Logger.Log($"SeeScreenHandler: AI 请求看屏幕（用户圈选模式），关注点=\"{question}\"");
                var plugin = VPetLLM.Instance;
                if (plugin is null) return null;

                return await plugin.RequestScreenshotFromUserAsync(reason, CaptureTimeoutSeconds);
            }

            Logger.Log($"SeeScreenHandler: AI 请求看屏幕（静默主屏模式，用户已关闭截图窗口），关注点=\"{question}\"");
            return ScreenCapture.CapturePrimaryScreen();
        }

        /// <summary>
        /// 把分析结果翻译成给模型看的话。
        ///
        /// 这里只做「AI 协议层」的措辞：原始报错（鉴权失败、状态码之类）一律只进日志——
        /// 那是给用户排查用的，塞进对话只会让模型对着一段它无能为力的错误信息瞎猜。
        /// 至于该走视觉还是 OCR、失败要不要回落，全都在 ScreenshotAnalyzer 里，这里不重复判断。
        /// </summary>
        private static string DescribeForAi(ScreenshotAnalysis analysis)
        {
            if (analysis.Kind == ScreenshotAnalysisKind.Text)
            {
                var tag = analysis.UsedProvider == "OCR" ? "[屏幕内容 - 文字识别]" : "[屏幕内容]";
                return $"{tag}\n{analysis.Text.Trim()}\n[/屏幕内容]";
            }

            var error = analysis.ErrorMessage ?? "";
            Logger.Log($"SeeScreenHandler: 识图失败，原始错误: {error}");

            if (error.Contains("没有识别到文字"))
                return "[屏幕内容] 屏幕上没有识别到文字。";

            if (error.Contains("视觉渠道"))
                return "[屏幕内容] 这次没能看清屏幕（前置多模态还没有配置可用的视觉渠道）。如实告诉用户你暂时看不了屏幕，" +
                       "建议他打开设置的「截图与模型视觉」，把多模态提供商配置好，不要重复请求。";

            return "[屏幕内容] 这次没能看清屏幕（识别服务不可用）。如实告诉用户你暂时看不了屏幕，建议他检查截图/视觉相关设置，不要复述技术错误，也不要重复请求。";
        }

        /// <summary>
        /// 参数就是 AI 的关注点。历史上支持过 all: 前缀指定抓全屏，
        /// 现在范围一律由用户在选区窗口里决定，这里只做兼容性剥离。
        /// </summary>
        private static string ParseCommand(string? value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) return "";

            foreach (var prefix in new[] { "all:", "all：", "全部:", "全部：" })
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Substring(prefix.Length).Trim();
                }
            }

            if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "全部")
            {
                return "";
            }

            return text.Trim('"');
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Execute("", mainWindow);
        public int GetAnimationDuration(string animationName) => 0;
    }
}
