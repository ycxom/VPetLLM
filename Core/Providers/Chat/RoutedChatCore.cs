using System.Diagnostics;
using System.Runtime.CompilerServices;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Routing;
using VPetLLM.Infrastructure.Exceptions;

namespace VPetLLM.Core.Providers.Chat
{
    /// <summary>
    /// 统一渠道的对外 core。
    ///
    /// 历史、记录、技能、溢出总结、向量检索都挂在它身上（只有一份）；每次请求由
    /// <see cref="ChannelRouter"/> 排出要试的渠道，逐个派给对应类型的工作 core，
    /// 工作 core 只负责把这一次请求按各自协议发出去。
    ///
    /// 失败转移的边界：
    ///   · 用户中断 —— 不转移，也不报错；
    ///   · 已经有内容送到了气泡/语音 —— 不转移，换个渠道从头再说一遍，用户听到的就是"回答了两遍"；
    ///   · 其余失败 —— 换下一个渠道，只有全部失败才把最后一个错误报给用户。
    /// </summary>
    public sealed partial class RoutedChatCore : ChatCoreBase
    {
        private string? _name;

        /// <summary>
        /// 聊天历史按这个名字分库（仅在"按提供商分开保存聊天"打开时有区别）。
        /// 取主渠道所属的旧提供商名，和以前各 core 的 Name 一致，老用户的历史接得上。
        /// </summary>
        public override string Name => _name ??= HistoryNameOf(Settings);

        private readonly ConditionalWeakTable<Setting.ChannelNodeBase, ChatCoreBase> _workers = new();
        private readonly object _workerLock = new();

        /// <summary>最近一次请求实际用到的渠道（成功或最后失败的那个）。</summary>
        public Setting.ChannelNodeBase? LastChannel { get; private set; }

        public RoutedChatCore(Setting settings, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(settings, mainWindow, actionProcessor)
        {
        }

        private static string HistoryNameOf(Setting? settings) => settings?.Provider switch
        {
            Setting.LLMType.OpenAI => "OpenAI",
            Setting.LLMType.Gemini => "Gemini",
            Setting.LLMType.Ollama => "Ollama",
            Setting.LLMType.LMStudio => "LM Studio",
            _ => "Free"
        };

        public override Task<string> Chat(string prompt) => Chat(prompt, false);

        public override async Task<string> Chat(string prompt, bool isRetry)
        {
            await RunAsync("Chat", requireVision: false, worker => worker.Chat(prompt, isRetry));
            return "";
        }

        public override async Task<string> ChatWithImages(string prompt, IReadOnlyList<byte[]> images)
        {
            await RunAsync("Chat", requireVision: true, worker => worker.ChatWithImages(prompt, images));
            return "";
        }

        public override async Task<string> Summarize(string systemPrompt, string userContent)
        {
            var plan = ChannelRouter.Plan(Settings!, "Compression");
            if (plan.Count == 0)
                throw new SummarizeFailedException(NoChannelMessage(false));

            SummarizeFailedException? last = null;
            foreach (var node in plan)
            {
                var worker = GetWorker(node);
                worker.OnBeforeRoutedCall();
                try
                {
                    var result = await worker.Summarize(systemPrompt, userContent);
                    ChannelStatsRecorder.Record(node, success: true, tokens: 0);
                    return result;
                }
                catch (SummarizeFailedException ex)
                {
                    ChannelStatsRecorder.Record(node, success: false, tokens: 0);
                    last = ex;
                    Logger.Log($"渠道 #{node.Id} {node.Name} 总结失败: {ex.Message}");
                }
            }

            throw last!;
        }

        private async Task RunAsync(string purpose, bool requireVision, Func<ChatCoreBase, Task> call)
        {
            LastCallFailed = false;
            LastTokenUsage = 0;

            var plan = ChannelRouter.Plan(Settings!, purpose, requireVision);
            if (plan.Count == 0)
            {
                ReportFailure(NoChannelMessage(requireVision));
                return;
            }

            string? lastError = null;
            for (var i = 0; i < plan.Count; i++)
            {
                var node = plan[i];
                var worker = GetWorker(node);

                var emitted = false;
                string? failure = null;
                worker.BeginRoutedCall(this,
                    text => { emitted = true; ResponseHandler?.Invoke(text); },
                    chunk => { emitted = true; StreamingChunkHandler?.Invoke(chunk); },
                    error => failure = error);
                worker.SuppressTurnBookkeeping = i > 0;
                LastChannel = node;

                var failed = false;
                try
                {
                    await call(worker);
                    failed = worker.LastCallFailed;
                }
                catch (Exception ex) when (!InterruptManager.IsInterrupted)
                {
                    failed = true;
                    failure ??= ex.Message;
                    Logger.Log($"渠道 #{node.Id} {node.Name} 异常: {ex}");
                }

                if (InterruptManager.IsInterrupted)
                    return;

                ChannelStatsRecorder.Record(node, !failed, worker.LastTokenUsage);

                if (!failed)
                {
                    LastTokenUsage = worker.LastTokenUsage;
                    return;
                }

                lastError = failure ?? lastError;

                if (emitted)
                {
                    Logger.Log($"渠道 #{node.Id} {node.Name} 中途失败，已有内容输出，不再转移: {failure}");
                    break;
                }

                if (i + 1 < plan.Count)
                    Logger.Log($"渠道 #{node.Id} {node.Name} 失败，转移到 #{plan[i + 1].Id} {plan[i + 1].Name}: {failure}");
            }

            ReportFailure(lastError ?? NoChannelMessage(requireVision));
        }

        private string NoChannelMessage(bool requireVision)
        {
            var zh = Settings?.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) != false;
            if (requireVision)
                return zh ? "没有启用视觉能力的渠道，请在渠道管理中为至少一个渠道开启「视觉」"
                          : "No enabled channel supports vision. Turn on Vision for at least one channel.";
            return zh ? "没有启用的渠道，请在渠道管理中启用至少一个渠道"
                      : "No channel is enabled. Enable at least one channel in Channel Management.";
        }

        /// <summary>
        /// 每个渠道节点一个工作 core，随节点对象存亡（删掉的渠道自动回收）。
        /// 复用而不是每次新建：Free 的构造要读云端配置，Gemini 的多 Key 轮换状态也在实例上。
        /// </summary>
        private ChatCoreBase GetWorker(Setting.ChannelNodeBase node)
        {
            lock (_workerLock)
            {
                if (_workers.TryGetValue(node, out var existing))
                    return existing;

                var worker = CreateWorker(node, this);
                _workers.Add(node, worker);
                return worker;
            }
        }

        internal static ChatCoreBase CreateWorker(Setting.ChannelNodeBase node, ChatCoreBase host) => node switch
        {
            Setting.OpenAINodeSetting n => new OpenAIChatCore(n, host),
            Setting.GeminiNodeSetting n => new GeminiChatCore(n, host),
            Setting.OllamaNodeSetting n => new OllamaChatCore(n, host),
            Setting.LMStudioNodeSetting n => new LMStudioChatCore(n, host),
            Setting.FreeNodeSetting n => new FreeChatCore(n, host),
            _ => throw new NotSupportedException($"未知的渠道类型: {node.GetType().Name}")
        };

        /// <summary>
        /// 测试一个渠道能不能用：发一次最小的请求，记下耗时和结果。
        ///
        /// 走的是各渠道真实的请求代码（协议、代理、鉴权、Free 的签名都一样），只是换成
        /// 不碰聊天历史的"总结"入口。渠道停用着也能测 —— 测的是它的副本。
        /// </summary>
        public async Task<ChannelTestResult> TestChannelAsync(Setting.ChannelNodeBase node, CancellationToken cancellationToken = default)
        {
            var probe = (Setting.ChannelNodeBase)JsonConvert.DeserializeObject(
                JsonConvert.SerializeObject(node), node.GetType())!;
            // 其余参数原样保留：测试要反映真实请求。强行带上 temperature / max_tokens
            // 会让不接受这两个参数的推理模型在测试里报错，而实际聊天却是好的。
            probe.Enabled = true;
            probe.Mode = Setting.ChannelMode.Unrestricted;

            var worker = CreateWorker(probe, this);
            worker.DetailedErrors = true;
            worker.OnBeforeRoutedCall();

            var timeout = Settings?.LLMRequestTimeoutSeconds is > 0 and var t ? t : 60;
            var sw = Stopwatch.StartNew();
            ChannelTestResult result;
            try
            {
                var request = worker.Summarize(
                    "You are a connectivity probe. Reply with the single word OK.",
                    "ping");
                var finished = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(timeout), cancellationToken));
                sw.Stop();

                if (finished != request)
                {
                    result = ChannelTestResult.Fail(sw.ElapsedMilliseconds,
                        cancellationToken.IsCancellationRequested ? "已取消" : $"超时（{timeout}s）");
                }
                else
                {
                    var reply = await request;
                    result = string.IsNullOrWhiteSpace(reply)
                        ? ChannelTestResult.Fail(sw.ElapsedMilliseconds, "请求成功但回复为空")
                        : ChannelTestResult.Ok(sw.ElapsedMilliseconds, reply.Trim());
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result = ChannelTestResult.Fail(sw.ElapsedMilliseconds, ex.Message);
            }

            ChannelStatsRecorder.RecordTest(node, result);
            return result;
        }
    }

    public sealed class ChannelTestResult
    {
        public bool Success { get; private init; }
        public long LatencyMs { get; private init; }
        public string? Error { get; private init; }
        public string? Reply { get; private init; }

        public static ChannelTestResult Ok(long ms, string reply) => new() { Success = true, LatencyMs = ms, Reply = reply };
        public static ChannelTestResult Fail(long ms, string error) => new() { Success = false, LatencyMs = ms, Error = error };
    }

    /// <summary>渠道统计的唯一写入口（请求可能在后台线程并发完成）。</summary>
    public static class ChannelStatsRecorder
    {
        private static readonly object Gate = new();

        /// <summary>统计变化时触发（UI 用来刷新表格）。</summary>
        public static event Action<Setting.ChannelNodeBase>? Changed;

        public static void Record(Setting.ChannelNodeBase node, bool success, long tokens)
        {
            lock (Gate)
            {
                var stats = node.Stats ??= new Setting.ChannelStats();
                stats.RequestCount++;
                if (!success) stats.FailureCount++;
                if (tokens > 0) stats.TotalTokens += tokens;
                stats.LastUsedAt = DateTime.Now;
            }
            Changed?.Invoke(node);
        }

        public static void RecordTest(Setting.ChannelNodeBase node, ChannelTestResult result)
        {
            lock (Gate)
            {
                var stats = node.Stats ??= new Setting.ChannelStats();
                stats.LastTestedAt = DateTime.Now;
                stats.LastTestOk = result.Success;
                stats.LastTestLatencyMs = (int)Math.Min(int.MaxValue, result.LatencyMs);
                stats.LastTestError = result.Success ? null : result.Error;
            }
            Changed?.Invoke(node);
        }
    }
}
