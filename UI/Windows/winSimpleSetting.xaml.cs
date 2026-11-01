using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 简易设置：只留下大多数人会用到的选项，其余都在完整设置里。
    /// 和完整窗口不同，这里是显式保存：新手更习惯"点保存才算数"。
    /// </summary>
    public partial class winSimpleSetting : Window
    {
        private readonly global::VPetLLM.VPetLLM _plugin;

        private global::VPetLLM.UI.Controls.QuickChannelPanel ChannelPanel
            => (global::VPetLLM.UI.Controls.QuickChannelPanel)Host_Channel.Child;

        private global::VPetLLM.UI.Controls.QuickProfilePanel ProfilePanel
            => (global::VPetLLM.UI.Controls.QuickProfilePanel)Host_Profile.Child;

        private global::VPetLLM.UI.Controls.QuickPluginPanel PluginPanel
            => (global::VPetLLM.UI.Controls.QuickPluginPanel)Host_Plugins.Child;

        private bool _pluginsLoaded;

        private string T(string path, string fallback)
            => LanguageHelper.Get(path, _plugin.Settings.Language, fallback);

        public winSimpleSetting(global::VPetLLM.VPetLLM plugin)
        {
            _plugin = plugin;
            InitializeComponent();

            var settings = _plugin.Settings;
            ChannelPanel.Attach(settings, () => _plugin.ChatCore as RoutedChatCore);
            ProfilePanel.Load(settings);
            PluginPanel.Attach(_plugin);
            PluginPanel.Changed += RefreshPluginSummary;

            Cb_EnableAction.IsChecked = settings.EnableAction;
            Cb_EnableBuy.IsChecked = settings.EnableBuy;
            Cb_ReadAloud.IsChecked = settings.TTS.IsEnabled;
            Cb_VoiceInput.IsChecked = settings.ASR.IsEnabled;
            Cb_ShowOnboardingOnStartup.IsChecked = settings.ShowOnboardingOnStartup;

            var ttsPlugin = _plugin.IsVPetTTSPluginDetected;
            Cb_ReadAloud.IsEnabled = !ttsPlugin;
            Tb_ReadAloudHint.Visibility = ttsPlugin ? Visibility.Visible : Visibility.Collapsed;
            Tb_VoiceInputHint.Text = string.Format(T("QuickSetup.VoiceInputHint", "按一下 {0} 开始说话，再按一下结束。"),
                $"{settings.ASR.HotkeyModifiers}+{settings.ASR.HotkeyKey}");

            RefreshOverview();
            RefreshPluginSummary();
            // 还没有能用的服务时直接摊开，保存就是在选服务，不能让用户对着折叠的卡片点保存
            if (ChannelPanel.NeedsApply)
                Host_Channel.Visibility = Visibility.Visible;

            Closed += (_, _) => _plugin.SimpleSettingWindow = null;
        }

        private void RefreshOverview()
        {
            var primary = QuickSetupService.GetPrimaryChannel(_plugin.Settings);
            Tb_ChannelSummary.Text = primary switch
            {
                null => T("QuickSetup.NoChannel", "还没有选择 AI 服务"),
                Setting.FreeNodeSetting => T("QuickSetup.FreeSummary", "免费服务"),
                _ when !QuickSetupService.IsChannelConfigured(primary) => T("QuickSetup.NoChannel", "还没有选择 AI 服务"),
                _ => string.IsNullOrWhiteSpace(primary.Model) ? primary.Name : $"{primary.Name} · {primary.Model}"
            };
            Panel_NotDefault.Visibility = _plugin.IsVPetLLMDefaultPlugin() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshPluginSummary()
        {
            Tb_PluginSummary.Text = string.Format(T("QuickSetup.PluginSummary", "已安装 {0} 个插件"),
                _plugin.Plugins.Count + _plugin.FailedPlugins.Count);
        }

        /// <summary>写回全部设置。校验不通过时什么都不改，让用户留在本页修正。</summary>
        private bool SaveAll()
        {
            var settings = _plugin.Settings;
            var oldProvider = settings.Provider;

            if (ChannelPanel.NeedsApply)
            {
                var draft = ChannelPanel.BuildDraft();
                if (draft is null)
                {
                    // 保存按钮在所有页共用，用户可能在别的页点的，不切回来就看不到错在哪
                    Tabs.SelectedItem = Tab_Channel;
                    Host_Channel.Visibility = Visibility.Visible;
                    ShowStatus(T("QuickSetup.FixChannel", "AI 服务还没填好，请看上面的提示。"), false);
                    return false;
                }
                QuickSetupService.ApplyChannel(settings, draft);
            }

            ProfilePanel.Save(settings);
            settings.EnableAction = Cb_EnableAction.IsChecked == true;
            settings.EnableBuy = Cb_EnableBuy.IsChecked == true;
            settings.ShowOnboardingOnStartup = Cb_ShowOnboardingOnStartup.IsChecked == true;
            QuickSetupService.ApplyVoice(settings, Cb_ReadAloud.IsChecked == true, Cb_VoiceInput.IsChecked == true);

            _plugin.ApplyQuickSettings(oldProvider);

            ChannelPanel.Reload();
            Host_Channel.Visibility = Visibility.Collapsed;
            RefreshOverview();
            return true;
        }

        private void ShowStatus(string text, bool ok)
        {
            Tb_Status.Text = text;
            Tb_Status.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "ErrorBrush");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (SaveAll())
                ShowStatus(T("QuickSetup.Saved", "已保存"), true);
        }

        private void ChangeChannel_Click(object sender, RoutedEventArgs e)
        {
            Host_Channel.Visibility = Host_Channel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // 页内 ComboBox 的 SelectionChanged 也会冒泡到这里，所以只看插件页是否选中
        private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切到插件页才加载：要算插件文件的哈希、还要联网，多数人打开设置并不是来看这个
            if (!Tab_Plugins.IsSelected || _pluginsLoaded) return;
            _pluginsLoaded = true;
            await PluginPanel.RefreshAsync();
        }

        private void SetDefault_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.SetAsDefaultChatPlugin())
            {
                RefreshOverview();
                ShowStatus(T("QuickSetup.SetDefaultOk", "设置好了，现在可以和桌宠聊天了。"), true);
            }
            else
            {
                ShowStatus(T("QuickSetup.SetDefaultFail", "没能自动设置。请打开桌宠的设置，在“聊天设置”里选择自定义接口并选中 VPetLLM。"), false);
            }
        }

        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            // 离开前先保存，别把用户刚填的内容悄悄丢掉；填错了就留在本页
            if (!SaveAll()) return;
            Close();
            _plugin.SwitchSettingsMode(simple: false);
        }

        private void Onboarding_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveAll()) return;
            Close();
            _plugin.ShowOnboarding(firstRun: false);
        }
    }
}
