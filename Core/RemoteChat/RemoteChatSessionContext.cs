using VPetLLM.Core.Interaction;

namespace VPetLLM.Core.RemoteChat;

public sealed class RemoteChatEvent
{
    public string Kind { get; init; } = "";
    public string? Content { get; init; }
    public string? PluginName { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public bool? Success { get; init; }
}

public sealed class RemoteChatResponse
{
    public IReadOnlyList<RemoteChatEvent> Events { get; init; } = Array.Empty<RemoteChatEvent>();
}

internal sealed class RemoteChatCollector
{
    private readonly object _sync = new();
    private readonly List<RemoteChatEvent> _events = new();
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private int _pendingInteractions;

    internal DateTimeOffset LastActivity
    {
        get { lock (_sync) return _lastActivity; }
    }

    /// <summary>是否存在正在等待用户应答的交互请求；有则处理管线不应被判定为空闲。</summary>
    internal bool HasPendingInteraction => Volatile.Read(ref _pendingInteractions) > 0;

    internal void BeginInteraction()
    {
        Interlocked.Increment(ref _pendingInteractions);
        lock (_sync) _lastActivity = DateTimeOffset.UtcNow;
    }

    internal void EndInteraction()
    {
        Interlocked.Decrement(ref _pendingInteractions);
        lock (_sync) _lastActivity = DateTimeOffset.UtcNow;
    }

    internal void AddAssistant(string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        lock (_sync)
        {
            if (_events.Count > 0 && _events[^1].Kind == "assistant")
            {
                var previous = _events[^1];
                _events[^1] = new RemoteChatEvent { Kind = "assistant", Content = (previous.Content ?? "") + content };
            }
            else
            {
                _events.Add(new RemoteChatEvent { Kind = "assistant", Content = content });
            }
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    internal void PluginStarted(string name, string arguments)
    {
        lock (_sync)
        {
            _events.Add(new RemoteChatEvent { Kind = "plugin_started", PluginName = name, Arguments = arguments });
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    internal void PluginCompleted(string name, string? result, bool success)
    {
        lock (_sync)
        {
            _events.Add(new RemoteChatEvent { Kind = "plugin_completed", PluginName = name, Result = result, Success = success });
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    internal RemoteChatResponse Snapshot()
    {
        lock (_sync)
        {
            return new RemoteChatResponse { Events = _events.ToArray() };
        }
    }
}

internal static class RemoteChatSessionContext
{
    private static readonly AsyncLocal<RemoteChatCollector?> CurrentCollector = new();
    private static readonly AsyncLocal<IRemoteInteractionResponder?> Responder = new();

    internal static RemoteChatCollector? Current => CurrentCollector.Value;

    /// <summary>当前远端会话注册的交互应答器（若有）。供 InteractionBroker 路由使用。</summary>
    public static IRemoteInteractionResponder? CurrentResponder => Responder.Value;

    internal static IDisposable Begin(RemoteChatCollector collector, IRemoteInteractionResponder? responder = null)
    {
        var previousCollector = CurrentCollector.Value;
        var previousResponder = Responder.Value;
        CurrentCollector.Value = collector;
        Responder.Value = responder;
        return new Scope(() =>
        {
            CurrentCollector.Value = previousCollector;
            Responder.Value = previousResponder;
        });
    }

    internal static void CaptureAssistant(string content) => CurrentCollector.Value?.AddAssistant(content);
    internal static void PluginStarted(string name, string arguments) => CurrentCollector.Value?.PluginStarted(name, arguments);
    internal static void PluginCompleted(string name, string? result, bool success) => CurrentCollector.Value?.PluginCompleted(name, result, success);

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
