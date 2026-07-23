namespace VPetLLM.Core.RemoteChat;

/// <summary>
/// 供远端会话展示的一条历史消息（已剔除系统提示与工具内部内容，仅保留用户/助手正文）。
/// </summary>
public sealed class RemoteChatHistoryItem
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public long? UnixTime { get; init; }
}
