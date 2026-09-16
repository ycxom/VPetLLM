using System.IO;
using VPetLLM;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;
using VPetLLM.Infrastructure.Exceptions;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.Data;

/// <summary>一次 Summarize 调用的记录，用来断言「发了什么、发了多大」。</summary>
internal sealed class RecordedCall
{
    internal required string SystemPrompt { get; init; }
    internal required string UserContent { get; init; }
    internal required int MessageCount { get; init; }
    internal bool Succeeded { get; set; }

    /// <summary>整个请求的估算 token 数，用来验证没有超出上下文预算。</summary>
    internal int TotalTokens => TokenCounter.EstimateTokenCount(SystemPrompt)
                                + TokenCounter.EstimateTokenCount(UserContent);
}

/// <summary>
/// 可编程的假 ChatCore：只实现 Summarize，把每次调用记下来，
/// 并按测试设定决定这次是成功还是抛 SummarizeFailedException。
/// </summary>
internal sealed class RecordingCore : ChatCoreBase
{
    private readonly int _contextTokens;
    private int _index;

    internal RecordingCore(Setting settings, int contextTokens) : base(settings, null, null)
    {
        _contextTokens = contextTokens;
    }

    public override string Name => "Recording";

    /// <summary>直接给定上下文预算，绕开 ContextLimitGuard 的学习值。</summary>
    protected override int MaxContextTokens => _contextTokens;

    internal List<RecordedCall> Calls { get; } = new();

    /// <summary>永远失败，用来模拟服务不可用。</summary>
    internal bool FailAlways { get; set; }

    /// <summary>条数超过这个值就失败，用来模拟服务端比我们估得更严。</summary>
    internal int FailWhenMessagesExceed { get; set; } = int.MaxValue;

    /// <summary>第 N 次成功调用返回什么文本；默认返回一段可辨识的占位总结。</summary>
    internal Func<int, string> SummaryFactory { get; set; } = index => $"summary-{index}";

    public override Task<string> Chat(string prompt) => Task.FromResult("");

    public override Task<string> Chat(string prompt, bool isRetry) => Task.FromResult("");

    public override Task<string> Summarize(string systemPrompt, string userContent)
    {
        // userContent 是 "[role]: content" 逐行拼出来的
        var messageCount = userContent.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var call = new RecordedCall
        {
            SystemPrompt = systemPrompt,
            UserContent = userContent,
            MessageCount = messageCount
        };
        Calls.Add(call);

        if (FailAlways || messageCount > FailWhenMessagesExceed)
        {
            throw new SummarizeFailedException(
                $"fake failure: {messageCount} messages exceed the fake limit");
        }

        call.Succeeded = true;
        return Task.FromResult(SummaryFactory(_index++));
    }
}

/// <summary>
/// 把 OverflowManager 架在一个临时库上跑真实的分批流程。
/// 刻意不去 mock OverflowManager 内部：要验的正是它那套循环。
/// </summary>
internal sealed class Harness : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    internal Harness(int contextTokens)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"vpet-overflow-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "chat_history.db");

        // Setting 的构造会在给定目录下初始化存储，指向临时目录即可隔离
        Settings = new Setting(_directory)
        {
            // 阈值压到 1，让每次 Run 都必然触发总结
            HistoryCompressionThreshold = 1,
            CompressionMode = Setting.CompressionTriggerMode.MessageCount,
            MaxContextTokens = contextTokens,
            EnableCompressionRecords = false,
            SeparateChatByProvider = false
        };

        Core = new RecordingCore(Settings, contextTokens);
        Manager = new OverflowManager(Settings, "Recording", Core, null, _databasePath);
    }

    internal Setting Settings { get; }

    internal RecordingCore Core { get; }

    internal OverflowManager Manager { get; }

    internal int Checkpoint => Manager.LastSummarizedIndex;

    internal int SummaryRowCount
    {
        get
        {
            using var database = new OverflowDatabase(_databasePath);
            return database.GetMaxSegmentEndIndex("") == 0 && CountRows() == 0 ? 0 : CountRows();
        }
    }

    private int CountRows()
    {
        using var connection = SQLiteHelper.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM overflow_summaries";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal void Run(List<Message> history)
    {
        // keepCount = min(threshold, count) = 1，所以最后 1 条留在窗口里不参与总结；
        // 断言里的 checkpoint 期望值据此写成 count - 0 ... count，见各用例注释。
        Manager.CheckAndTriggerAsync(history, history.Count).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }
}
