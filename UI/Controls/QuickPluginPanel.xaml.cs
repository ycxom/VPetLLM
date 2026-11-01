using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.Utils.Localization;
using VPetLLM.Utils.Plugin;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 简易界面的插件列表：安装、更新、卸载，以及打开插件自己的设置。启用开关留在极客界面。
    /// 操作立即生效，不等窗口的"保存"。
    /// </summary>
    public partial class QuickPluginPanel : UserControl
    {
        private global::VPetLLM.VPetLLM _plugin = null!;
        private PluginStoreService _store = null!;

        /// <summary>插件装上或卸掉之后触发，窗口用它刷新摘要。</summary>
        public event Action? Changed;

        public QuickPluginPanel()
        {
            InitializeComponent();
        }

        public void Attach(global::VPetLLM.VPetLLM plugin)
        {
            _plugin = plugin;
            _store = new PluginStoreService(plugin);
        }

        private string T(string path, string fallback)
            => LanguageHelper.Get(path, _plugin.Settings.Language, fallback);

        /// <summary>
        /// 先显示本地插件，再联网补上商店的内容。全程禁用列表：
        /// 卸载要靠清单才知道附属文件有哪些，清单到手之前点下去会留下残余。
        /// </summary>
        public async Task RefreshAsync()
        {
            SetBusy(true, T("QuickSetup.PluginLoading", "正在获取插件列表…"));

            var online = false;
            try
            {
                var lang = _plugin.Settings.Language;
                var items = _store.BuildLocalItems(lang);
                ShowItems(items);

                PluginStoreService.MergeCatalog(items, await _store.FetchCatalogAsync(), lang);
                ShowItems(items);
                online = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"简易界面获取插件列表失败: {ex.Message}");
            }

            SetBusy(false, online
                ? string.Empty
                : T("QuickSetup.PluginOffline", "连不上插件商店，只显示已安装的插件。可以到极客界面的插件页换一个代理地址。"));
        }

        private void ShowItems(Dictionary<string, UnifiedPluginItem> items)
        {
            List_Plugins.ItemsSource = items.Values
                .OrderByDescending(p => p.IsUpdatable)
                .ThenByDescending(p => p.IsLocal)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SetBusy(bool busy, string state)
        {
            Btn_Refresh.IsEnabled = !busy;
            List_Plugins.IsEnabled = !busy;
            Tb_State.Text = state;
            Tb_State.ClearValue(TextBlock.ForegroundProperty);
        }

        private void ShowResult(string text, bool ok)
        {
            Tb_State.Text = text;
            Tb_State.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "ErrorBrush");
        }

        // 列表在操作期间是禁用的，一次只会跑一个，用不着完整窗口那条批量队列
        private async Task RunAsync(UnifiedPluginItem item, string doneText, Func<Task<PluginOperationResult>> operation)
        {
            SetBusy(true, string.Format(T("QuickSetup.PluginWorking", "正在处理 {0}…"), item.Name));

            PluginOperationResult result;
            try
            {
                result = await operation();
            }
            catch (Exception ex)
            {
                Logger.Log($"简易界面插件操作失败 [{item.Name}]: {ex.Message}");
                result = PluginOperationResult.Fail(ex.Message);
            }

            await RefreshAsync();
            Changed?.Invoke();

            // 放在刷新之后：刷新会改写状态行，这条结果不能被盖掉
            if (result.Success)
                ShowResult(string.Format(doneText, item.Name), true);
            else
                ShowResult(string.Format(T("QuickSetup.PluginFailed", "{0} 没有完成：{1}"), item.Name, result.Reason), false);
        }

        private async void InstallOrUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is UnifiedPluginItem item)
                await RunAsync(item, T("QuickSetup.PluginInstalled", "{0} 已就绪"), () => _store.InstallOrUpdateAsync(item));
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is UnifiedPluginItem item)
                await RunAsync(item, T("QuickSetup.PluginUninstalled", "{0} 已卸载"), () => _store.UninstallAsync(item));
        }

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is not UnifiedPluginItem item) return;

            string? failure = null;

            // 优先走插件自己的入口：Terminal 的风险确认、日记的就绪检查都在这条路上，内嵌面板不经过它们
            if (item.CanRunSettingAction)
            {
                failure = await _store.RunSettingActionAsync(item);
            }
            else if (item.LocalPlugin is IPluginTab tab)
            {
                try
                {
                    ShowPanelWindow(tab);
                }
                catch (Exception ex)
                {
                    Logger.Log($"简易界面打开插件面板失败 [{item.Name}]: {ex}");
                    failure = _store.DescribeSettingFailure(item.Name, ex.Message);
                }
            }

            if (failure is not null)
                ShowResult(failure, false);
        }

        /// <summary>只有内嵌面板的插件没有自己的窗口，这里给它套一个。</summary>
        private void ShowPanelWindow(IPluginTab tab)
        {
            var owner = Window.GetWindow(this);
            var opened = owner?.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Tag == tab);
            if (opened is not null)
            {
                opened.Activate();
                return;
            }

            // 挂在简易界面名下：退出时的窗口清扫只认本程序集的窗口类型，这个普通 Window 要靠父窗口带走
            var window = new Window
            {
                Title = tab.TabTitle,
                Width = 560,
                Height = 640,
                Owner = owner,
                Tag = tab,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = tab.CreatePanel()
            };
            window.Resources.MergedDictionaries.Add(Resources);
            window.SetResourceReference(BackgroundProperty, "BackgroundBrush");
            window.Show();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    }
}
