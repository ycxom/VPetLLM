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
