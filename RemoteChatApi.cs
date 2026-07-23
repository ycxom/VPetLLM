using VPetLLM.Core.RemoteChat;

namespace VPetLLM;

public partial class VPetLLM
{
    private readonly SemaphoreSlim _remoteChatLock = new(1, 1);

    /// <summary>
    /// Processes trusted remote text through the same ChatCore, response handler,
    /// action processor and plugin pipeline used by local VPetLLM chat.
    /// </summary>
    public async Task<RemoteChatResponse> SendRemoteChatAsync(
        string text,
        CancellationToken cancellationToken = default)
        => await SendRemoteChatAsync(text, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 与 <see cref="SendRemoteChatAsync(string, CancellationToken)"/> 相同，但允许传入
    /// 一个远端交互应答器，使处理管线中途的插件交互请求（如命令确认）能被推送到远端并等待回执。
    /// </summary>
    public async Task<RemoteChatResponse> SendRemoteChatAsync(
        string text,
        Core.Interaction.IRemoteInteractionResponder? interactionResponder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Remote chat text cannot be empty.", nameof(text));
        if (text.Length > 4000)
            throw new ArgumentOutOfRangeException(nameof(text), "Remote chat text exceeds 4000 characters.");
        if (ChatCore is null)
            throw new InvalidOperationException("ChatCore is not initialized.");

        await _remoteChatLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var collector = new RemoteChatCollector();
        try
        {
            using var scope = RemoteChatSessionContext.Begin(collector, interactionResponder);
            await SendChat($"[RemoteChat User] {text}").ConfigureAwait(false);

            // Response handlers and plugin actions run asynchronously after ChatCore returns.
            // Wait until the local pipeline is idle and no captured event has changed briefly.
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            var earliestCompletion = DateTimeOffset.UtcNow.AddMilliseconds(1500);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                // 存在挂起交互时，处理并未结束——推迟截止时间，等待用户在本地/远端做出决定。
                if (collector.HasPendingInteraction)
                {
                    deadline = DateTimeOffset.UtcNow.AddMinutes(2);
                    continue;
                }
                var processing = TalkBox?.MessageProcessor?.IsProcessing == true;
                var quiet = DateTimeOffset.UtcNow - collector.LastActivity >= TimeSpan.FromMilliseconds(1500);
                if (DateTimeOffset.UtcNow >= earliestCompletion && !processing && quiet)
                    break;
            }

            return collector.Snapshot();
        }
        finally
        {
            _remoteChatLock.Release();
        }
    }

    /// <summary>
    /// 取近期聊天历史（用户/助手正文）供远端 Web UI 展示上下文，类似 Open WebUI 的会话回看。
    /// 剔除系统提示与非用户/助手消息，去掉远端入站前缀并按长度/条数截断。
    /// 该方法只读，不改变上下文。
    /// </summary>
    public IReadOnlyList<RemoteChatHistoryItem> GetRemoteHistory(int maxMessages = 40, int maxCharsPerMessage = 2000)
    {
        var core = ChatCore;
        if (core is null) return Array.Empty<RemoteChatHistoryItem>();

        List<Message> history;
        try { history = core.GetChatHistory(); }
        catch { return Array.Empty<RemoteChatHistoryItem>(); }
        if (history is null) return Array.Empty<RemoteChatHistoryItem>();

        const string remotePrefix = "[RemoteChat User] ";
        var items = new List<RemoteChatHistoryItem>(history.Count);
        foreach (var m in history)
        {
            var role = m.NormalizedRole;
            if (role != "user" && role != "assistant") continue;
            var content = (m.Content ?? "").Trim();
            if (content.Length == 0) continue;
            if (content.StartsWith(remotePrefix, StringComparison.Ordinal))
                content = content[remotePrefix.Length..];
            if (content.Length > maxCharsPerMessage)
                content = content[..maxCharsPerMessage] + "…";
            items.Add(new RemoteChatHistoryItem { Role = role, Content = content, UnixTime = m.UnixTime });
        }

        if (items.Count > maxMessages)
            items = items.GetRange(items.Count - maxMessages, maxMessages);
        return items;
    }
}
