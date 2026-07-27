using System.Windows;
using System.Windows.Threading;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Plugin
{
    /// <summary>
    /// 插件生命周期兜底：把 Initialize/Unload 这类第三方代码放到线程池执行并加超时上限，
    /// 任何插件卡死（阻塞式等待、async 死锁、网络无响应）都只会拖慢一次操作，不会冻结 VPetLLM。
    /// </summary>
    public static class PluginLifecycleGuard
    {
        /// <summary>Unload 默认上限。插件通常要断网络连接，给足几秒。</summary>
        public const int DefaultUnloadTimeoutMs = 5000;

        /// <summary>Initialize 默认上限。</summary>
        public const int DefaultInitializeTimeoutMs = 10000;

        /// <summary>
        /// 卸载插件。返回 false 表示超时或抛异常——调用方可以继续走后面的清理流程，
        /// 卡住的插件线程会被丢在后台自生自灭（其 AssemblyLoadContext 可能无法回收，但主程序仍可用）。
        /// </summary>
        public static bool SafeUnload(IVPetLLMPlugin plugin, int timeoutMs = DefaultUnloadTimeoutMs)
            => RunGuarded(plugin?.Name ?? "unknown", "Unload", () => plugin?.Unload(), timeoutMs);

        /// <inheritdoc cref="SafeUnload"/>
        public static Task<bool> SafeUnloadAsync(IVPetLLMPlugin plugin, int timeoutMs = DefaultUnloadTimeoutMs)
            => RunGuardedAsync(plugin?.Name ?? "unknown", "Unload", () => plugin?.Unload(), timeoutMs);

        /// <summary>初始化插件。返回 false 表示超时或抛异常，调用方应视为加载失败。</summary>
        public static bool SafeInitialize(IVPetLLMPlugin plugin, global::VPetLLM.VPetLLM host, int timeoutMs = DefaultInitializeTimeoutMs)
            => RunGuarded(plugin?.Name ?? "unknown", "Initialize", () => plugin?.Initialize(host), timeoutMs);

        /// <inheritdoc cref="SafeInitialize"/>
        public static Task<bool> SafeInitializeAsync(IVPetLLMPlugin plugin, global::VPetLLM.VPetLLM host, int timeoutMs = DefaultInitializeTimeoutMs)
            => RunGuardedAsync(plugin?.Name ?? "unknown", "Initialize", () => plugin?.Initialize(host), timeoutMs);

        private static async Task<bool> RunGuardedAsync(string pluginName, string stage, Action action, int timeoutMs)
        {
            if (action is null) return true;
            var work = Task.Run(action);
            var finished = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false) == work;
            return Report(pluginName, stage, work, finished);
        }

        private static bool RunGuarded(string pluginName, string stage, Action action, int timeoutMs)
        {
            if (action is null) return true;
            var work = Task.Run(action);
            var finished = WaitWithoutFreezingUi(work, timeoutMs);
            return Report(pluginName, stage, work, finished);
        }

        /// <summary>
        /// 在 UI 线程上等待时抽送 Dispatcher 消息：既保持界面可响应，也让插件内部的
        /// Dispatcher.Invoke 能够完成——直接 Task.Wait() 会把二者一起锁死。
        /// </summary>
        private static bool WaitWithoutFreezingUi(Task work, int timeoutMs)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || !dispatcher.CheckAccess())
                return work.Wait(timeoutMs);

            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (_, _) =>
            {
                if (work.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            timer.Start();
            work.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            return work.IsCompleted;
        }

        private static bool Report(string pluginName, string stage, Task work, bool finished)
        {
            if (!finished)
            {
                VPetLLMUtils.Logger.Log($"Plugin {stage} timed out for '{pluginName}'; continuing without it (the plugin thread was abandoned).");
                // 插件最终抛异常时避免 TaskScheduler.UnobservedTaskException 打扰主程序。
                work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            if (work.IsFaulted)
            {
                var ex = work.Exception?.GetBaseException();
                VPetLLMUtils.Logger.Log($"Plugin {stage} failed for '{pluginName}': {ex?.GetType().Name}: {ex?.Message}");
                return false;
            }
            return true;
        }
    }
}
