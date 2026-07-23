using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VPetLLM.Core.Interaction
{
    /// <summary>
    /// 交互请求的类型。前端（本地弹窗或远端 WebUI）据此渲染。
    /// </summary>
    public enum InteractionKind
    {
        /// <summary>确认/拒绝（可选带可编辑正文，如命令确认）。</summary>
        Confirm,
        /// <summary>纯文本输入。</summary>
        Input,
        /// <summary>从若干选项中选择一个。</summary>
        Choice,
        /// <summary>一次性告警确认（等价 OK/Cancel）。</summary>
        Warning
    }

    /// <summary>
    /// 插件向用户发起的一次交互请求。插件不再直接弹 WPF 窗口，
    /// 而是通过 <see cref="IInteractionService"/> 发出本对象，由中介决定
    /// 走本地弹窗还是远端 WebUI。
    /// </summary>
    public sealed class InteractionRequest
    {
        public InteractionKind Kind { get; init; } = InteractionKind.Confirm;

        /// <summary>发起方插件名，用于展示与审计。</summary>
        public string Source { get; init; } = "";

        public string Title { get; init; } = "";
        public string Message { get; init; } = "";

        /// <summary>可编辑正文的默认值（如待确认命令）。为空表示不可编辑。</summary>
        public string? DefaultValue { get; init; }

        /// <summary>Choice 类型的候选项。</summary>
        public IReadOnlyList<string>? Choices { get; init; }

        /// <summary>确认按钮文案（本地化由调用方或前端提供）。</summary>
        public string? ConfirmText { get; init; }

        /// <summary>取消按钮文案。</summary>
        public string? CancelText { get; init; }

        /// <summary>超时时间；超时按拒绝处理。默认 2 分钟。</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 是否允许把本请求路由到远端浏览器应答。为 false 时即使处于远端会话也强制走本地弹窗
        /// （高危操作如终端命令确认应设为 false，要求操作者在桌面本机亲自确认）。
        /// </summary>
        public bool AllowRemote { get; init; } = true;
    }

    /// <summary>
    /// 一次交互的结果。安全默认为“未确认”。
    /// </summary>
    public sealed class InteractionResult
    {
        public bool Confirmed { get; init; }

        /// <summary>用户提交的值（编辑后的命令 / 输入文本 / 选中项）。</summary>
        public string? Value { get; init; }

        /// <summary>是否因超时而结束（此时 <see cref="Confirmed"/> 恒为 false）。</summary>
        public bool TimedOut { get; init; }

        public static InteractionResult Rejected { get; } = new() { Confirmed = false };
        public static InteractionResult Timeout { get; } = new() { Confirmed = false, TimedOut = true };
        public static InteractionResult Accepted(string? value = null) => new() { Confirmed = true, Value = value };
    }

    /// <summary>
    /// 交互服务的公共入口。插件通过 <c>vpetLLM.Interaction</c> 获取本接口。
    /// 实现负责把请求路由到合适的前端（本地弹窗或远端 WebUI）。
    /// </summary>
    public interface IInteractionService
    {
        Task<InteractionResult> RequestAsync(InteractionRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 远端交互应答器。由传输插件（如 RemoteChatPlugin）实现并在当前远端会话
    /// 上下文中注册，使处理管线中途的交互请求能被推送到浏览器并等待回执。
    /// </summary>
    public interface IRemoteInteractionResponder
    {
        Task<InteractionResult> RequestAsync(InteractionRequest request, CancellationToken cancellationToken = default);
    }
}
