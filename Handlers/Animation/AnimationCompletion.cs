namespace VPetLLM.Handlers.Animation
{
    /// <summary>
    /// 一次动画下发的收尾器。它管两件**性质完全不同**的事，绝不能混为一谈：
    ///
    /// <list type="number">
    /// <item>
    /// <b>宿主回收</b>（<c>request.EndAction</c>，全项目一律是 <c>Main.DisplayToNomal</c>）——
    /// 这是 VPet 的契约：<c>Display(name, type, endAction)</c> 播到最后一帧就调它，
    /// 由它把宠物带回待机。**不调，宠物就永远停在最后一帧。**
    /// 所以这个动作即使迟到也必须执行，除非显示权已经被别人接管。
    /// </item>
    /// <item>
    /// <b>协调器记账</b>（<c>MarkAnimationCompleted()</c> + 兑现等待用的 Task）——
    /// 这个反过来：一旦我们超时走人，迟到的记账必须丢弃，
    /// 否则它会把协调器当时正在放的**另一个**动画错误地标记成"已完成"。
    /// </list>
    ///
    /// 之前这两件事被塞进同一个回调，于是只能二选一：
    /// 保留就会串台，作废就会卡死（默认超时 5 秒，而 B_Loop 动画根本播不完，
    /// 必然走超时分支 —— 等于每个循环动画都把自己的 DisplayToNomal 丢掉了）。
    ///
    /// 现在两条通道各有各的闸：记账走 <see cref="_settled"/>，回收走 <see cref="_reclaimed"/>
    /// 外加一道"我还持有显示权吗"的代际检查。
    /// </summary>
    internal sealed class AnimationCompletion : IDisposable
    {
        private readonly TaskCompletionSource<bool> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Func<bool> _stillOwnsDisplay;
        private readonly string _describe;

        private Action _bookkeeping;
        private Action _reclaim;

        private int _settled;    // 记账闸：0 = 还没结论
        private int _reclaimed;  // 回收闸：0 = 还没回收过

        /// <param name="reclaim">
        /// 宿主回收动作（<c>request.EndAction</c>）。最多执行一次，
        /// 且执行前会问 <paramref name="stillOwnsDisplay"/> —— 显示权被新动画接管了就放弃，
        /// 免得把别人的动画掐掉。
        /// </param>
        /// <param name="bookkeeping">协调器自己的记账，结算之后一律丢弃。</param>
        /// <param name="stillOwnsDisplay">代际检查；传 null 视为始终持有。</param>
        /// <param name="describe">日志用描述。</param>
        public AnimationCompletion(
            Action reclaim,
            Action bookkeeping,
            Func<bool> stillOwnsDisplay = null,
            string describe = null)
        {
            _reclaim = reclaim;
            _bookkeeping = bookkeeping;
            _stillOwnsDisplay = stillOwnsDisplay;
            _describe = describe ?? "(anonymous)";
        }

        /// <summary>等待结果：true = 动画正常播完，false = 超时/失败/被接管。</summary>
        public Task<bool> Task => _tcs.Task;

        /// <summary>记账是否已有结论。</summary>
        public bool IsSettled => Volatile.Read(ref _settled) != 0;

        /// <summary>宿主回收动作是否已经跑过（或已确认不需要跑）。</summary>
        public bool IsReclaimed => Volatile.Read(ref _reclaimed) != 0;

        /// <summary>
        /// 交给 VPet 的回调本体：<c>Display(name, type, completion.Complete)</c>。
        /// 动画播完时由 VPet 调用 —— 无论早晚，回收都要走一遍（前提是显示权还在我们手上）。
        /// </summary>
        public void Complete()
        {
            // 顺序要紧：先回收（把宠物带回待机），再记账。
            TryReclaim("animation-ended");
            Settle(true, runBookkeeping: true);
        }

        /// <summary>
        /// 动画压根没启动起来（Display 调用本身抛了）。
        /// 这时候屏幕上并没有我们的动画，不该去回收 —— 交给协调器的兜底逻辑判断。
        /// </summary>
        public void Fail(Exception ex)
        {
            Logger.Log($"AnimationCompletion: 动画启动失败 [{_describe}]: {ex?.Message}");
            DropReclaim();
            Settle(false, runBookkeeping: false);
        }

        /// <summary>
        /// 显示权被新动画接管：记账和回收一起丢。
        /// 新动画自带它自己的回收动作，这里再插一脚只会把它掐掉。
        /// </summary>
        public void HandOff()
        {
            DropReclaim();
            Settle(false, runBookkeeping: false);
        }

        /// <summary>
        /// 我们等超时了，不再等这次动画的结果。
        ///
        /// 注意**不动回收通道**：动画很可能只是还在正常播（B_Loop 更是永远播不完），
        /// 迟到的 VPet 回调仍然应该把宠物带回待机。那条路上有代际检查兜着，
        /// 一旦有新动画接管就会自动失效。
        /// </summary>
        public void Dispose()
        {
            Settle(false, runBookkeeping: false);
        }

        /// <param name="runBookkeeping">
        /// 只有 <see cref="Complete"/> 传 true —— 记账（MarkAnimationCompleted）的含义是
        /// "这个动画真的播完了"。超时 / 交棒 / 启动失败都不是播完，
        /// 这时候记账等于向同步器谎报完成，把状态写脏。
        /// 迟到的 Complete 会因为 _settled 已置位而在上面直接返回，记账自然被丢掉 —— 这正是要的。
        /// </param>
        private void Settle(bool result, bool runBookkeeping)
        {
            if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
                return;

            var callback = Interlocked.Exchange(ref _bookkeeping, null);
            try
            {
                if (runBookkeeping) callback?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompletion: 记账回调抛异常 [{_describe}]: {ex.Message}");
            }
            finally
            {
                _tcs.TrySetResult(result);
            }
        }

        private void TryReclaim(string trigger)
        {
            if (Interlocked.CompareExchange(ref _reclaimed, 1, 0) != 0)
                return;

            var reclaim = Interlocked.Exchange(ref _reclaim, null);
            if (reclaim is null) return;

            // 显示权已经易主：新动画有它自己的收尾，这里再 DisplayToNomal 就是掐别人。
            if (_stillOwnsDisplay is not null && !_stillOwnsDisplay())
            {
                Logger.Log($"AnimationCompletion: 放弃回收 [{_describe}] ({trigger}) —— 显示权已被接管");
                return;
            }

            try
            {
                reclaim();
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationCompletion: 回收动作抛异常 [{_describe}]: {ex.Message}");
            }
        }

        private void DropReclaim()
        {
            Interlocked.Exchange(ref _reclaimed, 1);
            Interlocked.Exchange(ref _reclaim, null);
        }
    }
}
