using System.Drawing;

namespace VPetLLM.Infrastructure.Events
{
    /// <summary>
    /// 截图捕获事件
    /// </summary>
    public class ScreenshotCapturedEvent
    {
        /// <summary>
        /// 截图图像
        /// </summary>
        public Bitmap Screenshot { get; set; }

        /// <summary>
        /// 捕获时间
        /// </summary>
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 是否需要预处理
        /// </summary>
        public bool RequiresPreprocessing { get; set; }

        /// <summary>
        /// 截图来源
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 附加元数据
        /// </summary>
        public object Metadata { get; set; }
    }

    /// <summary>
    /// 截图预处理完成事件
    /// </summary>
    public class ScreenshotPreprocessedEvent
    {
        /// <summary>
        /// 处理后的图像
        /// </summary>
        public Bitmap ProcessedImage { get; set; }

        /// <summary>
        /// 原始图像
        /// </summary>
        public Bitmap OriginalImage { get; set; }

        /// <summary>
        /// 处理时间
        /// </summary>
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 处理操作列表
        /// </summary>
        public string[] Operations { get; set; }
    }

    /// <summary>
    /// OCR完成事件
    /// </summary>
    public class OCRCompletedEvent
    {
        /// <summary>
        /// 识别的文本
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 源图像
        /// </summary>
        public Bitmap SourceImage { get; set; }

        /// <summary>
        /// 识别时间
        /// </summary>
        public DateTime CompletedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 置信度
        /// </summary>
        public double Confidence { get; set; }
    }

    /// <summary>
    /// 截图错误事件
    /// </summary>
    public class ScreenshotErrorEvent
    {
        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// 发生时间
        /// </summary>
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 错误阶段
        /// </summary>
        public string Stage { get; set; }
    }
}
