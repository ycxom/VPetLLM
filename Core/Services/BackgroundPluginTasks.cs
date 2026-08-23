using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 一个还在后台跑的插件调用。
    /// </summary>
    public sealed class BackgroundPluginTask
    {
        /// <summary>短句柄，回灌结果时用它对上是哪一次调用。</summary>
        public string CellId { get; init; } = "";

        public string PluginName { get; init; } = "";

        public string Arguments { get; init; } = "";

        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

        public TimeSpan Elapsed => DateTime.UtcNow - StartedUtc;
    }

    /// <summary>
    /// 插件长任务的"让出/续跑"登记处。
    ///
    /// 原来的流程是 <c>await plugin.Function(args)</c> 一等到底 —— Terminal 跑 30 秒，
    /// 桌宠就僵在那儿 30 秒，一句话都说不出来。这里借 codex code-mode 的 cell/wait 思路：
    /// 到点还没跑完就先把控制权还回去，让宠物能开口，任务挂到后台继续；
    /// 跑完之后结果走 <see cref="ResultAggregator.EnqueueDetached"/> 自动回灌给模型，
    /// 和插件自发事件（提醒到点、OneBot 收消息）走的是同一条成熟通路。
    /// </summary>
    public static class BackgroundPluginTasks
    {
        private static readonly ConcurrentDictionary<string, BackgroundPluginTask> _running = new();

        /// <summary>同时在后台跑的任务上限，防止模型反复发起长任务把机器堆满。</summary>
        public const int MaxConcurrent = 8;

        public static IReadOnlyList<BackgroundPluginTask> Running
            => _running.Values.OrderBy(t => t.StartedUtc).ToList();

        public static bool AtCapacity => _running.Count >= MaxConcurrent;

        /// <summary>
        /// 把一个已经在跑的任务挂到后台，返回给模型的句柄。
        /// </summary>
        /// <param name="task">
        /// 已经启动的插件调用。注意这里接的是**已启动**的 Task，不是委托 ——
        /// 调用方先 start 再 race 超时，超时才走到这里，任务本身不能重启。
        /// </param>
        public static BackgroundPluginTask Register(string pluginName, string arguments, Task<string> task)
        {
            var cell = new BackgroundPluginTask
            {
                CellId = Guid.NewGuid().ToString("N").Substring(0, 6),
                PluginName = pluginName,
                Arguments = arguments
            };

            _running[cell.CellId] = cell;
            Logger.Log($"BackgroundPluginTasks: {pluginName} 让出为后台任务 #{cell.CellId}");

            // 不 await：本方法的调用方要立刻返回，好让这一轮回复继续往下走
            _ = task.ContinueWith(t =>
            {
                _running.TryRemove(cell.CellId, out _);

                string payload;
                if (t.IsFaulted)
                {
                    var message = t.Exception?.GetBaseException().Message ?? "unknown error";
                    payload = $"[Plugin Result: {pluginName}#{cell.CellId}] (后台任务失败，用时 {cell.Elapsed.TotalSeconds:F0}s) {message}";
                    Logger.Log($"BackgroundPluginTasks: #{cell.CellId} 失败: {message}");
                }
                else if (t.IsCanceled)
                {
                    payload = $"[Plugin Result: {pluginName}#{cell.CellId}] (后台任务已取消)";
                }
                else
                {
                    var result = t.Result ?? "";
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        // 和同步路径保持一致：空结果不打扰模型
                        Logger.Log($"BackgroundPluginTasks: #{cell.CellId} 返回空，不回灌");
                        return;
                    }
                    payload = $"[Plugin Result: {pluginName}#{cell.CellId}] (后台任务完成，用时 {cell.Elapsed.TotalSeconds:F0}s) {result}";
                }

                // 必须走 Detached：这条延续继承了发起那一轮的会话 Id，
                // 而那一轮多半已经 flush 过了，按会话入队会石沉大海
                ResultAggregator.EnqueueDetached(payload);
                // 刻意不用 ExecuteSynchronously：插件的 Task 很可能是在 UI 线程上完成的
                // （不少插件的 Function 里有 Dispatcher.Invoke），那样这段回灌连同
                // ResultAggregator 的加锁就会压在 UI 线程上。丢给线程池更安全。
            }, TaskScheduler.Default);

            return cell;
        }

        /// <summary>让出时告诉模型的那句话。要讲清楚"别重发、结果会自己回来"。</summary>
        public static string BuildYieldNotice(BackgroundPluginTask cell, int yieldSeconds, string language)
            => language switch
            {
                "zh-hans" =>
                    $"[Plugin Running: {cell.PluginName}#{cell.CellId}] 该调用超过 {yieldSeconds} 秒仍未完成，已转入后台继续执行。" +
                    "请先正常回应用户（可以说明你正在处理），不要重复发起同一个调用；完成后系统会自动把结果发给你。",
                "zh-hant" =>
                    $"[Plugin Running: {cell.PluginName}#{cell.CellId}] 該調用超過 {yieldSeconds} 秒仍未完成，已轉入後台繼續執行。" +
                    "請先正常回應用戶（可以說明你正在處理），不要重複發起同一個調用；完成後系統會自動把結果發給你。",
                "ja" =>
                    $"[Plugin Running: {cell.PluginName}#{cell.CellId}] この呼び出しは {yieldSeconds} 秒を超えても完了しないため、バックグラウンドに移しました。" +
                    "まずは普通にユーザーへ返答してください（処理中だと伝えて構いません）。同じ呼び出しを繰り返さないでください。完了時に結果が自動で届きます。",
                _ =>
                    $"[Plugin Running: {cell.PluginName}#{cell.CellId}] This call is still running after {yieldSeconds}s and has moved to the background. " +
                    "Reply to the user normally for now (you may say you are working on it) and do NOT re-issue the same call; the result will be delivered to you automatically."
            };

        /// <summary>后台任务的概况，供动态提示词告诉模型"你还有几件事在跑"。</summary>
        public static string DescribeRunning(string language)
        {
            var running = Running;
            if (running.Count == 0) return "";

            var items = string.Join(", ", running.Select(t => $"{t.PluginName}#{t.CellId} ({t.Elapsed.TotalSeconds:F0}s)"));
            return language switch
            {
                "zh-hans" => $"后台仍在运行的插件调用：{items}。结果完成后会自动发给你，不要重复发起。",
                "zh-hant" => $"後台仍在運行的插件調用：{items}。結果完成後會自動發給你，不要重複發起。",
                "ja" => $"バックグラウンドで実行中の呼び出し：{items}。完了時に結果が自動で届くので、再実行しないでください。",
                _ => $"Plugin calls still running in the background: {items}. Results arrive automatically; do not re-issue them."
            };
        }
    }
}
