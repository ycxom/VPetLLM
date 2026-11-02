using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core.Services;
using VPetLLM.UI.Windows;

namespace VPetLLM.OllamaChecks;

internal static partial class Program
{
    private static string GridPage() => Page(
        Card("qwen2.5", "Plain.", "tools 4b 8b", "9M"),
        Card("qwen3", "Thinks.", "tools thinking 8b", "8M"),
        Card("qwen3-vl", "Sees.", "vision tools 8b", "7M"),
        Card("gemma", "One of two.", "vision 8b", "6M"),
        Card("gemma2", "Two of two.", "tools 8b", "5M"),
        Card("3dmodel", "Starts with a digit.", "tools 8b", ""),
        Card("4dmodel", "Starts with a digit.", "tools 8b", "1M"),
        Card("7dmodel", "Starts with a digit.", "tools 8b", "1M"),
        Card("talker", "Speaks.", "audio 8b", "4M"),
        Card("mixed", "Also hosted.", "tools cloud 8b", "3M"),
        Card("gizmo", "A badge nobody has seen.", "robotics 8b", "2M"));

    private static string ChipLine(IEnumerable<OllamaFilterChip> chips) => string.Join(" ", chips.Select(c => $"{c.Text}={c.Count}"));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static void CheckLibraryGrid()
    {
        const string state = "Items_StateChips", capability = "Items_CapabilityChips", series = "Items_SeriesChips";
        const string allStates = "所有模型=41 已下载=1";
        const string allCapabilities = "所有能力=41 看图=32 工具调用=8 深度思考=1 语音=1 robotics=1";
        const string allSeries = "所有系列=41 filler=30 qwen=3 其他=8";

        var net = new FakeOllamaCom { Html = GridPage(), Sizes = { ["qwen2.5:4b"] = 2 * GB, ["qwen2.5:8b"] = 5 * GB } };
        var api = new FakeApi { Running = true, Models = { new OllamaLocalModel("qwen3:8b", 5 * GB, "qwen") } };
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, FreeSpace = 200 * GB };
        var window = new ManagerWindow(installer, api, "", library: new OllamaLibrary(net, NewLibraryCache()));
        window.OpenLibrary();

        string Names() => string.Join(" ", window.Library.Select(r => r.Name));
        string Selected(string list) => string.Join(",", window.Chips(list).Where(c => c.Selected).Select(c => c.Text));
        void Layout()
        {
            window.Window.UpdateLayout();
            Pump(() => true);
        }

        // 系列
        Check("系列：取名字开头的字母", new[] { "qwen2.5", "qwen3-coder", "deepseek-r1", "3dmodel" }
            .Select(n => new OllamaLibraryModel(n, "", Array.Empty<string>(), Array.Empty<string>(), "").Series)
            .SequenceEqual(new[] { "qwen", "qwen", "deepseek", "" }));
        Check("系列：不写进缓存", !Newtonsoft.Json.JsonConvert.SerializeObject(window.Library[0].Model).Contains("Series"));

        // 三组筛选和计数
        Check("筛选：状态的数字", ChipLine(window.Chips(state)) == allStates, ChipLine(window.Chips(state)));
        Check("筛选：能力按数量排，不含 cloud，没翻译的照原文", ChipLine(window.Chips(capability)) == allCapabilities, ChipLine(window.Chips(capability)));
        Check("筛选：系列按数量排，不到三个归其他", ChipLine(window.Chips(series)) == allSeries, ChipLine(window.Chips(series)));
        Check("筛选：开始时选中“所有”", new[] { state, capability, series }.All(list => window.Chips(list)[0].Selected && window.Chips(list).Count(c => c.Selected) == 1));
        Check("重置：没有条件时不能点", !window.Enabled("Btn_LibraryReset"));

        window.PressChip(capability, "看图");
        Check("筛选：只留下这个能力", window.Library.Count == 32 && window.Library.All(r => r.Model.Capabilities.Contains("vision")), Names());
        Check("筛选：本组的数字不变", ChipLine(window.Chips(capability)) == allCapabilities, ChipLine(window.Chips(capability)));
        Check("筛选：别组的数字跟着变", ChipLine(window.Chips(state)) == "所有模型=32 已下载=0"
            && ChipLine(window.Chips(series)) == "所有系列=32 filler=30 qwen=1 其他=1", $"{ChipLine(window.Chips(state))} / {ChipLine(window.Chips(series))}");
        Check("筛选：选中的换了", Selected(capability) == "看图");
        Check("筛选：选中的看得出来", ReferenceEquals(window.ChipButton(capability, "看图").Foreground, window.Window.FindResource("PrimaryBrush"))
            && !ReferenceEquals(window.ChipButton(capability, "所有能力").Foreground, window.Window.FindResource("PrimaryBrush")));
        Check("重置：选了能力后可用", window.Enabled("Btn_LibraryReset"));

        window.PressChip(series, "qwen");
        Check("筛选：两组同时生效", Names() == "qwen3-vl", Names());
        Check("筛选：系列也换了选中", Selected(series) == "qwen", Selected(series));
        Check("筛选：系列自己的数字不变", ChipLine(window.Chips(series)) == "所有系列=32 filler=30 qwen=1 其他=1", ChipLine(window.Chips(series)));
        Check("筛选：数量是 0 的不能点", !window.ChipButton(capability, "语音").IsEnabled && !window.ChipButton(state, "已下载").IsEnabled);
        Check("筛选：数量不是 0 的能点", window.ChipButton(capability, "深度思考").IsEnabled);
        window.PressChip(series, "其他");
        Check("筛选：其他系列", Names() == "gemma", Names());
        window.PressChip(series, "所有系列");
        window.PressChip(capability, "所有能力");
        Check("筛选：选回“所有”", window.Library.Count == 41 && !window.Enabled("Btn_LibraryReset"), Names());

        window.PressChip(series, "qwen");
        Check("重置：选了系列后可用", window.Enabled("Btn_LibraryReset"));
        window.PressChip(series, "所有系列");

        window.PressChip(state, "已下载");
        Check("筛选：已下载，换了标签也算", Names() == "qwen3", Names());
        Check("筛选：状态也换了选中", Selected(state) == "已下载", Selected(state));
        Check("筛选：状态自己的数字不变", ChipLine(window.Chips(state)) == allStates, ChipLine(window.Chips(state)));
        Check("筛选：已下载算进别组的数字", ChipLine(window.Chips(capability)).StartsWith("所有能力=1 看图=0 工具调用=1")
            && ChipLine(window.Chips(series)) == "所有系列=1 filler=0 qwen=1 其他=0", $"{ChipLine(window.Chips(capability))} / {ChipLine(window.Chips(series))}");
        Check("重置：选了已下载后可用", window.Enabled("Btn_LibraryReset"));
        window.Search("nothing-like-this");
        Check("筛选：搜索也算进数字", ChipLine(window.Chips(state)) == "所有模型=0 已下载=0", ChipLine(window.Chips(state)));
        Check("筛选：没有结果时选中的还能点", window.ChipButton(state, "已下载").IsEnabled && !window.ChipButton(state, "所有模型").IsEnabled);
        Check("筛选：没有结果要说", window.Text("Tb_LibraryState") == "没有找到匹配的模型。", window.Text("Tb_LibraryState"));
        window.Search("");
        window.PressChip(capability, "看图");
        window.PressChip(series, "filler");
        window.Search("number 1");
        window.Click("Btn_LibraryReset");
        Pump(() => true);
        Check("重置：全部清掉", window.Library.Count == 41 && window.Find<TextBox>("Tb_LibrarySearch").Text == ""
            && ChipLine(window.Chips(state)) == allStates && ChipLine(window.Chips(capability)) == allCapabilities && ChipLine(window.Chips(series)) == allSeries
            && new[] { state, capability, series }.All(list => window.Chips(list)[0].Selected), Names());
        window.Search("qwen");
        Check("重置：只有搜索词时也可用", window.Enabled("Btn_LibraryReset"));
        window.Search("");

        // 排序
        var popular = Names();
        window.SortLibrary("Rb_SortName");
        Check("排序：按名称", window.Library.Select(r => r.Name).SequenceEqual(window.Library.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal))
            && window.Library[0].Name == "3dmodel", Names());
        window.PressChip(series, "qwen");
        Check("排序：筛选时也按名称", Names() == "qwen2.5 qwen3 qwen3-vl", Names());
        window.PressChip(series, "所有系列");
        window.SortLibrary("Rb_SortPopular");
        Check("排序：切回热度是页面顺序", Names() == popular && window.Library[0].Name == "qwen2.5", Names());

        // 分排
        Check("卡片：打开时按实际宽度分列", window.Blocks[0].Columns == 3, window.Blocks[0].Columns.ToString());
        Check("卡片：每排放满", window.Blocks.SkipLast(1).All(b => b.Rows.Count == 3) && window.Blocks.Count == 14 && window.Blocks[^1].Rows.Count == 2, window.Blocks.Count.ToString());
        Check("卡片：只生成看得见的排", window.RealizedRows() is > 3 and < 41, window.RealizedRows().ToString());
        var first = window.CardBounds("qwen2.5");
        var second = window.CardBounds("qwen3");
        var below = window.CardBounds("gemma");
        Check("卡片：同排左右排开", Near(first.Top, second.Top) && Near(second.Left, first.Right), $"{first} {second}");
        Check("卡片：下一排另起一行", Near(below.Left, first.Left) && Near(below.Top, first.Bottom), $"{first} {below}");

        var list = window.Find<ListBox>("Items_Library");
        window.Search("gemma");
        var one = window.CardBounds("gemma");
        var two = window.CardBounds("gemma2");
        Check("卡片：不满一排时靠左，不拉宽", Near(one.Width, two.Width) && Near(two.Left, one.Right) && Near(one.Left, first.Left) && one.Width < list.ActualWidth / 2, $"{one} {two}");
        window.Search("");

        var seen = new List<int>();
        // 滚动条占的宽度算在内，两张卡片的临界点上也要放得下
        var fixedWidth = window.Window.ActualWidth - list.ActualWidth + SystemParameters.VerticalScrollBarWidth;
        foreach (var width in new[] { 720.0, 800, 900, 1000, 1120, 1300, 1500, fixedWidth + 252 * 3 - 1, fixedWidth + 252 * 3 + 1 })
        {
            window.Window.Width = width;
            Layout();
            var columns = window.Blocks[0].Columns;
            var room = list.ActualWidth - SystemParameters.VerticalScrollBarWidth;
            seen.Add(columns);
            Check($"卡片：{width} 宽时放得下也不浪费", room / columns >= 252 && room / (columns + 1) < 252, $"{columns} 列，宽 {room}");
            // 列表自带 1px 内边距，所以比 252 少一点点
            Check($"卡片：{width} 宽时卡片不窄于 240", window.CardBounds("qwen2.5").Width >= 251, window.CardBounds("qwen2.5").ToString());
            Check($"卡片：{width} 宽时模型一个不少", window.Library.Count == 41 && window.Blocks.All(b => b.Columns == columns)
                && window.Blocks.SkipLast(1).All(b => b.Rows.Count == columns) && window.Blocks[^1].Rows.Count <= columns);
        }
        Check("卡片：窄的时候一列", seen[0] == 1 && seen.Contains(2) && seen.Contains(3), string.Join(",", seen));

        window.Window.Width = 1120;
        Layout();
        var built = list.ItemsSource;
        window.Window.Width = 1130;
        window.Window.Height = 650;
        Layout();
        Check("卡片：列数没变不重建", ReferenceEquals(list.ItemsSource, built));
        window.Window.Height = 720;
        Layout();

        // 换条件回到开头
        void BackToTop(string name, Action change)
        {
            window.ScrollLibraryToEnd();
            Check($"回到开头：{name}（前提：确实滚下去了）", window.LibraryViewer.VerticalOffset > 0);
            change();
            Pump(() => window.LibraryViewer.VerticalOffset == 0);
            Check($"回到开头：{name}", window.LibraryViewer.VerticalOffset == 0, window.LibraryViewer.VerticalOffset.ToString());
        }

        BackToTop("点筛选", () => window.PressChip(capability, "看图"));
        BackToTop("搜索", () => window.Search("filler"));
        BackToTop("排序", () => window.SortLibrary("Rb_SortName"));
        BackToTop("重置", () => { window.Click("Btn_LibraryReset"); Pump(() => true); });
        window.SortLibrary("Rb_SortPopular");

        // 下载完筛选跟着变，看到的位置不动
        window.ScrollLibraryToEnd();
        var offset = window.LibraryViewer.VerticalOffset / 2;
        window.LibraryViewer.ScrollToVerticalOffset(offset);
        Layout();
        api.Models.Add(new OllamaLocalModel("gemma:8b", 5 * GB, "gemma"));
        window.Click("Btn_Refresh");
        Pump(() => window.Chips(state)[1].Count == 2);
        Layout();
        Check("已下载：刷新后数字跟着变", window.Chips(state)[1].Count == 2, ChipLine(window.Chips(state)));
        Check("已下载：刷新后位置不动", Math.Abs(window.LibraryViewer.VerticalOffset - offset) < 1, $"{window.LibraryViewer.VerticalOffset} vs {offset}");
        built = list.ItemsSource;
        var refreshed = api.TagRequests;
        window.Click("Btn_Refresh");
        var settle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow >= settle);
        Check("已下载：没变化不重建", api.TagRequests > refreshed && ReferenceEquals(list.ItemsSource, built));

        window.PressChip(state, "已下载");
        Check("已下载：列出下载过的", Names() == "qwen3 gemma", Names());
        api.Models.RemoveAll(m => m.Name == "gemma:8b");
        window.Click("Btn_Refresh");
        Pump(() => window.Library.Count == 1);
        Check("已下载：删掉后立刻消失", Names() == "qwen3", Names());
        window.Click("Btn_LibraryReset");
        Pump(() => true);

        // 卡片内容
        Pump(() => window.Library[0].Selected.SizeBytes > 0);
        Layout();
        var card = window.CardTexts("qwen2.5");
        Check("卡片：首字母", card.Contains("Q"), string.Join("|", card));
        Check("卡片：能力、下载量、体积", card.Contains("工具调用") && card.Contains("9M") && card.Contains("5.0 GB"), string.Join("|", card));
        window.PickLibrary("qwen2.5", 0);
        Check("卡片：换规格后体积跟着变", window.CardTexts("qwen2.5").Contains("2.0 GB"), string.Join("|", window.CardTexts("qwen2.5")));

        window.Search("3dmodel");
        var bare = window.CardTexts("3dmodel");
        Check("卡片：没有数据写破折号", bare.Contains("3") && bare.Count(t => t == "—") == 2, string.Join("|", bare));
        window.Search("mixed");
        Check("卡片：能力里没有 cloud", window.CardTexts("mixed").Contains("工具调用"), string.Join("|", window.CardTexts("mixed")));
        window.Search("gizmo");
        Check("卡片：没翻译的能力照原文", window.CardTexts("gizmo").Contains("robotics"), string.Join("|", window.CardTexts("gizmo")));
        window.Search("");

        // 筛选栏比窗口长时自己滚，不能带着卡片一起动
        window.Window.Height = window.Window.MinHeight;
        Layout();
        Check("卡片：最矮时也能看全一张", window.CardBounds("qwen2.5").Bottom <= list.ActualHeight, $"{window.CardBounds("qwen2.5")} / {list.ActualHeight}");
        window.Window.Width = window.Window.MinWidth;
        Layout();
        Check("卡片：最小窗口也能看全一张", window.CardBounds("qwen2.5").Bottom <= list.ActualHeight, $"{window.CardBounds("qwen2.5")} / {list.ActualHeight}");
        Pump(() => window.FilterViewer.ScrollableHeight > 0);
        var chip = window.ChipButton(state, "所有模型");
        Check("筛选栏：滚轮归它自己", Wheel(chip, -120));
        Pump(() => window.FilterViewer.VerticalOffset > 0);
        Check("筛选栏：动的是筛选栏", window.FilterViewer.VerticalOffset > 0 && window.LibraryViewer.VerticalOffset == 0,
            $"{window.FilterViewer.VerticalOffset} {window.LibraryViewer.VerticalOffset}");

        Check("模型库：没有不认识的地址", net.Unexpected.IsEmpty, string.Join(", ", net.Unexpected));
        window.Close();
    }

    private static void CheckCatalogGrid()
    {
        const string fits = "Items_CatalogFitChips", states = "Items_CatalogStateChips";
        const string allFits = "所有情况=7 能流畅运行=2 能跑，但比较慢=1 这台电脑带不动=4";
        const string smooth = "能流畅运行", slow = "能跑，但比较慢", tooLarge = "这台电脑带不动";

        var api = new FakeApi
        {
            Running = true,
            Models = { new OllamaLocalModel("llama3.2:3b", 2 * GB, "llama"), new OllamaLocalModel("deepseek-r1:7b", 4 * GB, "deepseek") }
        };
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, FreeSpace = 200 * GB };
        var window = new ManagerWindow(installer, api, "", hardware: new OllamaHardware(8 * GB, 0, "", 8));
        window.Find<TabControl>("Tabs").SelectedIndex = 1;
        void Layout()
        {
            window.Window.UpdateLayout();
            Pump(() => true);
        }
        Layout();

        var scroll = window.Find<ScrollViewer>("Scroll_Catalog");
        var cards = window.Find<ItemsControl>("Items_Catalog");
        FrameworkElement Card(string name) => (FrameworkElement)cards.ItemContainerGenerator.ContainerFromItem(window.Catalog.Single(r => r.Name == name));
        Border CardBorder(string name) => Descendants(Card(name)).OfType<Border>().First(b => b.Style == window.Window.FindResource("OllamaHighlightCard"));
        List<string> CardTexts(string name) => Descendants(Card(name)).OfType<TextBlock>().Select(t => t.Text).ToList();
        string Names() => string.Join(" ", window.Catalog.Select(r => r.Name));
        string Selected(string list) => string.Join(",", window.Chips(list).Where(c => c.Selected).Select(c => c.Text));
        int Columns()
        {
            Layout();
            var tops = window.Catalog.Select(r => Card(r.Name).TranslatePoint(new Point(0, 0), cards).Y).ToList();
            return tops.Count(y => Near(y, tops[0]));
        }

        // 筛选和计数
        Check("推荐页：全部列出", window.Catalog.Count == OllamaModelCatalog.Models.Count, Names());
        Check("推荐页：数量", window.Text("Tb_CatalogState") == "共 7 个模型", window.Text("Tb_CatalogState"));
        Check("推荐页：适配的数字", ChipLine(window.Chips(fits)) == allFits, ChipLine(window.Chips(fits)));
        Check("推荐页：状态的数字", ChipLine(window.Chips(states)) == "所有模型=7 已下载=2", ChipLine(window.Chips(states)));
        Check("推荐页：开始时选中“所有”", Selected(fits) == "所有情况" && Selected(states) == "所有模型", $"{Selected(fits)} {Selected(states)}");
        Check("推荐页：没有条件时不能重置", !window.Enabled("Btn_CatalogReset"));
        Check("推荐页：推荐的排最前", window.Catalog[0].Highlighted && window.Catalog.Count(r => r.Highlighted) == 1, Names());

        window.PressChip(fits, smooth);
        Check("推荐页：按适配筛", window.Catalog.Count == 2 && window.Catalog.All(r => r.Fit == OllamaFit.Smooth), Names());
        Check("推荐页：筛完数量跟着变", window.Text("Tb_CatalogState") == "共 2 个模型", window.Text("Tb_CatalogState"));
        Check("推荐页：适配自己的数字不变", ChipLine(window.Chips(fits)) == allFits, ChipLine(window.Chips(fits)));
        Check("推荐页：别组的数字跟着变", ChipLine(window.Chips(states)) == "所有模型=2 已下载=1", ChipLine(window.Chips(states)));
        Check("推荐页：选中的跟着变", Selected(fits) == smooth, Selected(fits));
        Check("推荐页：有条件时能重置", window.Enabled("Btn_CatalogReset"));
        Check("推荐页：筛完推荐还在最前", window.Catalog[0].Highlighted, Names());

        window.PressChip(fits, "所有情况");
        window.PressChip(states, "已下载");
        Check("推荐页：按状态筛", window.Catalog.Count == 2 && window.Catalog.All(r => r.Installed), Names());
        Check("推荐页：状态自己的数字不变", ChipLine(window.Chips(states)) == "所有模型=7 已下载=2", ChipLine(window.Chips(states)));
        Check("推荐页：已下载算进适配的数字", ChipLine(window.Chips(fits)) == "所有情况=2 能流畅运行=1 能跑，但比较慢=0 这台电脑带不动=1", ChipLine(window.Chips(fits)));
        Check("推荐页：数量是 0 的不能点", !window.ChipButton(fits, slow).IsEnabled && window.ChipButton(fits, tooLarge).IsEnabled);

        window.PressChip(fits, tooLarge);
        Check("推荐页：两组一起算", Names() == "deepseek-r1:7b", Names());
        window.Click("Btn_CatalogReset");
        Pump(() => true);
        Check("推荐页：重置回到全部", window.Catalog.Count == 7 && Selected(fits) == "所有情况" && Selected(states) == "所有模型" && !window.Enabled("Btn_CatalogReset"),
            $"{Names()} / {Selected(fits)} / {Selected(states)}");

        window.PressChip(fits, slow);
        window.PressChip(states, "已下载");
        Check("推荐页：没有结果时说一声", window.Catalog.Count == 0 && window.Text("Tb_CatalogState") == "没有找到匹配的模型。", window.Text("Tb_CatalogState"));
        Check("推荐页：没有结果时选中的还能点", window.ChipButton(states, "已下载").IsEnabled && window.ChipButton(fits, slow).IsEnabled);
        window.Click("Btn_CatalogReset");
        Pump(() => true);

        // 卡片
        var best = window.Catalog[0];
        var other = window.Catalog.First(r => !r.Highlighted && !r.Installed);
        var downloaded = window.Catalog.First(r => r.Installed);
        Layout();
        Check("卡片：推荐的边框不一样", ReferenceEquals(CardBorder(best.Name).BorderBrush, window.Window.FindResource("PrimaryBrush"))
            && !ReferenceEquals(CardBorder(other.Name).BorderBrush, window.Window.FindResource("PrimaryBrush")));
        Check("卡片：写着名字、适配、体积", CardTexts(other.Name).Contains(other.Name) && CardTexts(other.Name).Contains(other.Badge) && CardTexts(other.Name).Contains(other.Size)
            && CardTexts(other.Name).Contains(other.Name[..1].ToUpperInvariant()), string.Join("|", CardTexts(other.Name)));
        Check("卡片：写着下没下", CardTexts(other.Name).Contains("未下载") && CardTexts(downloaded.Name).Contains("已下载"), string.Join("|", CardTexts(downloaded.Name)));

        // 列数跟着宽度走，卡片不能比模型库的窄
        var fixedWidth = window.Window.ActualWidth - scroll.ActualWidth + SystemParameters.VerticalScrollBarWidth;
        foreach (var (width, columns) in new[] { (fixedWidth + 252 * 3, 3), (fixedWidth + 252 * 3 - 1, 2), (fixedWidth + 252 * 2, 2), (fixedWidth + 252 * 2 - 1, 1) })
        {
            window.Window.Width = width;
            Check($"推荐页：{width:0} 宽排 {columns} 列", Columns() == columns, Columns().ToString());
            Check($"推荐页：{width:0} 宽卡片够宽", CardBorder(best.Name).ActualWidth >= 239, CardBorder(best.Name).ActualWidth.ToString());
        }

        window.Window.Width = window.Window.MinWidth;
        window.Window.Height = window.Window.MinHeight;
        Check("推荐页：最小窗口排一列", Columns() == 1, Columns().ToString());
        Check("推荐页：最小窗口看得全一张卡", CardBorder(best.Name).ActualHeight + 12 <= scroll.ViewportHeight, $"{CardBorder(best.Name).ActualHeight} / {scroll.ViewportHeight}");

        // 滚动位置
        scroll.ScrollToVerticalOffset(120);
        Layout();
        Check("推荐页：滚得动", scroll.VerticalOffset > 0, scroll.VerticalOffset.ToString());
        var before = scroll.VerticalOffset;
        window.Click("Btn_Refresh");
        var settle = DateTime.UtcNow.AddMilliseconds(200);
        Pump(() => DateTime.UtcNow >= settle);
        Layout();
        Check("推荐页：刷新不回顶", Near(scroll.VerticalOffset, before), scroll.VerticalOffset.ToString());
        window.PressChip(states, "已下载");
        Layout();
        window.PressChip(states, "所有模型");
        Layout();
        Check("推荐页：点筛选回顶", scroll.VerticalOffset == 0, scroll.VerticalOffset.ToString());
        scroll.ScrollToVerticalOffset(120);
        Layout();
        window.PressChip(fits, smooth);
        scroll.ScrollToVerticalOffset(120);
        Layout();
        window.Click("Btn_CatalogReset");
        Layout();
        Check("推荐页：重置回顶", scroll.VerticalOffset == 0, scroll.VerticalOffset.ToString());

        // 输入框的提示
        var hint = Descendants(window.Find<Grid>("Panel_Custom")).OfType<TextBlock>().First(t => t.Text.Contains("其他模型"));
        var custom = window.Find<TextBox>("Tb_CustomModel");
        Check("手填：没填时有提示", hint.Visibility == Visibility.Visible);
        custom.Text = "alpha:8b";
        Layout();
        Check("手填：填了提示就走", hint.Visibility != Visibility.Visible);
        custom.Text = "";

        // 别的电脑判断不了适配，这组筛选不算数，切回本机再恢复
        window.PressChip(fits, slow);
        const string remoteUrl = "http://192.168.1.5:11434";
        Call(window.Window, "Rebind", remoteUrl, "", (Func<string, string, bool>)((_, _) => true));
        Pump(() => window.Catalog.Count == 7);
        Check("远程：没有适配这组", !window.Shown("Panel_CatalogFit"));
        Check("远程：不标推荐", !window.Catalog.Any(r => r.Highlighted), Names());
        Check("远程：之前选的适配不算", window.Catalog.Count == 7 && !window.Enabled("Btn_CatalogReset"), Names());
        Call(window.Window, "Rebind", Url, "", (Func<string, string, bool>)((_, _) => true));
        Pump(() => window.Catalog.Count == 1);
        Check("切回本机：适配这组回来", window.Shown("Panel_CatalogFit") && Selected(fits) == slow && window.Enabled("Btn_CatalogReset"), Selected(fits));
        window.Close();

        // 两页共用一个按钮事件，点这页的筛选不能把那页滚回顶
        var net = new FakeOllamaCom { Html = GridPage(), Sizes = { ["qwen2.5:4b"] = 2 * GB, ["qwen2.5:8b"] = 5 * GB } };
        var both = new ManagerWindow(installer, api, "", library: new OllamaLibrary(net, NewLibraryCache()));
        both.OpenLibrary();
        both.LibraryViewer.ScrollToVerticalOffset(3);
        both.Window.UpdateLayout();
        Pump(() => both.LibraryViewer.VerticalOffset > 0);
        var libraryOffset = both.LibraryViewer.VerticalOffset;
        both.Find<TabControl>("Tabs").SelectedIndex = 1;
        both.Window.UpdateLayout();
        Pump(() => true);
        both.PressChip(states, "已下载");
        both.PressChip(states, "所有模型");
        both.Find<TabControl>("Tabs").SelectedIndex = 2;
        both.Window.UpdateLayout();
        Pump(() => true);
        Check("推荐页：筛选不动模型库", libraryOffset > 0 && Near(both.LibraryViewer.VerticalOffset, libraryOffset), $"{both.LibraryViewer.VerticalOffset} / {libraryOffset}");
        both.Close();
    }

    private static void CheckLocalGrid()
    {
        const string fits = "Items_LocalFitChips", purposes = "Items_LocalPurposeChips";
        const string allFits = "所有情况=5 能流畅运行=2 能跑，但比较慢=1 这台电脑带不动=1";
        const string allPurposes = "所有模型=5 能聊天=4 不能聊天=1";
        const string smooth = "能流畅运行", slow = "能跑，但比较慢", tooLarge = "这台电脑带不动";

        var api = new FakeApi
        {
            Running = true,
            Models =
            {
                new OllamaLocalModel("alpha:1b", GB, "alpha"),
                new OllamaLocalModel("BETA:2b", GB + GB / 2, "beta"),
                new OllamaLocalModel("nomic-embed-text:latest", GB / 4, "bert"),
                new OllamaLocalModel("delta:3b", (long)(2.5 * GB), "delta"),
                new OllamaLocalModel("gamma:9b", 9 * GB, "")
            }
        };
        var installer = new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = api, FreeSpace = 200 * GB };
        var window = new ManagerWindow(installer, api, "gamma:9b", hardware: new OllamaHardware(8 * GB, 0, "", 8));
        window.Find<TabControl>("Tabs").SelectedIndex = 3;
        void Layout()
        {
            window.Window.UpdateLayout();
            Pump(() => true);
        }
        Layout();

        var search = window.Find<TextBox>("Tb_LocalSearch");
        var scroll = window.Find<ScrollViewer>("Scroll_Local");
        var cards = window.Find<ItemsControl>("Items_Local");
        FrameworkElement Card(string name) => (FrameworkElement)cards.ItemContainerGenerator.ContainerFromItem(window.Local.Single(r => r.Name == name));
        Border CardBorder(string name) => Descendants(Card(name)).OfType<Border>().First(b => b.Style == window.Window.FindResource("OllamaHighlightCard"));
        List<string> CardTexts(string name) => Descendants(Card(name)).OfType<TextBlock>().Select(t => t.Text).ToList();
        string Names() => string.Join(" ", window.Local.Select(r => r.Name));
        string Selected(string list) => string.Join(",", window.Chips(list).Where(c => c.Selected).Select(c => c.Text));
        int Columns()
        {
            Layout();
            var tops = window.Local.Select(r => Card(r.Name).TranslatePoint(new Point(0, 0), cards).Y).ToList();
            return tops.Count(y => Near(y, tops[0]));
        }

        // 筛选和计数
        Check("我的模型：在用的排最前", window.Local[0].Name == "gamma:9b" && window.Local.Count == 5, Names());
        Check("我的模型：其余保持原来的顺序", Names() == "gamma:9b alpha:1b BETA:2b delta:3b nomic-embed-text:latest", Names());
        Check("我的模型：数量和占用", window.Text("Tb_LocalState") == "共 5 个模型，占用 14.3 GB", window.Text("Tb_LocalState"));
        Check("我的模型：运行情况的数字", ChipLine(window.Chips(fits)) == allFits, ChipLine(window.Chips(fits)));
        Check("我的模型：用途的数字", ChipLine(window.Chips(purposes)) == allPurposes, ChipLine(window.Chips(purposes)));
        Check("我的模型：开始时选中“所有”", Selected(fits) == "所有情况" && Selected(purposes) == "所有模型", $"{Selected(fits)} {Selected(purposes)}");
        Check("我的模型：没有条件时不能重置", !window.Enabled("Btn_LocalReset"));

        window.PressChip(fits, smooth);
        Check("我的模型：按运行情况筛", Names() == "alpha:1b BETA:2b", Names());
        Check("我的模型：占用跟着筛选变", window.Text("Tb_LocalState") == "共 2 个模型，占用 2.5 GB", window.Text("Tb_LocalState"));
        Check("我的模型：运行情况自己的数字不变", ChipLine(window.Chips(fits)) == allFits, ChipLine(window.Chips(fits)));
        Check("我的模型：别组的数字跟着变", ChipLine(window.Chips(purposes)) == "所有模型=2 能聊天=2 不能聊天=0", ChipLine(window.Chips(purposes)));
        Check("我的模型：数量是 0 的不能点", !window.ChipButton(purposes, "不能聊天").IsEnabled && window.ChipButton(purposes, "能聊天").IsEnabled);
        Check("我的模型：有条件时能重置", window.Enabled("Btn_LocalReset"));

        window.PressChip(fits, "所有情况");
        window.PressChip(purposes, "不能聊天");
        Check("我的模型：按用途筛", Names() == "nomic-embed-text:latest", Names());
        Check("我的模型：用途自己的数字不变", ChipLine(window.Chips(purposes)) == allPurposes, ChipLine(window.Chips(purposes)));
        Check("我的模型：不能聊天的不算运行情况", ChipLine(window.Chips(fits)) == "所有情况=1 能流畅运行=0 能跑，但比较慢=0 这台电脑带不动=0", ChipLine(window.Chips(fits)));
        window.PressChip(purposes, "能聊天");
        Check("我的模型：能聊天", window.Local.Count == 4 && window.Local.All(r => r.IsChat), Names());
        window.PressChip(fits, tooLarge);
        Check("我的模型：两组一起算", Names() == "gamma:9b", Names());
        window.Click("Btn_LocalReset");
        Pump(() => true);
        Check("我的模型：重置回到全部", window.Local.Count == 5 && Selected(fits) == "所有情况" && Selected(purposes) == "所有模型" && !window.Enabled("Btn_LocalReset"),
            $"{Names()} / {Selected(fits)} / {Selected(purposes)}");

        // 搜索
        var hint = ((Panel)search.Parent).Children.OfType<TextBlock>().Single();
        Check("我的模型：没搜时有提示", hint.Visibility == Visibility.Visible);
        search.Text = "beta:2";
        Pump(() => true);
        Check("我的模型：搜了提示就走", hint.Visibility != Visibility.Visible);
        Check("我的模型：搜索不分大小写", Names() == "BETA:2b", Names());
        Check("我的模型：搜索算进数字", ChipLine(window.Chips(purposes)) == "所有模型=1 能聊天=1 不能聊天=0", ChipLine(window.Chips(purposes)));
        Check("我的模型：有搜索时能重置", window.Enabled("Btn_LocalReset"));
        search.Text = "bert";
        Pump(() => true);
        Check("我的模型：系列也能搜", Names() == "nomic-embed-text:latest", Names());
        search.Text = "不存在";
        Pump(() => true);
        Check("我的模型：没有结果时说一声", window.Local.Count == 0 && window.Text("Tb_LocalState") == "没有找到匹配的模型。", window.Text("Tb_LocalState"));
        window.Click("Btn_LocalReset");
        Pump(() => true);
        Check("我的模型：重置清空搜索", search.Text == "" && window.Local.Count == 5, Names());

        // 卡片
        Layout();
        Check("卡片：在用的边框不一样", ReferenceEquals(CardBorder("gamma:9b").BorderBrush, window.Window.FindResource("PrimaryBrush"))
            && !ReferenceEquals(CardBorder("alpha:1b").BorderBrush, window.Window.FindResource("PrimaryBrush")));
        Check("卡片：写着名字、适配、体积、系列", new[] { "A", "alpha:1b", smooth, "1.0 GB", "alpha" }.All(CardTexts("alpha:1b").Contains), string.Join("|", CardTexts("alpha:1b")));
        Check("卡片：没有系列时写横线", CardTexts("gamma:9b").Contains("—"), string.Join("|", CardTexts("gamma:9b")));
        Check("卡片：不能聊天的不说跑得动", window.Local.Single(r => r.Name.StartsWith("nomic")) is { Badge: "", Fit: OllamaFit.Unknown }, window.Local.Single(r => r.Name.StartsWith("nomic")).ToString());
        Check("卡片：只有使用和删除两个按钮", Descendants(Card("alpha:1b")).OfType<Button>().Select(b => b.Content).SequenceEqual(new object[] { "使用", "删除" }));
        bool CanClickUse(string name) => Descendants(Card(name)).OfType<Button>().First().IsEnabled;
        Check("卡片：能用的才能点使用", CanClickUse("alpha:1b") && !CanClickUse("gamma:9b") && !CanClickUse("nomic-embed-text:latest"));
        Check("卡片：太大的模型也能看出来", window.Local[0].Badge == tooLarge, window.Local[0].ToString());

        // 列数：两页共用一个值，在别的页改了宽度，切过来也要对
        var fixedWidth = window.Window.ActualWidth - scroll.ActualWidth + SystemParameters.VerticalScrollBarWidth;
        foreach (var (width, columns) in new[] { (fixedWidth + 252 * 3, 3), (fixedWidth + 252 * 3 - 1, 2), (fixedWidth + 252 * 2 - 1, 1) })
        {
            window.Window.Width = width;
            Check($"我的模型：{width:0} 宽排 {columns} 列", Columns() == columns, Columns().ToString());
            Check($"我的模型：{width:0} 宽卡片够宽", CardBorder("alpha:1b").ActualWidth >= 239, CardBorder("alpha:1b").ActualWidth.ToString());
        }
        window.Find<TabControl>("Tabs").SelectedIndex = 2;
        Layout();
        window.Window.Width = fixedWidth + 252 * 3;
        Layout();
        window.Find<TabControl>("Tabs").SelectedIndex = 3;
        Check("我的模型：别的页改了宽度，切回来列数也对", Columns() == 3, Columns().ToString());

        window.Window.Width = window.Window.MinWidth;
        window.Window.Height = window.Window.MinHeight;
        Check("我的模型：最小窗口排一列", Columns() == 1, Columns().ToString());
        Check("我的模型：最小窗口看得全一张卡", CardBorder("alpha:1b").ActualHeight + 12 <= scroll.ViewportHeight, $"{CardBorder("alpha:1b").ActualHeight} / {scroll.ViewportHeight}");

        // 滚动位置
        scroll.ScrollToVerticalOffset(120);
        Layout();
        Check("我的模型：滚得动", scroll.VerticalOffset > 0, scroll.VerticalOffset.ToString());
        var before = scroll.VerticalOffset;
        window.Click("Btn_Refresh");
        var settle = DateTime.UtcNow.AddMilliseconds(200);
        Pump(() => DateTime.UtcNow >= settle);
        Layout();
        Check("我的模型：刷新不回顶", Near(scroll.VerticalOffset, before), scroll.VerticalOffset.ToString());
        window.PressChip(purposes, "能聊天");
        Layout();
        Check("我的模型：点筛选回顶", scroll.VerticalOffset == 0, scroll.VerticalOffset.ToString());
        window.PressChip(purposes, "所有模型");
        Layout();
        scroll.ScrollToVerticalOffset(120);
        Layout();
        search.Text = "a";
        Layout();
        Check("我的模型：搜索回顶", scroll.VerticalOffset == 0, scroll.VerticalOffset.ToString());
        search.Text = "";
        Layout();
        window.PressChip(purposes, "能聊天");
        Layout();
        scroll.ScrollToVerticalOffset(120);
        Layout();
        window.Click("Btn_LocalReset");
        Layout();
        Check("我的模型：重置回顶", scroll.VerticalOffset == 0, scroll.VerticalOffset.ToString());

        // 筛选中点使用：列表要跟着刷新，不能把用户的条件冲掉
        window.PressChip(purposes, "能聊天");
        window.ClickLocalUse("alpha:1b");
        Pump(() => window.Local[0].Name == "alpha:1b");
        Check("我的模型：点使用后排到最前", window.Local[0] is { Name: "alpha:1b", UseText: "使用中", Highlighted: true }, Names());
        Check("我的模型：点使用不冲掉筛选", Selected(purposes) == "能聊天" && window.Local.Count == 4, Names());

        // 别的电脑判断不了运行情况，这组筛选不算数，切回本机再恢复
        window.Click("Btn_LocalReset");
        Pump(() => true);
        window.PressChip(fits, slow);
        const string remoteUrl = "http://192.168.1.5:11434";
        Call(window.Window, "Rebind", remoteUrl, "", (Func<string, string, bool>)((_, _) => true));
        Pump(() => true);
        Check("远程：我的模型没有运行情况这组", !window.Shown("Panel_LocalFit"));
        Check("远程：我的模型之前选的不算", !window.Enabled("Btn_LocalReset"));
        Call(window.Window, "Rebind", Url, "", (Func<string, string, bool>)((_, _) => true));
        Pump(() => true);
        Check("切回本机：运行情况这组回来", window.Shown("Panel_LocalFit") && Selected(fits) == slow && window.Enabled("Btn_LocalReset"), Selected(fits));
        window.Close();

        // Rebind 换的是真客户端，要看远程的模型只能一开始就连假接口
        var far = new ManagerWindow(installer, api, url: remoteUrl, hardware: new OllamaHardware(8 * GB, 0, "", 8));
        Pump(() => far.Local.Count == 5);
        Check("远程：我的模型照常列出", far.Local.Count == 5 && far.Text("Tb_LocalState").StartsWith("共 5 个模型"), far.Text("Tb_LocalState"));
        Check("远程：我的模型不判断运行情况", far.Local.All(r => r is { Badge: "", Fit: OllamaFit.Unknown }), string.Join(" ", far.Local.Select(r => r.Badge)));
        Check("远程：一开始就没有运行情况这组", !far.Shown("Panel_LocalFit"));
        far.Close();

        // 空状态
        var none = new FakeApi { Running = true };
        var empty = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = none }, none, "");
        Check("我的模型：运行中但没模型", empty.Text("Tb_LocalState") == "还没有下载模型。", empty.Text("Tb_LocalState"));
        empty.Close();
        var stopped = new FakeApi();
        var off = new ManagerWindow(new FakeDeployInstaller(new OllamaInstallation(@"C:\Ollama", "0.40.13")) { Api = stopped }, stopped, "");
        Check("我的模型：没运行时说明原因", off.Text("Tb_LocalState").Contains("没有运行"), off.Text("Tb_LocalState"));
        off.Close();
    }
}
