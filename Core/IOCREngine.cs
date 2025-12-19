using System.Threading.Tasks;

namespace VPetLLM.Core
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
    }
}
