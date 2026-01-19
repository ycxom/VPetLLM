using VPetLLM.Configuration;

namespace VPetLLM.Services
{
    /// <summary>
    /// 前置多模态处理结果
    /// </summary>
    public class PreprocessingResult
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 图片描述（成功时返回）
        /// </summary>
        public string ImageDescription { get; set; } = "";

        /// <summary>
        /// 错误消息（失败时返回）
        /// </summary>
        public string ErrorMessage { get; set; } = "";

        /// <summary>
        /// 实际使用的提供商
        /// </summary>
        public string UsedProvider { get; set; } = "";

        /// <summary>
        /// 创建成功结果
        /// </summary>
        public static PreprocessingResult CreateSuccess(string description, string provider)
        {
            return new PreprocessingResult
            {
                Success = true,
                ImageDescription = description,
                UsedProvider = provider
            };
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        public static PreprocessingResult CreateFailure(string errorMessage)
        {
            return new PreprocessingResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    /// <summary>
    /// 前置多模态处理接口
    /// </summary>
    public interface IPreprocessingMultimodal
    {
        /// <summary>
        /// 分析图片并生成文本描述
        /// </summary>
        /// <param name="imageData">图片数据</param>
        /// <param name="customPrompt">自定义提示词（可选，为空时使用配置的提示词）</param>
        /// <returns>处理结果</returns>
        Task<PreprocessingResult> AnalyzeImageAsync(byte[] imageData, string? customPrompt = null);

        /// <summary>
        /// 获取所有可用的视觉节点
        /// </summary>
        /// <returns>视觉节点列表</returns>
        List<VisionNodeIdentifier> GetAvailableVisionNodes();

        /// <summary>
        /// 验证选中的节点是否仍然有效
        /// </summary>
        /// <param name="nodes">节点列表</param>
        /// <returns>有效的节点列表</returns>
        List<VisionNodeIdentifier> ValidateSelectedNodes(List<VisionNodeIdentifier> nodes);

        /// <summary>
        /// 检查是否有可用的多模态提供商
        /// </summary>
        /// <returns>是否有可用提供商</returns>
        bool HasAvailableProvider();
    }
}
