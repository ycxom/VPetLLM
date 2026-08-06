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
        private IPreprocessingMultimodal? _preprocessing;

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

        private IPreprocessingMultimodal GetPreprocessing()
        {
            // 延迟构造：插件实例在 ActionProcessor 注册期可能尚未就绪
            return _preprocessing ??= new PreprocessingMultimodal(_settings, VPetLLM.Instance);
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

                var mode = _settings.Screenshot?.ProcessingMode ?? ScreenshotProcessingMode.NativeMultimodal;

                // 原生多模态：不做任何描述，把截图原样交给回灌，
                // 由 ResultAggregator 在本回合结束时经 TalkBox.SendChatWithImages 送出——
                // 与手动截图完全同一条链路，主模型看到的是真实像素而不是自己写的描述。
                if (mode == ScreenshotProcessingMode.NativeMultimodal)
                {
                    var caption = string.IsNullOrWhiteSpace(question)
                        ? "[屏幕内容] 这是用户当前屏幕的截图，请据此回答。"
                        : $"[屏幕内容] 这是用户当前屏幕的截图，关注点：{question}";

                    Logger.Log("SeeScreenHandler: 原生多模态，截图直接随回灌送入主模型（不预先描述）");
                    ResultAggregator.Enqueue(caption, new[] { imageData });
                    return;
                }

                // 前置多模态 / OCR：先转成文字再回灌
                var text = await AnalyzeAsync(imageData, question);
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("SeeScreenHandler: 分析完成时已中断，不回灌结果");
                    return;
                }

                ResultAggregator.Enqueue(text);
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
        /// 把画面变成文字。AI 侧统一拿文本，不受主模型是否支持视觉影响。
        /// </summary>
        private async Task<string> AnalyzeAsync(byte[] imageData, string question)
        {
            var mode = _settings.Screenshot?.ProcessingMode ?? ScreenshotProcessingMode.NativeMultimodal;
            var preprocessing = GetPreprocessing();
            var visionPrompt = BuildVisionPrompt(question);

            // OCR 模式：纯文字识别
            if (mode == ScreenshotProcessingMode.OCRApi)
            {
                return await AnalyzeWithOcrAsync(imageData);
            }

            // 走到这里只剩前置多模态：原生模式在 Execute 里就已经把图直接交给回灌了，
            // 根本不进本方法。
            if (!preprocessing.HasAvailableProvider())
            {
                Logger.Log("SeeScreenHandler: 前置多模态未配置可用视觉节点，改用 OCR");
                return await AnalyzeWithOcrAsync(imageData);
            }

            Logger.Log("SeeScreenHandler: 前置多模态，使用配置的视觉节点识图");
            var result = await preprocessing.AnalyzeImageAsync(imageData, visionPrompt);

            if (result.Success && !string.IsNullOrWhiteSpace(result.ImageDescription))
            {
                return $"[屏幕内容]\n{result.ImageDescription.Trim()}\n[/屏幕内容]";
            }

            var visionError = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "视觉模型没有返回内容" : result.ErrorMessage;

            // 视觉不可用时回落 OCR——但只在 OCR 确实是另一条链路时才值得试。
            // OCR 未配独立端点时复用的就是刚失败的这批节点，凭据和地址完全一样，
            // 再打一次必然同样失败，只是多耗一次往返和一次超时等待。
            var ocrEngine = new OCREngine(_settings, VPetLLM.Instance);
            if (ocrEngine.UsesDedicatedEndpoint)
            {
                Logger.Log($"SeeScreenHandler: 视觉分析失败（{visionError}），回落独立 OCR 端点");

                var fallback = await AnalyzeWithOcrAsync(imageData);
                if (!fallback.StartsWith("[屏幕内容] "))
                {
                    return fallback;
                }
                Logger.Log("SeeScreenHandler: 独立 OCR 端点同样未取到文字");
            }
            else
            {
                Logger.Log($"SeeScreenHandler: 视觉分析失败（{visionError}）；" +
                           "OCR 与视觉共用同一批节点，跳过必然失败的重试");
            }

            // OCR 也没成：给 AI 一句干净的说明即可。
            // 原始报文（鉴权失败、状态码之类）只写日志——那是给用户排查用的，
            // 塞进对话只会让模型对着一段它无能为力的错误信息瞎猜。
            Logger.Log($"SeeScreenHandler: OCR 回落同样失败，视觉侧原始错误: {visionError}");
            return "[屏幕内容] 这次没能看清屏幕（识别服务不可用）。如实告诉用户你暂时看不了屏幕，建议他检查截图/视觉相关设置，不要复述技术错误，也不要重复请求。";
        }

        /// <summary>
        /// 用 OCR 读屏幕文字
        /// </summary>
        private async Task<string> AnalyzeWithOcrAsync(byte[] imageData)
        {
            try
            {
                Logger.Log("SeeScreenHandler: 使用 OCR 读取屏幕文字");
                var ocrEngine = new OCREngine(_settings, VPetLLM.Instance);
                var ocrText = await ocrEngine.RecognizeText(imageData);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    return "[屏幕内容] 屏幕上没有识别到文字。";
                }

                return $"[屏幕内容 - 文字识别]\n{ocrText.Trim()}\n[/屏幕内容]";
            }
            catch (Exception ex)
            {
                Logger.Log($"SeeScreenHandler: OCR 失败: {ex.Message}");
                return "[屏幕内容] OCR 识别失败。";
            }
        }

        /// <summary>
        /// 带上 AI 的关注点，让视觉模型有的放矢，而不是泛泛描述整屏
        /// </summary>
        private string BuildVisionPrompt(string question)
        {
            var lang = _settings.PromptLanguage ?? "zh";
            var basePrompt = PromptHelper.Get("SeeScreen_Vision_Prompt", lang);

            if (string.IsNullOrWhiteSpace(basePrompt))
            {
                basePrompt = lang == "zh"
                    ? "请客观描述这张屏幕截图的内容，包括正在使用的程序、可见的文字和画面重点。"
                    : "Objectively describe this screenshot: the app in use, visible text, and the main focus.";
            }

            if (string.IsNullOrWhiteSpace(question))
                return basePrompt;

            var focus = lang == "zh"
                ? $"\n特别关注：{question}"
                : $"\nPay special attention to: {question}";

            return basePrompt + focus;
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
