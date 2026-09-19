using System.IO;
using System.Text;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Services;
using V = VPetLLM.Core.Services.FormatComplianceTracker.Violation;
using L = VPetLLM.Core.Services.FormatComplianceTracker.Level;

// 动态格式纠正的回归检查。
//
// 长对话后期回复格式跑偏，根子在上下文：历史里躺着几条不带标记的回复，模型照着自己的
// 先例写；而原来的纠正只提醒一次、还放在离输出最远的 system 开头。这里钉死三件事：
//   1. 判定：标记外正文 / 完全没标记 / 标记写坏，三种能分清，空回复和纯标点不算；
//   2. 状态：连续违规升级、恢复要连续两次合规、提醒只读不消费（失败转移要重复带）；
//   3. 历史改写：只改发出去的副本，被中断回复末尾的系统说明不能被包成台词。

static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Test_Inspect();
        Test_Tracker();
        Test_HistoryNormalization();
        Test_RequestNote();

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_pass} / 失败 {_fail} =====");
        return _fail == 0 ? 0 : 1;
    }

    const string Good = "<|say_begin|> \"主人早上好\" <|say_end|>\n<|happy_begin|> 5 <|happy_end|>";

    static void Test_Inspect()
    {
        Console.WriteLine("[1] 判定与规范写法");

        var ok = ReplyFormatInspector.Inspect(Good);
        Check("规范回复：无违规、不给改写", ok.IsCompliant && ok.Canonical is null);

        var stray = ReplyFormatInspector.Inspect("\"干嘛突然叫我名字呀？\"\n<|happy_begin|> 5 <|happy_end|>");
        Check("标记外正文 → StrayText", stray.Violation == V.StrayText, stray.Violation.ToString());
        Check("规范写法把正文包进 say、保留原有指令",
              stray.Canonical?.Contains("<|say_begin|> \"干嘛突然叫我名字呀？\" <|say_end|>") == true
              && stray.Canonical.Contains("<|happy_begin|> 5 <|happy_end|>"),
              stray.Canonical ?? "");

        var prose = ReplyFormatInspector.Inspect("好的主人，我这就去睡觉啦。");
        Check("完全没标记 → NoMarkers", prose.Violation == V.NoMarkers, prose.Violation.ToString());
        Check("白话文整段包成 say", prose.Canonical == "<|say_begin|> \"好的主人，我这就去睡觉啦。\" <|say_end|>", prose.Canonical ?? "");

        var broken = ReplyFormatInspector.Inspect("<|say_begin|> \"你好\" <|say_ed|>");
        Check("标记没闭合/拼错 → BrokenMarker", broken.Violation == V.BrokenMarker, broken.Violation.ToString());

        var brokenNoPipe = ReplyFormatInspector.Inspect("say_begin \"你好\" say_end");
        Check("缺竖线的标记 → BrokenMarker", brokenNoPipe.Violation == V.BrokenMarker, brokenNoPipe.Violation.ToString());

        Check("空回复不算违规", ReplyFormatInspector.Inspect("   ").IsCompliant);
        Check("命令之间的换行和标点不算正文",
              ReplyFormatInspector.Inspect("<|say_begin|> \"嗯\" <|say_end|>,\n<|happy_begin|> 1 <|happy_end|>。").IsCompliant);
        Check("思考块不算标记外正文",
              ReplyFormatInspector.Inspect("<think>用户在打招呼，我应该开心地回应</think>\n" + Good).IsCompliant);
    }

    static void Test_Tracker()
    {
        Console.WriteLine("[2] 纠正状态：升级与恢复");
        FormatComplianceTracker.Reset();

        FormatComplianceTracker.RecordReply(V.None);
        Check("一直合规时什么都不注入", FormatComplianceTracker.CurrentReminder("zh") is null);

        FormatComplianceTracker.RecordReply(V.NoMarkers);
        var first = FormatComplianceTracker.CurrentReminder("zh");
        Check("违规一次 → 纠正级别，并点出具体问题",
              FormatComplianceTracker.CurrentLevel == L.Correct && first?.Contains("完全没有使用指令标记") == true, first ?? "");
        Check("提醒只读不消费（失败转移时同一轮要重复带上）", FormatComplianceTracker.CurrentReminder("zh") == first);

        FormatComplianceTracker.RecordReply(V.StrayText);
        var second = FormatComplianceTracker.CurrentReminder("zh") ?? "";
        Check("连续违规 → 升级：带次数、带示例、点明别模仿历史",
              FormatComplianceTracker.CurrentLevel == L.Escalated
              && second.Contains("第 2 次") && second.Contains("不要模仿") && second.Contains("<|say_begin|>"), second);
        Check("升级措辞描述的是最近一次的问题", second.Contains("写在了指令标记之外"), second);

        FormatComplianceTracker.RecordReply(V.None);
        Check("改过来一次 → 只轻提醒保持格式，不立刻撤", FormatComplianceTracker.CurrentLevel == L.Recovering
              && FormatComplianceTracker.CurrentReminder("zh")?.Contains("继续保持") == true);

        FormatComplianceTracker.RecordReply(V.BrokenMarker);
        Check("恢复期又违规 → 重新从纠正级别开始计数", FormatComplianceTracker.CurrentLevel == L.Correct
              && FormatComplianceTracker.CurrentReminder("zh")?.Contains("写坏了") == true);

        FormatComplianceTracker.RecordReply(V.None);
        FormatComplianceTracker.RecordReply(V.None);
        Check("连续两次合规 → 撤销提醒", FormatComplianceTracker.CurrentLevel == L.None
              && FormatComplianceTracker.CurrentReminder("zh") is null);

        FormatComplianceTracker.RecordReply(V.StrayText);
        Check("英文提示词给英文纠正", FormatComplianceTracker.CurrentReminder("en")?.StartsWith("[FORMAT CORRECTION]") == true);
        FormatComplianceTracker.Reset();
    }

    static void Test_HistoryNormalization()
    {
        Console.WriteLine("[3] 历史改写只动请求副本");

        var bad = new Message { Role = "assistant", Content = "好呀好呀，我们一起玩吧！" };
        var good = new Message { Role = "assistant", Content = Good };
        var user = new Message { Role = "user", Content = "陪我玩" };
        const string marker = "\n[System: Interrupted by user — the rest of this reply was never delivered. 用户中断了本次回复，后续内容未送出。]";
        var interruptedGood = new Message { Role = "assistant", Content = Good + marker };
        var interruptedBad = new Message { Role = "assistant", Content = "我刚想说" + marker };

        var history = new List<Message> { new() { Role = "system", Content = "sys" }, user, bad, good, interruptedGood, interruptedBad };
        var rewritten = ReplyFormatInspector.NormalizeAssistantHistory(history);

        Check("只改写格式错误的两条", rewritten == 2, rewritten.ToString());
        Check("原消息对象的内容不变（聊天记录不动）", bad.Content == "好呀好呀，我们一起玩吧！" && interruptedBad.Content!.StartsWith("我刚想说"));
        Check("请求副本里换成了规范写法", history[2].Content == "<|say_begin|> \"好呀好呀，我们一起玩吧！\" <|say_end|>", history[2].Content ?? "");
        Check("合规回复原样保留（同一个对象）", ReferenceEquals(history[3], good));
        Check("被中断但格式正确的回复不被误判",
              ReferenceEquals(history[4], interruptedGood));
        var fixedInterrupted = history[5].Content ?? "";
        Check("被中断且格式错误：正文规范化、中断说明原样留在末尾而不是被包成台词",
              fixedInterrupted.StartsWith("<|say_begin|> \"我刚想说\" <|say_end|>")
              && fixedInterrupted.EndsWith("未送出。]")
              && !fixedInterrupted.Contains("\"[System"), fixedInterrupted);
        Check("user 消息不参与改写", ReferenceEquals(history[1], user));

        // 再发一次：走缓存，结果一致；用户编辑过内容后按新内容重算
        var again = new List<Message> { bad };
        ReplyFormatInspector.NormalizeAssistantHistory(again);
        Check("重复请求结果一致", again[0].Content == history[2].Content);
        bad.Content = Good;
        var edited = new List<Message> { bad };
        Check("内容被编辑成合规后不再改写", ReplyFormatInspector.NormalizeAssistantHistory(edited) == 0 && ReferenceEquals(edited[0], bad));
    }

    static void Test_RequestNote()
    {
        Console.WriteLine("[4] 纠正贴在本轮输入末尾、不入库");

        var msg = new Message { Role = "user", Content = "你好", UnixTime = 1_700_000_000 };
        var plain = msg.DisplayContent;
        msg.RequestNote = "【格式纠正】测试";
        var withNote = msg.DisplayContent;
        Check("附注追加在发送内容的最末尾", withNote.StartsWith(plain) && withNote.EndsWith("[System: 【格式纠正】测试]"), withNote);
        Check("附注不写进 Content（入库的是 Content）", msg.Content == "你好");

        var copy = msg.WithContent("改了");
        Check("WithContent 是拷贝：原对象不变，其余字段保留", msg.Content == "你好" && copy.Content == "改了" && copy.UnixTime == msg.UnixTime);
    }
}
