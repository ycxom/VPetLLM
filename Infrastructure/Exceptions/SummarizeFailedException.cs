namespace VPetLLM.Infrastructure.Exceptions;

/// <summary>
/// Thrown when <c>IChatCore.Summarize</c> cannot produce a summary.
/// </summary>
/// <remarks>
/// Summarize used to report failures by returning the error text as if it were
/// the summary. Callers had no way to tell the two apart, so OverflowManager
/// committed error strings as real summaries: the rolling summary was replaced,
/// the checkpoint advanced past messages that were never summarized, and the
/// poisoned text was fed back as "previous summary context" on the next round.
/// Failures must therefore surface as exceptions — every caller already has a
/// catch block with a correct fallback.
///
/// <see cref="Exception.Message"/> carries the user-facing text (friendly or raw
/// depending on debug mode) so callers that display it keep the old wording.
/// </remarks>
public class SummarizeFailedException : Exception
{
    public SummarizeFailedException(string message) : base(message)
    {
    }

    public SummarizeFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 服务端回了非成功状态码时的那个码；连不上、超时、没有可用渠道等没拿到
    /// HTTP 响应的失败为 null。OverflowManager 靠它区分「这一片太大」和
    /// 「服务出了问题」——前者值得切小重试，后者切多小都一样失败。
    /// </summary>
    public System.Net.HttpStatusCode? StatusCode { get; init; }
}
