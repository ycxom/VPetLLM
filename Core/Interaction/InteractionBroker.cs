using System;
using System.Threading;
using System.Threading.Tasks;
using VPetLLM.Core.RemoteChat;

namespace VPetLLM.Core.Interaction
{
    /// <summary>
    /// 交互中介：按当前处理上下文把交互请求路由到合适的前端。
    /// 若当前处理由远端会话发起且该会话注册了应答器，则推送到远端 WebUI；
    /// 否则回退到本地桌面弹窗，保持原有本地行为不变。
    /// </summary>
    public sealed class InteractionBroker : IInteractionService
    {
        private readonly IInteractionService _local;

        public InteractionBroker(IInteractionService? localFallback = null)
        {
            _local = localFallback ?? new LocalDialogInteractionService();
        }

        public Task<InteractionResult> RequestAsync(InteractionRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            var responder = RemoteChatSessionContext.CurrentResponder;
            if (responder is not null && request.AllowRemote)
                return RequestRemoteAsync(responder, request, cancellationToken);

            return _local.RequestAsync(request, cancellationToken);
        }

        private static async Task<InteractionResult> RequestRemoteAsync(
            IRemoteInteractionResponder responder, InteractionRequest request, CancellationToken cancellationToken)
        {
            // 标记会话处于等待应答状态，避免远端处理循环在用户点击前提前收尾。
            var collector = RemoteChatSessionContext.Current;
            collector?.BeginInteraction();
            try
            {
                return await responder.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 远端通道异常一律按拒绝处理（安全默认）。
                return InteractionResult.Rejected;
            }
            finally
            {
                collector?.EndInteraction();
            }
        }
    }
}
