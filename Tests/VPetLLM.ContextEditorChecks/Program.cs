using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.UI.Windows;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var tempDirectory = Path.Combine(Path.GetTempPath(), $"vpet-context-check-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDirectory);
var dbPath = Path.Combine(tempDirectory, "chat_history.db");
try
{
    using (var database = new ChatHistoryDatabase(dbPath))
    {
        database.AddMessages("check", new List<Message>
        {
            new() { Role = "system", Content = "hidden" },
            new() { Role = "user", Content = "one" },
            new() { Role = "assistant", Content = "two" },
            new() { Role = "user", Content = "three" }
        });

        Assert(database.GetEditingMessageCount("check", true) == 3, "editable count");
        Assert(database.GetEditingMessagesPage("check", true, 1, 2).Select(m => m.Content).SequenceEqual(["two", "three"]), "page boundary");
        Assert(database.GetEditingMessagesPage("check", true, 3, 2).Count == 0, "empty tail page");
        Assert(database.GetSystemMessages("check", true).Single().Content == "hidden", "system message");
    }

    var clean = new ContextEditorItem(0, "user");
    Assert(!clean.IsLoaded, "placeholder starts unloaded");
    clean.LoadFrom(new Message { Role = "user", Content = "content" });
    Assert(clean.IsLoaded && !clean.IsDirty, "loaded item state");
    clean.Unload();
    Assert(!clean.IsLoaded, "clean item unloads");

    var dirty = new ContextEditorItem(1, "assistant");
    dirty.LoadFrom(new Message { Role = "assistant", Content = "content" });
    dirty.Content = "changed";
    dirty.Unload();
    Assert(dirty.IsLoaded && dirty.IsDirty, "dirty item remains resident");

    // ── 来源识别与收纳 ──────────────────────────────────────────────────
    // user 角色里混着用户打的字和程序灌入的消息，后者不该顶着用户名显示。

    static ContextEditorItem Make(string content, string role = "user")
    {
        var item = new ContextEditorItem(new Message { Role = role, Content = content });
        item.SetDisplayNames("Pet", "Owner");
        return item;
    }

    var pluginResult = Make("[Plugin Result: ForegroundAppWatcher] The user is now using: explorer");
    Assert(pluginResult.IsSystemGenerated, "plugin result is system generated");
    Assert(pluginResult.RoleDisplay == "ForegroundAppWatcher", "plugin result shows plugin name");
    Assert(pluginResult.IsCollapsed && pluginResult.IsCollapsedView, "system message starts collapsed");
    Assert(!pluginResult.ShowSegments, "collapsed message hides segments");
    Assert(!pluginResult.CollapsedPreview.Contains("Plugin Result"), "preview strips the source marker");

    var seeScreen = Make("[屏幕内容]\n这是一张桌面截图。\n[/屏幕内容]");
    Assert(seeScreen.RoleDisplay == "看屏幕", "see-screen shows feature name");
    Assert(!seeScreen.CollapsedPreview.Contains('\n'), "preview is a single line");

    var ocr = Make("[屏幕内容 - 文字识别]\nhello");
    Assert(ocr.RoleDisplay == "看屏幕 · 文字识别", "ocr variant is distinguished");

    var systemEvent = Make("[System] 用户摸了摸头");
    Assert(systemEvent.RoleDisplay == "系统事件", "bare [System] shows a generic feature name");

    var merged = Make("[Plugin Result: A] a\n[Plugin Result: B] b");
    Assert(merged.RoleDisplay == "A 等 2 项", "aggregated feed-in reports the source count");

    // 用户真正打的字：名字仍是用户名，且不提供收纳
    var typed = Make("帮我看看屏幕");
    Assert(!typed.IsSystemGenerated, "typed text is not system generated");
    Assert(typed.RoleDisplay == "Owner", "typed text keeps the user name");
    Assert(!typed.CanCollapse && !typed.IsCollapsedView, "typed text is never collapsed");
    Assert(typed.ShowSegments, "typed text shows segments in simple mode");

    // 助手消息永远不算系统灌入
    var reply = Make("<|say_begin|> hi <|say_end|>", "assistant");
    Assert(!reply.IsSystemGenerated && reply.RoleDisplay == "Pet", "assistant keeps the AI name");
    Assert(!reply.CanCollapse, "assistant is never collapsible");

    // 完全模式不考虑收纳：开关消失、原文照常可编辑
    var full = Make("[Plugin Result: X] payload");
    Assert(full.CanCollapse, "collapsible in simple mode");
    full.IsSimpleMode = false;
    Assert(!full.CanCollapse && !full.IsCollapsedView, "full mode never collapses");
    Assert(!full.ShowSegments && full.IsFullMode, "full mode shows the raw editor");
    full.IsSimpleMode = true;
    Assert(full.CanCollapse && full.IsCollapsedView, "returning to simple mode restores collapse");

    // 展开是用户的显式动作
    full.ToggleCollapsed();
    Assert(!full.IsCollapsed && !full.IsCollapsedView, "toggle expands");
    Assert(full.ShowSegments, "expanded message shows segments");

    // 收纳纯属显示状态，不该把消息标脏
    Assert(!full.IsDirty, "collapsing does not dirty the message");

    Console.WriteLine("Context editor checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Context editor checks failed: {ex.Message}");
    return 1;
}
finally
{
    try
    {
        if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
    }
    catch
    {
    }
}
