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
            BtnToggleDetails.Content = "▼ 收起详细文本报告";
            BtnToggleDetails.IsEnabled = false;

            CardNetwork.Visibility = Visibility.Collapsed;
            CardProxy.Visibility = Visibility.Collapsed;
            ItemsChannels.Visibility = Visibility.Collapsed;
            TxtChannelSection.Visibility = Visibility.Collapsed;
            CardStore.Visibility = Visibility.Collapsed;
            CardTTS.Visibility = Visibility.Collapsed;
            BorderOverall.Visibility = Visibility.Collapsed;
        }

        private void InitCommon(string title, string status,
            Action? onTestLLM, Action? onApplyRecommendations, Action? onOpenSettings)
        {
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
                TxtOverallStatus.Text = "全部正常";
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
                TxtOverallStatus.Foreground = GreenBrush;
            }
            else if (networkFail)
            {
                TxtOverallStatus.Text = "网络异常";
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA));
                TxtOverallStatus.Foreground = RedBrush;
            }
            else
            {
                TxtOverallStatus.Text = "部分异常";
                BorderOverall.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5));
                TxtOverallStatus.Foreground = OrangeBrush;
            }
        }

        private void RenderNetworkCard(DiagnosticResult result)
        {
            if (result.NetworkConnectivityOk)
            {
                DotNetwork.Background = GreenBrush;
                TxtNetworkPing.Text = "Ping 8.8.8.8 / 1.1.1.1 - 成功";
                TxtNetworkHttp.Text = "HTTP 访问测试 - 成功";
            }
            else
            {
                DotNetwork.Background = RedBrush;
                TxtNetworkPing.Text = result.NetworkDetails;
                TxtNetworkHttp.Text = "网络不可达，请检查网络设置";
            }
        }

        private void RenderProxyCard(DiagnosticResult result)
        {
            if (!result.ProxyEnabled)
            {
                DotProxy.Background = GrayBrush;
                TxtProxyStatus.Text = "代理未启用，跳过检查";
                return;
            }

            if (result.ProxyOk)
            {
                DotProxy.Background = GreenBrush;
                TxtProxyStatus.Text = "代理可用: " + result.ProxyDetails;
            }
            else
            {
                DotProxy.Background = RedBrush;
                TxtProxyStatus.Text = "代理不可用: " + result.ProxyDetails;
            }
        }

        private void RenderChannelCards(DiagnosticResult result)
        {
            var cards = new List<ChannelCardViewModel>();

            foreach (var cr in result.ChannelResults)
            {
                var vm = new ChannelCardViewModel
                {
                    ChannelType = cr.ChannelType,
                    ChannelName = cr.ChannelName,
                    ApiUrl = cr.ApiUrl
                };

                if (cr.ApiAvailable)
                {
                    vm.StatusColor = GreenBrush;
                    vm.ModelCount = cr.AvailableModels.Count > 0
                        ? $"可用模型: {cr.AvailableModels.Count} 个"
                        : "已连接";
                }
                else if (!cr.Enabled)
                {
                    vm.StatusColor = GrayBrush;
                    vm.ModelCount = "未启用";
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
                        vm.DirectStatus = "成功";
                        vm.HasDirectTest = Visibility.Visible;
                        vm.HasDirectFail = Visibility.Collapsed;
                    }
                    else
                    {
                        vm.DirectStatus = "失败: " + Truncate(cr.DirectMessage, 60);
                        vm.HasDirectTest = Visibility.Collapsed;
                        vm.HasDirectFail = Visibility.Visible;
                    }
                }

                if (cr.ProxyTried)
                {
                    if (cr.ProxyConnectionOk)
                    {
                        vm.ProxyStatus = "成功";
                        vm.HasProxyTest = Visibility.Visible;
                        vm.HasProxyFail = Visibility.Collapsed;
                    }
                    else
                    {
                        vm.ProxyStatus = "失败: " + Truncate(cr.ProxyConnectionMessage, 60);
                        vm.HasProxyTest = Visibility.Collapsed;
                        vm.HasProxyFail = Visibility.Visible;
                    }
                }

                if (cr.LlmTested)
                {
                    vm.LLMStatus = cr.LlmResponded ? "LLM: 正常" : "LLM: 无响应";
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
            var ps = result.PluginStoreResult;
            if (ps == null)
            {
                CardStore.Visibility = Visibility.Collapsed;
                return;
            }

            TxtStoreUrl.Text = "地址: " + ps.StoreUrl;

            if (ps.DirectOk)
            {
                TxtStoreDirect.Text = "直连: 成功";
                TxtStoreDirect.Foreground = GreenBrush;
                DotStore.Background = GreenBrush;
            }
            else
            {
                TxtStoreDirect.Text = "直连: " + Truncate(ps.DirectMessage, 50);
                TxtStoreDirect.Foreground = RedBrush;
            }

            if (ps.ProxyOk)
            {
                TxtStoreProxy.Text = "代理: 成功";
                TxtStoreProxy.Foreground = GreenBrush;
            }
            else
            {
                TxtStoreProxy.Text = "代理: " + Truncate(ps.ProxyMessage, 50);
                TxtStoreProxy.Foreground = RedBrush;
            }

            if (!ps.DirectOk && !ps.ProxyOk)
                DotStore.Background = RedBrush;
            else if (ps.DirectOk && ps.ProxyOk)
                DotStore.Background = GreenBrush;
            else
                DotStore.Background = OrangeBrush;

            TxtStoreRec.Text = "建议: " + ps.Recommendation;
        }

        private void RenderTTSCard(DiagnosticResult result)
        {
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

            TxtTTSMain.Text = "提供商: " + tts.Provider;
            TxtTTSEndpoint.Text = "端点: " + tts.Endpoint;

            if (tts.DirectOk)
            {
                TxtTTSDirect.Text = "直连: 成功";
                TxtTTSDirect.Foreground = GreenBrush;
            }
            else
            {
                TxtTTSDirect.Text = "直连: " + Truncate(tts.DirectMessage, 50);
                TxtTTSDirect.Foreground = RedBrush;
            }

            if (tts.ProxyOk)
            {
                TxtTTSProxy.Text = "代理: 成功";
                TxtTTSProxy.Foreground = GreenBrush;
            }
            else
            {
                TxtTTSProxy.Text = "代理: " + Truncate(tts.ProxyMessage, 50);
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
            var isZh = System.Threading.Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh");

            foreach (var rec in recommendations)
            {
                var vm = new RecommendationCardViewModel
                {
                    DisplayName = rec.DisplayName,
                    CurrentLabel = isZh ? "当前: " : "Current: ",
                    CurrentDisplay = HumanizeValue(rec.CurrentValue, isZh),
                    RecommendedDisplay = HumanizeValue(rec.RecommendedValue, isZh),
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
            TxtRecSectionTitle.Text = isZh ? "推荐设置调整" : "Recommended Settings";
            TxtRecCount.Text = isZh
                ? $"({criticalCount} 项关键, {recCount} 项建议)"
                : $"({criticalCount} critical, {recCount} suggested)";
            BtnRecApplyAll.Content = isZh ? "全部应用" : "Apply All";
            BtnRecIgnore.Content = isZh ? "忽略" : "Ignore";

            SetActionsEnabled(false);
        }

        public void HideRecommendations()
        {
            PanelRecommendations.Visibility = Visibility.Collapsed;
            _recommendationsCallback = null;
            SetActionsEnabled(true);
        }

        public bool IsRecommendationsVisible => PanelRecommendations.Visibility == Visibility.Visible;

        private static string HumanizeValue(string value, bool isZh)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value.ToLowerInvariant() switch
            {
                "true" => isZh ? "启用" : "Enabled",
                "false" => isZh ? "禁用" : "Disabled",
                "direct" => isZh ? "直连" : "Direct",
                "forceproxy" => isZh ? "强制代理" : "Force Proxy",
                "followdefault" => isZh ? "跟随默认" : "Follow Default",
                "not configured" => isZh ? "未配置" : "Not configured",
                "not set" => isZh ? "未设置" : "Not set",
                _ => value
            };
        }

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
                BtnToggleDetails.Content = "▶ 查看详细文本报告";
            }
            else
            {
                TxtDetailReport.Visibility = Visibility.Visible;
                BtnToggleDetails.Content = "▼ 收起详细文本报告";
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