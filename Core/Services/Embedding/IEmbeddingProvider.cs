namespace VPetLLM.Core.Services.Embedding
{
    /// <summary>
    /// 文本向量化后端。实现应当是无状态、可并发调用的。
    /// </summary>
    public interface IEmbeddingProvider
    {
        /// <summary>
        /// 标识「这批向量是谁产出的」。写进向量缓存，模型或端点一变即自动失效，
        /// 避免不同模型的向量混在同一个空间里比余弦。
        /// </summary>
        string ModelKey { get; }

        /// <summary>
        /// 批量嵌入。返回列表与 <paramref name="texts"/> 一一对应；
        /// 单条失败时对应位置为 null，不影响其余条目。
        /// 整体失败（网络、鉴权）应抛异常，由调用方降级。
        /// </summary>
        Task<IReadOnlyList<float[]?>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
    }
}
