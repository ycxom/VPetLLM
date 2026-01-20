using System.Net.Http;

namespace VPetLLM.Core.Providers.TTS.GPTSoVITS
{
    /// <summary>
    /// GPT-SoVITS API 模式策略接口
    /// 用于分离不同 API 模式的实现逻辑
    /// </summary>
    internal interface IGPTSoVITSApiStrategy
    {
        /// <summary>
        /// 获取 API 端点路径
        /// </summary>
        /// <param name="baseUrl">基础 URL</param>
        /// <returns>完整的端点 URL</returns>
        string GetEndpoint(string baseUrl);

        /// <summary>
        /// 构建请求体对象
        /// </summary>
        /// <param name="text">要合成的文本</param>
        /// <param name="settings">GPT-SoVITS 设置</param>
        /// <returns>请求体对象（将被序列化为 JSON）</returns>
        object BuildRequestBody(string text, Setting.GPTSoVITSTTSSetting settings);

        /// <summary>
        /// 解析 HTTP 响应并返回音频数据
        /// </summary>
        /// <param name="response">HTTP 响应</param>
        /// <param name="httpClient">HTTP 客户端（用于可能的二次请求）</param>
        /// <returns>音频字节数组</returns>
        Task<byte[]> ParseResponseAsync(HttpResponseMessage response, HttpClient httpClient);

        /// <summary>
        /// 验证设置是否满足当前模式的要求
        /// </summary>
        /// <param name="settings">GPT-SoVITS 设置</param>
        /// <exception cref="System.ArgumentException">当设置不满足要求时抛出</exception>
        void ValidateSettings(Setting.GPTSoVITSTTSSetting settings);

        /// <summary>
        /// 获取当前模式的名称（用于日志）
        /// </summary>
        string ModeName { get; }
    }
}
