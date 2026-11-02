using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.UI.Windows;
using VPetLLM.Utils.Data;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 简易设置和新手引导共用的"选 AI 服务"面板：把渠道、优先级这些概念收成三张卡片。
    /// 这里只组装草稿，写回设置由外层窗口经 <see cref="QuickSetupService.ApplyChannel"/> 完成。
    /// </summary>
    public partial class QuickChannelPanel : UserControl
    {
        private Setting? _settings;
        private Func<RoutedChatCore?>? _coreProvider;
        private ApiPresetData? _presets;
        private CancellationTokenSource? _fetchCts;
        private readonly ComboBoxItem _customPreset = new();

        private bool _loading;
        private bool _changed;
        private bool _hasUsablePrimary;

        /// <summary>用户改了选择或填写内容。</summary>
        public event Action? SelectionChanged;

        /// <summary>
        /// 是否需要写回。用户没动过、现有主渠道又能用时不写：
        /// 面板表达不了 Gemini 这类渠道，否则打开看一眼再保存就把主渠道换掉了。
        /// </summary>
        public bool NeedsApply => _changed || !_hasUsablePrimary;

        private string Lang => _settings?.Language ?? "zh-hans";

        private string T(string path, string fallback) => LanguageHelper.Get(path, Lang, fallback);

        private Setting.ChannelKind LocalKind =>
            Rb_LMStudio.IsChecked == true ? Setting.ChannelKind.LMStudio : Setting.ChannelKind.Ollama;

        private ApiPresetItem? SelectedPreset => (Cb_Preset.SelectedItem as ComboBoxItem)?.Tag as ApiPresetItem;

        private string ApiUrl => SelectedPreset?.Url ?? Tb_Url.Text.Trim();

        public QuickChannelPanel()
        {
            InitializeComponent();
        }

        public void Attach(Setting settings, Func<RoutedChatCore?> coreProvider)
        {
            _settings = settings;
            _coreProvider = coreProvider;
            _presets = ApiPresetManager.GetDefaultPresets();
            Reload();
            _ = RefreshPresetsAsync();
            _ = RefreshFreeInfoAsync();
        }

        /// <summary>按当前主渠道重新填表，丢弃未保存的输入。</summary>
        public void Reload()
        {
            var primary = QuickSetupService.GetPrimaryChannel(_settings!);
            _hasUsablePrimary = primary is not null && QuickSetupService.IsChannelConfigured(primary);
            _changed = false;

            _loading = true;
            try
            {
                FillPresets();
                Pb_ApiKey.Clear();
                Rb_Ollama.IsChecked = true;
                LoadLocal(Setting.ChannelKind.Ollama);
                SelectPreset("");
                ApplyPresetModels();
                ShowStatus(null, null);

                switch (_hasUsablePrimary ? primary : null)
                {
                    case Setting.OpenAINodeSetting openAi:
                        Rb_ApiKey.IsChecked = true;
                        Tb_Url.Text = openAi.Url;
                        SelectPreset(openAi.Url);
                        ApplyPresetModels();
                        Cb_Model.Text = openAi.Model ?? "";
                        break;
                    case Setting.OllamaNodeSetting ollama:
                        Rb_Local.IsChecked = true;
                        Tb_LocalUrl.Text = ollama.Url;
                        Cb_LocalModel.Text = ollama.Model ?? "";
                        break;
                    case Setting.LMStudioNodeSetting lmStudio:
                        Rb_Local.IsChecked = true;
                        Rb_LMStudio.IsChecked = true;
                        Tb_LocalUrl.Text = lmStudio.Url;
                        Cb_LocalModel.Text = lmStudio.Model ?? "";
                        break;
                    default:
                        Rb_Free.IsChecked = true;
                        break;
                }
                UpdateVisibility();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>校验表单并组装草稿。不通过时在面板里显示原因并返回 null。</summary>
        public Setting.ChannelNodeBase? BuildDraft()
        {
            if (Rb_Free.IsChecked == true)
                return QuickSetupService.BuildDraft(_settings!, Setting.ChannelKind.Free, null, null, null, null);

            var isLocal = Rb_Local.IsChecked == true;
            var url = isLocal ? Tb_LocalUrl.Text.Trim() : ApiUrl;
            var model = (isLocal ? Cb_LocalModel.Text : Cb_Model.Text).Trim();

            if (!IsHttpUrl(url))
                return Reject(T("QuickSetup.InvalidUrl", "接口地址要以 http:// 或 https:// 开头。"));
            if (model.Length == 0)
                return Reject(T("QuickSetup.ModelRequired", "请选择或填写模型。"));

            if (isLocal)
                return QuickSetupService.BuildDraft(_settings!, LocalKind, null, url, null, model);

            var draft = (Setting.OpenAINodeSetting)QuickSetupService.BuildDraft(
                _settings!, Setting.ChannelKind.OpenAIChat, SelectedPreset?.Name, url, Pb_ApiKey.Password, model);

            // 自定义地址可能是不要密钥的本地转发，只有预设的服务商才强制要求
            if (SelectedPreset is not null && string.IsNullOrWhiteSpace(draft.ApiKey))
                return Reject(T("QuickSetup.KeyRequired", "请填写 API Key。"));

            return draft;
        }

        /// <summary>用真实请求试一次草稿，结果显示在面板里。</summary>
        public async Task<bool> TestAsync(Setting.ChannelNodeBase draft)
        {
            var core = _coreProvider?.Invoke();
            if (core is null)
            {
                ShowStatus(T("QuickSetup.CoreNotReady", "聊天服务还在启动，请稍后再试。"), false);
                return false;
            }

            ShowStatus(T("QuickSetup.Testing", "正在连接，请稍候…"), null);
            try
            {
                var result = await core.TestChannelAsync(draft);
                ShowStatus(result.Success
                        ? T("QuickSetup.TestOk", "连接成功！")
                        : string.Format(T("QuickSetup.TestFail", "连接失败：{0}"), result.Error),
                    result.Success);
                return result.Success;
            }
            catch (Exception ex)
            {
                ShowStatus(string.Format(T("QuickSetup.TestFail", "连接失败：{0}"), ex.Message), false);
                return false;
            }
        }

        public void RefreshLanguage()
        {
            _customPreset.Content = T("QuickSetup.CustomProvider", "自定义地址");
            UpdateFreeInfo();
        }

        private Setting.ChannelNodeBase? Reject(string reason)
        {
            ShowStatus(reason, false);
            return null;
        }

        private async Task RefreshPresetsAsync()
        {
            ApiPresetData? presets;
            try
            {
                presets = await ApiPresetManager.GetPresetsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"加载 API 预设失败: {ex.Message}");
                return;
            }
            if (presets is null) return;

            var url = ApiUrl;
            var wasCustom = ReferenceEquals(Cb_Preset.SelectedItem, _customPreset);
            _presets = presets;
            _loading = true;
            try
            {
                FillPresets();
                SelectPreset(url);
                if (wasCustom) Cb_Preset.SelectedItem = _customPreset;
                UpdateVisibility();
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task RefreshFreeInfoAsync()
        {
            try
            {
                await FreeConfigManager.StartupDownload;
            }
            catch (Exception ex)
            {
                Logger.Log($"等待免费服务配置失败: {ex.Message}");
            }
            UpdateFreeInfo();
        }

        private void UpdateFreeInfo()
        {
            var info = "";
            try
            {
                var config = FreeConfigManager.GetChatConfig();
                if (config is not null)
                {
                    info = string.Join("\n", new[]
                    {
                        FreeConfigManager.GetDescription(config, Lang),
                        FreeConfigManager.GetProviderInfo(config, Lang)
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"读取免费服务说明失败: {ex.Message}");
            }
            Tb_FreeInfo.Text = info;
            Tb_FreeInfo.Visibility = info.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FillPresets()
        {
            _customPreset.Content = T("QuickSetup.CustomProvider", "自定义地址");

            Cb_Preset.Items.Clear();
            foreach (var preset in (_presets?.Categories ?? new List<ApiPresetCategory>()).SelectMany(c => c.Presets))
                Cb_Preset.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
            Cb_Preset.Items.Add(_customPreset);
        }

        /// <summary>选中地址对应的预设；空地址选第一项，匹配不上归到自定义地址。</summary>
        private void SelectPreset(string url)
        {
            var items = Cb_Preset.Items.OfType<ComboBoxItem>();
            Cb_Preset.SelectedItem = url.Length == 0
                ? items.First()
                : items.FirstOrDefault(i => i.Tag is ApiPresetItem p && IsSameUrl(p.Url, url)) ?? _customPreset;
            if (ReferenceEquals(Cb_Preset.SelectedItem, _customPreset) && url.Length > 0)
                Tb_Url.Text = url;
        }

        private void ApplyPresetModels()
        {
            if (SelectedPreset is not { } preset) return;
            Cb_Model.ItemsSource = preset.Models;
            Cb_Model.Text = preset.DefaultModel ?? preset.Models.FirstOrDefault() ?? "";
        }

        private void LoadLocal(Setting.ChannelKind kind)
        {
            var node = _settings!.EnumerateChannels().FirstOrDefault(c => c.Kind == kind)
                       ?? Setting.CreateChannel(kind);
            Tb_LocalUrl.Text = node.Endpoint ?? "";
            Cb_LocalModel.ItemsSource = null;
            Cb_LocalModel.Text = node.Model ?? "";
        }

        private void UpdateVisibility()
        {
            Panel_ApiKey.Visibility = Rb_ApiKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Panel_Local.Visibility = Rb_Local.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Panel_OllamaManager.Visibility = Rb_Ollama.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Panel_CustomUrl.Visibility = ReferenceEquals(Cb_Preset.SelectedItem, _customPreset)
                ? Visibility.Visible
                : Visibility.Collapsed;

            var existing = QuickSetupService.FindExisting(_settings!, Setting.ChannelKind.OpenAIChat, ApiUrl);
            Tb_KeySaved.Visibility = existing is Setting.OpenAINodeSetting { ApiKey.Length: > 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ShowStatus(string? text, bool? ok)
        {
            Tb_Status.Text = text ?? "";
            Tb_Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            Tb_Status.Foreground = (Brush)FindResource(ok switch
            {
                true => "SuccessBrush",
                false => "ErrorBrush",
                _ => "SecondaryTextBrush"
            });
        }

        private void NotifyChanged()
        {
            if (_loading || _settings is null) return;
            _changed = true;
            ShowStatus(null, null);
            SelectionChanged?.Invoke();
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings is null) return;
            UpdateVisibility();
            NotifyChanged();
        }

        private void LocalKind_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings is null) return;
            LoadLocal(LocalKind);
            UpdateVisibility();
            NotifyChanged();
        }

        private void OllamaManager_Click(object sender, RoutedEventArgs e)
        {
            var url = Tb_LocalUrl.Text.Trim();
            if (url.Length > 0 && !IsHttpUrl(url))
            {
                ShowStatus(T("QuickSetup.InvalidUrl", "接口地址要以 http:// 或 https:// 开头。"), false);
                return;
            }
            winOllamaManager.Open(Window.GetWindow(this), _settings!, url, Cb_LocalModel.Text, ApplyOllamaChoice);
        }

        private bool ApplyOllamaChoice(string model, string url)
        {
            Rb_Local.IsChecked = true;
            Rb_Ollama.IsChecked = true;
            Tb_LocalUrl.Text = url;
            Cb_LocalModel.Text = model;
            // 模型框只把键盘输入算作改动，回填要自己标记。NotifyChanged 会清状态，所以提示放在后面
            NotifyChanged();
            ShowStatus(string.Format(T("QuickSetup.OllamaChosen", "已选用 {0}，保存后生效。"), model), true);
            return true;
        }

        private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _settings is null) return;
            ApplyPresetModels();
            UpdateVisibility();
            NotifyChanged();
        }

        private void Url_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings is null) return;
            UpdateVisibility();
            NotifyChanged();
        }

        private void Field_Changed(object sender, RoutedEventArgs e) => NotifyChanged();

        // 可编辑下拉首次显示时，内部文本框拿到初值也会冒泡 TextChanged，不能算用户改动
        private void ModelText_Changed(object sender, TextChangedEventArgs e)
        {
            if (((ComboBox)sender).IsKeyboardFocusWithin) NotifyChanged();
        }

        private async void FetchModels_Click(object sender, RoutedEventArgs e)
        {
            var isLocal = ReferenceEquals(sender, Btn_FetchLocalModels);
            var combo = isLocal ? Cb_LocalModel : Cb_Model;
            var url = isLocal ? Tb_LocalUrl.Text.Trim() : ApiUrl;

            if (!IsHttpUrl(url))
            {
                ShowStatus(T("QuickSetup.InvalidUrl", "接口地址要以 http:// 或 https:// 开头。"), false);
                return;
            }
            var core = _coreProvider?.Invoke();
            if (core is null)
            {
                ShowStatus(T("QuickSetup.CoreNotReady", "聊天服务还在启动，请稍后再试。"), false);
                return;
            }

            _fetchCts?.Cancel();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;
            var draft = QuickSetupService.BuildDraft(_settings!,
                isLocal ? LocalKind : Setting.ChannelKind.OpenAIChat, null, url, Pb_ApiKey.Password, null);

            var button = (Button)sender;
            button.IsEnabled = false;
            ShowStatus(T("QuickSetup.Fetching", "正在获取模型列表…"), null);
            try
            {
                var models = await core.FetchModelsAsync(draft, token);
                if (token.IsCancellationRequested) return;

                var current = combo.Text;
                combo.ItemsSource = models;
                combo.Text = string.IsNullOrWhiteSpace(current) && models.Count > 0 ? models[0] : current;
                ShowStatus(string.Format(T("QuickSetup.Fetched", "获取到 {0} 个模型"), models.Count), true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowStatus(string.Format(T("QuickSetup.FetchFail", "获取模型失败：{0}"), ex.Message), false);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private static bool IsHttpUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static bool IsSameUrl(string? a, string? b)
            => string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
