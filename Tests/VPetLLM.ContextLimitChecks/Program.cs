using System.Reflection;
using System.Text;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Utils.Common;

// 上下文超长的自愈回归检查。
//
// 用户实际报的错（复现基准，全篇都拿它当输入）：
//   {"error":{"code":400,
//     "message":"request (13392 tokens) exceeds the available context size (8192 tokens), try increasing it",
//     "type":"exceed_context_size_error","n_prompt_tokens":13392,"n_ctx":8192}}
//
// 第一版修复只读了 n_ctx=8192 就去裁剪，结果**一刀都没裁**、用户照样看到同一个 400。
// 两个原因，这份检查把它们分别钉死：
//   1. 估算器数的是 Message.Content，可真正发上线的是 DisplayContent（JSON 包了一层）；
//   2. 就算数对了，我们的尺子和服务端的分词器也不是一个刻度 ——
//      按 8192 裁，我们量出 5000 就以为没超，照发不误。
//      正解是拿错误里的 n_prompt_tokens 反算校准系数。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    const string RealError =
        "{\"error\":{\"code\":400,\"message\":\"request (13392 tokens) exceeds the available " +
        "context size (8192 tokens), try increasing it\",\"type\":\"exceed_context_size_error\"," +
        "\"n_prompt_tokens\":13392,\"n_ctx\":8192}}";

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Test_Parsing();
        Test_Calibration();
        Test_EstimatorCountsWhatIsSent();
        Test_TrimActuallyHappens();
        Test_SummaryDoesNotEatTheWindow();
        Test_GivesUpWhenSystemAloneTooBig();

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    // =================================================================
    // 1. 从错误里把两个数字读出来
    // =================================================================
    static void Test_Parsing()
    {
        Console.WriteLine("[1] 解析用户报的那条错误");

        Check("认得出这是上下文超长", ContextLimitGuard.IsContextLimitError(400, RealError));
        Check("★ 读出窗口大小 8192", ContextLimitGuard.ParseContextTokens(RealError) == 8192,
              $"实际 {ContextLimitGuard.ParseContextTokens(RealError)}");
        Check("★ 读出实发 token 13392", ContextLimitGuard.ParsePromptTokens(RealError) == 13392,
              $"实际 {ContextLimitGuard.ParsePromptTokens(RealError)}");

        // 别把无关的 400 也当成上下文超长
        Check("鉴权错误不误判",
            !ContextLimitGuard.IsContextLimitError(400,
                "{\"error\":{\"message\":\"Incorrect API key provided\",\"code\":\"invalid_api_key\"}}"));
        Check("5xx 不误判（服务端自己的问题，裁历史没用）",
            !ContextLimitGuard.IsContextLimitError(500, RealError));

        // OpenAI 官方那套说法也得认
        const string openAi =
            "{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens. " +
            "However, your messages resulted in 13392 tokens.\",\"code\":\"context_length_exceeded\"}}";
        Check("OpenAI 口径：读出窗口", ContextLimitGuard.ParseContextTokens(openAi) == 8192);
        Check("OpenAI 口径：读出实发", ContextLimitGuard.ParsePromptTokens(openAi) == 13392);

        Console.WriteLine();
    }

    // =================================================================
    // 2. 校准：把 8192 换算到"我们估算器的刻度"上
    // =================================================================
    static void Test_Calibration()
    {
        Console.WriteLine("[2] 估算校准");

        // 我们估 5000，服务端数出 13392 → 我们每 1 个 token 实际值 2.68 个
        var ratio = ContextLimitGuard.CalibrationRatio(5000, 13392);
        Check("★ 算得出估算系数 ≈2.68", Math.Abs(ratio - 2.6784) < 0.01, $"实际 {ratio:F4}");

        Check("没有样本时不校准（系数 1）", ContextLimitGuard.CalibrationRatio(0, 0) == 1.0);
        Check("样本太小不校准（比出来的倍率没意义）", ContextLimitGuard.CalibrationRatio(50, 5000) == 1.0);
        Check("我们估多了就按 1 算（已经足够保守）", ContextLimitGuard.CalibrationRatio(9000, 5000) == 1.0);
        Check("系数封顶，别把预算算成个位数", ContextLimitGuard.CalibrationRatio(1000, 999999) == 8.0);

        // 存进去的预算必须是"我们的刻度"下的值：8192 / 2.68 ≈ 3058
        ContextLimitGuard.Reset();
        var key = ContextLimitGuard.MakeKey("OpenAI", "http://127.0.0.1:8080/v1", "local");
        Check("记录成功", ContextLimitGuard.Remember(key, 8192, 5000, 13392));
        var stored = ContextLimitGuard.GetLimit(key);
        Check("★★ 存的是换算后的预算而不是裸 8192", stored is > 2900 and < 3200, $"实际 {stored}");

        // 同一个上限再撞一次 → 继续收紧，否则重发的还是同一坨
        var before = stored;
        Check("同上限再撞一次会继续收紧", ContextLimitGuard.Remember(key, 8192, 0, 0));
        Check("★ 确实变小了", ContextLimitGuard.GetLimit(key) < before,
              $"{before} -> {ContextLimitGuard.GetLimit(key)}");

        ContextLimitGuard.Reset();
        Check("Reset 之后忘干净", ContextLimitGuard.GetLimit(key) == 0);

        Console.WriteLine();
    }

    // =================================================================
    // 3. 估算器数的必须是真正发上线的那份文本
    // =================================================================
    static void Test_EstimatorCountsWhatIsSent()
    {
        Console.WriteLine("[3] 估算器数的是 DisplayContent");

        var msg = new Message
        {
            Role = "user",
            Content = "今天天气怎么样",
            MessageType = "User",
            UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
            StatusInfo = "心情 80, 饱食度 60, 口渴 55, 体力 70"
        };

        var wire = msg.DisplayContent;
        Check("前提：DisplayContent 确实比 Content 长（包了 JSON）", wire.Length > msg.Content!.Length,
              $"{msg.Content.Length} -> {wire.Length}");

        var counted = TokenCounter.EstimateMessagesTokenCount(new[] { msg });
        var contentOnly = TokenCounter.EstimateTokenCount(msg.Content) + 4;
        var wireTokens = TokenCounter.EstimateTokenCount(wire) + 4;

        Check("★★ 数的是上线那份（旧实现只数 Content，系统性低估）",
              counted == wireTokens && counted > contentOnly,
              $"实际 {counted}，Content 口径 {contentOnly}，DisplayContent 口径 {wireTokens}");

        Console.WriteLine();
    }

    // =================================================================
    // 4. 真跑：学到上限之后，历史必须真的被裁短
    //    —— 这一条就是"问题还存在"的那一刀
    // =================================================================
    static void Test_TrimActuallyHappens()
    {
        Console.WriteLine("[4] 真跑：裁剪确实发生");

        // 造一份中文历史，规模对齐用户现场（服务端数出来 13392）
        var history = new List<Message>
        {
            new() { Role = "system", Content = string.Concat(Enumerable.Repeat("你是一个可爱的虚拟宠物助手，请用友好可爱的语气回应主人。", 20)) }
        };
        for (int i = 0; i < 120; i++)
        {
            history.Add(new Message
            {
                Role = "user",
                Content = $"第{i}轮：主人今天跟你说了一些很长很长的话，内容大概是关于天气、工作、心情以及晚饭吃什么的讨论。",
                MessageType = "User",
                UnixTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                StatusInfo = "心情 80, 饱食度 60, 口渴 55, 体力 70"
            });
            history.Add(new Message
            {
                Role = "assistant",
                Content = $"第{i}轮回复：好的呀主人～今天的天气看起来很不错呢，要不要一起出去走走呀？晚饭我觉得可以吃点清淡的。"
            });
        }

        var ourEstimate = TokenCounter.EstimateMessagesTokenCount(history);

        // 旧口径：只数 Content。这就是第一版修复"一刀没裁"的根源之一 ——
        // 按旧口径量出来的数字比 8192 还小，EnforceTokenBudget 直接 return。
        var legacyEstimate = history.Sum(m =>
            string.IsNullOrWhiteSpace(m.Content) ? 0 : TokenCounter.EstimateTokenCount(m.Content!) + 4);

        Console.WriteLine($"  [环境] 上线口径估 {ourEstimate} tokens / 旧的 Content 口径估 {legacyEstimate}，" +
                          $"模拟服务端数出 13392、窗口 8192");
        Check("★ 旧口径确实系统性低估（差额就是 DisplayContent 那层 JSON）",
              legacyEstimate < ourEstimate, $"{legacyEstimate} vs {ourEstimate}");

        var trimmed = InvokeEnforce(history, out var storedBudget);

        Check("★ 学到的预算已按估算系数换算", storedBudget > 0 && storedBudget < 8192,
              $"实际 {storedBudget}");
        Check("★★ 历史确实被裁短了（第一版修复卡在这里：一刀没裁）",
              trimmed.Count < history.Count,
              $"{history.Count} -> {trimmed.Count}");

        var after = TokenCounter.EstimateMessagesTokenCount(trimmed);
        var backToServer = after * (13392.0 / ourEstimate);
        Check("★★ 裁完之后换算回服务端刻度必须塞得进 8192",
              backToServer < 8192,
              $"裁后我们估 {after}，换算 ≈{backToServer:F0}");

        Check("system 前缀没被裁掉（裁了模型就失忆了）",
              trimmed.Count > 0 && trimmed[0].Role == "system");
        Check("窗口以 user 开头（部分 API 拒绝 system 后紧跟 assistant）",
              trimmed.Count < 2 || trimmed[1].NormalizedRole == "user",
              trimmed.Count > 1 ? $"实际 {trimmed[1].NormalizedRole}" : "");

        Console.WriteLine();
    }

    // =================================================================
    // 5. 滚动总结不许把窗口吃光
    //    第二次"问题还存在"（13392 -> 11806，裁了但还是超）的根源：
    //    总结拼进 system，而 system 永远不参与裁剪；它的上限又挂在
    //    MaxContextTokens 上，而那个默认是 0 = 不限制。
    // =================================================================
    static void Test_SummaryDoesNotEatTheWindow()
    {
        Console.WriteLine("[5] 滚动总结不许把 system 撑爆");

        ContextLimitGuard.Reset();
        var core = new ProbeCore();
        var t = typeof(ChatCoreBase);

        // 先撞一次，让它学到窗口（8192，按估算系数换算后落在 3000 上下）
        var learn = t.GetMethod("LearnContextLimitFromError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var enforce = t.GetMethod("EnforceContextBudget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        enforce.Invoke(core, new object?[] { new List<Message> {
            new() { Role = "system", Content = new string('主', 6000) },
            new() { Role = "user", Content = new string('话', 6000), MessageType = "User" } } });
        learn.Invoke(core, new object?[] { 400, RealError });

        var budget = core.EffectiveContextTokenBudget;
        Check("前提：已经学到预算", budget > 0, $"实际 {budget}");

        // 一份"在还不知道上限时生成"的超大总结
        var huge = string.Concat(Enumerable.Repeat("主人今天说了很多话，聊到了天气工作心情和晚饭。", 400));
        var hugeTokens = TokenCounter.EstimateTokenCount(huge);
        Console.WriteLine($"  [环境] 陈年总结 {hugeTokens} tokens，生效预算 {budget}");
        Check("前提：这份总结确实超过预算", hugeTokens > budget);

        var clamp = t.GetMethod("ClampSummaryForPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var clamped = (string)clamp.Invoke(core, new object?[] { huge })!;
        var clampedTokens = TokenCounter.EstimateTokenCount(clamped);

        Check("★★ 拼进 system 之前被截断（否则光它一个就占满窗口）",
              clampedTokens < hugeTokens, $"{hugeTokens} -> {clampedTokens}");
        Check("★ 截断后落在配额内（预算的 15% 上下）",
              clampedTokens <= Math.Max(256, (int)(budget * 0.15)) + 32,
              $"实际 {clampedTokens}，配额 {Math.Max(256, (int)(budget * 0.15))}");
        Check("保留的是末尾（滚动总结越靠后越新）", clamped.Contains("[...早期总结已省略...]"));

        // 预算未知时不该乱砍
        ContextLimitGuard.Reset();
        var fresh = new ProbeCore();
        var untouched = (string)clamp.Invoke(fresh, new object?[] { huge })!;
        Check("★ 预算未知时原样返回（没有可信目标就别丢信息）", ReferenceEquals(untouched, huge));

        Console.WriteLine();
    }

    // =================================================================
    // 6. system 自己就装不下时：不许重试，也不许把预算越收越小
    //
    //    用户 Debug.log 里的真实现场（16 个插件 + 原生工具调用，n_ctx=8192）：
    //      构成：system 6881 tokens（1 条，不参与裁剪）+ 窗口 164 tokens（1 条）
    //      预算 5294 -> 3705 -> 2593 -> 1815 -> 1270 -> 889 -> 728 -> 510 ...
    //    历史早就裁到只剩 1 条了，可 system 一个人就超窗口 —— 每次重试都是注定 400 的
    //    往返，而收紧后的预算会**留到以后**，等用户真调大了窗口，历史反被小预算按着裁。
    // =================================================================
    static void Test_GivesUpWhenSystemAloneTooBig()
    {
        Console.WriteLine("[6] system 自己装不下时不做无用重试");

        ContextLimitGuard.Reset();
        var core = new ProbeCore();
        var t = typeof(ChatCoreBase);
        var enforce = t.GetMethod("EnforceContextBudget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var learn = t.GetMethod("LearnContextLimitFromError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shouldRetry = t.GetMethod("ShouldRetryAfterContextLimit", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 复刻现场：巨大的 system + 只剩一条的窗口
        var history = new List<Message>
        {
            new() { Role = "system", Content = string.Concat(Enumerable.Repeat("插件说明与角色设定。", 900)) },
            new() { Role = "user", Content = "你好", MessageType = "User" }
        };
        var shaped = (List<Message>)enforce.Invoke(core, new object?[] { history })!;
        Check("前提：system 占了绝大部分",
            TokenCounter.EstimateMessagesTokenCount(shaped.Take(1)) >
            TokenCounter.EstimateMessagesTokenCount(shaped.Skip(1)) * 10);

        learn.Invoke(core, new object?[] { 400, RealError });

        Check("★★ 不重试（重发多少次都是同一个 400）",
              !(bool)shouldRetry.Invoke(core, new object?[] { "你好" })!);
        Check("★★ 也不把预算记下来往下拧（否则会留到以后误伤历史）",
              ContextLimitGuard.GetLimit(core.Key) == 0,
              $"实际 {ContextLimitGuard.GetLimit(core.Key)}");

        // 连撞多次也不该把预算越拧越小
        for (int i = 0; i < 5; i++) learn.Invoke(core, new object?[] { 400, RealError });
        Check("★ 连撞多次依然不留下预算", ContextLimitGuard.GetLimit(core.Key) == 0,
              $"实际 {ContextLimitGuard.GetLimit(core.Key)}");

        // 给用户的文案必须可行动，而不是甩一段 JSON
        var describe = t.GetMethod("DescribeHttpFailure", BindingFlags.NonPublic | BindingFlags.Instance)!;
        learn.Invoke(core, new object?[] { 400, RealError });
        var text = (string)describe.Invoke(core,
            new object?[] { System.Net.HttpStatusCode.BadRequest, RealError, "OpenAI" })!;
        Check("★★ 文案说人话且给出该动哪里",
              text.Contains("上下文装不下") && text.Contains("插件") && !text.StartsWith("{"),
              text.Length > 90 ? text[..90] + "..." : text);

        // 反面：历史还有得裁的时候，该重试还是要重试
        ContextLimitGuard.Reset();
        var normal = new ProbeCore();
        var longHistory = new List<Message> { new() { Role = "system", Content = "你是一个虚拟宠物。" } };
        for (int i = 0; i < 80; i++)
        {
            longHistory.Add(new Message { Role = "user", Content = $"第{i}轮问题，内容比较长一些。", MessageType = "User" });
            longHistory.Add(new Message { Role = "assistant", Content = $"第{i}轮回复，内容也比较长一些。" });
        }
        enforce.Invoke(normal, new object?[] { longHistory });
        learn.Invoke(normal, new object?[] { 400, RealError });
        Check("★ 历史仍有得裁时照常重试（别把自愈一起关掉）",
              (bool)shouldRetry.Invoke(normal, new object?[] { "你好" })!);
        Check("★ 这种情况下预算要记下来", ContextLimitGuard.GetLimit(normal.Key) > 0);

        Console.WriteLine();
    }

    /// <summary>
    /// 走真实代码路径：让 ChatCoreBase 先从错误里学，再调它自己的 EnforceContextBudget。
    /// 用反射是因为这两个成员都是 protected —— 测试要验的正是"插件里那条路"，
    /// 复制一份逻辑到测试里就什么都没验到。
    /// </summary>
    static List<Message> InvokeEnforce(List<Message> history, out int storedBudget)
    {
        ContextLimitGuard.Reset();

        var core = new ProbeCore();
        var t = typeof(ChatCoreBase);

        // 先让它按当前历史算一次估算值（Learn 要拿它当校准样本）
        var enforce = t.GetMethod("EnforceContextBudget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        enforce.Invoke(core, new object?[] { history });

        var learn = t.GetMethod("LearnContextLimitFromError", BindingFlags.NonPublic | BindingFlags.Instance)!;
        learn.Invoke(core, new object?[] { 400, RealError });

        storedBudget = ContextLimitGuard.GetLimit(core.Key);

        return (List<Message>)enforce.Invoke(core, new object?[] { history })!;
    }

    /// <summary>最小可用的 ChatCore：只为了走通基类里那套预算/学习逻辑。</summary>
    sealed class ProbeCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        public string Key { get; }

        public ProbeCore() : base(null, null, null)
        {
            Key = ContextLimitGuard.MakeKey("OpenAI", "http://127.0.0.1:8080/v1", "local");
            ContextLimitKey = Key;
        }

        public override Task<string> Chat(string prompt) => Task.FromResult("");
        public override Task<string> Chat(string prompt, bool isRetry) => Task.FromResult("");
        public override Task<string> Summarize(string s, string u) => Task.FromResult("");
    }
}
