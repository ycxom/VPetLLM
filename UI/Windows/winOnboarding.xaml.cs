using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 新手引导。选择都先留在控件里，最后一步才一次性写回：
    /// 中途关掉窗口不会留下只配了一半的设置。语言例外，选了就要立刻看到效果；
    /// 设置界面也例外，极客用户常常选完直接跳过，这个选择不能跟着丢掉。
    /// </summary>
    public partial class winOnboarding : Window
    {
        private readonly global::VPetLLM.VPetLLM _plugin;
        private readonly UIElement[] _pages;
        private int _step;
        private bool _languageChanged;
        private bool _testFailed;

        private global::VPetLLM.UI.Controls.QuickChannelPanel ChannelPanel
            => (global::VPetLLM.UI.Controls.QuickChannelPanel)Host_Channel.Child;

        private global::VPetLLM.UI.Controls.QuickProfilePanel ProfilePanel
            => (global::VPetLLM.UI.Controls.QuickProfilePanel)Host_Profile.Child;

        private string T(string path, string fallback)
            => LanguageHelper.Get(path, _plugin.Settings.Language, fallback);

        public winOnboarding(global::VPetLLM.VPetLLM plugin, bool firstRun)
        {
            _plugin = plugin;
            InitializeComponent();

            _pages = new UIElement[] { Page_Welcome, Page_Channel, Page_Profile, Page_Voice, Page_Done };
            foreach (var _ in _pages)
                Panel_Progress.Children.Add(new Rectangle { Height = 4, Margin = new Thickness(2, 0, 2, 0), RadiusX = 2, RadiusY = 2 });

            var settings = _plugin.Settings;
            Cb_Language.ItemsSource = LanguageHelper.LanguageDisplayMap;
            Cb_Language.SelectedValue = settings.Language;

            var simple = QuickSetupService.PreferSimpleSettings(settings, firstRun);
            Rb_SimpleMode.IsChecked = simple;
            Rb_GeekMode.IsChecked = !simple;

            ChannelPanel.Attach(settings, () => _plugin.ChatCore as RoutedChatCore);
            ChannelPanel.SelectionChanged += OnChannelSelectionChanged;
            ProfilePanel.Load(settings);

            Cb_ReadAloud.IsChecked = settings.TTS.IsEnabled;
            Cb_VoiceInput.IsChecked = settings.ASR.IsEnabled;
            Panel_SetDefault.Visibility = _plugin.IsVPetLLMDefaultPlugin() ? Visibility.Collapsed : Visibility.Visible;

            Closed += OnWindowClosed;
            ShowStep(0);
        }

        private void ShowStep(int step)
        {
            _step = step;
            for (var i = 0; i < _pages.Length; i++)
            {
                _pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
                ((Rectangle)Panel_Progress.Children[i]).Fill =
                    (Brush)FindResource(i <= step ? "PrimaryBrush" : "BorderBrush");
            }
            Scroll_Pages.ScrollToTop();
            UpdateTexts();
        }

        // 步骤计数和底部按钮文字随步骤变化，绑不了 Localize，切语言时也要跟着重算
        private void UpdateTexts()
        {
            var isLast = _step == _pages.Length - 1;
            Tb_StepCounter.Text = string.Format(T("QuickSetup.StepCounter", "第 {0} / {1} 步"), _step + 1, _pages.Length);
            Btn_Back.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
            Btn_Skip.Visibility = isLast ? Visibility.Collapsed : Visibility.Visible;
            Btn_Next.Content = isLast
                ? T("QuickSetup.Finish", "开始使用")
                : _testFailed && ReferenceEquals(_pages[_step], Page_Channel)
                    ? T("QuickSetup.ContinueAnyway", "仍然继续")
                    : T("QuickSetup.Next", "下一步");

            var hotkey = $"{_plugin.Settings.ASR.HotkeyModifiers}+{_plugin.Settings.ASR.HotkeyKey}";
            Tb_VoiceInputHint.Text = string.Format(T("QuickSetup.VoiceInputHint", "按一下 {0} 开始说话，再按一下结束。"), hotkey);
            Tb_GuideVoice.Text = string.Format(T("QuickSetup.GuideVoice", "语音输入：按一下 {0} 开始说话，再按一下结束。"), hotkey);
            Tb_GuideVoice.Visibility = Cb_VoiceInput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            // VPetTTS 在时朗读归它管，VPetLLM 这边开着也不会发声
            var ttsPlugin = _plugin.IsVPetTTSPluginDetected;
            Cb_ReadAloud.IsEnabled = !ttsPlugin;
            Tb_ReadAloudHint.Text = ttsPlugin
                ? T("QuickSetup.ReadAloudByPlugin", "检测到 VPetTTS 插件，朗读由它负责，请在它的设置里修改。")
                : T("QuickSetup.ReadAloudHint", "回复会转成语音播放。");
        }

        private void OnChannelSelectionChanged()
        {
            _testFailed = false;
            UpdateTexts();
        }

        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Cb_Language.SelectedValue is not string code || code == _plugin.Settings.Language) return;

            _languageChanged = true;
            _plugin.Settings.Language = code;
            _plugin.Settings.PromptLanguage = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
            LanguageHelper.ReloadLanguages();
            LocalizationService.Instance.ChangeLanguage(code);
            ChannelPanel.RefreshLanguage();
            UpdateTexts();
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

        private void LocalDeploy_Click(object sender, RoutedEventArgs e) => ChannelPanel.OpenLocalDeploy();

        private void Skip_Click(object sender, RoutedEventArgs e) => Close();

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(_pages[_step], Page_Channel) && !_testFailed && ChannelPanel.NeedsApply)
            {
                var draft = ChannelPanel.BuildDraft();
                if (draft is null) return;

                Btn_Next.IsEnabled = false;
                Btn_Back.IsEnabled = false;
                var ok = await ChannelPanel.TestAsync(draft);
                Btn_Next.IsEnabled = true;
                Btn_Back.IsEnabled = true;

                if (!ok)
                {
                    // 可能只是服务暂时不通，让用户留在本页看原因，要继续也拦不住
                    _testFailed = true;
                    UpdateTexts();
                    return;
                }
            }

            if (_step == _pages.Length - 1)
                Finish();
            else
                ShowStep(_step + 1);
        }

        private void Finish()
        {
            var settings = _plugin.Settings;
            var oldProvider = settings.Provider;

            if (ChannelPanel.NeedsApply)
            {
                var draft = ChannelPanel.BuildDraft();
                if (draft is null)
                {
                    ShowStep(Array.IndexOf(_pages, Page_Channel));
                    return;
                }
                QuickSetupService.ApplyChannel(settings, draft);
            }

            ProfilePanel.Save(settings);
            QuickSetupService.ApplyVoice(settings, Cb_ReadAloud.IsChecked == true, Cb_VoiceInput.IsChecked == true);
            _plugin.ApplyQuickSettings(oldProvider, _languageChanged);

            if (Panel_SetDefault.Visibility == Visibility.Visible && Cb_SetDefault.IsChecked == true
                && !_plugin.SetAsDefaultChatPlugin())
            {
                MessageBox.Show(this,
                    T("QuickSetup.SetDefaultFail", "没能自动设置。请打开桌宠的设置，在“聊天设置”里选择自定义接口并选中 VPetLLM。"),
                    Title);
            }
            Close();
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _plugin.OnboardingWindow = null;

            // 宿主退出时被一起关掉的不算看过；其余情况（完成、跳过、点叉）都算，否则每次启动都弹
            if (!_plugin.IsExiting)
                _plugin.CompleteOnboarding(Rb_SimpleMode.IsChecked == true);
        }
    }
}
