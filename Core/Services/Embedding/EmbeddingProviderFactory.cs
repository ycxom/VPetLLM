using System.Net.Http;

namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// 按协议构造 <see cref="IEmbeddingProvider"/>。Free 来源和自配来源共用此工厂，
    /// 只是参数来路不同（云配置 vs 用户设置）。
    /// </summary>
    public static class EmbeddingProviderFactory
    {
        public static IEmbeddingProvider Create(
            Setting.EmbeddingProtocol protocol,
            HttpClient http,
            string baseUrl,
            string? apiKey,
            string model)
        {
            return protocol switch
            {
                Setting.EmbeddingProtocol.Ollama => new OllamaEmbeddingProvider(http, baseUrl, apiKey, model),
                Setting.EmbeddingProtocol.Gemini => new GeminiEmbeddingProvider(http, baseUrl, apiKey, model),
                _ => new OpenAiCompatibleEmbeddingProvider(http, baseUrl, apiKey, model),
            };
        }
    }
}
