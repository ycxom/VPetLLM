using System;
using System.Threading.Tasks;

namespace VPetLLM.Services
{
    /// <summary>
    /// 截图状态
    /// </summary>
    public enum ScreenshotState
    {
        /// <summary>
        /// 空闲状态
        /// </summary>
        Idle,
        
        /// <summary>
        /// 正在截图
        /// </summary>
        Capturing,
        
        /// <summary>
        /// 正在处理
        /// </summary>
        Processing
    }

    /// <summary>
    /// 截图完成事件参数
    /// </summary>
    public class ScreenshotCapturedEventArgs : EventArgs
    {
        /// <summary>
        /// 图像数据
        /// </summary>
        public byte[] ImageData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 图像宽度
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// 图像高度
        /// </summary>
        public int Height { get; set; }
    }

    /// <summary>
    /// 前置多模态处理完成事件参数
    /// </summary>
    public class PreprocessingCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 组合后的消息（图片描述 + 用户问题）
        /// </summary>
        public string CombinedMessage { get; set; } = "";

        /// <summary>
        /// 图片描述
        /// </summary>
        public string ImageDescription { get; set; } = "";

        /// <summary>
        /// 使用的提供商
        /// </summary>
        public string UsedProvider { get; set; } = "";

        /// <summary>
        /// 错误消息（失败时）
        /// </summary>
        public string ErrorMessage { get; set; } = "";
    }

    /// <summary>
    /// 截图服务接口
    /// </summary>
    public interface IScreenshotService : IDisposable
    {
        /// <summary>
        /// 当前状态
        /// </summary>
        ScreenshotState CurrentState { get; }

        /// <summary>
        /// 当前图像数据
        /// </summary>
        byte[]? CurrentImage { get; }

        /// <summary>
        /// 截图完成事件
        /// </summary>
        event EventHandler<ScreenshotCapturedEventArgs>? ScreenshotCaptured;

        /// <summary>
        /// OCR 完成事件
        /// </summary>
        event EventHandler<string>? OCRCompleted;

        /// <summary>
        /// 状态变更事件
        /// </summary>
        event EventHandler<ScreenshotState>? StateChanged;

        /// <summary>
        /// 错误发生事件
        /// </summary>
        event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// 初始化快捷键
        /// </summary>
        void InitializeHotkey();

        /// <summary>
        /// 更新快捷键
        /// </summary>
        void UpdateHotkey();

        /// <summary>
        /// 开始截图
        /// </summary>
        void StartCapture();

        /// <summary>
        /// 取消截图
        /// </summary>
        void CancelCapture();

        /// <summary>
        /// 处理截图
        /// </summary>
        /// <param name="imageData">图像数据</param>
        void ProcessScreenshot(byte[] imageData);

        /// <summary>
        /// 清除当前图像
        /// </summary>
        void ClearCurrentImage();

        /// <summary>
        /// 执行 OCR
        /// </summary>
        /// <param name="imageData">图像数据</param>
        /// <returns>识别的文本</returns>
        Task<string> PerformOCR(byte[] imageData);
    }
}
