namespace VPetLLM.Core.Abstractions.Interfaces
{
    /// <summary>
    /// OCR 引擎接口
    /// </summary>
    public interface IOCREngine
    {
        /// <summary>
        /// 识别图像中的文字
        /// </summary>
        /// <param name="imageData">图像数据</param>
        /// <returns>识别的文本</returns>
        Task<string> RecognizeText(byte[] imageData);

        /// <summary>
        /// OCR 是否走独立端点。为 false 时它复用截图视觉的节点配置，
        /// 与视觉调用共享端点和凭据，因此不能用作视觉失败后的兜底。
        /// </summary>
        bool UsesDedicatedEndpoint { get; }
    }
}
