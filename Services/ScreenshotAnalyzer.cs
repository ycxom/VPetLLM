using VPetLLM.Configuration;
using VPetLLM.Core.Engine;

namespace VPetLLM.Services
{
    /// <summary>截图分析的结果形态。</summary>
    public enum ScreenshotAnalysisKind
    {
        /// <summary>不做文字转换：图原样交回调用方，由它送进主模型（原生多模态）。</summary>
        RawImages,

        /// <summary>已经转成文字。</summary>
        Text,

        /// <summary>没能识别。</summary>
        Failed
    }

    /// <summary>一次截图分析的结果。</summary>
    public sealed class ScreenshotAnalysis
    {
        private ScreenshotAnalysis(ScreenshotAnalysisKind kind, string text, string provider, string error)
        {
            Kind = kind;
            Text = text ?? "";
            UsedProvider = provider ?? "";
            ErrorMessage = error ?? "";
        }

        public ScreenshotAnalysisKind Kind { get; }

        /// <summary>识别出的文字（<see cref="ScreenshotAnalysisKind.Text"/> 时有效）。</summary>
        public string Text { get; }

        /// <summary>实际使用的提供商，用于日志和事件回传。</summary>
        public string UsedProvider { get; }

        /// <summary>失败原因。这是给日志和用户看的中性描述，不要直接塞给模型。</summary>
        public string ErrorMessage { get; }

        public bool Success => Kind != ScreenshotAnalysisKind.Failed;

        public static ScreenshotAnalysis RawImages() =>
            new(ScreenshotAnalysisKind.RawImages, "", "", "");

        public static ScreenshotAnalysis FromText(string text, string provider) =>
            new(ScreenshotAnalysisKind.Text, text, provider, "");

        public static ScreenshotAnalysis Failed(string error) =>
            new(ScreenshotAnalysisKind.Failed, "", "", error);

        /// <summary>转成旧的 PreprocessingResult，供还在用它的调用方过渡。</summary>
        public PreprocessingResult ToPreprocessingResult() => Kind switch
        {
            ScreenshotAnalysisKind.Text => PreprocessingResult.CreateSuccess(Text, UsedProvider),
            ScreenshotAnalysisKind.RawImages => PreprocessingResult.CreateSuccess("", "native"),
            _ => PreprocessingResult.CreateFailure(ErrorMessage)
        };
    }

    /// <summary>
    /// 截图「处理类型」的**唯一**分发点。
    ///
    /// 之前这套 <see cref="ScreenshotProcessingMode"/> 的分支在两个地方各写了一遍，
    /// 而且能力并不对等：
    ///
    /// <list type="bullet">
    /// <item>ScreenshotService.RecognizeImageAsync —— 支持多图，但识图不带关注点提示词，
    ///       视觉失败后没有任何回落，没有可用视觉节点时直接报错。</item>
    /// <item>SeeScreenHandler.AnalyzeAsync —— 有完整的回落策略（先判 OCR 是否独立端点，
    ///       是才回落，否则跳过必然失败的重试），识图带关注点，但只支持单图，
    ///       而且一次调用里 new 了两三个 OCREngine。</item>
    /// </list>
    ///
    /// 于是同一个模式在两条入口下表现不同：用户手动截图享受不到回落，
    /// AI 看屏幕享受不到多图。现在两边都走这里。
    /// </summary>
    public sealed class ScreenshotAnalyzer
    {
        private readonly Setting _settings;
        private readonly VPetLLM? _plugin;
        private readonly IPreprocessingMultimodal _preprocessing;

        public ScreenshotAnalyzer(Setting settings, VPetLLM? plugin, IPreprocessingMultimodal? preprocessing = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin;
            _preprocessing = preprocessing ?? new PreprocessingMultimodal(settings, plugin);
        }

        /// <summary>当前生效的处理类型。</summary>
        public ScreenshotProcessingMode Mode =>
            _settings.Screenshot?.ProcessingMode ?? ScreenshotProcessingMode.NativeMultimodal;

        /// <summary>原生多模态：图不转文字，直接送主模型。</summary>
        public bool IsNativeMode => Mode == ScreenshotProcessingMode.NativeMultimodal;

        /// <summary>单图便捷重载。</summary>
        public Task<ScreenshotAnalysis> AnalyzeAsync(byte[] image, string? focus = null)
            => AnalyzeAsync(new[] { image }, focus);

        /// <summary>
        /// 按当前处理类型把截图变成可交给主模型的东西。
        /// </summary>
        /// <param name="images">一张或多张截图。</param>
        /// <param name="focus">关注点，会拼进视觉提示词；OCR 模式下无意义。</param>
        public async Task<ScreenshotAnalysis> AnalyzeAsync(IReadOnlyList<byte[]> images, string? focus = null)
        {
            var valid = (images ?? Array.Empty<byte[]>())
                .Where(i => i is not null && i.Length > 0)
                .ToList();

            if (valid.Count == 0)
                return ScreenshotAnalysis.Failed("没有可分析的图片");

            // 原生多模态：这里什么都不做。图要原样送进主模型，
            // 中间任何一次「转描述」都会让主模型看到的是二手信息。
            if (IsNativeMode)
                return ScreenshotAnalysis.RawImages();

            // OCREngine 惰性构造、且只建一次。
            // 惰性是必须的：前置多模态这条路多数时候根本用不到 OCR，
            // 而 OCREngine 的构造函数在插件实例还没就绪时会直接抛 —— 那不该让整条识图链路崩掉。
            // （以前 SeeScreenHandler 一次调用里能 new 两三个，还全是无条件 new。）
            var ocr = TryCreateOcr();

            if (Mode == ScreenshotProcessingMode.OCRApi)
            {
                if (ocr is null)
                    return ScreenshotAnalysis.Failed("OCR 引擎不可用（插件尚未就绪）");

                return await RecognizeAllWithOcrAsync(valid, ocr);
            }

            // ---- 前置多模态 ----
            if (!_preprocessing.HasAvailableProvider())
            {
                // OCR 只有配了独立端点才算另一条链路；否则它复用的就是这套没配好的视觉节点，
                // 打过去必然同样失败。更糟的是 OCR 的最终兜底会落到主聊天渠道，
                // 那等于绕过用户「不要把图交给主模型」的选择偷偷做了原生多模态。
                if (ocr is not null && ocr.UsesDedicatedEndpoint)
                {
                    Logger.Log("ScreenshotAnalyzer: 前置多模态无可用视觉节点，改用独立 OCR 端点");
                    return await RecognizeAllWithOcrAsync(valid, ocr);
                }

                Logger.Log("ScreenshotAnalyzer: 前置多模态无可用视觉节点，且无独立 OCR 端点，放弃识图");
                return ScreenshotAnalysis.Failed("前置多模态还没有配置可用的视觉渠道");
            }

            var visionPrompt = BuildVisionPrompt(focus);
            var descriptions = new List<string>();
            var provider = "";
            var anySuccess = false;
            var lastError = "";

            for (int i = 0; i < valid.Count; i++)
            {
                var result = await _preprocessing.AnalyzeImageAsync(valid[i], visionPrompt);

                if (result.Success && !string.IsNullOrWhiteSpace(result.ImageDescription))
                {
                    anySuccess = true;
                    provider = result.UsedProvider;
                    descriptions.Add(Label(i, valid.Count, result.ImageDescription.Trim()));
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "视觉模型没有返回内容"
                        : result.ErrorMessage;
                    Logger.Log($"ScreenshotAnalyzer: 第 {i + 1}/{valid.Count} 张识图失败: {lastError}");
                    descriptions.Add(Label(i, valid.Count, $"（识别失败：{lastError}）"));
                }
            }

            if (anySuccess)
                return ScreenshotAnalysis.FromText(string.Join("\n\n", descriptions), provider);

            // 全军覆没才考虑回落 OCR，且同样只在它是独立链路时才值得试。
            if (ocr is not null && ocr.UsesDedicatedEndpoint)
            {
                Logger.Log($"ScreenshotAnalyzer: 视觉全部失败（{lastError}），回落独立 OCR 端点");
                var fallback = await RecognizeAllWithOcrAsync(valid, ocr);
                if (fallback.Success) return fallback;
                Logger.Log("ScreenshotAnalyzer: 独立 OCR 端点同样没取到文字");
            }
            else
            {
                Logger.Log($"ScreenshotAnalyzer: 视觉失败（{lastError}）；OCR 与视觉共用同一批节点，跳过必然失败的重试");
            }

            return ScreenshotAnalysis.Failed(lastError);
        }

        /// <summary>
        /// 构造 OCR 引擎。插件实例没就绪时返回 null 而不是抛 —— 调用方据此判定「OCR 这条路不可用」，
        /// 该失败失败、该跳过跳过，但不会把整条识图链路带崩。
        /// </summary>
        private OCREngine? TryCreateOcr()
        {
            if (_plugin is null)
            {
                Logger.Log("ScreenshotAnalyzer: 插件实例尚未就绪，OCR 不可用");
                return null;
            }

            try
            {
                return new OCREngine(_settings, _plugin);
            }
            catch (Exception ex)
            {
                Logger.Log($"ScreenshotAnalyzer: 构造 OCR 引擎失败: {ex.Message}");
                return null;
            }
        }

        private async Task<ScreenshotAnalysis> RecognizeAllWithOcrAsync(IReadOnlyList<byte[]> images, OCREngine ocr)
        {
            var parts = new List<string>();
            var any = false;
            var lastError = "屏幕上没有识别到文字";

            for (int i = 0; i < images.Count; i++)
            {
                try
                {
                    var text = await ocr.RecognizeText(images[i]);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        any = true;
                        parts.Add(Label(i, images.Count, text.Trim()));
                    }
                    else
                    {
                        parts.Add(Label(i, images.Count, "（没有识别到文字）"));
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"OCR 识别失败: {ex.Message}";
                    Logger.Log($"ScreenshotAnalyzer: 第 {i + 1}/{images.Count} 张 OCR 失败: {ex.Message}");
                    parts.Add(Label(i, images.Count, $"（识别失败：{ex.Message}）"));
                }
            }

            return any
                ? ScreenshotAnalysis.FromText(string.Join("\n\n", parts), "OCR")
                : ScreenshotAnalysis.Failed(lastError);
        }

        /// <summary>单图时不加编号前缀，保持和以前一致的干净输出。</summary>
        private static string Label(int index, int total, string body)
            => total == 1 ? body : $"【图片 {index + 1}/{total}】{body}";

        /// <summary>
        /// 带上关注点，让视觉模型有的放矢，而不是泛泛描述整屏。
        /// 关注点为空时就是配置里的通用提示词。
        /// </summary>
        private string BuildVisionPrompt(string? focus)
        {
            if (string.IsNullOrWhiteSpace(focus))
                return "";

            var lang = _settings.PromptLanguage ?? "zh";
            var basePrompt = PromptHelper.Get("SeeScreen_Vision_Prompt", lang);

            if (string.IsNullOrWhiteSpace(basePrompt))
            {
                basePrompt = lang == "zh"
                    ? "请客观描述这张屏幕截图的内容，包括正在使用的程序、可见的文字和画面重点。"
                    : "Objectively describe this screenshot: the app in use, visible text, and the main focus.";
            }

            var suffix = lang == "zh" ? $"\n特别关注：{focus}" : $"\nPay special attention to: {focus}";
            return basePrompt + suffix;
        }
    }
}
