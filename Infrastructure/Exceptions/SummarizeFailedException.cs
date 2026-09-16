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
}
