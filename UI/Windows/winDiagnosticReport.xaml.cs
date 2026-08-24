using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPetLLM.Services;

namespace VPetLLM.UI.Windows
{
    public class ChannelCardViewModel : INotifyPropertyChanged
    {
        public string ChannelType { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string ApiUrl { get; set; } = "";
        public string DirectStatus { get; set; } = "";
        public string ProxyStatus { get; set; } = "";
        public string ModelCount { get; set; } = "";
        public string LLMStatus { get; set; } = "";

        // 卡片模板里的三个前缀标签。DataTemplate 内的元素拿不到 x:Name（每张卡一份），
        // 所以本地化只能随数据一起传进来。
        public string AddressLabel { get; set; } = "";
        public string DirectLabel { get; set; } = "";
        public string ProxyLabel { get; set; } = "";

        public Brush StatusColor { get; set; } = new SolidColorBrush(Color.FromRgb(0xA1, 0x9F, 0x9D));
        public Visibility HasDirectTest { get; set; } = Visibility.Collapsed;
        public Visibility HasDirectFail { get; set; } = Visibility.Collapsed;
        public Visibility HasProxyTest { get; set; } = Visibility.Collapsed;
        public Visibility HasProxyFail { get; set; } = Visibility.Collapsed;
        public Visibility HasModels { get; set; } = Visibility.Collapsed;
        public Visibility HasLLMResult { get; set; } = Visibility.Collapsed;

#pragma warning disable CS0067
        public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
    }

    public class RecommendationCardViewModel
    {
        public string DisplayName { get; set; } = "";
        public string CurrentLabel { get; set; } = "";
        public string CurrentDisplay { get; set; } = "";
        public string RecommendedDisplay { get; set; } = "";
        public string Reason { get; set; } = "";
        public Brush CategoryColor { get; set; } = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
    }

    public partial class winDiagnosticReport : Window
    {
        private Action? _onTestLLM;
        private Action? _onApplyRecommendations;
        private Action? _onOpenSettings;
        private Action<bool>? _confirmCallback;
        private Action? _infoCallback;
        private Action<bool>? _recommendationsCallback;
        private bool _recommendationsApplied;

        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0x7C, 0x10));
        private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xD1, 0x34, 0x38));
        private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(0xFF, 0x8C, 0x00));
        private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(0xA1, 0x9F, 0x9D));

        public winDiagnosticReport(
            string title,
            DiagnosticResult result,
            string detailReport,
            string status = "",
            Action? onTestLLM = null,
            Action? onApplyRecommendations = null,
            Action? onOpenSettings = null)
        {
            InitializeComponent();
            InitCommon(title, status, onTestLLM, onApplyRecommendations, onOpenSettings);
            RenderVisualReport(result, detailReport);
        }

        public winDiagnosticReport(
            string title,
            string report,
            string status = "",
            Action? onTestLLM = null,
            Action? onApplyRecommendations = null,
            Action? onOpenSettings = null)
        {
            InitializeComponent();
            InitCommon(title, status, onTestLLM, onApplyRecommendations, onOpenSettings);

            TxtDetailReport.Text = report;
            TxtDetailReport.Visibility = Visibility.Visible;
            BtnToggleDetails.Content = "▼ " + L("Diagnostic.UiHideDetail", "收起详细文本报告", UiLanguage);
            BtnToggleDetails.IsEnabled = false;

            CardNetwork.Visibility = Visibility.Collapsed;
            CardProxy.Visibility = Visibility.Collapsed;
            ItemsChannels.Visibility = Visibility.Collapsed;
            TxtChannelSection.Visibility = Visibility.Collapsed;
            CardStore.Visibility = Visibility.Collapsed;
            CardTTS.Visibility = Visibility.Collapsed;
            BorderOverall.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 刷新窗口自身的静态文案。XAML 里写的是中文占位，真正的取值在这里 ——
        /// 这个窗口不用 {utils:Localize}：那条路依赖 LocalizationService.LangCode 的
        /// 生命周期，而本窗口可能在设置窗口从未打开过时被唤起，直接读设置更稳。
        /// </summary>
        private void ApplyStaticLocalization()
        {
            var lang = UiLanguage;

            TxtSectionNetwork.Text = L("Diagnostic.UiSectionNetwork", "网络连通性", lang);
            TxtSectionProxy.Text = L("Diagnostic.UiSectionProxy", "代理连接", lang);
            TxtChannelSection.Text = L("Diagnostic.SectionChannels", "API 渠道", lang);
            TxtSectionPluginStore.Text = L("Diagnostic.UiSectionPluginStore", "插件商店", lang);
            TxtSectionTTS.Text = L("Diagnostic.UiSectionTTS", "语音合成 (TTS)", lang);

            BtnToggleDetails.Content = "▶ " + L("Diagnostic.UiShowDetail", "查看详细文本报告", lang);

            BtnConfirmNo.Content = L("Diagnostic.No", "否", lang);
            BtnConfirmYes.Content = L("Diagnostic.Yes", "是", lang);
            BtnConfirmOk.Content = L("Diagnostic.UiConfirmOk", "确定", lang);

            BtnTestLLM.Content = L("Diagnostic.UiTestLLM", "测试 LLM 响应", lang);
            BtnApplyRecs.Content = L("Diagnostic.UiApplyRecs", "应用推荐设置", lang);
            BtnOpenSettings.Content = L("Diagnostic.UiOpenSettings", "打开设置", lang);
            BtnClose.Content = L("Diagnostic.UiClose", "关闭", lang);
        }

        private void InitCommon(string title, string status,
            Action? onTestLLM, Action? onApplyRecommendations, Action? onOpenSettings)
        {
            ApplyStaticLocalization();

            TxtTitle.Text = title;
            if (!string.IsNullOrEmpty(status))
                TxtStatus.Text = status;

            _onTestLLM = onTestLLM;
            _onApplyRecommendations = onApplyRecommendations;
            _onOpenSettings = onOpenSettings;

            BtnTestLLM.Visibility = onTestLLM != null ? Visibility.Visible : Visibility.Collapsed;
            BtnApplyRecs.Visibility = onApplyRecommendations != null ? Visibility.Visible : Visibility.Collapsed;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_recommendationsCallback != null)
                    {
                        var cb = _recommendationsCallback;
                        HideRecommendations();
                        cb(false);
                    }
                    else if (_confirmCallback != null || _infoCallback != null)
                    {
                        if (_confirmCallback != null)
                        {
                            var cb = _confirmCallback;
                            HideConfirm();
                            cb(false);
                        }
                        else if (_infoCallback != null)
                        {
                            var cb = _infoCallback;
                            HideConfirm();
                            cb();
                        }
                    }
                    else
                    {
                        Close();
                    }
                }
            };
        }

        private void RenderVisualReport(DiagnosticResult result, string detailReport)
        {
            TxtDetailReport.Text = detailReport;

            RenderOverallStatus(result);
            RenderNetworkCard(result);
            RenderProxyCard(result);
            RenderChannelCards(result);
            RenderStoreCard(result);
            RenderTTSCard(result);
        }

        private void RenderOverallStatus(DiagnosticResult result)
        {
            bool allOk = result.NetworkConnectivityOk && result.ProxyOk;
            bool networkFail = !result.NetworkConnectivityOk;

            foreach (var ch in result.ChannelResults)
            {
                if (ch.Enabled && !ch.ApiAvailable) allOk = false;
            }

            if (allOk && result.AllPassed)
            {
                TxtOverallStatus.Text = L("Diagnostic.UiOverallOk", "全部正常", UiLanguage);
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
                TxtOverallStatus.Foreground = GreenBrush;
            }
            else if (networkFail)
            {
                TxtOverallStatus.Text = L("Diagnostic.UiOverallNetworkFail", "网络异常", UiLanguage);
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA));
                TxtOverallStatus.Foreground = RedBrush;
            }
            else
            {
                TxtOverallStatus.Text = L("Diagnostic.UiOverallPartial", "部分异常", UiLanguage);
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5));
                TxtOverallStatus.Foreground = OrangeBrush;
            }
        }

        private void RenderNetworkCard(DiagnosticResult result)
        {
            if (result.NetworkConnectivityOk)
            {
                DotNetwork.Background = GreenBrush;
                TxtNetworkPing.Text = L("Diagnostic.UiNetworkPingOk", "Ping 8.8.8.8 / 1.1.1.1 - 成功", UiLanguage);
                TxtNetworkHttp.Text = L("Diagnostic.UiNetworkHttpOk", "HTTP 访问测试 - 成功", UiLanguage);
            }
            else
            {
                DotNetwork.Background = RedBrush;
                TxtNetworkPing.Text = result.NetworkDetails;
                TxtNetworkHttp.Text = L("Diagnostic.UiNetworkUnreachable", "网络不可达，请检查网络设置", UiLanguage);
            }
        }

        private void RenderProxyCard(DiagnosticResult result)
        {
            if (!result.ProxyEnabled)
            {
                DotProxy.Background = GrayBrush;
                TxtProxyStatus.Text = L("Diagnostic.UiProxySkipped", "代理未启用，跳过检查", UiLanguage);
                return;
            }

            if (result.ProxyOk)
            {
                DotProxy.Background = GreenBrush;
                TxtProxyStatus.Text = L("Diagnostic.UiProxyAvailable", "代理可用: ", UiLanguage) + result.ProxyDetails;
            }
            else
            {
                DotProxy.Background = RedBrush;
                TxtProxyStatus.Text = L("Diagnostic.UiProxyUnavailable", "代理不可用: ", UiLanguage) + result.ProxyDetails;
            }
        }

        private void RenderChannelCards(DiagnosticResult result)
        {
            var lang = UiLanguage;
            var cards = new List<ChannelCardViewModel>();

            foreach (var cr in result.ChannelResults)
            {
                var vm = new ChannelCardViewModel
                {
                    ChannelType = cr.ChannelType,
                    ChannelName = cr.ChannelName,
                    ApiUrl = cr.ApiUrl,
                    AddressLabel = L("Diagnostic.UiChannelAddress", "地址: ", lang),
                    DirectLabel = L("Diagnostic.UiChannelDirect", "直连: ", lang),
                    ProxyLabel = L("Diagnostic.UiChannelProxy", "代理: ", lang)
                };

                if (cr.ApiAvailable)
                {
                    vm.StatusColor = GreenBrush;
                    vm.ModelCount = cr.AvailableModels.Count > 0
                        ? string.Format(L("Diagnostic.UiModelsAvailable", "可用模型: {0} 个", lang),
                                        cr.AvailableModels.Count)
                        : L("Diagnostic.UiConnected", "已连接", lang);
                }
                else if (!cr.Enabled)
                {
                    vm.StatusColor = GrayBrush;
                    vm.ModelCount = L("Diagnostic.UiDisabled", "未启用", lang);
                }
                else
                {
                    vm.StatusColor = RedBrush;
                    vm.ModelCount = cr.ApiMessage;
                }

                if (cr.DirectTried)
                {
                    if (cr.DirectOk)
                    {
                        vm.DirectStatus = L("Diagnostic.UiSuccess", "成功", lang);
                        vm.HasDirectTest = Visibility.Visible;
                        vm.HasDirectFail = Visibility.Collapsed;
                    }
                    else
                    {
                        vm.DirectStatus = L("Diagnostic.UiFailedPrefix", "失败: ", lang) + Truncate(cr.DirectMessage, 60);
                        vm.HasDirectTest = Visibility.Collapsed;
                        vm.HasDirectFail = Visibility.Visible;
                    }
                }

                if (cr.ProxyTried)
                {
                    if (cr.ProxyConnectionOk)
                    {
                        vm.ProxyStatus = L("Diagnostic.UiSuccess", "成功", lang);
                        vm.HasProxyTest = Visibility.Visible;
                        vm.HasProxyFail = Visibility.Collapsed;
                    }
                    else
                    {
                        vm.ProxyStatus = L("Diagnostic.UiFailedPrefix", "失败: ", lang) + Truncate(cr.ProxyConnectionMessage, 60);
                        vm.HasProxyTest = Visibility.Collapsed;
                        vm.HasProxyFail = Visibility.Visible;
                    }
                }

                if (cr.LlmTested)
                {
                    vm.LLMStatus = cr.LlmResponded
                        ? L("Diagnostic.UiLlmOk", "LLM: 正常", lang)
                        : L("Diagnostic.UiLlmNoResponse", "LLM: 无响应", lang);
                    vm.HasLLMResult = Visibility.Visible;
                }

                if (!string.IsNullOrEmpty(vm.ModelCount))
                    vm.HasModels = Visibility.Visible;

                if (cr.Enabled && !cr.ApiAvailable && !string.IsNullOrEmpty(cr.ApiMessage))
                {
                    vm.ModelCount = Truncate(cr.ApiMessage, 80);
                }

                cards.Add(vm);
            }

            ItemsChannels.ItemsSource = cards;
        }

        private void RenderStoreCard(DiagnosticResult result)
        {
            var lang = UiLanguage;
            var ps = result.PluginStoreResult;
            if (ps == null)
            {
                CardStore.Visibility = Visibility.Collapsed;
                return;
            }

            TxtStoreUrl.Text = L("Diagnostic.UiChannelAddress", "地址: ", lang) + ps.StoreUrl;

            if (ps.DirectOk)
            {
                TxtStoreDirect.Text = L("Diagnostic.UiChannelDirect", "直连: ", lang)
                    + L("Diagnostic.UiSuccess", "成功", lang);
                TxtStoreDirect.Foreground = GreenBrush;
                DotStore.Background = GreenBrush;
            }
            else
            {
                TxtStoreDirect.Text = L("Diagnostic.UiChannelDirect", "直连: ", lang) + Truncate(ps.DirectMessage, 50);
                TxtStoreDirect.Foreground = RedBrush;
            }

            if (ps.ProxyOk)
            {
                TxtStoreProxy.Text = L("Diagnostic.UiChannelProxy", "代理: ", lang)
                    + L("Diagnostic.UiSuccess", "成功", lang);
                TxtStoreProxy.Foreground = GreenBrush;
            }
            else
            {
                TxtStoreProxy.Text = L("Diagnostic.UiChannelProxy", "代理: ", lang) + Truncate(ps.ProxyMessage, 50);
                TxtStoreProxy.Foreground = RedBrush;
            }

            if (!ps.DirectOk && !ps.ProxyOk)
                DotStore.Background = RedBrush;
            else if (ps.DirectOk && ps.ProxyOk)
                DotStore.Background = GreenBrush;
            else
                DotStore.Background = OrangeBrush;

            TxtStoreRec.Text = L("Diagnostic.UiStoreRecPrefix", "建议: ", lang) + ps.Recommendation;
        }

        private void RenderTTSCard(DiagnosticResult result)
        {
            var lang = UiLanguage;
            var tts = result.TTSResult;
            if (tts == null)
            {
                CardTTS.Visibility = Visibility.Collapsed;
                return;
            }

            if (!tts.TTSEnabled)
            {
                DotTTS.Background = GrayBrush;
                TxtTTSMain.Text = tts.Summary;
                TxtTTSEndpoint.Visibility = Visibility.Collapsed;
                TxtTTSDirect.Visibility = Visibility.Collapsed;
                TxtTTSProxy.Visibility = Visibility.Collapsed;
                return;
            }

            TxtTTSMain.Text = L("Diagnostic.UiTtsProvider", "提供商: ", lang) + tts.Provider;
            TxtTTSEndpoint.Text = L("Diagnostic.UiTtsEndpoint", "端点: ", lang) + tts.Endpoint;

            if (tts.DirectOk)
            {
                TxtTTSDirect.Text = L("Diagnostic.UiChannelDirect", "直连: ", lang)
                    + L("Diagnostic.UiSuccess", "成功", lang);
                TxtTTSDirect.Foreground = GreenBrush;
            }
            else
            {
                TxtTTSDirect.Text = L("Diagnostic.UiChannelDirect", "直连: ", lang) + Truncate(tts.DirectMessage, 50);
                TxtTTSDirect.Foreground = RedBrush;
            }

            if (tts.ProxyOk)
            {
                TxtTTSProxy.Text = L("Diagnostic.UiChannelProxy", "代理: ", lang)
                    + L("Diagnostic.UiSuccess", "成功", lang);
                TxtTTSProxy.Foreground = GreenBrush;
            }
            else
            {
                TxtTTSProxy.Text = L("Diagnostic.UiChannelProxy", "代理: ", lang) + Truncate(tts.ProxyMessage, 50);
                TxtTTSProxy.Foreground = RedBrush;
            }

            DotTTS.Background = tts.Reachable ? GreenBrush : RedBrush;
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
        }

        public void UpdateReport(string report, string status = "")
        {
            TxtDetailReport.Text = report;
            if (!string.IsNullOrEmpty(status))
                TxtStatus.Text = status;
        }

        public void UpdateTitle(string title)
        {
            TxtTitle.Text = title;
        }

        public void UpdateFromResult(DiagnosticResult result, string detailReport, string status = "")
        {
            RenderVisualReport(result, detailReport);
            if (!string.IsNullOrEmpty(status))
                TxtStatus.Text = status;
        }

        public void ShowProgress(string message)
        {
            TxtStatus.Text = message;
            BtnTestLLM.IsEnabled = false;
            BtnApplyRecs.IsEnabled = false;
        }

        public void HideProgress()
        {
            TxtStatus.Text = "";
            BtnTestLLM.IsEnabled = true;
            BtnApplyRecs.IsEnabled = true;
        }

        public void OnRecommendationsApplied()
        {
            _recommendationsApplied = true;
            BtnApplyRecs.Visibility = Visibility.Collapsed;
            BtnTestLLM.Visibility = Visibility.Collapsed;
        }

        public bool RecommendationsApplied => _recommendationsApplied;

        public void ShowConfirm(string title, string message, Action<bool> onResult)
        {
            _confirmCallback = onResult;
            _infoCallback = null;
            BorderConfirm.Visibility = Visibility.Visible;
            TxtConfirmTitle.Text = title;
            TxtConfirmMessage.Text = message;
            BtnConfirmYes.Visibility = Visibility.Visible;
            BtnConfirmNo.Visibility = Visibility.Visible;
            BtnConfirmOk.Visibility = Visibility.Collapsed;
            SetActionsEnabled(false);
        }

        public void ShowInfo(string title, string message, Action? onDismiss = null)
        {
            _infoCallback = onDismiss;
            _confirmCallback = null;
            BorderConfirm.Visibility = Visibility.Visible;
            TxtConfirmTitle.Text = title;
            TxtConfirmMessage.Text = message;
            BtnConfirmOk.Visibility = Visibility.Visible;
            BtnConfirmYes.Visibility = Visibility.Collapsed;
            BtnConfirmNo.Visibility = Visibility.Collapsed;
            SetActionsEnabled(false);
        }

        public void HideConfirm()
        {
            BorderConfirm.Visibility = Visibility.Collapsed;
            _confirmCallback = null;
            _infoCallback = null;
            SetActionsEnabled(true);
        }

        public void ShowRecommendations(List<RecommendedSetting> recommendations, Action<bool> onResult)
        {
            _recommendationsCallback = onResult;

            var cards = new List<RecommendationCardViewModel>();
            var lang = UiLanguage;

            foreach (var rec in recommendations)
            {
                var vm = new RecommendationCardViewModel
                {
                    DisplayName = rec.DisplayName,
                    CurrentLabel = L("Diagnostic.CurrentPrefix", "当前: ", lang),
                    CurrentDisplay = HumanizeValue(rec.CurrentValue, lang),
                    RecommendedDisplay = HumanizeValue(rec.RecommendedValue, lang),
                    Reason = rec.Reason,
                    CategoryColor = rec.Category == "critical"
                        ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
                        : new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00))
                };
                cards.Add(vm);
            }

            ItemsRecommendations.ItemsSource = cards;
            PanelRecommendations.Visibility = Visibility.Visible;

            var criticalCount = recommendations.Count(r => r.Category == "critical");
            var recCount = recommendations.Count(r => r.Category == "recommended");
            TxtRecSectionTitle.Text = L("Diagnostic.RecSectionTitle", "推荐设置调整", lang);
            TxtRecCount.Text = string.Format(
                L("Diagnostic.RecCount", "({0} 项关键, {1} 项建议)", lang), criticalCount, recCount);
            BtnRecApplyAll.Content = L("Diagnostic.RecApplyAll", "全部应用", lang);
            BtnRecIgnore.Content = L("Diagnostic.RecIgnore", "忽略", lang);

            SetActionsEnabled(false);
        }

        public void HideRecommendations()
        {
            PanelRecommendations.Visibility = Visibility.Collapsed;
            _recommendationsCallback = null;
            SetActionsEnabled(true);
        }

        public bool IsRecommendationsVisible => PanelRecommendations.Visibility == Visibility.Visible;

        private static string HumanizeValue(string value, string lang)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value.ToLowerInvariant() switch
            {
                "true" => L("Diagnostic.ValueEnabled", "启用", lang),
                "false" => L("Diagnostic.ValueDisabled", "禁用", lang),
                "direct" => L("Diagnostic.ValueDirect", "直连", lang),
                "forceproxy" => L("Diagnostic.ValueForceProxy", "强制代理", lang),
                "followdefault" => L("Diagnostic.ValueFollowDefault", "跟随默认", lang),
                "not configured" => L("Diagnostic.ValueNotConfigured", "未配置", lang),
                "not set" => L("Diagnostic.ValueNotSet", "未设置", lang),
                _ => value
            };
        }

        /// <summary>
        /// 界面语言。用 VPetLLM 自己的设置，**不是** Thread.CurrentUICulture ——
        /// 后者是操作系统语言，用户在插件里选了中文却跑在英文系统上时会拿到英文，反之亦然。
        /// </summary>
        private static string UiLanguage
            => global::VPetLLM.VPetLLM.Instance?.Settings?.Language ?? "zh-hans";

        /// <summary>
        /// 取词条，缺失时回落到中文字面量。
        /// 刻意要求传**完整键路径**而不是在这里拼 "Diagnostic." 前缀 ——
        /// 拼出来的键在源码里不是字面量，"哪些词条没人用"的审计就扫不到，
        /// 清理弃用词条时会被误删。
        /// </summary>
        private static string L(string key, string fallback, string lang)
            => Utils.Localization.LanguageHelper.Get(key, lang, fallback);

        public bool IsConfirmVisible => BorderConfirm.Visibility == Visibility.Visible;

        private void SetActionsEnabled(bool enabled)
        {
            BtnTestLLM.IsEnabled = enabled;
            BtnApplyRecs.IsEnabled = enabled;
            BtnOpenSettings.IsEnabled = enabled;
        }

        private void BtnConfirmYes_Click(object sender, RoutedEventArgs e)
        {
            var cb = _confirmCallback;
            HideConfirm();
            cb?.Invoke(true);
        }

        private void BtnConfirmNo_Click(object sender, RoutedEventArgs e)
        {
            var cb = _confirmCallback;
            HideConfirm();
            cb?.Invoke(false);
        }

        private void BtnConfirmOk_Click(object sender, RoutedEventArgs e)
        {
            var cb = _infoCallback;
            HideConfirm();
            cb?.Invoke();
        }

        private void BtnTestLLM_Click(object sender, RoutedEventArgs e)
        {
            _onTestLLM?.Invoke();
        }

        private void BtnApplyRecs_Click(object sender, RoutedEventArgs e)
        {
            _onApplyRecommendations?.Invoke();
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            _onOpenSettings?.Invoke();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_recommendationsCallback != null)
            {
                var cb = _recommendationsCallback;
                HideRecommendations();
                cb(false);
            }
            if (_confirmCallback != null)
            {
                var cb = _confirmCallback;
                HideConfirm();
                cb(false);
            }
            else if (_infoCallback != null)
            {
                var cb = _infoCallback;
                HideConfirm();
                cb();
            }
            Close();
        }

        private void BtnToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            if (TxtDetailReport.Visibility == Visibility.Visible)
            {
                TxtDetailReport.Visibility = Visibility.Collapsed;
                BtnToggleDetails.Content = "▶ " + L("Diagnostic.UiShowDetail", "查看详细文本报告", UiLanguage);
            }
            else
            {
                TxtDetailReport.Visibility = Visibility.Visible;
                BtnToggleDetails.Content = "▼ " + L("Diagnostic.UiHideDetail", "收起详细文本报告", UiLanguage);
            }
        }

        private void BtnRecApplyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_recommendationsCallback == null) return;

            var cb = _recommendationsCallback;
            HideRecommendations();
            cb(true);
        }

        private void BtnRecIgnore_Click(object sender, RoutedEventArgs e)
        {
            if (_recommendationsCallback == null) return;

            var cb = _recommendationsCallback;
            HideRecommendations();
            cb(false);
        }
    }
}