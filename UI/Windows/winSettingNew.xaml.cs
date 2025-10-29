 using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.UI.Controls;
using VPetLLM.Utils;

namespace VPetLLM.UI.Windows
{
    public class UnifiedPluginItem
    {
        public bool IsLocal { get; set; }
        public bool IsFailed { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string OriginalName { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string OriginalDescription { get; set; }
        public bool IsEnabled { get; set; }
        public string ActionText { get; set; }
        public string Icon { get; set; }
        public string FileUrl { get; set; }
        public string SHA256 { get; set; }
        public IVPetLLMPlugin LocalPlugin { get; set; }
        public FailedPlugin FailedPlugin { get; set; }
        public string Version { get; set; }
        public string RemoteVersion { get; set; }
        public bool IsUpdatable { get; set; }
        public string UpdateAvailableText { get; set; }
        public string UninstallActionText { get; set; }
        public string LocalFilePath { get; set; }
        public bool HasSettingAction { get; set; }
        public string StatusText { get; set; }
        public string SettingActionText { get; set; }
    }

    public partial class winSettingNew : Window
    {
        private Setting.OpenAINodeSetting GetCurrentOpenAISetting()
        {
            // 获取当前活跃的OpenAI节点设置
            if (_plugin.Settings.OpenAI.OpenAINodes.Count == 0)
            {
                // 如果没有配置节点，返回默认设置（并在渠道内多 key 自动随机）
                return new Setting.OpenAINodeSetting
                {
                    ApiKey = SelectRandomKey(_plugin.Settings.OpenAI.ApiKey),
                    Model = _plugin.Settings.OpenAI.Model,
                    Url = _plugin.Settings.OpenAI.Url,
                    Temperature = _plugin.Settings.OpenAI.Temperature,
                    MaxTokens = _plugin.Settings.OpenAI.MaxTokens,
                    EnableAdvanced = _plugin.Settings.OpenAI.EnableAdvanced,
                    Enabled = _plugin.Settings.OpenAI.Enabled,
                    Name = _plugin.Settings.OpenAI.Name
                };
            }

            // 使用负载均衡或当前索引获取节点
            if (_plugin.Settings.OpenAI.EnableLoadBalancing)
            {
                // 负载均衡：随机在已启用节点中选择
                var enabledNodes = _plugin.Settings.OpenAI.OpenAINodes.Where(n => n.Enabled).ToList();
                if (enabledNodes.Count == 0)
                {
                    // 如果没有启用的节点，返回第一个节点（并随机化其 ApiKey）
                    return CloneOpenAINodeWithRandomKey(_plugin.Settings.OpenAI.OpenAINodes.First());
                }
                var idx = _rand.Next(enabledNodes.Count);
                return CloneOpenAINodeWithRandomKey(enabledNodes[idx]);
            }
            else
            {
                // 使用固定索引
                if (_plugin.Settings.OpenAI.CurrentNodeIndex >= 0 && _plugin.Settings.OpenAI.CurrentNodeIndex < _plugin.Settings.OpenAI.OpenAINodes.Count)
                {
                    return CloneOpenAINodeWithRandomKey(_plugin.Settings.OpenAI.OpenAINodes[_plugin.Settings.OpenAI.CurrentNodeIndex]);
                }
                return CloneOpenAINodeWithRandomKey(_plugin.Settings.OpenAI.OpenAINodes.First());
            }
        }

        private Setting.GeminiSetting GetCurrentGeminiSetting()
        {
            var g = _plugin.Settings.Gemini;

            // 未启用负载均衡：仍进行渠道内多 key 自动随机
            if (!g.EnableLoadBalancing)
            {
                return new Setting.GeminiSetting
                {
                    EnableLoadBalancing = false,
                    ApiKey = SelectRandomKey(g.ApiKey),
                    Model = g.Model,
                    Url = g.Url,
                    Temperature = g.Temperature,
                    MaxTokens = g.MaxTokens,
                    EnableAdvanced = g.EnableAdvanced,
                    GeminiNodes = g.GeminiNodes
                };
            }

            var nodes = g.GeminiNodes ?? new List<Setting.GeminiNodeSetting>();
            var enabledNodes = nodes.Where(n => n.Enabled).ToList();

            // 若无启用节点，沿用全局设置，但仍随机化渠道内多 key
            if (enabledNodes.Count == 0)
            {
                return new Setting.GeminiSetting
                {
                    EnableLoadBalancing = g.EnableLoadBalancing,
                    ApiKey = SelectRandomKey(g.ApiKey),
                    Model = g.Model,
                    Url = g.Url,
                    Temperature = g.Temperature,
                    MaxTokens = g.MaxTokens,
                    EnableAdvanced = g.EnableAdvanced,
                    GeminiNodes = g.GeminiNodes
                };
            }

            // 在启用节点中随机选一个，并对其 ApiKey 做随机化
            var picked = enabledNodes[_rand.Next(enabledNodes.Count)];

            return new Setting.GeminiSetting
            {
                EnableLoadBalancing = true,
                ApiKey = SelectRandomKey(picked.ApiKey),
                Model = picked.Model,
                Url = picked.Url,
                Temperature = picked.Temperature,
                MaxTokens = picked.MaxTokens,
                EnableAdvanced = picked.EnableAdvanced,
                GeminiNodes = g.GeminiNodes
            };
        }

        private string GetPluginDescription(IVPetLLMPlugin plugin, string langCode)
        {
            try
            {
                // 尝试获取插件的多语言描述
                // 如果插件支持多语言，它应该能根据当前语言返回正确的描述
                if (plugin.Enabled)
                {
                    // 启用的插件应该已经正确初始化，可以直接获取Description
                    return plugin.Description;
                }
                else
                {
                    // 禁用的插件需要特殊处理来获取正确的多语言描述
                    try
                    {
                        // 临时初始化插件以获取正确的多语言描述
                        var wasEnabled = plugin.Enabled;
                        plugin.Initialize(_plugin);
                        var description = plugin.Description;

                        // 如果插件原本是禁用的，恢复禁用状态
                        if (!wasEnabled)
                        {
                            plugin.Unload();
                        }

                        return description;
                    }
                    catch (Exception ex)
                    {
                        Utils.Logger.Log($"Failed to get localized description for disabled plugin {plugin.Name}: {ex.Message}");
                        // 回退到原始描述
                        return plugin.Description;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting plugin description for {plugin.Name}: {ex.Message}");
                return plugin.Description ?? string.Empty;
            }
        }

        private readonly global::VPetLLM.VPetLLM _plugin;

        // 自动保存相关字段
        private DispatcherTimer? _autoSaveTimer;
        // 密钥输入专用去抖保存计时器（避免每次按键均保存）
        private DispatcherTimer? _secretSaveTimer;
        // Gemini 列表刷新去抖计时器（避免每次键入都刷新列表导致重绘卡顿）
        private DispatcherTimer? _geminiRefreshTimer;
        private bool _hasUnsavedChanges = false;
        private readonly object _saveLock = new object();
        // 合并异步保存标记，防止重复排队
        private bool _isSaveScheduled = false;
        // UI 就绪标记：Loaded 后才允许保存，避免初始化阶段触发立即保存导致 NRE
        private bool _isReadyToSave = false;
        private bool _isUpdatingNodeDetails = false;

        // TTS服务
        private TTSService? _ttsService;
        // 随机选择器（负载均衡随机用）
        private readonly Random _rand = new Random();

        // 在同一渠道内，若 ApiKey 包含多个 key，则自动随机选择一个（不受负载均衡开关影响）
        private string SelectRandomKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var parts = raw
                .Split(new[] { ',', ';', '|', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .ToList();
            if (parts.Count <= 1) return raw.Trim();
            var idx = _rand.Next(parts.Count);
            return parts[idx];
        }

        // 克隆一个 OpenAI 节点，并将 ApiKey 随机拆分后取单个 key
        private Setting.OpenAINodeSetting CloneOpenAINodeWithRandomKey(Setting.OpenAINodeSetting src)
        {
            if (src == null) return null;
            return new Setting.OpenAINodeSetting
            {
                Name = src.Name,
                ApiKey = SelectRandomKey(src.ApiKey),
                Model = src.Model,
                Url = src.Url,
                Temperature = src.Temperature,
                MaxTokens = src.MaxTokens,
                EnableAdvanced = src.EnableAdvanced,
                Enabled = src.Enabled
            };
        }

        public winSettingNew(global::VPetLLM.VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            
            // 初始化触摸反馈设置控件
            InitializeTouchFeedbackSettings();
            _plugin.SettingWindow = this;
            LoadSettings();
            Closed += Window_Closed;
            Loaded += (s, e) =>
            {
                UpdateUIForLanguage();
                // 同步本地化服务的语言，确保 XAML 中 {utils:Localize} 初次加载和后续切换都正确刷新
                LocalizationService.Instance.ChangeLanguage(_plugin.Settings.Language);
                // 监听全局本地化变更，自动刷新手动赋值的 UI（如列头）
                LocalizationService.Instance.PropertyChanged += (sender2, e2) =>
                {
                    if (e2.PropertyName == "Item[]")
                    {
                        Dispatcher.BeginInvoke(new Action(UpdateUIForLanguage), System.Windows.Threading.DispatcherPriority.Background);
                    }
                };
                Button_RefreshPlugins_Click(this, new RoutedEventArgs());
                // 标记界面已就绪，允许后续的立即保存
                _isReadyToSave = true;
                // 视觉树稳定后再挂载列表冒泡监听，避免初始化/布局阶段触发保存
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    AttachImmediateSaveForChannelLists();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
            ((Slider)this.FindName("Slider_Ollama_Temperature")).ValueChanged += Control_ValueChanged;
            // OpenAI多节点配置 - 温度设置事件处理已移至多节点管理界面
            if (this.FindName("Slider_Gemini_Temperature") is Slider sliderGemTemp)
                sliderGemTemp.ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_Free_Temperature")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_TTS_Volume")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_TTS_VolumeGain")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_TTS_Speed")).ValueChanged += Control_ValueChanged;

            // Add event handlers for all other controls
            ((ComboBox)this.FindName("ComboBox_Provider")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_Language")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_PromptLanguage")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_AiName")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_UserName")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_FollowVPetName")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_Role")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_OllamaUrl")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_OllamaModel")).SelectionChanged += Control_SelectionChanged;
            // OpenAI多节点配置 - 事件处理已移至多节点管理界面
            if (this.FindName("TextBox_GeminiApiKey") is TextBox tbGeminiApiKey)
                tbGeminiApiKey.TextChanged += Control_TextChanged;

            if (this.FindName("ComboBox_GeminiModel") is ComboBox comboGemModel)
                comboGemModel.SelectionChanged += Control_SelectionChanged;



            ((CheckBox)this.FindName("CheckBox_KeepContext")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableChatHistory")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_SeparateChatByProvider")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableAction")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableBuy")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableState")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableActionExecution")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableMove")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableTime")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableLiveMode")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_LimitStateChanges")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).Click += Control_Click;
            ((ComboBox)this.FindName("ComboBox_CompressionMode")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_HistoryCompressionTokenThreshold")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_LogAutoScroll")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_MaxLogCount")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableStreaming")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_Ollama_MaxTokens")).TextChanged += Control_TextChanged;
            // OpenAI多节点配置 - 高级设置事件处理已移至多节点管理界面
            if (this.FindName("CheckBox_Gemini_EnableAdvanced") is CheckBox cbGemAdvBind)
                cbGemAdvBind.Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Free_EnableStreaming")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Free_EnableAdvanced")).Click += Control_Click;
            if (this.FindName("TextBox_Gemini_MaxTokens") is TextBox tbGemMaxBind)
                tbGemMaxBind.TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_Free_MaxTokens")).TextChanged += Control_TextChanged;

            // Proxy settings
            ((CheckBox)this.FindName("CheckBox_Proxy_IsEnabled")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_FollowSystemProxy")).Click += Control_Click;
            ((RadioButton)this.FindName("RadioButton_Proxy_Http")).Click += Control_Click;
            ((RadioButton)this.FindName("RadioButton_Proxy_Socks")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_Proxy_Address")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForAllAPI")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForOllama")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForOpenAI")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForGemini")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForFree")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForTTS")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForASR")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).Click += Control_Click;

            // Plugin Store Proxy settings
            ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).TextChanged += Control_TextChanged;

            // TTS settings
            ((CheckBox)this.FindName("CheckBox_TTS_IsEnabled")).Click += Control_Click;
            ((ComboBox)this.FindName("ComboBox_TTS_Provider")).SelectionChanged += Control_SelectionChanged;
            ((CheckBox)this.FindName("CheckBox_TTS_OnlyPlayAIResponse")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_TTS_UseQueueDownload")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_TTS_URL_BaseUrl")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_URL_Voice")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_URL_RequestMethod")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_BaseUrl")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_ApiKey")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Model")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Voice")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Format")).SelectionChanged += Control_SelectionChanged;

            // DIY TTS 按钮事件绑定 - 已在 XAML 中绑定，无需重复绑定

            // GPT-SoVITS TTS settings
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLanguage")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_CutPunc")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopK")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopP")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Temperature")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Speed")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_SplitMethod")).SelectionChanged += Control_SelectionChanged;

            // ASR settings
            ((CheckBox)this.FindName("CheckBox_ASR_IsEnabled")).Click += Control_Click;
            ((ComboBox)this.FindName("ComboBox_ASR_Provider")).SelectionChanged += Control_SelectionChanged;
            // 快捷键现在通过捕获功能设置，不需要绑定TextChanged事件
            ((ComboBox)this.FindName("ComboBox_ASR_RecordingDevice")).SelectionChanged += Control_SelectionChanged;
            ((CheckBox)this.FindName("CheckBox_ASR_AutoSend")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_ASR_ShowTranscriptionWindow")).Click += Control_Click;
            
            // OpenAI ASR settings
            ((TextBox)this.FindName("TextBox_ASR_OpenAI_ApiKey")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_ASR_OpenAI_BaseUrl")).TextChanged += Control_TextChanged;
            var comboBoxASROpenAIModel = (ComboBox)this.FindName("ComboBox_ASR_OpenAI_Model");
            comboBoxASROpenAIModel.SelectionChanged += Control_SelectionChanged;
            comboBoxASROpenAIModel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(Control_TextChanged), true);
            
            // Soniox ASR settings
            ((TextBox)this.FindName("TextBox_ASR_Soniox_ApiKey")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_ASR_Soniox_BaseUrl")).TextChanged += Control_TextChanged;
            var comboBoxASRSonioxModel = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Model");
            comboBoxASRSonioxModel.SelectionChanged += ComboBox_ASR_Soniox_Model_SelectionChanged;
            comboBoxASRSonioxModel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(Control_TextChanged), true);
            ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnablePunctuation")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnableProfanityFilter")).Click += Control_Click;
            ((ComboBox)this.FindName("ComboBox_ASR_OpenAI_Model")).SelectionChanged += Control_SelectionChanged;

            ((Button)this.FindName("Button_RefreshPlugins")).Click += Button_RefreshPlugins_Click;

            // 绑定 OpenAI 节点详情控件事件，确保编辑即时回写
            EnsureOpenAINodeDetailHandlers();
            // 初始化自动保存定时器
            InitializeAutoSaveTimer();
            // 初始化密钥输入去抖保存定时器
            InitializeSecretSaveTimer();
            // 初始化 Gemini 列表刷新去抖定时器
            InitializeGeminiRefreshTimer();

            // 负载均衡开关点击即保存
            if (this.FindName("CheckBox_Gemini_EnableLoadBalancing") is CheckBox cbGemLBBind) cbGemLBBind.Click += LoadBalancing_CheckBox_Click;
            if (this.FindName("CheckBox_OpenAI_EnableLoadBalancing") is CheckBox cbOpenLBBind) cbOpenLBBind.Click += LoadBalancing_CheckBox_Click;
        }

        private void Control_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 实时更新温度值显示
            if (sender is Slider slider)
            {
                if (slider.Name == "Slider_Ollama_Temperature")
                {
                    ((TextBlock)this.FindName("TextBlock_Ollama_TemperatureValue")).Text = slider.Value.ToString("F2");
                }
                else if (slider.Name == "Slider_OpenAI_Temperature")
                {
                    ((TextBlock)this.FindName("TextBlock_OpenAI_TemperatureValue")).Text = slider.Value.ToString("F2");
                }
                else if (slider.Name == "Slider_Gemini_Temperature")
                {
                    if (this.FindName("TextBlock_Gemini_TemperatureValue") is TextBlock tvGem)
                        tvGem.Text = slider.Value.ToString("F2");
                }
                else if (slider.Name == "Slider_Free_Temperature")
                {
                    ((TextBlock)this.FindName("TextBlock_Free_TemperatureValue")).Text = slider.Value.ToString("F2");
                }
                else if (slider.Name == "Slider_TTS_Volume")
                {
                    ((TextBlock)this.FindName("TextBlock_TTS_VolumeValue")).Text = slider.Value.ToString("F2");

                    // 立即更新所有TTS服务实例的音量设置
                    var volumeGainSlider = (Slider)this.FindName("Slider_TTS_VolumeGain");
                    _ttsService?.UpdateVolumeSettings(slider.Value, volumeGainSlider.Value);
                    _plugin.TTSService?.UpdateVolumeSettings(slider.Value, volumeGainSlider.Value);
                }
                else if (slider.Name == "Slider_TTS_VolumeGain")
                {
                    ((TextBlock)this.FindName("TextBlock_TTS_VolumeGainValue")).Text = slider.Value.ToString("F1");

                    // 立即更新所有TTS服务实例的音量设置
                    var volumeSlider = (Slider)this.FindName("Slider_TTS_Volume");
                    _ttsService?.UpdateVolumeSettings(volumeSlider.Value, slider.Value);
                    _plugin.TTSService?.UpdateVolumeSettings(volumeSlider.Value, slider.Value);
                }
                else if (slider.Name == "Slider_TTS_Speed")
                {
                    ((TextBlock)this.FindName("TextBlock_TTS_SpeedValue")).Text = slider.Value.ToString("F2");
                }
            }

            // 立即保存重要配置
            ScheduleAutoSave();
        }

        private void Control_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 先处理特殊逻辑
            if (sender == FindName("ComboBox_Language"))
            {
                // 保存到设置（更新 _plugin.Settings.Language）
                SaveSettings();
                // 重新加载语言资源
                LanguageHelper.ReloadLanguages();

                // 立刻广播一次，XAML 中 {utils:Localize} 立即更新
                LocalizationService.Instance.ChangeLanguage(_plugin.Settings.Language);
                // 同时刷新代码后台赋值的控件
                UpdateUIForLanguage();

                // 在下一帧（空闲时）再强制刷新一次，确保语言资源完全就位后所有文本都更新
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LocalizationService.Instance.Refresh();
                    UpdateUIForLanguage();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);

                // 重新初始化部分非绑定或复杂逻辑的控件
                InitializeTouchFeedbackSettings();
                // 刷新插件列表以更新动态生成的描述等文本
                Button_RefreshPlugins_Click(this, new RoutedEventArgs());
            }
            if (sender == FindName("ComboBox_PromptLanguage"))
            {
                // 立即保存提示词语言设置
                SaveSettings();
                PromptHelper.ReloadPrompts();
            }

            // 对于关键配置项，使用Dispatcher确保UI更新完成后再保存
            if (sender is ComboBox comboBox)
            {
                string name = comboBox.Name ?? "";
                bool isImportantConfig = name.Contains("Provider") || name.Contains("Model");

                if (isImportantConfig)
                {
                    // 使用Dispatcher.BeginInvoke确保在UI更新完成后保存
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SaveSettings();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    ScheduleAutoSave();
                }
            }
            else
            {
                ScheduleAutoSave();
            }
        }

        private void Control_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 对于关键配置字段，立即保存
            if (sender is TextBox textBox)
            {
                string[] criticalFields = {
                    "TextBox_OpenAIApiKey", "TextBox_GeminiApiKey",
                    "TextBox_OllamaUrl", "TextBox_OpenAIUrl"
                };

                if (criticalFields.Contains(textBox.Name))
                {
                    // 关键字段使用Dispatcher确保UI更新完成后保存
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SaveSettings();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    // 其他字段延迟保存
                    ScheduleAutoSave();
                }
            }
        }

        private void Control_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Name == "CheckBox_AutoMigrateChatHistory")
                return;

            // 立即保存重要配置
            ScheduleAutoSave();
        }

        private void Control_PasswordChanged(object sender, RoutedEventArgs e)
        {
            ScheduleAutoSave();
        }

        /// <summary>
        /// 初始化触摸反馈设置控件
        /// </summary>
        private void InitializeTouchFeedbackSettings()
        {
            try
            {
                var touchFeedbackControl = new TouchFeedbackSettingsControl(_plugin);
                TouchFeedbackSettingsContainer.Content = touchFeedbackControl;
                Logger.Log("TouchFeedbackSettingsControl initialized successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing TouchFeedbackSettingsControl: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            ((ComboBox)this.FindName("ComboBox_Provider")).ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            ((ComboBox)this.FindName("ComboBox_Provider")).SelectedItem = _plugin.Settings.Provider;
            var languageComboBox = (ComboBox)this.FindName("ComboBox_Language");
            languageComboBox.ItemsSource = LanguageHelper.LanguageDisplayMap.Values;
            languageComboBox.SelectedItem = LanguageHelper.LanguageDisplayMap.FirstOrDefault(x => x.Key == _plugin.Settings.Language).Value;
            var promptlanguageComboBox = (ComboBox)this.FindName("ComboBox_PromptLanguage");
            promptlanguageComboBox.ItemsSource = new List<string> { "English", "简体中文" };
            promptlanguageComboBox.SelectedItem = _plugin.Settings.PromptLanguage == "en" ? "English" : "简体中文";
            ((TextBox)this.FindName("TextBox_AiName")).Text = _plugin.Settings.AiName;
            ((TextBox)this.FindName("TextBox_UserName")).Text = _plugin.Settings.UserName;
            ((CheckBox)this.FindName("CheckBox_FollowVPetName")).IsChecked = _plugin.Settings.FollowVPetName;
            ((TextBox)this.FindName("TextBox_Role")).Text = _plugin.Settings.Role;
            if (_plugin.Settings.OpenAI == null)
            {
                _plugin.Settings.OpenAI = new Setting.OpenAISetting();
            }
            if (_plugin.Settings.Free == null)
            {
                _plugin.Settings.Free = new Setting.FreeSetting();
            }
            if (_plugin.Settings.Ollama == null)
            {
                _plugin.Settings.Ollama = new Setting.OllamaSetting();
            }
            ((TextBox)this.FindName("TextBox_OllamaUrl")).Text = _plugin.Settings.Ollama.Url;
            ((ComboBox)this.FindName("ComboBox_OllamaModel")).Text = _plugin.Settings.Ollama.Model;
            // OpenAI多节点配置 - 不再使用单一配置
            // 这些控件现在用于多节点管理界面
            //((TextBox)this.FindName("TextBox_OpenAIApiKey")).Text = "";
            //((ComboBox)this.FindName("ComboBox_OpenAIModel")).Text = "";
            //((TextBox)this.FindName("TextBox_OpenAIUrl")).Text = "";
            if (_plugin.Settings.Gemini == null)
            {
                _plugin.Settings.Gemini = new Setting.GeminiSetting();
            }
            // 负载均衡开关
            if (this.FindName("CheckBox_Gemini_EnableLoadBalancing") is CheckBox cbGemLB)
                cbGemLB.IsChecked = _plugin.Settings.Gemini.EnableLoadBalancing;
            // OpenAI 负载均衡开关
            if (this.FindName("CheckBox_OpenAI_EnableLoadBalancing") is CheckBox cbOpenLB)
                cbOpenLB.IsChecked = _plugin.Settings.OpenAI.EnableLoadBalancing;

            // 刷新Gemini多渠道列表
            RefreshGeminiNodesList();

            // 兼容旧表单（保留显示）
            if (this.FindName("TextBox_GeminiApiKey") is TextBox tbGeminiApiKey2)
                tbGeminiApiKey2.Text = _plugin.Settings.Gemini.ApiKey;
            if (this.FindName("ComboBox_GeminiModel") is ComboBox cbGeminiModel)
                cbGeminiModel.Text = _plugin.Settings.Gemini.Model;


            ((CheckBox)this.FindName("CheckBox_KeepContext")).IsChecked = _plugin.Settings.KeepContext;
            ((CheckBox)this.FindName("CheckBox_EnableChatHistory")).IsChecked = _plugin.Settings.EnableChatHistory;
            ((CheckBox)this.FindName("CheckBox_SeparateChatByProvider")).IsChecked = _plugin.Settings.SeparateChatByProvider;
            ((CheckBox)this.FindName("CheckBox_EnableAction")).IsChecked = _plugin.Settings.EnableAction;
            ((CheckBox)this.FindName("CheckBox_EnableBuy")).IsChecked = _plugin.Settings.EnableBuy;
            ((CheckBox)this.FindName("CheckBox_EnableState")).IsChecked = _plugin.Settings.EnableState;
            ((CheckBox)this.FindName("CheckBox_EnableActionExecution")).IsChecked = _plugin.Settings.EnableActionExecution;
            ((CheckBox)this.FindName("CheckBox_EnableMove")).IsChecked = _plugin.Settings.EnableMove;
            ((CheckBox)this.FindName("CheckBox_EnableTime")).IsChecked = _plugin.Settings.EnableTime;
            ((CheckBox)this.FindName("CheckBox_EnableBuyFeedback")).IsChecked = _plugin.Settings.EnableBuyFeedback;
            ((CheckBox)this.FindName("CheckBox_EnableLiveMode")).IsChecked = _plugin.Settings.EnableLiveMode;
            ((CheckBox)this.FindName("CheckBox_LimitStateChanges")).IsChecked = _plugin.Settings.LimitStateChanges;
            ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).IsChecked = _plugin.Settings.EnableHistoryCompression;
            
            // 加载压缩模式
            var compressionModeComboBox = (ComboBox)this.FindName("ComboBox_CompressionMode");
            foreach (ComboBoxItem item in compressionModeComboBox.Items)
            {
                if (item.Tag.ToString() == _plugin.Settings.CompressionMode.ToString())
                {
                    compressionModeComboBox.SelectedItem = item;
                    break;
                }
            }
            
            ((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).Text = _plugin.Settings.HistoryCompressionThreshold.ToString();
            ((TextBox)this.FindName("TextBox_HistoryCompressionTokenThreshold")).Text = _plugin.Settings.HistoryCompressionTokenThreshold.ToString();
            ((CheckBox)this.FindName("CheckBox_LogAutoScroll")).IsChecked = _plugin.Settings.LogAutoScroll;
            ((TextBox)this.FindName("TextBox_MaxLogCount")).Text = _plugin.Settings.MaxLogCount.ToString();
            ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced")).IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableStreaming")).IsChecked = _plugin.Settings.Ollama.EnableStreaming;
            ((Slider)this.FindName("Slider_Ollama_Temperature")).Value = _plugin.Settings.Ollama.Temperature;
            ((TextBlock)this.FindName("TextBlock_Ollama_TemperatureValue")).Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_Ollama_MaxTokens")).Text = _plugin.Settings.Ollama.MaxTokens.ToString();
            ((CheckBox)this.FindName("CheckBox_OpenAI_EnableAdvanced")).IsChecked = _plugin.Settings.OpenAI.EnableAdvanced;
            ((Slider)this.FindName("Slider_OpenAI_Temperature")).Value = _plugin.Settings.OpenAI.Temperature;
            ((TextBlock)this.FindName("TextBlock_OpenAI_TemperatureValue")).Text = _plugin.Settings.OpenAI.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_OpenAI_MaxTokens")).Text = _plugin.Settings.OpenAI.MaxTokens.ToString();
            // 刷新OpenAI多节点列表，确保迁移后的节点显示且避免空引用
            RefreshOpenAINodesList();
            if (this.FindName("CheckBox_Gemini_EnableAdvanced") is CheckBox cbGemAdv)
                cbGemAdv.IsChecked = _plugin.Settings.Gemini.EnableAdvanced;
            if (this.FindName("Slider_Gemini_Temperature") is Slider slGemTemp)
            {
                slGemTemp.Value = _plugin.Settings.Gemini.Temperature;
                if (this.FindName("TextBlock_Gemini_TemperatureValue") is TextBlock tvGemTemp)
                    tvGemTemp.Text = _plugin.Settings.Gemini.Temperature.ToString("F2");
            }
            if (this.FindName("TextBox_Gemini_MaxTokens") is TextBox tbGemMax)
                tbGemMax.Text = _plugin.Settings.Gemini.MaxTokens.ToString();
            ((CheckBox)this.FindName("CheckBox_Free_EnableStreaming")).IsChecked = _plugin.Settings.Free.EnableStreaming;
            ((CheckBox)this.FindName("CheckBox_Free_EnableAdvanced")).IsChecked = _plugin.Settings.Free.EnableAdvanced;
            ((Slider)this.FindName("Slider_Free_Temperature")).Value = _plugin.Settings.Free.Temperature;
            ((TextBlock)this.FindName("TextBlock_Free_TemperatureValue")).Text = _plugin.Settings.Free.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_Free_MaxTokens")).Text = _plugin.Settings.Free.MaxTokens.ToString();
            ((TextBlock)this.FindName("TextBlock_CurrentContextLength")).Text = _plugin.ChatCore.GetChatHistory().Count.ToString();
            ((ListBox)this.FindName("LogBox")).ItemsSource = Logger.Logs;

            // Proxy settings
            if (_plugin.Settings.Proxy == null)
            {
                _plugin.Settings.Proxy = new Setting.ProxySetting();
            }
            ((CheckBox)this.FindName("CheckBox_Proxy_IsEnabled")).IsChecked = _plugin.Settings.Proxy.IsEnabled;
            ((CheckBox)this.FindName("CheckBox_Proxy_FollowSystemProxy")).IsChecked = _plugin.Settings.Proxy.FollowSystemProxy;
            if (_plugin.Settings.Proxy.Protocol == "http")
            {
                ((RadioButton)this.FindName("RadioButton_Proxy_Http")).IsChecked = true;
            }
            else
            {
                ((RadioButton)this.FindName("RadioButton_Proxy_Socks")).IsChecked = true;
            }
            ((TextBox)this.FindName("TextBox_Proxy_Address")).Text = _plugin.Settings.Proxy.Address;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForAllAPI")).IsChecked = _plugin.Settings.Proxy.ForAllAPI;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForOllama")).IsChecked = _plugin.Settings.Proxy.ForOllama;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForOpenAI")).IsChecked = _plugin.Settings.Proxy.ForOpenAI;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForGemini")).IsChecked = _plugin.Settings.Proxy.ForGemini;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForFree")).IsChecked = _plugin.Settings.Proxy.ForFree;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForTTS")).IsChecked = _plugin.Settings.Proxy.ForTTS;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForASR")).IsChecked = _plugin.Settings.Proxy.ForASR;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).IsChecked = _plugin.Settings.Proxy.ForMcp;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).IsChecked = _plugin.Settings.Proxy.ForPlugin;

            // Plugin Store Proxy settings
            if (_plugin.Settings.PluginStore == null)
            {
                _plugin.Settings.PluginStore = new Setting.PluginStoreSetting();
            }
            ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).IsChecked = _plugin.Settings.PluginStore.UseProxy;
            ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).Text = _plugin.Settings.PluginStore.ProxyUrl;

            // TTS settings
            if (_plugin.Settings.TTS == null)
            {
                _plugin.Settings.TTS = new Setting.TTSSetting();
            }

            // 基本TTS设置
            ((CheckBox)this.FindName("CheckBox_TTS_IsEnabled")).IsChecked = _plugin.Settings.TTS.IsEnabled;
            ((CheckBox)this.FindName("CheckBox_TTS_OnlyPlayAIResponse")).IsChecked = _plugin.Settings.TTS.OnlyPlayAIResponse;
            ((CheckBox)this.FindName("CheckBox_TTS_UseQueueDownload")).IsChecked = _plugin.Settings.TTS.UseQueueDownload;
            ((Slider)this.FindName("Slider_TTS_Volume")).Value = _plugin.Settings.TTS.Volume;
            ((TextBlock)this.FindName("TextBlock_TTS_VolumeValue")).Text = _plugin.Settings.TTS.Volume.ToString("F2");
            ((Slider)this.FindName("Slider_TTS_VolumeGain")).Value = _plugin.Settings.TTS.VolumeGain;
            ((TextBlock)this.FindName("TextBlock_TTS_VolumeGainValue")).Text = _plugin.Settings.TTS.VolumeGain.ToString("F1");
            ((Slider)this.FindName("Slider_TTS_Speed")).Value = _plugin.Settings.TTS.Speed;
            ((TextBlock)this.FindName("TextBlock_TTS_SpeedValue")).Text = _plugin.Settings.TTS.Speed.ToString("F2");

            // TTS提供商设置
            var providerComboBox = (ComboBox)this.FindName("ComboBox_TTS_Provider");
            foreach (ComboBoxItem item in providerComboBox.Items)
            {
                if (item.Tag?.ToString() == _plugin.Settings.TTS.Provider)
                {
                    providerComboBox.SelectedItem = item;
                    break;
                }
            }

            // URL TTS设置
            ((TextBox)this.FindName("TextBox_TTS_URL_BaseUrl")).Text = _plugin.Settings.TTS.URL.BaseUrl;
            ((TextBox)this.FindName("TextBox_TTS_URL_Voice")).Text = _plugin.Settings.TTS.URL.Voice;

            // 加载请求方法设置
            var URLMethodComboBox = (ComboBox)this.FindName("ComboBox_TTS_URL_RequestMethod");
            foreach (ComboBoxItem item in URLMethodComboBox.Items)
            {
                if (item.Tag?.ToString() == _plugin.Settings.TTS.URL.Method)
                {
                    URLMethodComboBox.SelectedItem = item;
                    break;
                }
            }

            // OpenAI TTS设置
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_BaseUrl")).Text = _plugin.Settings.TTS.OpenAI.BaseUrl;
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_ApiKey")).Text = _plugin.Settings.TTS.OpenAI.ApiKey;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Model")).Text = _plugin.Settings.TTS.OpenAI.Model;

            var openAIVoiceComboBox = (ComboBox)this.FindName("ComboBox_TTS_OpenAI_Voice");
            foreach (ComboBoxItem item in openAIVoiceComboBox.Items)
            {
                if (item.Tag?.ToString() == _plugin.Settings.TTS.OpenAI.Voice)
                {
                    openAIVoiceComboBox.SelectedItem = item;
                    break;
                }
            }

            var formatComboBox = (ComboBox)this.FindName("ComboBox_TTS_OpenAI_Format");
            foreach (ComboBoxItem item in formatComboBox.Items)
            {
                if (item.Tag?.ToString() == _plugin.Settings.TTS.OpenAI.Format)
                {
                    formatComboBox.SelectedItem = item;
                    break;
                }
            }

            // DIY TTS 配置通过 JSON 文件管理，无需在此加载

            // 触发提供商切换事件来显示正确的面板
            ComboBox_TTS_Provider_SelectionChanged(providerComboBox, null);

            // 加载 GPT-SoVITS 设置
            LoadGPTSoVITSSettings();

            // 初始化TTS服务
            _ttsService = new TTSService(_plugin.Settings.TTS, _plugin.Settings.Proxy);

            // ASR settings
            if (_plugin.Settings.ASR == null)
            {
                _plugin.Settings.ASR = new Setting.ASRSetting();
            }

            ((CheckBox)this.FindName("CheckBox_ASR_IsEnabled")).IsChecked = _plugin.Settings.ASR.IsEnabled;
            // 显示当前快捷键
            UpdateHotkeyDisplay();
            ((CheckBox)this.FindName("CheckBox_ASR_AutoSend")).IsChecked = _plugin.Settings.ASR.AutoSend;
            ((CheckBox)this.FindName("CheckBox_ASR_ShowTranscriptionWindow")).IsChecked = _plugin.Settings.ASR.ShowTranscriptionWindow;

            // ASR Provider 设置
            var asrProviderComboBox = (ComboBox)this.FindName("ComboBox_ASR_Provider");
            foreach (ComboBoxItem item in asrProviderComboBox.Items)
            {
                if (item.Tag?.ToString() == _plugin.Settings.ASR.Provider)
                {
                    asrProviderComboBox.SelectedItem = item;
                    break;
                }
            }

            // ASR 录音设备设置
            LoadRecordingDevices();

            // OpenAI ASR 设置
            ((TextBox)this.FindName("TextBox_ASR_OpenAI_ApiKey")).Text = _plugin.Settings.ASR.OpenAI.ApiKey;
            ((TextBox)this.FindName("TextBox_ASR_OpenAI_BaseUrl")).Text = _plugin.Settings.ASR.OpenAI.BaseUrl;
            ((ComboBox)this.FindName("ComboBox_ASR_OpenAI_Model")).Text = _plugin.Settings.ASR.OpenAI.Model;

            // Soniox ASR 设置
            ((TextBox)this.FindName("TextBox_ASR_Soniox_ApiKey")).Text = _plugin.Settings.ASR.Soniox.ApiKey;
            ((TextBox)this.FindName("TextBox_ASR_Soniox_BaseUrl")).Text = _plugin.Settings.ASR.Soniox.BaseUrl;
            ((ComboBox)this.FindName("ComboBox_ASR_Soniox_Model")).Text = _plugin.Settings.ASR.Soniox.Model;
            
            // 初始化 Soniox 语言列表（如果有 API Key，尝试自动加载）
            InitializeSonioxLanguages();
            
            ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnablePunctuation")).IsChecked = _plugin.Settings.ASR.Soniox.EnablePunctuation;
            ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnableProfanityFilter")).IsChecked = _plugin.Settings.ASR.Soniox.EnableProfanityFilter;

            // Free ASR 无需配置

            // 更新 ASR Provider 面板显示
            UpdateASRProviderPanel();
        }

        private void SaveSettings()
        {
            if (!_isReadyToSave) return;
            var providerComboBox = (ComboBox)this.FindName("ComboBox_Provider");
            var languageComboBox = (ComboBox)this.FindName("ComboBox_Language");
            var promptlanguageComboBox = (ComboBox)this.FindName("ComboBox_PromptLanguage");
            var aiNameTextBox = (TextBox)this.FindName("TextBox_AiName");
            var userNameTextBox = (TextBox)this.FindName("TextBox_UserName");
            var followVPetNameCheckBox = (CheckBox)this.FindName("CheckBox_FollowVPetName");
            var roleTextBox = (TextBox)this.FindName("TextBox_Role");
            var ollamaUrlTextBox = (TextBox)this.FindName("TextBox_OllamaUrl");
            var ollamaModelComboBox = (ComboBox)this.FindName("ComboBox_OllamaModel");
            var openAIApiKeyTextBox = (TextBox)this.FindName("TextBox_OpenAIApiKey");
            var openAIModelComboBox = (ComboBox)this.FindName("ComboBox_OpenAIModel");
            var openAIUrlTextBox = (TextBox)this.FindName("TextBox_OpenAIUrl");
            var geminiApiKeyTextBox = (TextBox)this.FindName("TextBox_GeminiApiKey");
            var geminiModelComboBox = (ComboBox)this.FindName("ComboBox_GeminiModel");


            var keepContextCheckBox = (CheckBox)this.FindName("CheckBox_KeepContext");
            var enableChatHistoryCheckBox = (CheckBox)this.FindName("CheckBox_EnableChatHistory");
            var separateChatByProviderCheckBox = (CheckBox)this.FindName("CheckBox_SeparateChatByProvider");
            var enableActionCheckBox = (CheckBox)this.FindName("CheckBox_EnableAction");
            var enableBuyCheckBox = (CheckBox)this.FindName("CheckBox_EnableBuy");
            var enableStateCheckBox = (CheckBox)this.FindName("CheckBox_EnableState");
            var enableActionExecutionCheckBox = (CheckBox)this.FindName("CheckBox_EnableActionExecution");
            var enableMoveCheckBox = (CheckBox)this.FindName("CheckBox_EnableMove");
            var enableTimeCheckBox = (CheckBox)this.FindName("CheckBox_EnableTime");
            var enableBuyFeedbackCheckBox = (CheckBox)this.FindName("CheckBox_EnableBuyFeedback");
            var enableLiveModeCheckBox = (CheckBox)this.FindName("CheckBox_EnableLiveMode");
            var logAutoScrollCheckBox = (CheckBox)this.FindName("CheckBox_LogAutoScroll");
            var maxLogCountTextBox = (TextBox)this.FindName("TextBox_MaxLogCount");
            var ollamaEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced");
            var ollamaEnableStreamingCheckBox = (CheckBox)this.FindName("CheckBox_Ollama_EnableStreaming");
            var ollamaTemperatureSlider = (Slider)this.FindName("Slider_Ollama_Temperature");
            var ollamaMaxTokensTextBox = (TextBox)this.FindName("TextBox_Ollama_MaxTokens");
            var openAIEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_OpenAI_EnableAdvanced");
            var openAITemperatureSlider = (Slider)this.FindName("Slider_OpenAI_Temperature");
            var openAIMaxTokensTextBox = (TextBox)this.FindName("TextBox_OpenAI_MaxTokens");
            var geminiEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_Gemini_EnableAdvanced");
            var geminiTemperatureSlider = (Slider)this.FindName("Slider_Gemini_Temperature");
            var geminiMaxTokensTextBox = (TextBox)this.FindName("TextBox_Gemini_MaxTokens");
            var freeEnableStreamingCheckBox = (CheckBox)this.FindName("CheckBox_Free_EnableStreaming");
            var freeEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_Free_EnableAdvanced");
            var freeTemperatureSlider = (Slider)this.FindName("Slider_Free_Temperature");
            var freeMaxTokensTextBox = (TextBox)this.FindName("TextBox_Free_MaxTokens");
            var toolsDataGrid = (DataGrid)this.FindName("DataGrid_Tools");

            var oldProvider = _plugin.Settings.Provider;
            var newProvider = (Setting.LLMType)providerComboBox.SelectedItem;

            _plugin.Settings.Provider = newProvider;
            var selectedLanguageDisplay = (string)languageComboBox.SelectedItem;
            var selectedLangCode = LanguageHelper.LanguageDisplayMap.FirstOrDefault(x => x.Value == selectedLanguageDisplay).Key;
            if (selectedLangCode != null)
            {
                _plugin.Settings.Language = selectedLangCode;
            }
            _plugin.Settings.PromptLanguage = (string)promptlanguageComboBox.SelectedItem == "English" ? "en" : "zh";
            _plugin.Settings.FollowVPetName = followVPetNameCheckBox.IsChecked ?? true;
            // 始终保存AI名称和用户名，让程序逻辑决定是否使用
            _plugin.Settings.AiName = aiNameTextBox.Text;
            _plugin.Settings.UserName = userNameTextBox.Text;
            _plugin.Settings.Role = roleTextBox.Text;
            _plugin.Settings.Ollama.Url = ollamaUrlTextBox.Text;
            _plugin.Settings.Ollama.Model = ollamaModelComboBox.Text;
            // OpenAI多节点配置 - 不再使用单一配置
            // 这些控件现在用于多节点管理界面
            // OpenAI配置通过多节点列表管理
            if (geminiApiKeyTextBox != null)
                _plugin.Settings.Gemini.ApiKey = geminiApiKeyTextBox.Text;
            if (geminiModelComboBox != null)
                _plugin.Settings.Gemini.Model = geminiModelComboBox.Text;


            _plugin.Settings.KeepContext = keepContextCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableChatHistory = enableChatHistoryCheckBox.IsChecked ?? true;
            _plugin.Settings.SeparateChatByProvider = separateChatByProviderCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableAction = enableActionCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableBuy = enableBuyCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableState = enableStateCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableActionExecution = enableActionExecutionCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableMove = enableMoveCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableTime = enableTimeCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableBuyFeedback = enableBuyFeedbackCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableLiveMode = enableLiveModeCheckBox.IsChecked ?? false;
            _plugin.Settings.LimitStateChanges = ((CheckBox)this.FindName("CheckBox_LimitStateChanges")).IsChecked ?? false;
            _plugin.Settings.EnableHistoryCompression = ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).IsChecked ?? false;
            
            // 保存压缩模式
            var compressionModeComboBox = (ComboBox)this.FindName("ComboBox_CompressionMode");
            if (compressionModeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                if (Enum.TryParse<Setting.CompressionTriggerMode>(selectedItem.Tag.ToString(), out var mode))
                {
                    _plugin.Settings.CompressionMode = mode;
                }
            }
            
            if (int.TryParse(((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).Text, out int historyCompressionThreshold))
                _plugin.Settings.HistoryCompressionThreshold = historyCompressionThreshold;
            
            if (int.TryParse(((TextBox)this.FindName("TextBox_HistoryCompressionTokenThreshold")).Text, out int historyCompressionTokenThreshold))
                _plugin.Settings.HistoryCompressionTokenThreshold = historyCompressionTokenThreshold;
            
            _plugin.Settings.LogAutoScroll = logAutoScrollCheckBox.IsChecked ?? true;
            if (int.TryParse(maxLogCountTextBox.Text, out int maxLogCount))
                _plugin.Settings.MaxLogCount = maxLogCount;
            _plugin.Settings.Ollama.EnableAdvanced = ollamaEnableAdvancedCheckBox.IsChecked ?? false;
            _plugin.Settings.Ollama.EnableStreaming = ollamaEnableStreamingCheckBox.IsChecked ?? true;
            _plugin.Settings.Ollama.Temperature = ollamaTemperatureSlider.Value;
            if (int.TryParse(ollamaMaxTokensTextBox.Text, out int ollamaMaxTokens))
                _plugin.Settings.Ollama.MaxTokens = ollamaMaxTokens;
            // OpenAI多节点配置 - 高级设置通过多节点管理界面处理
            // 温度和最大令牌数现在由各个节点独立配置
            if (geminiEnableAdvancedCheckBox != null)
                _plugin.Settings.Gemini.EnableAdvanced = geminiEnableAdvancedCheckBox.IsChecked ?? false;
            if (geminiTemperatureSlider != null)
                _plugin.Settings.Gemini.Temperature = geminiTemperatureSlider.Value;
            if (geminiMaxTokensTextBox != null && int.TryParse(geminiMaxTokensTextBox.Text, out int geminiMaxTokens))
                _plugin.Settings.Gemini.MaxTokens = geminiMaxTokens;

            // 保存 Gemini 负载均衡开关
            if (this.FindName("CheckBox_Gemini_EnableLoadBalancing") is CheckBox cbGemLB2)
                _plugin.Settings.Gemini.EnableLoadBalancing = cbGemLB2.IsChecked ?? false;
            // 保存 OpenAI 负载均衡开关
            if (this.FindName("CheckBox_OpenAI_EnableLoadBalancing") is CheckBox cbOpenLB2)
                _plugin.Settings.OpenAI.EnableLoadBalancing = cbOpenLB2.IsChecked ?? false;
            
            // 保存 Free 设置
            if (freeEnableStreamingCheckBox != null)
                _plugin.Settings.Free.EnableStreaming = freeEnableStreamingCheckBox.IsChecked ?? false;
            if (freeEnableAdvancedCheckBox != null)
                _plugin.Settings.Free.EnableAdvanced = freeEnableAdvancedCheckBox.IsChecked ?? false;
            if (freeTemperatureSlider != null)
                _plugin.Settings.Free.Temperature = freeTemperatureSlider.Value;
            if (freeMaxTokensTextBox != null && int.TryParse(freeMaxTokensTextBox.Text, out int freeMaxTokens))
                _plugin.Settings.Free.MaxTokens = freeMaxTokens;
            
            _plugin.Settings.Tools = new List<Setting.ToolSetting>((IEnumerable<Setting.ToolSetting>)toolsDataGrid.ItemsSource);

            // Proxy settings
            _plugin.Settings.Proxy.IsEnabled = ((CheckBox)this.FindName("CheckBox_Proxy_IsEnabled")).IsChecked ?? false;
            _plugin.Settings.Proxy.FollowSystemProxy = ((CheckBox)this.FindName("CheckBox_Proxy_FollowSystemProxy")).IsChecked ?? true;
            if (((RadioButton)this.FindName("RadioButton_Proxy_Http")).IsChecked == true)
            {
                _plugin.Settings.Proxy.Protocol = "http";
            }
            else
            {
                _plugin.Settings.Proxy.Protocol = "socks";
            }
            _plugin.Settings.Proxy.Address = ((TextBox)this.FindName("TextBox_Proxy_Address")).Text;
            _plugin.Settings.Proxy.ForAllAPI = ((CheckBox)this.FindName("CheckBox_Proxy_ForAllAPI")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForOllama = ((CheckBox)this.FindName("CheckBox_Proxy_ForOllama")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForOpenAI = ((CheckBox)this.FindName("CheckBox_Proxy_ForOpenAI")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForGemini = ((CheckBox)this.FindName("CheckBox_Proxy_ForGemini")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForFree = ((CheckBox)this.FindName("CheckBox_Proxy_ForFree")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForTTS = ((CheckBox)this.FindName("CheckBox_Proxy_ForTTS")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForASR = ((CheckBox)this.FindName("CheckBox_Proxy_ForASR")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForMcp = ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForPlugin = ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).IsChecked ?? true;

            // Plugin Store Proxy settings
            _plugin.Settings.PluginStore.UseProxy = ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).IsChecked ?? true;
            _plugin.Settings.PluginStore.ProxyUrl = ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).Text;

            // TTS settings
            _plugin.Settings.TTS.IsEnabled = ((CheckBox)this.FindName("CheckBox_TTS_IsEnabled")).IsChecked ?? false;
            _plugin.Settings.TTS.OnlyPlayAIResponse = ((CheckBox)this.FindName("CheckBox_TTS_OnlyPlayAIResponse")).IsChecked ?? true;
            _plugin.Settings.TTS.UseQueueDownload = ((CheckBox)this.FindName("CheckBox_TTS_UseQueueDownload")).IsChecked ?? false;
            _plugin.Settings.TTS.Volume = ((Slider)this.FindName("Slider_TTS_Volume")).Value;
            _plugin.Settings.TTS.VolumeGain = ((Slider)this.FindName("Slider_TTS_VolumeGain")).Value;
            _plugin.Settings.TTS.Speed = ((Slider)this.FindName("Slider_TTS_Speed")).Value;

            // TTS提供商设置
            var selectedProviderItem = ((ComboBox)this.FindName("ComboBox_TTS_Provider")).SelectedItem as ComboBoxItem;
            if (selectedProviderItem != null)
            {
                _plugin.Settings.TTS.Provider = selectedProviderItem.Tag?.ToString() ?? "URL";
            }

            // URL TTS设置
            _plugin.Settings.TTS.URL.BaseUrl = ((TextBox)this.FindName("TextBox_TTS_URL_BaseUrl")).Text;
            _plugin.Settings.TTS.URL.Voice = ((TextBox)this.FindName("TextBox_TTS_URL_Voice")).Text;

            // 保存请求方法设置
            var selectedURLMethodItem = ((ComboBox)this.FindName("ComboBox_TTS_URL_RequestMethod")).SelectedItem as ComboBoxItem;
            if (selectedURLMethodItem != null)
            {
                _plugin.Settings.TTS.URL.Method = selectedURLMethodItem.Tag?.ToString() ?? "GET";
            }

            // OpenAI TTS设置
            _plugin.Settings.TTS.OpenAI.BaseUrl = ((TextBox)this.FindName("TextBox_TTS_OpenAI_BaseUrl")).Text;
            _plugin.Settings.TTS.OpenAI.ApiKey = ((TextBox)this.FindName("TextBox_TTS_OpenAI_ApiKey")).Text;
            _plugin.Settings.TTS.OpenAI.Model = ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Model")).Text;

            var selectedOpenAIVoiceItem = ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Voice")).SelectedItem as ComboBoxItem;
            if (selectedOpenAIVoiceItem != null)
            {
                _plugin.Settings.TTS.OpenAI.Voice = selectedOpenAIVoiceItem.Tag?.ToString() ?? "alloy";
            }

            var selectedFormatItem = ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Format")).SelectedItem as ComboBoxItem;
            if (selectedFormatItem != null)
            {
                _plugin.Settings.TTS.OpenAI.Format = selectedFormatItem.Tag?.ToString() ?? "mp3";
            }

            // DIY TTS 配置通过 JSON 文件管理，无需在此保存

            // 保存 GPT-SoVITS 设置
            SaveGPTSoVITSSettings();

            // 更新TTS服务设置
            _ttsService?.UpdateSettings(_plugin.Settings.TTS, _plugin.Settings.Proxy);
            _plugin.UpdateTTSService();

            // ASR settings
            _plugin.Settings.ASR.IsEnabled = ((CheckBox)this.FindName("CheckBox_ASR_IsEnabled")).IsChecked ?? false;
            // 快捷键已通过捕获功能直接保存到设置中
            _plugin.Settings.ASR.AutoSend = ((CheckBox)this.FindName("CheckBox_ASR_AutoSend")).IsChecked ?? true;
            _plugin.Settings.ASR.ShowTranscriptionWindow = ((CheckBox)this.FindName("CheckBox_ASR_ShowTranscriptionWindow")).IsChecked ?? true;

            // ASR Provider 设置
            var selectedASRProviderItem = ((ComboBox)this.FindName("ComboBox_ASR_Provider")).SelectedItem as ComboBoxItem;
            if (selectedASRProviderItem != null)
            {
                _plugin.Settings.ASR.Provider = selectedASRProviderItem.Tag?.ToString() ?? "OpenAI";
            }

            // ASR 录音设备设置
            var selectedDeviceItem = ((ComboBox)this.FindName("ComboBox_ASR_RecordingDevice")).SelectedItem as ComboBoxItem;
            if (selectedDeviceItem != null && selectedDeviceItem.Tag != null)
            {
                _plugin.Settings.ASR.RecordingDeviceNumber = int.Parse(selectedDeviceItem.Tag.ToString());
            }

            // ASR 语言设置 - 根据 Provider 选择对应的语言设置
            if (_plugin.Settings.ASR.Provider == "Soniox")
            {
                // Soniox 使用独立的语言选择
                var selectedSonioxLanguageItem = ((ComboBox)this.FindName("ComboBox_ASR_Soniox_Language")).SelectedItem as ComboBoxItem;
                if (selectedSonioxLanguageItem != null)
                {
                    _plugin.Settings.ASR.Language = selectedSonioxLanguageItem.Tag?.ToString() ?? "en";
                }
            }

            // OpenAI ASR 设置
            _plugin.Settings.ASR.OpenAI.ApiKey = ((TextBox)this.FindName("TextBox_ASR_OpenAI_ApiKey")).Text;
            _plugin.Settings.ASR.OpenAI.BaseUrl = ((TextBox)this.FindName("TextBox_ASR_OpenAI_BaseUrl")).Text;
            _plugin.Settings.ASR.OpenAI.Model = ((ComboBox)this.FindName("ComboBox_ASR_OpenAI_Model")).Text;

            // Soniox ASR 设置
            _plugin.Settings.ASR.Soniox.ApiKey = ((TextBox)this.FindName("TextBox_ASR_Soniox_ApiKey")).Text;
            _plugin.Settings.ASR.Soniox.BaseUrl = ((TextBox)this.FindName("TextBox_ASR_Soniox_BaseUrl")).Text;
            
            // 获取 Soniox 模型 ID（从 Tag 获取，而不是显示文本）
            var sonioxModelComboBox = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Model");
            if (sonioxModelComboBox.SelectedItem is ComboBoxItem selectedModelItem)
            {
                _plugin.Settings.ASR.Soniox.Model = selectedModelItem.Tag?.ToString() ?? sonioxModelComboBox.Text;
            }
            else
            {
                _plugin.Settings.ASR.Soniox.Model = sonioxModelComboBox.Text;
            }
            
            _plugin.Settings.ASR.Soniox.EnablePunctuation = ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnablePunctuation")).IsChecked ?? true;
            _plugin.Settings.ASR.Soniox.EnableProfanityFilter = ((CheckBox)this.FindName("CheckBox_ASR_Soniox_EnableProfanityFilter")).IsChecked ?? false;

            // Free ASR 无需保存配置

// 更新语音输入快捷键
            _plugin.UpdateVoiceInputHotkey();

            // 只在没有发生实际更改时才保存设置，避免循环保存
            lock (_saveLock)
            {
                if (_hasUnsavedChanges)
                {
                    _plugin.Settings.Save();
                    _hasUnsavedChanges = false;
                    Utils.Logger.Log("设置已保存到文件");
                }
            }

            if (oldProvider != newProvider)
            {
                var oldHistory = _plugin.ChatCore.GetChatHistory();
                IChatCore newChatCore = newProvider switch
                {
                    Setting.LLMType.Ollama => new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.OpenAI => new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Gemini => new GeminiChatCore(GetCurrentGeminiSetting(), _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Free => new FreeChatCore(_plugin.Settings.Free, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    _ => throw new NotImplementedException()
                };
                if (_plugin.Settings.EnableChatHistory && oldHistory != null)
                {
                    newChatCore.SetChatHistory(oldHistory);
                }
                _plugin.UpdateChatCore(newChatCore);
            }
            else
            {
                // 如果提供商没变，也需要重新创建ChatCore以应用新的设置
                var currentHistory = _plugin.ChatCore.GetChatHistory();
                IChatCore updatedChatCore = newProvider switch
                {
                    Setting.LLMType.Ollama => new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.OpenAI => new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Gemini => new GeminiChatCore(GetCurrentGeminiSetting(), _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Free => new FreeChatCore(_plugin.Settings.Free, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    _ => throw new NotImplementedException()
                };
                if (_plugin.Settings.EnableChatHistory && currentHistory != null)
                {
                    updatedChatCore.SetChatHistory(currentHistory);
                }
                _plugin.UpdateChatCore(updatedChatCore);
            }
            _plugin.UpdateActionProcessor();

            // 重置未保存更改标志
            lock (_saveLock)
            {
                _hasUnsavedChanges = false;
            }
        }
        private void ComboBox_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void Button_RefreshOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                var ollamaCore = new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = await Task.Run(() => ollamaCore.RefreshModels());
                ((ComboBox)this.FindName("ComboBox_OllamaModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_OllamaModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_OllamaModel")).SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("RefreshOllamaModels.Fail", _plugin.Settings.Language, "刷新Ollama模型失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 确保在UI线程上停止动画和重置按钮状态（使用异步避免潜在阻塞）
                await Dispatcher.InvokeAsync(() =>
                {
                    StopButtonLoadingAnimation(button);

                    if (button != null)
                    {
                        button.IsEnabled = true;
                        button.UpdateLayout();
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private async void Button_RefreshOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                // 使用界面上选中的节点，而不是当前启用的节点
                var selectedNode = GetSelectedOpenAINode();
                if (selectedNode == null)
                {
                    MessageBox.Show(
                        ErrorMessageHelper.GetLocalizedMessage("RefreshOpenAIModels.NoNodeSelected", _plugin.Settings.Language, "请先选择一个OpenAI节点"),
                        ErrorMessageHelper.GetLocalizedTitle("Warning", _plugin.Settings.Language, "警告"), 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Warning);
                    return;
                }

                var openAICore = new OpenAIChatCore(selectedNode, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = await Task.Run(() => openAICore.RefreshModels());
                ((ComboBox)this.FindName("ComboBox_OpenAIModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_OpenAIModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_OpenAIModel")).SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("RefreshOpenAIModels.Fail", _plugin.Settings.Language, "刷新OpenAI模型失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }



        private async void Button_RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                // 使用界面上选中的节点，而不是当前启用的节点
                var selectedNode = GetSelectedGeminiNode();
                Setting.GeminiSetting geminiSetting;
                
                if (selectedNode != null)
                {
                    // 如果选中了节点，使用选中节点的配置
                    geminiSetting = new Setting.GeminiSetting
                    {
                        ApiKey = selectedNode.ApiKey,
                        Model = selectedNode.Model,
                        Url = selectedNode.Url,
                        Temperature = selectedNode.Temperature,
                        MaxTokens = selectedNode.MaxTokens,
                        EnableAdvanced = selectedNode.EnableAdvanced,
                        EnableStreaming = selectedNode.EnableStreaming,
                        GeminiNodes = _plugin.Settings.Gemini.GeminiNodes
                    };
                }
                else
                {
                    // 如果没有选中节点，使用当前设置
                    geminiSetting = _plugin.Settings.Gemini;
                }

                var geminiCore = new GeminiChatCore(geminiSetting, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = await Task.Run(() => geminiCore.RefreshModels());

                // 记录刷新前两个下拉框的文本，避免刷新后丢失用户输入
                string oldMainText = (this.FindName("ComboBox_GeminiModel") as ComboBox)?.Text ?? string.Empty;
                string oldNodeText = (this.FindName("ComboBox_GeminiNodeModel") as ComboBox)?.Text ?? string.Empty;

                // 刷新旧表单下拉框
                if (this.FindName("ComboBox_GeminiModel") is ComboBox cbMain)
                {
                    cbMain.ItemsSource = models;
                    // 若原文本为空，默认选第一项
                    if (models.Count > 0 && string.IsNullOrEmpty(oldMainText))
                        cbMain.SelectedIndex = 0;
                    else
                        cbMain.Text = oldMainText;
                }

                // 刷新节点详情下拉框
                if (this.FindName("ComboBox_GeminiNodeModel") is ComboBox cbNode)
                {
                    cbNode.ItemsSource = models;
                    if (models.Count > 0 && string.IsNullOrEmpty(oldNodeText))
                        cbNode.SelectedIndex = 0;
                    else
                        cbNode.Text = oldNodeText;
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("RefreshGeminiModels.Fail", _plugin.Settings.Language, "刷新Gemini模型失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private void Button_ClearContext_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ChatCore?.ClearContext();
            ((TextBlock)this.FindName("TextBlock_CurrentContextLength")).Text = _plugin.ChatCore.GetChatHistory().Count.ToString();
        }
        private void Button_EditContext_Click(object sender, RoutedEventArgs e)
        {
            var contextEditor = new winContextEditor(_plugin);
            contextEditor.Show();
        }
        private async void Button_CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var logBox = (ListBox)this.FindName("LogBox");
            var items = logBox.Items.Cast<object>().ToList();

            var textToCopy = await Task.Run(() =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in items)
                {
                    sb.AppendLine(item.ToString());
                }
                return sb.ToString();
            });

            try
            {
                Clipboard.SetText(textToCopy);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // 即使SetText成功，也可能抛出异常，我们直接忽略它
            }
        }
        private void Button_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            Logger.Clear();
        }
        private async void Button_AddTool_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                await Task.Run(() =>
                {
                    var newTool = new Setting.ToolSetting();
                    _plugin.Settings.Tools.Add(newTool);
                });

                var dataGrid = (DataGrid)this.FindName("DataGrid_Tools");
                Dispatcher.Invoke(() =>
                {
                    dataGrid.ItemsSource = null;
                    dataGrid.ItemsSource = _plugin.Settings.Tools;
                });
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private async void Button_DeleteTool_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                var dataGrid = (DataGrid)this.FindName("DataGrid_Tools");
                var selectedTool = dataGrid.SelectedItem as Setting.ToolSetting;

                if (selectedTool != null)
                {
                    await Task.Run(() =>
                    {
                        _plugin.Settings.Tools.Remove(selectedTool);
                    });

                    Dispatcher.Invoke(() =>
                    {
                        dataGrid.ItemsSource = null;
                        dataGrid.ItemsSource = _plugin.Settings.Tools;
                    });
                }
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private void DataGrid_Plugins_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 插件选择变化时的处理逻辑
            // 这里可以添加需要的逻辑，目前保持空实现以避免编译错误
        }

        private async void Button_ImportPlugin_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "插件文件 (*.dll)|*.dll"
            };
            if (dialog.ShowDialog() == true)
            {
                StartButtonLoadingAnimation(button);
                try
                {
                    await Task.Run(() => _plugin.ImportPlugin(dialog.FileName));
                    Button_RefreshPlugins_Click(this, new RoutedEventArgs());
                }
                finally
                {
                    StopButtonLoadingAnimation(button);
                }
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            e.Handled = true;
        }

        public void RefreshPluginList()
        {
            Button_RefreshPlugins_Click(this, new RoutedEventArgs());
        }

        private void PluginEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is UnifiedPluginItem item && item.LocalPlugin != null)
            {
                var langCode = _plugin.Settings.Language;

                try
                {
                    bool isEnabled = checkBox.IsChecked ?? false;
                    item.IsEnabled = isEnabled;
                    item.LocalPlugin.Enabled = isEnabled;

                    if (isEnabled)
                    {
                        try
                        {
                            item.LocalPlugin.Initialize(_plugin);
                            _plugin.ChatCore?.AddPlugin(item.LocalPlugin);
                            Logger.Log($"Plugin '{item.LocalPlugin.Name}' has been enabled and initialized.");
                        }
                        catch (Exception ex)
                        {
                            // 如果初始化失败，回滚状态
                            item.IsEnabled = false;
                            item.LocalPlugin.Enabled = false;
                            checkBox.IsChecked = false;

                            var errorMsg = $"{LanguageHelper.Get("Plugin.InitializeFailed", langCode) ?? "插件初始化失败"}: {ex.Message}";
                            MessageBox.Show(errorMsg, LanguageHelper.Get("Error", langCode) ?? "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                            Logger.Log($"Plugin '{item.LocalPlugin.Name}' initialization failed: {ex.Message}");
                            return;
                        }
                    }
                    else
                    {
                        try
                        {
                            item.LocalPlugin.Unload();
                            _plugin.ChatCore?.RemovePlugin(item.LocalPlugin);
                            Logger.Log($"Plugin '{item.LocalPlugin.Name}' has been disabled and unloaded.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Warning: Plugin '{item.LocalPlugin.Name}' unload failed: {ex.Message}");
                            // 卸载失败不回滚状态，因为插件可能已经部分卸载
                        }
                    }

                    // 更新状态文本
                    item.StatusText = isEnabled ?
                        LanguageHelper.Get("Plugin.StatusRunning", langCode) ?? "运行中" :
                        LanguageHelper.Get("Plugin.StatusDisabled", langCode) ?? "已禁用";

                    // 更新描述（因为启用/禁用状态可能影响多语言描述的获取）
                    try
                    {
                        item.Description = GetPluginDescription(item.LocalPlugin, langCode);
                        item.OriginalDescription = GetPluginDescription(item.LocalPlugin, langCode);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to update plugin description for '{item.LocalPlugin.Name}': {ex.Message}");
                    }

                    // 刷新DataGrid显示
                    try
                    {
                        if (FindName("DataGrid_Plugins") is DataGrid dataGrid)
                        {
                            dataGrid.Items.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to refresh plugin grid: {ex.Message}");
                    }

                    // 保存插件状态
                    try
                    {
                        _plugin.SavePluginStates();
                        Logger.Log("Plugin states have been saved.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to save plugin states: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = $"{LanguageHelper.Get("Plugin.OperationFailed", langCode) ?? "插件操作失败"}: {ex.Message}";
                    MessageBox.Show(errorMsg, LanguageHelper.Get("Error", langCode) ?? "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    Logger.Log($"Plugin operation failed for '{item.LocalPlugin?.Name}': {ex}");

                    // 尝试重置复选框状态
                    try
                    {
                        checkBox.IsChecked = item.LocalPlugin?.Enabled ?? false;
                    }
                    catch
                    {
                        // 忽略重置失败
                    }
                }
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            // 停止并清理定时器
            _autoSaveTimer?.Stop();
            _autoSaveTimer = null;

            // 释放TTS资源
            _ttsService?.Dispose();
            _ttsService = null;

            _plugin.SettingWindow = null;
        }

        #region 自动保存逻辑

        /// <summary>
        /// 初始化自动保存定时器
        /// </summary>
        private void InitializeAutoSaveTimer()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // 500ms延迟
            };
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        }

        // 密钥输入去抖保存（避免每次按键均触发保存）
        private void InitializeSecretSaveTimer()
        {
            _secretSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _secretSaveTimer.Tick += (s, e) =>
            {
                _secretSaveTimer?.Stop();
                if (_isReadyToSave)
                {
                    SaveSettings();
                }
            };
        }

        private void ScheduleSecretSave()
        {
            if (!_isReadyToSave) return;
            // 标记有未保存变更，但不立即保存，延迟到用户停止输入片刻
            _hasUnsavedChanges = true;
            _secretSaveTimer?.Stop();
            _secretSaveTimer?.Start();
        }

        // Gemini 列表刷新去抖，合并短时间内多次刷新请求，降低重绘频率
        private void InitializeGeminiRefreshTimer()
        {
            _geminiRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _geminiRefreshTimer.Tick += (s, e) =>
            {
                _geminiRefreshTimer?.Stop();
                RefreshGeminiNodesList();
            };
        }

        private void ScheduleGeminiListRefresh()
        {
            _geminiRefreshTimer?.Stop();
            _geminiRefreshTimer?.Start();
        }

        /// <summary>
        /// 调度自动保存（异步合并，避免UI线程阻塞/重入）
        /// </summary>
        /// <param name="immediate">保留参数，维持兼容</param>
        private void ScheduleAutoSave(bool immediate = false)
        {
            // 初始化阶段不保存，避免未构造完成的控件/服务导致 NRE
            if (!_isReadyToSave) return;

            lock (_saveLock)
            {
                _hasUnsavedChanges = true;
                if (_isSaveScheduled) return;
                _isSaveScheduled = true;
            }

            // 异步合并到一次保存，使用后台优先级，不阻塞事件回调/布局周期
            Dispatcher.BeginInvoke(new Action(() =>
            {
                bool doSave = false;
                lock (_saveLock)
                {
                    if (_hasUnsavedChanges)
                    {
                        _hasUnsavedChanges = false;
                        doSave = true;
                    }
                    _isSaveScheduled = false;
                }
                if (doSave)
                {
                    SaveSettings();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 自动保存定时器事件
        /// </summary>
        private void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            _autoSaveTimer?.Stop();

            if (_hasUnsavedChanges)
            {
                SaveSettings();
            }
        }

        #endregion

        // 旋转动画辅助方法
        private void StartButtonLoadingAnimation(Button button)
        {
            if (button == null) return;

            // 保存原始内容
            button.Tag = button.Content;

            // 创建旋转图标
            var rotateIcon = new TextBlock
            {
                Text = "⟳",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            // 创建旋转变换
            var rotateTransform = new RotateTransform();
            rotateIcon.RenderTransform = rotateTransform;

            // 创建旋转动画
            var rotateAnimation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // 设置按钮内容为旋转图标
            button.Content = rotateIcon;
            button.IsEnabled = false;

            // 开始动画
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
        }

        private void StopButtonLoadingAnimation(Button button)
        {
            if (button == null) return;

            // 停止动画
            if (button.Content is TextBlock rotateIcon && rotateIcon.RenderTransform is RotateTransform transform)
            {
                transform.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            // 恢复原始内容
            if (button.Tag != null)
            {
                button.Content = button.Tag;
                button.Tag = null;
            }

            // 确保按钮重新启用
            button.IsEnabled = true;

            // 强制更新按钮状态
            button.UpdateLayout();
        }

        private void UpdateUIForLanguage()
        {
            var langCode = _plugin.Settings.Language;

            if (FindName("Tab_LLM") is TabItem tabLLM) tabLLM.Header = LanguageHelper.Get("LLM_Settings.Tab", langCode);
            if (FindName("Tab_Language") is TabItem tabLanguage) tabLanguage.Header = LanguageHelper.Get("Language.Tab", langCode);
            if (FindName("Tab_Advanced") is TabItem tabAdvanced) tabAdvanced.Header = LanguageHelper.Get("Advanced_Options.Tab", langCode);
            if (FindName("Tab_Tools") is TabItem tabTools) tabTools.Header = LanguageHelper.Get("Tools.Tab", langCode);
            if (FindName("Tab_Log") is TabItem tabLog) tabLog.Header = LanguageHelper.Get("Log.Tab", langCode);
            if (FindName("Tab_Plugin") is TabItem tabPlugin) tabPlugin.Header = LanguageHelper.Get("Plugin.Tab", langCode);
            if (FindName("Tab_Proxy") is TabItem tabProxy) tabProxy.Header = LanguageHelper.Get("Proxy.Tab", langCode);
            if (FindName("Tab_TTS") is TabItem tabTTS) tabTTS.Header = LanguageHelper.Get("TTS.Tab", langCode);

            if (FindName("Label_Language") is Label labelLanguage) labelLanguage.Content = LanguageHelper.Get("Language.Select", langCode);
            if (FindName("Label_PromptLanguage") is Label labelPromptLanguage) labelPromptLanguage.Content = LanguageHelper.Get("Language.PromptLanguage", langCode);
            if (FindName("TextBlock_PromptLanguageTooltip") is TextBlock textBlockPromptLanguageTooltip) textBlockPromptLanguageTooltip.Text = LanguageHelper.Get("Language.PromptLanguageTooltip", langCode);
            if (FindName("Label_Provider") is Label labelProvider) labelProvider.Content = LanguageHelper.Get("LLM_Settings.Provider", langCode);
            if (FindName("Label_AiName") is Label labelAiName) labelAiName.Content = LanguageHelper.Get("LLM_Settings.AiName", langCode);
            if (FindName("Label_UserName") is Label labelUserName) labelUserName.Content = LanguageHelper.Get("LLM_Settings.UserName", langCode);
            if (FindName("CheckBox_FollowVPetName") is CheckBox checkBoxFollowVPetName) checkBoxFollowVPetName.Content = LanguageHelper.Get("LLM_Settings.FollowVPetName", langCode);
            if (FindName("Label_Role") is Label labelRole) labelRole.Content = LanguageHelper.Get("LLM_Settings.Role", langCode);
            if (FindName("Label_ContextControl") is Label labelContextControl) labelContextControl.Content = LanguageHelper.Get("LLM_Settings.ContextControl", langCode);
            if (FindName("CheckBox_KeepContext") is CheckBox checkBoxKeepContext) checkBoxKeepContext.Content = LanguageHelper.Get("LLM_Settings.KeepContext", langCode);
            if (FindName("CheckBox_EnableChatHistory") is CheckBox checkBoxEnableChatHistory) checkBoxEnableChatHistory.Content = LanguageHelper.Get("LLM_Settings.EnableChatHistory", langCode);
            if (FindName("CheckBox_SeparateChatByProvider") is CheckBox checkBoxSeparateChatByProvider) checkBoxSeparateChatByProvider.Content = LanguageHelper.Get("LLM_Settings.SeparateChatByProvider", langCode);
            if (FindName("Button_ClearContext") is Button buttonClearContext) buttonClearContext.Content = LanguageHelper.Get("LLM_Settings.ClearContext", langCode);
            if (FindName("Button_EditContext") is Button buttonEditContext) buttonEditContext.Content = LanguageHelper.Get("LLM_Settings.EditContext", langCode);

            if (FindName("CheckBox_EnableAction") is CheckBox checkBoxEnableAction)
            {
                checkBoxEnableAction.Content = LanguageHelper.Get("Advanced_Options.EnableAction", langCode);
                if (checkBoxEnableAction.ToolTip is ToolTip toolTip)
                {
                    toolTip.Content = LanguageHelper.Get("Advanced_Options.EnableActionToolTip", langCode);
                }
            }
            if (FindName("CheckBox_EnableBuy") is CheckBox checkBoxEnableBuy) checkBoxEnableBuy.Content = LanguageHelper.Get("Advanced_Options.EnableBuy", langCode);
            if (FindName("CheckBox_EnableState") is CheckBox checkBoxEnableState) checkBoxEnableState.Content = LanguageHelper.Get("Advanced_Options.EnableState", langCode);
            if (FindName("CheckBox_EnableActionExecution") is CheckBox checkBoxEnableActionExecution) checkBoxEnableActionExecution.Content = LanguageHelper.Get("Advanced_Options.EnableActionExecution", langCode);
            if (FindName("CheckBox_EnableMove") is CheckBox checkBoxEnableMove) checkBoxEnableMove.Content = LanguageHelper.Get("Advanced_Options.EnableMove", langCode);
            if (FindName("CheckBox_EnableTime") is CheckBox checkBoxEnableTime) checkBoxEnableTime.Content = LanguageHelper.Get("Advanced_Options.EnableTime", langCode);
            if (FindName("CheckBox_EnableBuyFeedback") is CheckBox checkBoxEnableBuyFeedback) 
            {
                checkBoxEnableBuyFeedback.Content = LanguageHelper.Get("BuyInteraction.Enable", langCode);
                if (checkBoxEnableBuyFeedback.ToolTip is ToolTip toolTip && toolTip.Content is TextBlock textBlock)
                {
                    textBlock.Text = LanguageHelper.Get("BuyInteraction.EnableTooltip", langCode);
                }
            }
            if (FindName("CheckBox_EnableLiveMode") is CheckBox checkBoxEnableLiveMode)
            {
                checkBoxEnableLiveMode.Content = LanguageHelper.Get("Advanced_Options.EnableLiveMode", langCode);
                if (checkBoxEnableLiveMode.ToolTip is ToolTip toolTip && toolTip.Content is TextBlock textBlock)
                {
                    textBlock.Text = LanguageHelper.Get("Advanced_Options.EnableLiveModeToolTip", langCode);
                }
            }
            if (FindName("CheckBox_EnableHistoryCompression") is CheckBox checkBoxEnableHistoryCompression) checkBoxEnableHistoryCompression.Content = LanguageHelper.Get("Advanced_Options.EnableHistoryCompression", langCode);
            
            // 压缩模式
            if (FindName("TextBlock_CompressionMode") is TextBlock textBlockCompressionMode) textBlockCompressionMode.Text = LanguageHelper.Get("Advanced_Options.CompressionMode", langCode);
            if (FindName("ComboBox_CompressionMode") is ComboBox comboBoxCompressionMode)
            {
                foreach (ComboBoxItem item in comboBoxCompressionMode.Items)
                {
                    var tag = item.Tag.ToString();
                    item.Content = LanguageHelper.Get($"Advanced_Options.CompressionMode_{tag}", langCode);
                }
            }
            
            // 消息数量阈值
            if (FindName("TextBlock_HistoryCompressionThreshold") is TextBlock textBlockHistoryCompressionThreshold) textBlockHistoryCompressionThreshold.Text = LanguageHelper.Get("Advanced_Options.HistoryCompressionThreshold", langCode);
            if (FindName("TextBlock_CurrentContextLengthLabel") is TextBlock textBlockCurrentContextLengthLabel) textBlockCurrentContextLengthLabel.Text = LanguageHelper.Get("Advanced_Options.CurrentContextLength", langCode);
            if (FindName("TextBlock_CurrentContextLength") is TextBlock textBlockCurrentContextLength) textBlockCurrentContextLength.Text = _plugin.ChatCore.GetChatHistory().Count.ToString();
            
            // Token数量阈值
            if (FindName("TextBlock_HistoryCompressionTokenThreshold") is TextBlock textBlockHistoryCompressionTokenThreshold) textBlockHistoryCompressionTokenThreshold.Text = LanguageHelper.Get("Advanced_Options.HistoryCompressionTokenThreshold", langCode);
            if (FindName("TextBlock_CurrentTokenCountLabel") is TextBlock textBlockCurrentTokenCountLabel) textBlockCurrentTokenCountLabel.Text = LanguageHelper.Get("Advanced_Options.CurrentTokenCount", langCode);
            if (FindName("TextBlock_CurrentTokenCount") is TextBlock textBlockCurrentTokenCount) textBlockCurrentTokenCount.Text = _plugin.ChatCore.GetCurrentTokenCount().ToString();

            // 更新工具管理头部区域的多语言文本
            if (FindName("TextBlock_ToolsManagement") is TextBlock textBlockToolsManagement)
                textBlockToolsManagement.Text = LanguageHelper.Get("Tools.Management", langCode);

            if (FindName("TextBlock_ToolsManagementDescription") is TextBlock textBlockToolsManagementDescription)
                textBlockToolsManagementDescription.Text = LanguageHelper.Get("Tools.ManagementDescription", langCode);

            if (FindName("TextBlock_MCPToolsConfig") is TextBlock textBlockMCPToolsConfig)
                textBlockMCPToolsConfig.Text = LanguageHelper.Get("Tools.MCPConfig", langCode);

            if (FindName("TextBlock_ToolsDescription") is TextBlock textBlockToolsDescription) textBlockToolsDescription.Text = LanguageHelper.Get("Tools.ToolsDescription", langCode);
            if (FindName("Button_AddTool") is Button buttonAddTool) buttonAddTool.Content = LanguageHelper.Get("Tools.Add", langCode);
            if (FindName("Button_DeleteTool") is Button buttonDeleteTool) buttonDeleteTool.Content = LanguageHelper.Get("Tools.Delete", langCode);
            if (FindName("DataGrid_Tools") is DataGrid dataGridTools)
            {
                if (dataGridTools.Columns.Count >= 5)
                {
                    dataGridTools.Columns[0].Header = LanguageHelper.Get("Tools.Enabled", langCode) ?? "启用";
                    dataGridTools.Columns[1].Header = LanguageHelper.Get("Tools.Name", langCode) ?? "名称";
                    dataGridTools.Columns[2].Header = LanguageHelper.Get("Tools.URL", langCode) ?? "URL";
                    dataGridTools.Columns[3].Header = LanguageHelper.Get("Tools.Description", langCode) ?? "描述";
                    dataGridTools.Columns[4].Header = LanguageHelper.Get("Tools.ApiKey", langCode) ?? "API Key";
                }
            }

            if (FindName("CheckBox_LogAutoScroll") is CheckBox checkBoxLogAutoScroll) checkBoxLogAutoScroll.Content = LanguageHelper.Get("Log.AutoScroll", langCode);
            if (FindName("TextBlock_MaxLogCount") is TextBlock textBlockMaxLogCount) textBlockMaxLogCount.Text = LanguageHelper.Get("Log.MaxLogCount", langCode);
            if (FindName("Button_CopyLog") is Button buttonCopyLog) buttonCopyLog.Content = LanguageHelper.Get("Log.Copy", langCode);
            if (FindName("Button_ClearLog") is Button buttonClearLog) buttonClearLog.Content = LanguageHelper.Get("Log.Clear", langCode);

            // 更新插件管理头部区域的多语言文本
            if (FindName("TextBlock_PluginManagementCenter") is TextBlock textBlockPluginManagementCenter)
                textBlockPluginManagementCenter.Text = LanguageHelper.Get("Plugin.ManagementCenter", langCode);

            // 动态构建插件描述文本（包含超链接）
            if (FindName("TextBlock_HowToUse") is TextBlock howToUseTextBlock)
            {
                howToUseTextBlock.Inlines.Clear();
                howToUseTextBlock.Inlines.Add(new Run(LanguageHelper.Get("Plugin.ManagementDescription", langCode)));
                howToUseTextBlock.Inlines.Add(new LineBreak());
                howToUseTextBlock.Inlines.Add(new Run(LanguageHelper.Get("Plugin.NeedHelp", langCode)));
                var hyperlink = new Hyperlink(new Run(LanguageHelper.Get("Plugin.PluginDocs", langCode)));
                hyperlink.NavigateUri = new Uri("https://github.com/ycxom/VPetLLM_Plugin");
                hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                hyperlink.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#667EEA"));
                hyperlink.TextDecorations = null;
                howToUseTextBlock.Inlines.Add(hyperlink);
                howToUseTextBlock.Inlines.Add(new Run(LanguageHelper.Get("Plugin.LearnMore", langCode)));
            }

            if (FindName("TextBlock_RefreshButton") is TextBlock textBlockRefreshButton)
                textBlockRefreshButton.Text = LanguageHelper.Get("Plugin.Refresh", langCode);

            if (FindName("TextBlock_ImportButton") is TextBlock textBlockImportButton)
                textBlockImportButton.Text = LanguageHelper.Get("Plugin.ImportPlugin", langCode);

            if (FindName("Button_RefreshPlugins") is Button buttonRefreshPlugins) buttonRefreshPlugins.Content = LanguageHelper.Get("Plugin.Refresh", langCode);
            if (FindName("Button_ImportPlugin") is Button buttonImportPlugin) buttonImportPlugin.Content = LanguageHelper.Get("Plugin.ImportPlugin", langCode);

            // TTS 多语言支持
            if (FindName("CheckBox_TTS_IsEnabled") is CheckBox checkBoxTTSIsEnabled) checkBoxTTSIsEnabled.Content = LanguageHelper.Get("TTS.Enable", langCode);
            if (FindName("CheckBox_TTS_OnlyPlayAIResponse") is CheckBox checkBoxTTSOnlyPlayAIResponse) checkBoxTTSOnlyPlayAIResponse.Content = LanguageHelper.Get("TTS.OnlyPlayAIResponse", langCode);
            if (FindName("Label_TTS_Voice") is Label labelTTSVoice) labelTTSVoice.Content = LanguageHelper.Get("TTS.Voice", langCode);
            if (FindName("Label_TTS_Speed") is Label labelTTSSpeed) labelTTSSpeed.Content = LanguageHelper.Get("TTS.Speed", langCode);
            if (FindName("Label_TTS_Volume") is Label labelTTSVolume) labelTTSVolume.Content = LanguageHelper.Get("TTS.Volume", langCode);
            if (FindName("TextBlock_TTS_VolumeGain") is TextBlock textBlockTTSVolumeGain) textBlockTTSVolumeGain.Text = (LanguageHelper.Get("TTS.VolumeGain", langCode) ?? "音量增益") + ":";
            if (FindName("Button_TTS_Test") is Button buttonTTSTest) buttonTTSTest.Content = LanguageHelper.Get("TTS.TestTTS", langCode) ?? "测试TTS";
            if (FindName("TextBlock_TTS_Description") is TextBlock textBlockTTSDescription) textBlockTTSDescription.Text = LanguageHelper.Get("TTS.Description", langCode);

            // DIY TTS 多语言支持
            if (FindName("TextBlock_TTS_DIY_Config") is TextBlock textBlockTTSDIYConfig) textBlockTTSDIYConfig.Text = LanguageHelper.Get("TTS.DIYTTSConfig", langCode) ?? "DIY TTS 配置";
            if (FindName("TextBlock_TTS_DIY_Description") is TextBlock textBlockTTSDIYDescription) textBlockTTSDIYDescription.Text = LanguageHelper.Get("TTS.DIYTTSDescription", langCode) ?? "DIY TTS 使用 JSON 配置文件进行配置，支持强大的自定义功能。";
            if (FindName("Button_TTS_DIY_OpenConfig") is Button buttonTTSDIYOpenConfig) buttonTTSDIYOpenConfig.Content = LanguageHelper.Get("TTS.OpenConfigFolder", langCode) ?? "打开配置文件夹";
            if (FindName("TextBlock_TTS_DIY_ConfigLocation") is TextBlock textBlockTTSDIYConfigLocation) textBlockTTSDIYConfigLocation.Text = LanguageHelper.Get("TTS.ConfigFileLocation", langCode) ?? "配置文件位置: 文档\\VPetLLM\\DiyTTS\\Config.json";
            if (FindName("TextBlock_TTS_DIY_Features") is TextBlock textBlockTTSDIYFeatures) textBlockTTSDIYFeatures.Text = LanguageHelper.Get("TTS.DIYTTSFeatures", langCode) ?? "支持自定义请求头、请求体、请求方法等参数。User-Agent 将自动限制为 VPetLLM。";
            // 更新插件 DataGrid 的列标题
            if (FindName("DataGrid_Plugins") is DataGrid dataGridPlugins)
            {
                if (dataGridPlugins.Columns.Count > 0)
                    dataGridPlugins.Columns[0].Header = LanguageHelper.Get("Plugin.Enabled", langCode) ?? "启用";
                if (dataGridPlugins.Columns.Count > 1)
                    dataGridPlugins.Columns[1].Header = LanguageHelper.Get("Plugin.Status", langCode) ?? "状态";
                if (dataGridPlugins.Columns.Count > 2)
                    dataGridPlugins.Columns[2].Header = LanguageHelper.Get("Plugin.Name", langCode) ?? "插件信息";
                if (dataGridPlugins.Columns.Count > 3)
                    dataGridPlugins.Columns[3].Header = LanguageHelper.Get("Plugin.Description", langCode) ?? "描述";
                if (dataGridPlugins.Columns.Count > 4)
                    dataGridPlugins.Columns[4].Header = LanguageHelper.Get("Plugin.Action", langCode) ?? "操作";
                dataGridPlugins.Items.Refresh();
            }

            // OpenAI 多节点 ListView 列头（GridViewColumn.Header 非依赖属性，需手动刷新）
            if (FindName("ListView_OpenAINodes") is ListView lvOpenAI && lvOpenAI.View is GridView gvOpenAI)
            {
                if (gvOpenAI.Columns.Count > 0) gvOpenAI.Columns[0].Header = LanguageHelper.Get("OpenAI.Enabled", langCode) ?? "启用";
                if (gvOpenAI.Columns.Count > 1) gvOpenAI.Columns[1].Header = LanguageHelper.Get("OpenAI.ChannelName", langCode) ?? "渠道名称";
                if (gvOpenAI.Columns.Count > 2) gvOpenAI.Columns[2].Header = LanguageHelper.Get("OpenAI.ApiKey", langCode) ?? "API 密钥";
                if (gvOpenAI.Columns.Count > 3) gvOpenAI.Columns[3].Header = LanguageHelper.Get("OpenAI.Model", langCode) ?? "模型";
                if (gvOpenAI.Columns.Count > 4) gvOpenAI.Columns[4].Header = LanguageHelper.Get("OpenAI.ApiAddress", langCode) ?? "API 地址";
            }

            // Gemini 多节点 ListView 列头
            if (FindName("ListView_GeminiNodes") is ListView lvGem && lvGem.View is GridView gvGem)
            {
                if (gvGem.Columns.Count > 0) gvGem.Columns[0].Header = LanguageHelper.Get("Gemini.Enabled", langCode) ?? "启用";
                if (gvGem.Columns.Count > 1) gvGem.Columns[1].Header = LanguageHelper.Get("Gemini.ChannelName", langCode) ?? "渠道名称";
                if (gvGem.Columns.Count > 2) gvGem.Columns[2].Header = LanguageHelper.Get("Gemini.ApiKey", langCode) ?? "API 密钥";
                if (gvGem.Columns.Count > 3) gvGem.Columns[3].Header = LanguageHelper.Get("Gemini.Model", langCode) ?? "模型";
                if (gvGem.Columns.Count > 4) gvGem.Columns[4].Header = LanguageHelper.Get("Gemini.ApiAddress", langCode) ?? "API 地址";
            }



            if (FindName("Tab_Ollama") is TabItem tabOllama) tabOllama.Header = LanguageHelper.Get("Ollama.Header", langCode);
            if (FindName("Label_OllamaUrl") is Label labelOllamaUrl) labelOllamaUrl.Content = LanguageHelper.Get("Ollama.URL", langCode);
            if (FindName("Label_OllamaModel") is Label labelOllamaModel) labelOllamaModel.Content = LanguageHelper.Get("Ollama.Model", langCode);
            if (FindName("Button_RefreshOllamaModels") is Button buttonRefreshOllamaModels) buttonRefreshOllamaModels.Content = LanguageHelper.Get("Ollama.Refresh", langCode);
            if (FindName("CheckBox_Ollama_EnableAdvanced") is CheckBox checkBoxOllamaEnableAdvanced) checkBoxOllamaEnableAdvanced.Content = LanguageHelper.Get("Ollama.EnableAdvanced", langCode);
            if (FindName("TextBlock_Ollama_Temperature") is TextBlock textBlockOllamaTemperature) textBlockOllamaTemperature.Text = LanguageHelper.Get("Ollama.Temperature", langCode);
            if (FindName("TextBlock_Ollama_MaxTokens") is TextBlock textBlockOllamaMaxTokens) textBlockOllamaMaxTokens.Text = LanguageHelper.Get("Ollama.MaxTokens", langCode);

            if (FindName("Tab_OpenAI") is TabItem tabOpenAI) tabOpenAI.Header = LanguageHelper.Get("OpenAI.Header", langCode);
            if (FindName("Label_OpenAIApiKey") is Label labelOpenAIApiKey) labelOpenAIApiKey.Content = LanguageHelper.Get("OpenAI.ApiKey", langCode);
            if (FindName("Label_OpenAIModel") is Label labelOpenAIModel) labelOpenAIModel.Content = LanguageHelper.Get("OpenAI.Model", langCode);
            if (FindName("Button_RefreshOpenAIModels") is Button buttonRefreshOpenAIModels) buttonRefreshOpenAIModels.Content = LanguageHelper.Get("OpenAI.Refresh", langCode);
            if (FindName("Label_OpenAIUrl") is Label labelOpenAIUrl) labelOpenAIUrl.Content = LanguageHelper.Get("OpenAI.ApiAddress", langCode);
            if (FindName("CheckBox_OpenAI_EnableAdvanced") is CheckBox checkBoxOpenAIEnableAdvanced) checkBoxOpenAIEnableAdvanced.Content = LanguageHelper.Get("OpenAI.EnableAdvanced", langCode);
            if (FindName("TextBlock_OpenAI_Temperature") is TextBlock textBlockOpenAITemperature) textBlockOpenAITemperature.Text = LanguageHelper.Get("OpenAI.Temperature", langCode);
            if (FindName("TextBlock_OpenAI_MaxTokens") is TextBlock textBlockOpenAIMaxTokens) textBlockOpenAIMaxTokens.Text = LanguageHelper.Get("OpenAI.MaxTokens", langCode);

            if (FindName("Tab_Gemini") is TabItem tabGemini) tabGemini.Header = LanguageHelper.Get("Gemini.Header", langCode);
            if (FindName("Tab_Free") is TabItem tabFree) tabFree.Header = LanguageHelper.Get("Free.Header", langCode);
            if (FindName("Label_GeminiApiKey") is Label labelGeminiApiKey) labelGeminiApiKey.Content = LanguageHelper.Get("Gemini.ApiKey", langCode);

            if (FindName("Label_GeminiModel") is Label labelGeminiModel) labelGeminiModel.Content = LanguageHelper.Get("Gemini.Model", langCode);

            if (FindName("Button_RefreshGeminiModels") is Button buttonRefreshGeminiModels) buttonRefreshGeminiModels.Content = LanguageHelper.Get("Gemini.Refresh", langCode);
            if (FindName("Button_RefreshFreeModels") is Button buttonRefreshFreeModels) buttonRefreshFreeModels.Content = LanguageHelper.Get("Free.Refresh", langCode);
            if (FindName("Label_GeminiUrl") is Label labelGeminiUrl) labelGeminiUrl.Content = LanguageHelper.Get("Gemini.ApiAddress", langCode);

            if (FindName("TextBlock_GeminiApiEndpointNote") is TextBlock textBlockGeminiApiEndpointNote) textBlockGeminiApiEndpointNote.Text = LanguageHelper.Get("Gemini.ApiEndpointNote", langCode);
            if (FindName("TextBlock_FreeApiEndpointNote") is TextBlock textBlockFreeApiEndpointNote) textBlockFreeApiEndpointNote.Text = LanguageHelper.Get("Free.ApiEndpointNote", langCode);
            if (FindName("TextBlock_ApiServiceNotice") is TextBlock textBlockApiServiceNotice) textBlockApiServiceNotice.Text = LanguageHelper.Get("Free.ApiServiceNotice", langCode);
            if (FindName("CheckBox_Gemini_EnableAdvanced") is CheckBox checkBoxGeminiEnableAdvanced) checkBoxGeminiEnableAdvanced.Content = LanguageHelper.Get("Gemini.EnableAdvanced", langCode);
            if (FindName("CheckBox_Free_EnableAdvanced") is CheckBox checkBoxFreeEnableAdvanced) checkBoxFreeEnableAdvanced.Content = LanguageHelper.Get("Free.EnableAdvanced", langCode);
            if (FindName("TextBlock_Gemini_Temperature") is TextBlock textBlockGeminiTemperature) textBlockGeminiTemperature.Text = LanguageHelper.Get("Gemini.Temperature", langCode);
            if (FindName("TextBlock_Free_Temperature") is TextBlock textBlockFreeTemperature) textBlockFreeTemperature.Text = LanguageHelper.Get("Free.Temperature", langCode);
            if (FindName("TextBlock_Gemini_MaxTokens") is TextBlock textBlockGeminiMaxTokens) textBlockGeminiMaxTokens.Text = LanguageHelper.Get("Gemini.MaxTokens", langCode);
            if (FindName("TextBlock_Free_MaxTokens") is TextBlock textBlockFreeMaxTokens) textBlockFreeMaxTokens.Text = LanguageHelper.Get("Free.MaxTokens", langCode);

            // Proxy UI
            if (FindName("CheckBox_Proxy_IsEnabled") is CheckBox checkBoxProxyIsEnabled) checkBoxProxyIsEnabled.Content = LanguageHelper.Get("Proxy.EnableProxy", langCode);
            if (FindName("CheckBox_Proxy_FollowSystemProxy") is CheckBox checkBoxProxyFollowSystemProxy) checkBoxProxyFollowSystemProxy.Content = LanguageHelper.Get("Proxy.FollowSystemProxy", langCode);
            if (FindName("Label_Proxy_Protocol") is Label labelProxyProtocol) labelProxyProtocol.Content = LanguageHelper.Get("Proxy.Protocol", langCode);
            if (FindName("RadioButton_Proxy_Http") is RadioButton radioButtonProxyHttp) radioButtonProxyHttp.Content = LanguageHelper.Get("Proxy.Http", langCode);
            if (FindName("RadioButton_Proxy_Socks") is RadioButton radioButtonProxySocks) radioButtonProxySocks.Content = LanguageHelper.Get("Proxy.Socks5", langCode);
            if (FindName("Label_Proxy_Address") is Label labelProxyAddress) labelProxyAddress.Content = LanguageHelper.Get("Proxy.ProxyAddress", langCode);
            if (FindName("Label_Proxy_Scope") is Label labelProxyScope) labelProxyScope.Content = LanguageHelper.Get("Proxy.ProxyScope", langCode);
            if (FindName("CheckBox_Proxy_ForAllAPI") is CheckBox checkBoxProxyForAllAPI) checkBoxProxyForAllAPI.Content = LanguageHelper.Get("Proxy.ForAllAPI", langCode);
            if (FindName("CheckBox_Proxy_ForOllama") is CheckBox checkBoxProxyForOllama) checkBoxProxyForOllama.Content = LanguageHelper.Get("Proxy.ForOllama", langCode);
            if (FindName("CheckBox_Proxy_ForOpenAI") is CheckBox checkBoxProxyForOpenAI) checkBoxProxyForOpenAI.Content = LanguageHelper.Get("Proxy.ForOpenAI", langCode);
            if (FindName("CheckBox_Proxy_ForGemini") is CheckBox checkBoxProxyForGemini) checkBoxProxyForGemini.Content = LanguageHelper.Get("Proxy.ForGemini", langCode);
            if (FindName("CheckBox_Proxy_ForFree") is CheckBox checkBoxProxyForFree) checkBoxProxyForFree.Content = LanguageHelper.Get("Proxy.ForFree", langCode);
            if (FindName("CheckBox_Proxy_ForTTS") is CheckBox checkBoxProxyForTTS) checkBoxProxyForTTS.Content = LanguageHelper.Get("Proxy.ForTTS", langCode);
            if (FindName("CheckBox_Proxy_ForASR") is CheckBox checkBoxProxyForASR) checkBoxProxyForASR.Content = LanguageHelper.Get("Proxy.ForASR", langCode);
            if (FindName("CheckBox_Proxy_ForMcp") is CheckBox checkBoxProxyForMcp) checkBoxProxyForMcp.Content = LanguageHelper.Get("Proxy.ForMcp", langCode);
            if (FindName("CheckBox_Proxy_ForPlugin") is CheckBox checkBoxProxyForPlugin) checkBoxProxyForPlugin.Content = LanguageHelper.Get("Proxy.ForPlugin", langCode);

            // Plugin Store Proxy UI
            if (FindName("Label_PluginStore_Proxy") is Label labelPluginStoreProxy) labelPluginStoreProxy.Content = LanguageHelper.Get("PluginStore.ProxySettings", langCode);
            if (FindName("CheckBox_PluginStore_UseProxy") is CheckBox checkBoxPluginStoreUseProxy) checkBoxPluginStoreUseProxy.Content = LanguageHelper.Get("PluginStore.EnableProxy", langCode);
            if (FindName("Label_PluginStore_ProxyUrl") is Label labelPluginStoreProxyUrl) labelPluginStoreProxyUrl.Content = LanguageHelper.Get("PluginStore.ProxyUrl", langCode);
            if (FindName("TextBlock_PluginStore_ProxyUrlNote") is TextBlock textBlockPluginStoreProxyUrlNote) textBlockPluginStoreProxyUrlNote.Text = LanguageHelper.Get("PluginStore.ProxyUrlNote", langCode);

            // TTS UI - 更新的多语言支持
            if (FindName("Label_TTS_Provider") is Label labelTTSProvider) labelTTSProvider.Content = LanguageHelper.Get("TTS.Provider", langCode);
            if (FindName("TextBlock_TTS_Volume") is TextBlock textBlockTTSVolume) textBlockTTSVolume.Text = LanguageHelper.Get("TTS.Volume", langCode);
            if (FindName("TextBlock_TTS_Speed") is TextBlock textBlockTTSSpeed) textBlockTTSSpeed.Text = LanguageHelper.Get("TTS.Speed", langCode);

            // URL TTS
            if (FindName("Label_TTS_URL_BaseUrl") is Label labelTTSURLBaseUrl) labelTTSURLBaseUrl.Content = LanguageHelper.Get("TTS.URL.BaseUrl", langCode);
            if (FindName("Label_TTS_URL_Voice") is Label labelTTSURLVoice) labelTTSURLVoice.Content = LanguageHelper.Get("TTS.URL.Voice", langCode);
            if (FindName("Label_TTS_URL_RequestMethod") is Label labelTTSURLRequestMethod) labelTTSURLRequestMethod.Content = LanguageHelper.Get("TTS.URL.RequestMethod", langCode);
            if (FindName("TextBlock_TTS_URL_Config") is TextBlock textBlockTTSURLConfig) textBlockTTSURLConfig.Text = LanguageHelper.Get("TTS.URL.Config", langCode);
            if (FindName("TextBlock_TTS_URL_Description") is TextBlock textBlockTTSURLDescription) textBlockTTSURLDescription.Text = LanguageHelper.Get("TTS.URL.Description", langCode);
            if (FindName("TextBlock_TTS_URL_Voice_Tip") is TextBlock textBlockTTSURLVoiceTip) textBlockTTSURLVoiceTip.Text = LanguageHelper.Get("TTS.URL.VoiceTip", langCode);
            if (FindName("TextBlock_TTS_OpenAI_Config") is TextBlock textBlockTTSOpenAIConfig) textBlockTTSOpenAIConfig.Text = LanguageHelper.Get("TTS.OpenAI.Config", langCode);

            // OpenAI TTS
            if (FindName("Label_TTS_OpenAI_BaseUrl") is Label labelTTSOpenAIBaseUrl) labelTTSOpenAIBaseUrl.Content = LanguageHelper.Get("TTS.OpenAI.BaseUrl", langCode);
            if (FindName("Label_TTS_OpenAI_ApiKey") is Label labelTTSOpenAIApiKey) labelTTSOpenAIApiKey.Content = LanguageHelper.Get("TTS.OpenAI.ApiKey", langCode);
            if (FindName("Label_TTS_OpenAI_Model") is Label labelTTSOpenAIModel) labelTTSOpenAIModel.Content = LanguageHelper.Get("TTS.OpenAI.Model", langCode);
            if (FindName("Label_TTS_OpenAI_Voice") is Label labelTTSOpenAIVoice) labelTTSOpenAIVoice.Content = LanguageHelper.Get("TTS.OpenAI.Voice", langCode);
            if (FindName("Label_TTS_OpenAI_Format") is Label labelTTSOpenAIFormat) labelTTSOpenAIFormat.Content = LanguageHelper.Get("TTS.OpenAI.Format", langCode);
            if (FindName("Button_TTS_OpenAI_RefreshModels") is Button buttonTTSOpenAIRefreshModels) buttonTTSOpenAIRefreshModels.Content = LanguageHelper.Get("TTS.OpenAI.RefreshModels", langCode);
            if (FindName("TextBlock_TTS_OpenAI_BaseUrl_Tip") is TextBlock textBlockTTSOpenAIBaseUrlTip) textBlockTTSOpenAIBaseUrlTip.Text = LanguageHelper.Get("TTS.OpenAI.BaseUrlTip", langCode);
            if (FindName("TextBlock_TTS_OpenAI_Description") is TextBlock textBlockTTSOpenAIDescription) textBlockTTSOpenAIDescription.Text = LanguageHelper.Get("TTS.OpenAI.Description", langCode);

            // 更新身体交互设置标题
            if (FindName("TextBlock_TouchFeedbackTitle") is TextBlock textBlockTouchFeedbackTitle)
            {
                textBlockTouchFeedbackTitle.Text = LanguageHelper.Get("TouchFeedback.Title", langCode);
            }

            // 更新 TouchFeedbackSettingsControl 的多语言
            if (FindName("TouchFeedbackSettingsContainer") is ContentControl touchFeedbackContainer)
            {
                if (touchFeedbackContainer.Content is TouchFeedbackSettingsControl touchFeedbackControl)
                {
                    // 立即刷新语言
                    touchFeedbackControl.RefreshLanguage();
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CS4014", Justification = "异步刷新云端插件列表为有意的 fire-and-forget，不应阻塞UI")]
private void Button_RefreshPlugins_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;

            // 防止重复点击
            if (button != null && !button.IsEnabled)
                return;

            StartButtonLoadingAnimation(button);

            try
            {
                var langCode = _plugin.Settings.Language;
                var pluginItems = new Dictionary<string, UnifiedPluginItem>(StringComparer.OrdinalIgnoreCase);

                // 第一步：获取当前已加载的插件信息（不重新加载）
                Logger.Log($"开始刷新插件列表 - 当前本地插件数量: {_plugin.Plugins.Count}");
                System.Diagnostics.Debug.WriteLine($"[PluginStore] 当前插件数量: {_plugin.Plugins.Count}");
                System.Diagnostics.Debug.WriteLine($"[PluginStore] 失败的插件数量: {_plugin.FailedPlugins.Count}");

                // 添加本地成功插件
                foreach (var localPlugin in _plugin.Plugins)
                {
                    var sha = PluginManager.GetFileSha256(localPlugin.FilePath);
                    var id = localPlugin.Name; // 先用本地名称作为ID

                    var item = new UnifiedPluginItem
                    {
                        IsLocal = true,
                        Id = id,
                        Name = localPlugin.Name,
                        OriginalName = localPlugin.Name,
                        Author = localPlugin.Author,
                        Description = GetPluginDescription(localPlugin, langCode),
                        OriginalDescription = GetPluginDescription(localPlugin, langCode),
                        IsEnabled = localPlugin.Enabled,
                        ActionText = LanguageHelper.Get("Plugin.UnloadPlugin", langCode),
                        Icon = "\uE8A5", // Folder icon
                        LocalPlugin = localPlugin,
                        Version = sha,
                        LocalFilePath = localPlugin.FilePath,
                        StatusText = localPlugin.Enabled ?
                            LanguageHelper.Get("Plugin.StatusRunning", langCode) ?? "运行中" :
                            LanguageHelper.Get("Plugin.StatusDisabled", langCode) ?? "已禁用",
                        UninstallActionText = LanguageHelper.Get("Plugin.Uninstall", langCode) ?? "卸载"
                    };

                    if (localPlugin is IActionPlugin && localPlugin.Parameters.Contains("setting"))
                    {
                        item.HasSettingAction = true;
                        item.SettingActionText = LanguageHelper.Get("Plugin.Setting", langCode) ?? "设置";
                    }
                    pluginItems[id] = item;
                }

                // 添加本地失败插件
                foreach (var failedPlugin in _plugin.FailedPlugins)
                {
                    var id = failedPlugin.Name;
                    pluginItems[id] = new UnifiedPluginItem
                    {
                        IsFailed = true,
                        IsLocal = true,
                        Id = id,
                        Name = $"{failedPlugin.Name} ({LanguageHelper.Get("Plugin.Outdated", langCode)})",
                        OriginalName = failedPlugin.Name,
                        Author = failedPlugin.Author,
                        Description = $"{LanguageHelper.Get("Plugin.LoadFailed", langCode)}: {failedPlugin.Description}",
                        OriginalDescription = failedPlugin.Description,
                        IsEnabled = false,
                        ActionText = LanguageHelper.Get("Plugin.Delete", langCode),
                        Icon = "\uE783", // Error icon
                        FailedPlugin = failedPlugin,
                        UninstallActionText = LanguageHelper.Get("Plugin.Uninstall", langCode) ?? "卸载",
                        Version = LanguageHelper.Get("Plugin.UnknownVersion", langCode),
                        LocalFilePath = failedPlugin.FilePath,
                        StatusText = LanguageHelper.Get("Plugin.StatusDisabled", langCode) ?? "已禁用"
                    };
                }

                // 立即显示本地插件
                Dispatcher.Invoke(() =>
                {
                    var dataGrid = (DataGrid)this.FindName("DataGrid_Plugins");
                    dataGrid.ItemsSource = pluginItems.Values.ToList();
                    System.Diagnostics.Debug.WriteLine($"[PluginStore] 立即显示本地插件数量: {pluginItems.Count}");
                });

                Logger.Log($"正在连接云端插件商店进行版本对比...");

                // 第二步：异步加载在线插件（不阻塞UI）
#pragma warning disable CS4014
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Dictionary<string, JObject> remotePlugins = new Dictionary<string, JObject>();

                        using (var client = CreatePluginStoreHttpClient())
                        {
                            var pluginListUrl = GetPluginStoreUrl("https://raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json");
                            var json = await client.GetStringAsync(pluginListUrl);
                            remotePlugins = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(json);
                        }

                        Logger.Log($"云端插件商店连接成功，发现 {remotePlugins.Count} 个在线插件");
                        System.Diagnostics.Debug.WriteLine($"[PluginStore] 在线插件加载成功，数量: {remotePlugins.Count}");

                        // 更新本地插件的远程信息
                        foreach (var item in remotePlugins)
                        {
                            var id = item.Key;
                            var remoteInfo = item.Value;
                            var remoteSha256 = remoteInfo["SHA256"]?.ToString() ?? string.Empty;
                            var pluginName = remoteInfo["Name"]?.ToString() ?? string.Empty;

                            // 查找对应的本地插件 - 支持多种匹配方式
                            var localItem = pluginItems.Values.FirstOrDefault(p =>
                                p.OriginalName == pluginName ||
                                p.Name == pluginName ||
                                p.Id == id ||
                                (p.LocalPlugin != null && p.LocalPlugin.Name == pluginName));

                            // 如果通过名称没有找到，尝试通过文件路径匹配（处理插件名称变更的情况）
                            if (localItem == null)
                            {
                                var expectedFilePath = Path.Combine(PluginManager.PluginPath, $"{id}.dll");
                                localItem = pluginItems.Values.FirstOrDefault(p =>
                                    p.LocalFilePath != null &&
                                    p.LocalFilePath.Equals(expectedFilePath, StringComparison.OrdinalIgnoreCase));

                                if (localItem != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[PluginStore] 通过文件路径匹配到插件: {localItem.Name} -> {pluginName}");
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[PluginStore] 匹配远程插件 '{pluginName}' (ID: {id})");
                            System.Diagnostics.Debug.WriteLine($"[PluginStore] 本地插件列表: {string.Join(", ", pluginItems.Values.Select(p => $"{p.Name}({p.OriginalName})"))}");

                            if (localItem != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[PluginStore] 找到匹配的本地插件: {localItem.Name}");

                                // 更新本地插件的远程信息
                                localItem.RemoteVersion = remoteSha256;
                                localItem.FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty;
                                localItem.SHA256 = remoteSha256;

                                bool isUpdatable = !string.IsNullOrEmpty(localItem.Version) &&
                                                   !string.IsNullOrEmpty(remoteSha256) &&
                                                   !localItem.Version.Equals(remoteSha256, StringComparison.OrdinalIgnoreCase);

                                System.Diagnostics.Debug.WriteLine($"[PluginStore] 版本比较 - 本地: '{localItem.Version}', 远程: '{remoteSha256}', 可更新: {isUpdatable}");

                                if (isUpdatable)
                                {
                                    localItem.IsUpdatable = true;
                                    localItem.UpdateAvailableText = LanguageHelper.Get("Plugin.UpdateAvailable", langCode);
                                    localItem.ActionText = LanguageHelper.Get("Plugin.Update", langCode);
                                    localItem.Icon = "\uE948"; // Sync icon
                                }
                                else
                                {
                                    // 确保已更新的插件显示正确的状态
                                    localItem.IsUpdatable = false;
                                    localItem.UpdateAvailableText = null;
                                    localItem.ActionText = LanguageHelper.Get("Plugin.UnloadPlugin", langCode);
                                    localItem.Icon = "\uE8A5"; // Folder icon
                                }

                                // 更新描述
                                var des = remoteInfo["Description"]?.ToObject<Dictionary<string, string>>();
                                var remoteDescription = des != null ? (des.TryGetValue(langCode, out var d) ? d : (des.TryGetValue("en", out var enD) ? enD : string.Empty)) : string.Empty;
                                if (!string.IsNullOrEmpty(remoteDescription))
                                {
                                    var localSource = LanguageHelper.Get("Plugin.Source.Local", langCode);
                                    localItem.Description = $"({localSource}) {remoteDescription}";
                                }
                            }
                            else
                            {
                                // 添加纯在线插件
                                var des = remoteInfo["Description"]?.ToObject<Dictionary<string, string>>();
                                var description = des != null ? (des.TryGetValue(langCode, out var d) ? d : (des.TryGetValue("en", out var enD) ? enD : string.Empty)) : string.Empty;

                                pluginItems[id] = new UnifiedPluginItem
                                {
                                    IsLocal = false,
                                    Id = id,
                                    Name = pluginName,
                                    Author = remoteInfo["Author"]?.ToString() ?? string.Empty,
                                    Description = $"({LanguageHelper.Get("Plugin.Source.Cloud", langCode)}) {description}",
                                    OriginalDescription = description,
                                    IsEnabled = false,
                                    ActionText = LanguageHelper.Get("PluginStore.Install", langCode),
                                    Icon = "\uE753", // Cloud download icon
                                    FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty,
                                    SHA256 = remoteSha256,
                                    RemoteVersion = remoteSha256,
                                    StatusText = LanguageHelper.Get("Plugin.StatusNotDownloaded", langCode) ?? "未下载"
                                };
                            }
                        }

                        // 更新UI
                        Dispatcher.Invoke(() =>
                        {
                            var dataGrid = (DataGrid)this.FindName("DataGrid_Plugins");
                            dataGrid.ItemsSource = pluginItems.Values.ToList();
                            System.Diagnostics.Debug.WriteLine($"[PluginStore] 更新后总插件数量: {pluginItems.Count}");
                        });
#pragma warning restore CS4014

                        var updatableCount = pluginItems.Values.Count(p => p.IsUpdatable);
                        Logger.Log($"插件列表刷新完成 - 总计 {pluginItems.Count} 个插件，其中 {updatableCount} 个可更新");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"无法连接云端插件商店，使用离线模式: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[PluginStore] 在线插件加载失败，继续使用离线模式: {ex.Message}");

                        Dispatcher.Invoke(() =>
                        {
                            // 可以在这里显示离线状态提示
                            System.Diagnostics.Debug.WriteLine("[PluginStore] 当前为离线模式，仅显示本地插件");
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("RefreshPlugins.Fail", _plugin.Settings.Language, "刷新插件列表失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private async void Button_PluginAction_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                if (((Button)sender).DataContext is UnifiedPluginItem plugin)
                {
                    var langCode = _plugin.Settings.Language;
                    string action = plugin.ActionText;

                    if (action == LanguageHelper.Get("Plugin.Delete", langCode))
                    {
                        await HandleDeletePlugin(plugin);
                    }
                    else if (action == LanguageHelper.Get("Plugin.UnloadPlugin", langCode))
                    {
                        await HandleUninstallPlugin(plugin);
                    }
                    else if (action == LanguageHelper.Get("PluginStore.Install", langCode) || action == LanguageHelper.Get("Plugin.Update", langCode))
                    {
                        await HandleInstallOrUpdatePlugin(plugin);
                    }
                }
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private async void Button_UninstallPlugin_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                if (((Button)sender).DataContext is UnifiedPluginItem plugin)
                {
                    await HandleUninstallPlugin(plugin);
                }
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private async Task HandleUninstallPlugin(UnifiedPluginItem plugin)
        {
            // 直接执行卸载，不再显示确认弹窗
            bool uninstalled = false;
            var pluginNameToFind = plugin.OriginalName ?? plugin.Name;
            var localPlugin = _plugin.Plugins.FirstOrDefault(p => p.FilePath == plugin.LocalFilePath);
            var failedPlugin = _plugin.FailedPlugins.FirstOrDefault(p => p.Name == pluginNameToFind);

            // 尝试1: 通过Plugin实例卸载
            if (localPlugin != null)
            {
                uninstalled = await _plugin.UnloadAndTryDeletePlugin(localPlugin);
            }
            // 尝试2: 通过FailedPlugin卸载
            else if (failedPlugin != null && !string.IsNullOrEmpty(failedPlugin.FilePath))
            {
                uninstalled = await _plugin.DeletePluginFile(failedPlugin.FilePath);
            }

            // 尝试3: 备用文件定位删除 - 如果前面的方法都失败，尝试直接通过文件路径删除
            if (!uninstalled && !string.IsNullOrEmpty(plugin.LocalFilePath))
            {
                string validFilePath = plugin.LocalFilePath;
                
                // 检查是否是目录路径
                if (Directory.Exists(validFilePath))
                {
                    Logger.Log($"Fallback: LocalFilePath is a directory, will use DeletePluginByName instead: {validFilePath}");
                    // 如果是目录路径，不要随便删除文件，而是跳到方法4使用插件名称定位
                    validFilePath = null;
                }
                
                if (!string.IsNullOrEmpty(validFilePath) && File.Exists(validFilePath))
                {
                    Logger.Log($"Fallback: Attempting to delete plugin file directly: {validFilePath}");
                    uninstalled = await _plugin.DeletePluginFile(validFilePath);
                }
            }

            // 尝试4: 最后的备用方案 - 通过插件名称在插件目录中查找并删除
            if (!uninstalled)
            {
                Logger.Log($"Fallback: Attempting to locate and delete plugin by name: {pluginNameToFind}");
                uninstalled = await _plugin.DeletePluginByName(pluginNameToFind);
            }

            // 如果卸载成功，立即从内存中移除插件引用
            if (uninstalled)
            {
                // 从失败插件列表中移除
                if (failedPlugin != null)
                {
                    _plugin.FailedPlugins.Remove(failedPlugin);
                    Logger.Log($"Removed failed plugin from memory: {pluginNameToFind}");
                }
                
                // 从已加载插件列表中移除（如果还在的话）
                if (localPlugin != null && _plugin.Plugins.Contains(localPlugin))
                {
                    _plugin.Plugins.Remove(localPlugin);
                    Logger.Log($"Removed loaded plugin from memory: {pluginNameToFind}");
                }
            }

            // 刷新UI显示
            Button_RefreshPlugins_Click(this, new RoutedEventArgs());

            if (!uninstalled)
            {
                string fileName = !string.IsNullOrEmpty(plugin.LocalFilePath) 
                    ? Path.GetFileName(plugin.LocalFilePath) 
                    : pluginNameToFind;
                MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {fileName}"),
                                ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleDeletePlugin(UnifiedPluginItem plugin)
        {
            string pluginFilePath = plugin.FailedPlugin.FilePath;
            bool deleted = await _plugin.DeletePluginFile(pluginFilePath);
            
            // 如果删除成功，立即从失败插件列表中移除
            if (deleted && plugin.FailedPlugin != null)
            {
                _plugin.FailedPlugins.Remove(plugin.FailedPlugin);
                Logger.Log($"Removed failed plugin from memory: {plugin.FailedPlugin.Name}");
            }
            
            Button_RefreshPlugins_Click(this, new RoutedEventArgs());
            
            if (!deleted)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {Path.GetFileName(pluginFilePath)}"),
                               ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleInstallOrUpdatePlugin(UnifiedPluginItem plugin)
        {
            try
            {
                using (var client = CreatePluginStoreHttpClient())
                {
                    var downloadUrl = GetPluginStoreUrl(plugin.FileUrl);
                    byte[] data = null;
                    string downloadedFileHash = null;
                    bool downloadSuccess = false;

                    // 重试下载机制
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            Logger.Log($"Downloading plugin (attempt {attempt}/3): {downloadUrl}");
                            data = await client.GetByteArrayAsync(downloadUrl);
                            Logger.Log($"Downloaded {data.Length} bytes");

                            using (var sha256 = SHA256.Create())
                            {
                                var hash = sha256.ComputeHash(data);
                                downloadedFileHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            }

                            var expectedHash = plugin.SHA256?.ToLowerInvariant() ?? "";
                            Logger.Log($"File validation - Expected: {expectedHash}");
                            Logger.Log($"File validation - Downloaded: {downloadedFileHash}");
                            Logger.Log($"File validation - Size: {data.Length} bytes");

                            if (downloadedFileHash == expectedHash)
                            {
                                Logger.Log("File validation successful");
                                downloadSuccess = true;
                                break;
                            }
                            else
                            {
                                Logger.Log($"File validation failed on attempt {attempt}");
                                if (attempt < 3)
                                {
                                    Logger.Log("Retrying download...");
                                    await Task.Delay(1000); // 等待1秒后重试
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Download attempt {attempt} failed: {ex.Message}");
                            if (attempt < 3)
                            {
                                await Task.Delay(1000);
                            }
                        }
                    }

                    if (!downloadSuccess)
                    {
                        var errorMessage = $"文件校验失败！\n" +
                                         $"插件: {plugin.Name}\n" +
                                         $"期望哈希: {plugin.SHA256}\n" +
                                         $"实际哈希: {downloadedFileHash ?? "下载失败"}\n" +
                                         $"文件大小: {data?.Length ?? 0} 字节\n\n" +
                                         $"可能原因:\n" +
                                         $"1. 网络连接不稳定\n" +
                                         $"2. 服务器文件已更新但哈希值未同步\n" +
                                         $"3. 文件在传输过程中损坏\n\n" +
                                         $"是否要强制安装此插件？\n" +
                                         $"(仅在确信文件来源安全时选择'是')";

                        var result = MessageBox.Show(errorMessage, "文件校验失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        if (result == MessageBoxResult.No)
                        {
                            return;
                        }
                        else
                        {
                            Logger.Log($"User chose to force install plugin despite validation failure");
                            // 继续安装，但使用实际下载的文件哈希值作为新的期望值
                            downloadedFileHash = downloadedFileHash ?? "unknown";

                            // 额外的安全检查：验证文件是否为有效的.NET程序集
                            try
                            {
                                using (var ms = new MemoryStream(data))
                                {
                                    var assembly = System.Reflection.Assembly.Load(data);
                                    Logger.Log($"Assembly validation successful: {assembly.FullName}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Assembly validation failed: {ex.Message}");
                                MessageBox.Show($"下载的文件不是有效的.NET程序集！\n错误: {ex.Message}\n\n为了安全起见，安装已取消。",
                                    "文件格式错误", MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }
                    }

                    var pluginDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");
                    if (!Directory.Exists(pluginDir))
                    {
                        Directory.CreateDirectory(pluginDir);
                    }

                    string filePath;

                    // 如果是更新操作，使用现有插件的文件路径，确保替换而不是创建新文件
                    if (plugin.IsLocal && plugin.LocalPlugin != null && !string.IsNullOrEmpty(plugin.LocalPlugin.FilePath))
                    {
                        filePath = plugin.LocalPlugin.FilePath;
                        Logger.Log($"Updating existing plugin file: {filePath}");
                    }
                    else
                    {
                        // 新安装的插件，使用 plugin.Id 作为文件名
                        filePath = Path.Combine(pluginDir, $"{plugin.Id}.dll");
                        Logger.Log($"Installing new plugin file: {filePath}");
                    }

                    // 写入新文件
                    File.WriteAllBytes(filePath, data);
                    Logger.Log($"Plugin file written to: {filePath}");

                    // 确保文件写入完成并释放文件句柄
                    await Task.Delay(200);

                    // 验证文件确实已更新（在加载插件之前）
                    string actualFileHash;
                    try
                    {
                        actualFileHash = PluginManager.GetFileSha256(filePath);
                        Logger.Log($"Plugin update verification - Expected: {downloadedFileHash}, Actual: {actualFileHash}");

                        if (actualFileHash != downloadedFileHash)
                        {
                            Logger.Log($"Error: File hash mismatch after update. Expected: {downloadedFileHash}, Got: {actualFileHash}");
                            MessageBox.Show($"插件更新验证失败！\n期望: {downloadedFileHash}\n实际: {actualFileHash}",
                                "更新验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error verifying updated plugin file: {ex.Message}");
                        MessageBox.Show($"无法验证更新后的插件文件: {ex.Message}",
                            "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // 如果是更新操作，使用专门的更新方法
                    if (plugin.IsLocal && plugin.LocalPlugin != null)
                    {
                        Logger.Log($"Updating existing plugin: {plugin.LocalPlugin.Name}");
                        bool updateSuccess = await _plugin.UpdatePlugin(filePath);
                        if (!updateSuccess)
                        {
                            Logger.Log($"Failed to update plugin using UpdatePlugin method, falling back to full reload");
                            _plugin.LoadPlugins();
                        }
                        else
                        {
                            Logger.Log($"Plugin updated successfully using UpdatePlugin method");
                        }
                    }
                    else
                    {
                        // 新安装的插件，使用常规加载方法
                        Logger.Log($"Installing new plugin: {plugin.Name}");
                        _plugin.LoadPlugins();
                    }
                    Logger.Log($"Plugins reloaded after install/update");

                    // 等待插件加载完成
                    await Task.Delay(800);

                    // 再次验证加载后的状态
                    var reloadedHash = PluginManager.GetFileSha256(filePath);
                    Logger.Log($"Post-reload verification - Expected: {downloadedFileHash}, Actual: {reloadedHash}");

                    // 强制刷新UI显示 - 让系统重新计算所有版本信息
                    Logger.Log($"Refreshing UI to update plugin status...");
                    Button_RefreshPlugins_Click(this, new RoutedEventArgs());

                    // 等待UI刷新完成后再次检查
                    await Task.Delay(1000);

                    // 查找更新后的插件项并验证状态
                    var dataGrid = (DataGrid)this.FindName("DataGrid_Plugins");
                    if (dataGrid?.ItemsSource is IEnumerable<UnifiedPluginItem> currentItems)
                    {
                        var updatedItem = currentItems.FirstOrDefault(p => p.Id == plugin.Id || p.OriginalName == plugin.OriginalName);
                        if (updatedItem != null)
                        {
                            Logger.Log($"Updated plugin item found - Name: {updatedItem.Name}");
                            Logger.Log($"  Local Version (SHA256): {updatedItem.Version}");
                            Logger.Log($"  Remote Version (SHA256): {updatedItem.SHA256}");
                            Logger.Log($"  IsUpdatable: {updatedItem.IsUpdatable}");
                            Logger.Log($"  ActionText: {updatedItem.ActionText}");

                            if (updatedItem.IsUpdatable)
                            {
                                Logger.Log($"Warning: Plugin still shows as updatable after update!");
                                Logger.Log($"  This suggests the file hash comparison is not working correctly.");

                                // 尝试再次强制刷新
                                Logger.Log($"Attempting additional UI refresh...");
                                await Task.Delay(500);
                                Button_RefreshPlugins_Click(this, new RoutedEventArgs());
                                await Task.Delay(1000);
                            }
                            else
                            {
                                Logger.Log($"Success: Plugin no longer shows as updatable. Update completed successfully.");
                            }
                        }
                        else
                        {
                            Logger.Log($"Warning: Could not find updated plugin item in UI list");
                            Logger.Log($"  Searching for plugin with Id: {plugin.Id} or OriginalName: {plugin.OriginalName}");
                            Logger.Log($"  Available items: {string.Join(", ", currentItems.Select(p => $"{p.Id}({p.OriginalName})"))}");
                        }
                    }
                    else
                    {
                        Logger.Log($"Warning: Could not access DataGrid ItemsSource for verification");
                    }

                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("InstallPlugin.Success", _plugin.Settings.Language, "插件安装/更新成功！"),
                        ErrorMessageHelper.GetLocalizedTitle("Success", _plugin.Settings.Language, "成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("InstallPlugin.Fail", _plugin.Settings.Language, "安装插件失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_PluginSetting_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is UnifiedPluginItem plugin)
            {
                if (plugin.LocalPlugin is IActionPlugin actionPlugin)
                {
                    try
                    {
                        actionPlugin.Function("action(setting)");
                    }
                    catch (Exception ex)
                    {
                        var langCode = _plugin.Settings.Language;

                        // 针对不同类型的错误提供更具体的提示
                        string errorMessage;
                        if (ex.Message.Contains("不具有由 URI") || ex.Message.Contains("识别的资源"))
                        {
                            errorMessage = $"{LanguageHelper.Get("Plugin.ResourceError", langCode) ?? "插件资源文件缺失或损坏"}\n\n" +
                                         $"插件: {plugin.Name}\n" +
                                         $"建议: 尝试重新安装此插件或联系插件开发者";
                        }
                        else
                        {
                            errorMessage = $"{LanguageHelper.Get("Plugin.SettingError", langCode) ?? "插件设置打开失败"}: {ex.Message}";
                        }

                        var errorTitle = LanguageHelper.Get("Error", langCode) ?? "错误";

                        MessageBox.Show(errorMessage, errorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

                        // 记录详细错误信息到调试日志
                        System.Diagnostics.Debug.WriteLine($"[PluginSetting] 插件 {plugin.Name} 设置打开失败: {ex}");
                    }
                }
            }
        }

        private void ComboBox_TTS_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    var provider = selectedItem.Tag?.ToString();
                    System.Diagnostics.Debug.WriteLine($"[TTS Provider] 切换到提供商: {provider}");

                    // 先隐藏所有面板
                    var urlPanel = FindName("Panel_TTS_URL") as StackPanel;
                    var openAIPanel = FindName("Panel_TTS_OpenAI") as StackPanel;
                    var diyPanel = FindName("Panel_TTS_DIY") as StackPanel;
                    var gptSoVITSPanel = FindName("Panel_TTS_GPTSoVITS") as StackPanel;

                    if (urlPanel != null) urlPanel.Visibility = Visibility.Collapsed;
                    if (openAIPanel != null) openAIPanel.Visibility = Visibility.Collapsed;
                    if (diyPanel != null) diyPanel.Visibility = Visibility.Collapsed;
                    if (gptSoVITSPanel != null) gptSoVITSPanel.Visibility = Visibility.Collapsed;

                    // 显示对应的面板
                    switch (provider)
                    {
                        case "URL":
                            if (urlPanel != null)
                            {
                                urlPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 显示URL面板");
                            }
                            break;
                        case "OpenAI":
                            if (openAIPanel != null)
                            {
                                openAIPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 显示OpenAI面板");
                            }
                            break;
                        case "DIY":
                            if (diyPanel != null)
                            {
                                diyPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 显示DIY面板");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 错误: 找不到DIY面板!");
                            }
                            break;
                        case "GPT-SoVITS":
                            if (gptSoVITSPanel != null)
                            {
                                gptSoVITSPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 显示GPT-SoVITS面板");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[TTS Provider] 错误: 找不到GPT-SoVITS面板!");
                            }
                            break;
                        default:
                            System.Diagnostics.Debug.WriteLine($"[TTS Provider] 未知提供商: {provider}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TTS Provider] 面板切换异常: {ex.Message}");
            }

            // 调用原有的保存逻辑
            ScheduleAutoSave();
        }

        private void Button_TTS_DIY_OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // DIY TTS 配置已集成到主配置系统中，无需独立配置文件
                // 显示说明信息
                var message = "DIY TTS 配置已集成到 VPetLLM 主设置中。\n\n" +
                             "配置位置：VPetLLM 设置 -> TTS 标签页 -> DIY 提供商\n\n" +
                             "您可以在主设置界面中直接配置 DIY TTS 的所有参数，包括：\n" +
                             "• BaseUrl - API 基础地址\n" +
                             "• Method - 请求方法 (GET/POST)\n" +
                             "• RequestBody - 请求体模板\n" +
                             "• CustomHeaders - 自定义请求头\n" +
                             "• ResponseFormat - 响应格式\n\n" +
                             "详细配置说明请参考文档。";

                MessageBox.Show(message, "DIY TTS 配置说明",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"显示配置说明失败：{ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Button_TTS_Test_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                // 保存当前设置到TTS服务
                SaveSettings();

                // 重新创建TTS服务实例以使用最新设置
                _ttsService?.Dispose();
                _ttsService = new TTSService(_plugin.Settings.TTS, _plugin.Settings.Proxy);

                if (_ttsService != null)
                {
                    var success = await _ttsService.TestTTSAsync();
                    if (success)
                    {
                        MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("TTS.TestSuccess", _plugin.Settings.Language, "TTS测试成功！"),
                            ErrorMessageHelper.GetLocalizedTitle("Success", _plugin.Settings.Language, "成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("TTS.TestFail", _plugin.Settings.Language, "TTS测试失败，请检查设置。"),
                            ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("TTS.TestError", _plugin.Settings.Language, "TTS测试出错", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        private string GetPluginStoreUrl(string originalUrl)
        {
            var (useUrlRewrite, proxy) = GetPluginStoreProxyInfo();

            // 如果需要使用URL重写（拼接类型代理）
            if (useUrlRewrite)
            {
                var pluginStoreSettings = _plugin.Settings.PluginStore;
                var proxyUrl = pluginStoreSettings.ProxyUrl.Trim();

                // 移除原URL中的协议部分，然后拼接代理地址
                var uri = new Uri(originalUrl);
                var pathAndQuery = originalUrl.Substring(uri.Scheme.Length + 3); // 移除 "https://" 或 "http://"
                return $"{proxyUrl.TrimEnd('/')}/{pathAndQuery}";
            }

            // 对于传统代理或无代理，直接返回原URL
            return originalUrl;
        }

        private async void Button_TTS_OpenAI_RefreshModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                var apiKey = ((TextBox)this.FindName("TextBox_TTS_OpenAI_ApiKey")).Text;
                var baseUrl = ((TextBox)this.FindName("TextBox_TTS_OpenAI_BaseUrl")).Text;

                if (string.IsNullOrEmpty(apiKey))
                {
                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("TTS.ApiKeyRequired", _plugin.Settings.Language, "请先输入API Key"),
                        ErrorMessageHelper.GetLocalizedTitle("Warning", _plugin.Settings.Language, "警告"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                    // 从TTS URL推导模型API URL
                    var modelUrl = baseUrl.Replace("/v1/tts", "/model").Replace("/tts", "/model");
                    var response = await client.GetAsync(modelUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var modelsResponse = JsonConvert.DeserializeObject<JObject>(json);
                        var items = modelsResponse["items"] as JArray;

                        var comboBox = (ComboBox)this.FindName("ComboBox_TTS_OpenAI_Model");
                        var currentValue = comboBox.Text;

                        comboBox.Items.Clear();

                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                var modelId = item["_id"]?.ToString();
                                var title = item["title"]?.ToString();
                                var type = item["type"]?.ToString();

                                if (!string.IsNullOrEmpty(modelId) && type == "tts")
                                {
                                    var displayName = !string.IsNullOrEmpty(title) ? $"{title} ({modelId})" : modelId;
                                    var comboBoxItem = new ComboBoxItem
                                    {
                                        Content = displayName,
                                        Tag = modelId
                                    };
                                    comboBox.Items.Add(comboBoxItem);
                                }
                            }
                        }

                        // 恢复之前选择的值
                        comboBox.Text = currentValue;

                        MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("TTS.ModelsRefreshed", _plugin.Settings.Language, "模型列表已刷新"),
                            ErrorMessageHelper.GetLocalizedTitle("Success", _plugin.Settings.Language, "成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("TTS.RefreshModelsFail", _plugin.Settings.Language, "获取模型列表失败，请检查API Key和网络连接。"),
                            ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("TTS.RefreshModelsError", _plugin.Settings.Language, "获取模型列表出错", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        /// <summary>
        /// 获取插件商店代理配置信息
        /// </summary>
        private (bool useUrlRewrite, System.Net.IWebProxy proxy) GetPluginStoreProxyInfo()
        {
            // 优先检查插件商店专用代理设置
            var pluginStoreSettings = _plugin.Settings.PluginStore;
            if (pluginStoreSettings != null && pluginStoreSettings.UseProxy && !string.IsNullOrEmpty(pluginStoreSettings.ProxyUrl))
            {
                var proxyUrl = pluginStoreSettings.ProxyUrl.Trim();

                // 如果是URL拼接类型的代理（如ghfast.top），使用URL重写方式
                if (proxyUrl.StartsWith("http://") || proxyUrl.StartsWith("https://"))
                {
                    // 检查是否是GitHub加速服务（URL拼接类型）
                    if (proxyUrl.Contains("ghfast.top") || proxyUrl.Contains("github") || proxyUrl.Contains("raw.githubusercontent"))
                    {
                        return (true, null); // 使用URL拼接方式，不使用HttpClient代理
                    }

                    // 其他HTTP/HTTPS代理，使用传统代理方式
                    return (false, new System.Net.WebProxy(proxyUrl));
                }
                else
                {
                    // 假设是 IP:Port 格式的代理
                    return (false, new System.Net.WebProxy($"http://{proxyUrl}"));
                }
            }

            // 如果插件商店专用代理未设置，则检查通用代理设置
            var proxySettings = _plugin.Settings.Proxy;

            // 如果通用代理未启用，不使用代理
            if (proxySettings == null || !proxySettings.IsEnabled)
            {
                return (false, null);
            }

            bool useProxy = false;

            // 如果ForAllAPI为true，则对所有API使用代理
            if (proxySettings.ForAllAPI)
            {
                useProxy = true;
            }
            else
            {
                // 如果ForAllAPI为false，则根据ForPlugin设置决定
                useProxy = proxySettings.ForPlugin;
            }

            if (useProxy)
            {
                if (proxySettings.FollowSystemProxy)
                {
                    return (false, System.Net.WebRequest.GetSystemWebProxy());
                }
                else if (!string.IsNullOrEmpty(proxySettings.Address))
                {
                    if (string.IsNullOrEmpty(proxySettings.Protocol))
                    {
                        proxySettings.Protocol = "http";
                    }
                    var protocol = proxySettings.Protocol.ToLower() == "socks" ? "socks5" : "http";
                    return (false, new System.Net.WebProxy(new Uri($"{protocol}://{proxySettings.Address}")));
                }
            }

            return (false, null);
        }

        private System.Net.IWebProxy GetPluginStoreProxy()
        {
            var (useUrlRewrite, proxy) = GetPluginStoreProxyInfo();
            return proxy;
        }

        /// <summary>
        /// 创建带有正确插件商店代理设置的HttpClient
        /// </summary>
        private HttpClient CreatePluginStoreHttpClient()
        {
            var handler = new HttpClientHandler();
            var proxy = GetPluginStoreProxy();

            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                // 明确禁用代理，防止使用系统默认代理
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            return new HttpClient(handler);
        }

        private void Button_AddOpenAINode_Click(object sender, RoutedEventArgs e)
        {
            var newNode = new Setting.OpenAINodeSetting
            {
                Name = $"OpenAI渠道{_plugin.Settings.OpenAI.OpenAINodes.Count + 1}",
                ApiKey = "",
                Model = "gpt-3.5-turbo",
                Url = "https://api.openai.com/v1",
                Enabled = true,
                EnableAdvanced = false,
                Temperature = 0.7,
                MaxTokens = 2048
            };
            
            _plugin.Settings.OpenAI.OpenAINodes.Add(newNode);
            RefreshOpenAINodesList();
            MarkUnsavedChanges();
        }

        private void Button_RemoveOpenAINode_Click(object sender, RoutedEventArgs e)
        {
            var list = this.FindName("ListView_OpenAINodes") as ListView;
            if (list?.SelectedItem is Setting.OpenAINodeSetting node)
            {
                _plugin.Settings.OpenAI.OpenAINodes.Remove(node);
                RefreshOpenAINodesList();
                MarkUnsavedChanges();
            }
        }

        private void RefreshOpenAINodesList()
        {
            var list = this.FindName("ListView_OpenAINodes") as ListView;
            if (list != null)
            {
                var selectedItem = list.SelectedItem;
                var selectedIndex = list.SelectedIndex;

                list.ItemsSource = null;
                list.ItemsSource = _plugin.Settings.OpenAI.OpenAINodes;

                if (selectedItem != null && _plugin.Settings.OpenAI.OpenAINodes.Contains(selectedItem))
                    list.SelectedItem = selectedItem;
                else if (selectedIndex >= 0 && selectedIndex < list.Items.Count)
                    list.SelectedIndex = selectedIndex;

                list.UpdateLayout();
            }
        }

        private void MarkUnsavedChanges()
        {
            // 标记变更后立即保存
            ScheduleAutoSave(true);
        }

        // ========== Gemini 多渠道管理 ==========
        private void RefreshGeminiNodesList()
        {
            var list = this.FindName("ListView_GeminiNodes") as ListView;
            if (list != null)
            {
                var selectedItem = list.SelectedItem;
                var selectedIndex = list.SelectedIndex;

                list.ItemsSource = null;
                list.ItemsSource = _plugin.Settings.Gemini.GeminiNodes;

                if (selectedItem != null && _plugin.Settings.Gemini.GeminiNodes.Contains(selectedItem))
                    list.SelectedItem = selectedItem;
                else if (selectedIndex >= 0 && selectedIndex < list.Items.Count)
                    list.SelectedIndex = selectedIndex;

                list.UpdateLayout();
            }
        }

        private void Button_AddGeminiNode_Click(object sender, RoutedEventArgs e)
        {
            var newNode = new Setting.GeminiNodeSetting
            {
                Name = $"Gemini渠道{_plugin.Settings.Gemini.GeminiNodes.Count + 1}",
                ApiKey = "",
                Model = "gemini-1.5-flash",
                Url = "https://generativelanguage.googleapis.com/v1beta",
                Enabled = true,
                EnableAdvanced = false,
                Temperature = 0.7,
                MaxTokens = 2048
            };
            _plugin.Settings.Gemini.GeminiNodes.Add(newNode);
            RefreshGeminiNodesList();
            MarkUnsavedChanges();
        }

        private void Button_RemoveGeminiNode_Click(object sender, RoutedEventArgs e)
        {
            var list = this.FindName("ListView_GeminiNodes") as ListView;
            if (list?.SelectedItem is Setting.GeminiNodeSetting node)
            {
                _plugin.Settings.Gemini.GeminiNodes.Remove(node);
                RefreshGeminiNodesList();
                MarkUnsavedChanges();
            }
        }

        // ========== OpenAI 节点选择与详情回显 ==========
        private Setting.OpenAINodeSetting GetSelectedOpenAINode()
        {
            if (this.FindName("ListView_OpenAINodes") is ListView list && list.SelectedItem is Setting.OpenAINodeSetting node)
                return node;
            return null;
        }

        private void ListView_OpenAINodes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _isUpdatingNodeDetails = true;
            var node = GetSelectedOpenAINode();
            if (node == null) { _isUpdatingNodeDetails = false; return; }

            if (this.FindName("TextBox_OpenAINodeName") is TextBox tbName) tbName.Text = node.Name ?? string.Empty;
            if (this.FindName("PasswordBox_OpenAIApiKey") is PasswordBox pb) pb.Password = node.ApiKey ?? string.Empty;
            if (this.FindName("TextBox_OpenAIApiKey_Plain") is TextBox tbPlain) tbPlain.Text = node.ApiKey ?? string.Empty;
            if (this.FindName("ComboBox_OpenAIModel") is ComboBox cbModel) cbModel.Text = node.Model ?? string.Empty;
            if (this.FindName("TextBox_OpenAIUrl") is TextBox tbUrl) tbUrl.Text = node.Url ?? string.Empty;
            if (this.FindName("CheckBox_OpenAI_EnableAdvanced") is CheckBox cbAdv) cbAdv.IsChecked = node.EnableAdvanced;
            if (this.FindName("CheckBox_OpenAI_EnableStreaming") is CheckBox cbStream) cbStream.IsChecked = node.EnableStreaming;
            if (this.FindName("Slider_OpenAI_Temperature") is Slider slTemp)
            {
                slTemp.Value = node.Temperature;
                if (this.FindName("TextBlock_OpenAI_TemperatureValue") is TextBlock tv) tv.Text = node.Temperature.ToString("F2");
            }
            if (this.FindName("TextBox_OpenAI_MaxTokens") is TextBox tbMax) tbMax.Text = node.MaxTokens.ToString();
            _isUpdatingNodeDetails = false;
        }

        private void OpenAINodeDetail_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;

            if (sender is TextBox tb)
            {
                switch (tb.Name)
                {
                    case "TextBox_OpenAINodeName":
                        node.Name = tb.Text;
                        break;
                    case "TextBox_OpenAIUrl":
                        node.Url = tb.Text;
                        break;
                    case "TextBox_OpenAI_MaxTokens":
                        if (int.TryParse(tb.Text, out var mt)) node.MaxTokens = mt;
                        break;
                    case "TextBox_OpenAIApiKey_Plain":
                        node.ApiKey = tb.Text;
                        if (this.FindName("PasswordBox_OpenAIApiKey") is PasswordBox pb2 && pb2.Password != tb.Text)
                            pb2.Password = tb.Text;
                        break;
                }
                RefreshOpenAINodesList();
                ScheduleSecretSave();
            }
        }

        private void OpenAINodeDetail_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;
            if (sender is PasswordBox pb)
            {
                node.ApiKey = pb.Password;
                if (this.FindName("TextBox_OpenAIApiKey_Plain") is TextBox tbPlain && tbPlain.Text != pb.Password)
                    tbPlain.Text = pb.Password;
                RefreshOpenAINodesList();
                // 密钥输入采用去抖保存，避免每次按键都保存导致卡顿
                ScheduleSecretSave();
            }
        }

        private void OpenAINodeDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;
            if (sender is ComboBox cb && cb.Name == "ComboBox_OpenAIModel")
            {
                node.Model = cb.Text;
                RefreshOpenAINodesList();
                SaveSettings();
            }
        }

        private void OpenAINodeDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;
            if (sender is CheckBox cb)
            {
                if (cb.Name == "CheckBox_OpenAI_EnableAdvanced")
                {
                    node.EnableAdvanced = cb.IsChecked ?? false;
                    RefreshOpenAINodesList();
                    SaveSettings();
                }
                else if (cb.Name == "CheckBox_OpenAI_EnableStreaming")
                {
                    node.EnableStreaming = cb.IsChecked ?? false;
                    RefreshOpenAINodesList();
                    SaveSettings();
                }
            }
        }

        private void OpenAINodeDetail_TemperatureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;
            if (sender is Slider sl && sl.Name == "Slider_OpenAI_Temperature")
            {
                node.Temperature = sl.Value;
                if (this.FindName("TextBlock_OpenAI_TemperatureValue") is TextBlock tv) tv.Text = sl.Value.ToString("F2");
                RefreshOpenAINodesList();
                SaveSettings();
            }
        }

        // 绑定 OpenAI 节点详情控件事件（在窗口构造后调用一次也可，当前通过 XAML 绑定列表选择事件触发）
        private void EnsureOpenAINodeDetailHandlers()
        {
            if (this.FindName("TextBox_OpenAINodeName") is TextBox tbName) tbName.TextChanged += OpenAINodeDetail_TextChanged;
            if (this.FindName("PasswordBox_OpenAIApiKey") is PasswordBox pb) pb.PasswordChanged += OpenAINodeDetail_PasswordChanged;
            if (this.FindName("ComboBox_OpenAIModel") is ComboBox cbModel)
            {
                cbModel.SelectionChanged += OpenAINodeDetail_SelectionChanged;
                cbModel.LostFocus += OpenAIModel_LostFocus;
                cbModel.DropDownClosed += OpenAIModel_DropDownClosed;
                cbModel.KeyDown += OpenAIModel_KeyDown;
            }
            if (this.FindName("TextBox_OpenAIUrl") is TextBox tbUrl) { tbUrl.TextChanged += OpenAINodeDetail_TextChanged; tbUrl.LostFocus += Detail_TextBox_LostFocus; }
            if (this.FindName("CheckBox_OpenAI_EnableAdvanced") is CheckBox cbAdv) cbAdv.Click += OpenAINodeDetail_Click;
            if (this.FindName("CheckBox_OpenAI_EnableStreaming") is CheckBox cbStream) cbStream.Click += OpenAINodeDetail_Click;
            if (this.FindName("Slider_OpenAI_Temperature") is Slider slTemp) slTemp.ValueChanged += OpenAINodeDetail_TemperatureChanged;
            if (this.FindName("TextBox_OpenAI_MaxTokens") is TextBox tbMax) tbMax.TextChanged += OpenAINodeDetail_TextChanged;
            if (this.FindName("TextBox_OpenAIApiKey_Plain") is TextBox tbPlain) tbPlain.TextChanged += OpenAINodeDetail_TextChanged;

        }

        private void Detail_TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ScheduleAutoSave();
        }

        // 负载均衡复选框点击：写回设置并立即保存，使随机/轮询立刻生效
        private void LoadBalancing_CheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is CheckBox cb)
                {
                    if (cb.Name == "CheckBox_Gemini_EnableLoadBalancing")
                    {
                        _plugin.Settings.Gemini.EnableLoadBalancing = cb.IsChecked ?? false;
                    }
                    else if (cb.Name == "CheckBox_OpenAI_EnableLoadBalancing")
                    {
                        _plugin.Settings.OpenAI.EnableLoadBalancing = cb.IsChecked ?? false;
                        // 重置OpenAI轮换索引，从头开始轮询
                        _plugin.Settings.OpenAI.CurrentNodeIndex = -1;
                    }
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadBalancing] 切换失败: {ex.Message}");
            }
        }

        // ========== Gemini 渠道选择与详情回显 ==========
        private Setting.GeminiNodeSetting GetSelectedGeminiNode()
        {
            if (this.FindName("ListView_GeminiNodes") is ListView list && list.SelectedItem is Setting.GeminiNodeSetting node)
                return node;
            return null;
        }

        private void ListView_GeminiNodes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 确保详情控件事件已绑定（避免重复绑定造成多次触发，简单容错）
            EnsureGeminiNodeDetailHandlers();

            _isUpdatingNodeDetails = true;
            var node = GetSelectedGeminiNode();
            if (node == null) { _isUpdatingNodeDetails = false; return; }

            if (this.FindName("TextBox_GeminiNodeName") is TextBox tbName) tbName.Text = node.Name ?? string.Empty;
            if (this.FindName("PasswordBox_GeminiApiKey") is PasswordBox pb) pb.Password = node.ApiKey ?? string.Empty;
            if (this.FindName("TextBox_GeminiApiKey_Plain") is TextBox tbPlain) tbPlain.Text = node.ApiKey ?? string.Empty;
            if (this.FindName("ComboBox_GeminiNodeModel") is ComboBox cbModel) cbModel.Text = node.Model ?? string.Empty;
            if (this.FindName("TextBox_GeminiNodeUrl") is TextBox tbUrl) tbUrl.Text = node.Url ?? string.Empty;
            if (this.FindName("CheckBox_GeminiNode_EnableAdvanced") is CheckBox cbAdv) cbAdv.IsChecked = node.EnableAdvanced;
            if (this.FindName("CheckBox_GeminiNode_EnableStreaming") is CheckBox cbStream) cbStream.IsChecked = node.EnableStreaming;
            if (this.FindName("Slider_GeminiNode_Temperature") is Slider slTemp)
            {
                slTemp.Value = node.Temperature;
                if (this.FindName("TextBlock_GeminiNode_TemperatureValue") is TextBlock tv) tv.Text = node.Temperature.ToString("F2");
            }
            if (this.FindName("TextBox_GeminiNode_MaxTokens") is TextBox tbMax) tbMax.Text = (node.MaxTokens > 0 ? node.MaxTokens : _plugin.Settings.Gemini.MaxTokens).ToString();
            _isUpdatingNodeDetails = false;
        }

        private void GeminiNodeDetail_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedGeminiNode();
            if (node == null) return;

            if (sender is TextBox tb)
            {
                switch (tb.Name)
                {
                    case "TextBox_GeminiNodeName":
                        node.Name = tb.Text;
                        break;
                    case "TextBox_GeminiNodeUrl":
                        node.Url = tb.Text;
                        break;
                    case "TextBox_GeminiNode_MaxTokens":
                        if (int.TryParse(tb.Text, out var mt)) node.MaxTokens = mt;
                        break;
                    case "TextBox_GeminiApiKey_Plain":
                        node.ApiKey = tb.Text;
                        if (this.FindName("PasswordBox_GeminiApiKey") is PasswordBox pb2 && pb2.Password != tb.Text)
                            pb2.Password = tb.Text;
                        break;
                }
                ScheduleGeminiListRefresh();
                // Gemini 文本编辑采用去抖保存，避免每次键入都触发重型保存
                ScheduleSecretSave();
            }
        }

        private void GeminiNodeDetail_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedGeminiNode();
            if (node == null) return;
            if (sender is PasswordBox pb)
            {
                node.ApiKey = pb.Password;
                if (this.FindName("TextBox_GeminiApiKey_Plain") is TextBox tbPlain && tbPlain.Text != pb.Password)
                    tbPlain.Text = pb.Password;
                ScheduleGeminiListRefresh();
                // 密钥输入采用去抖保存，避免每次按键都保存导致卡顿
                ScheduleSecretSave();
            }
        }

        private void GeminiNodeDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedGeminiNode();
            if (node == null) return;
            if (sender is ComboBox cb && cb.Name == "ComboBox_GeminiNodeModel")
            {
                cb.Dispatcher.BeginInvoke(new Action(() =>
                {
                    node.Model = cb.Text;
                    ScheduleGeminiListRefresh();
                    ScheduleAutoSave();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void GeminiNodeDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedGeminiNode();
            if (node == null) return;
            if (sender is CheckBox cb)
            {
                if (cb.Name == "CheckBox_GeminiNode_EnableAdvanced")
                {
                    node.EnableAdvanced = cb.IsChecked ?? false;
                    ScheduleGeminiListRefresh();
                    ScheduleAutoSave();
                }
                else if (cb.Name == "CheckBox_GeminiNode_EnableStreaming")
                {
                    node.EnableStreaming = cb.IsChecked ?? false;
                    ScheduleGeminiListRefresh();
                    ScheduleAutoSave();
                }
            }
        }

        private void GeminiNodeDetail_TemperatureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedGeminiNode();
            if (node == null) return;
            if (sender is Slider sl && sl.Name == "Slider_GeminiNode_Temperature")
            {
                node.Temperature = sl.Value;
                if (this.FindName("TextBlock_GeminiNode_TemperatureValue") is TextBlock tv) tv.Text = sl.Value.ToString("F2");
                ScheduleGeminiListRefresh();
                ScheduleAutoSave();
            }
        }

        private void EnsureGeminiNodeDetailHandlers()
        {
            if (this.FindName("TextBox_GeminiNodeName") is TextBox tbName) tbName.TextChanged += GeminiNodeDetail_TextChanged;
            if (this.FindName("PasswordBox_GeminiApiKey") is PasswordBox pb) pb.PasswordChanged += GeminiNodeDetail_PasswordChanged;
            if (this.FindName("ComboBox_GeminiNodeModel") is ComboBox cbModel) cbModel.SelectionChanged += GeminiNodeDetail_SelectionChanged;
            if (this.FindName("TextBox_GeminiNodeUrl") is TextBox tbUrl) { tbUrl.TextChanged += GeminiNodeDetail_TextChanged; tbUrl.LostFocus += Detail_TextBox_LostFocus; }
            if (this.FindName("CheckBox_GeminiNode_EnableAdvanced") is CheckBox cbAdv) cbAdv.Click += GeminiNodeDetail_Click;
            if (this.FindName("CheckBox_GeminiNode_EnableStreaming") is CheckBox cbStream) cbStream.Click += GeminiNodeDetail_Click;
            if (this.FindName("Slider_GeminiNode_Temperature") is Slider slTemp) slTemp.ValueChanged += GeminiNodeDetail_TemperatureChanged;
            if (this.FindName("TextBox_GeminiNode_MaxTokens") is TextBox tbMax) tbMax.TextChanged += GeminiNodeDetail_TextChanged;
            if (this.FindName("TextBox_GeminiApiKey_Plain") is TextBox tbPlain) tbPlain.TextChanged += GeminiNodeDetail_TextChanged;

        }

        // 小眼睛：OpenAI API Key 显示/隐藏切换（供 XAML Click 使用）
        private void Button_Toggle_OpenAIApiKey_Click(object sender, RoutedEventArgs e)
        {
            // 抑制节点详情事件与自动保存，避免在切换可见性时触发重型保存导致卡顿
            var prevFlag = _isUpdatingNodeDetails;
            _isUpdatingNodeDetails = true;
            try
            {
                if (this.FindName("PasswordBox_OpenAIApiKey") is PasswordBox pbx && this.FindName("TextBox_OpenAIApiKey_Plain") is TextBox tbx)
                {
                    if (pbx.Visibility == Visibility.Visible)
                    {
                        tbx.Text = pbx.Password;
                        pbx.Visibility = Visibility.Collapsed;
                        tbx.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        pbx.Password = tbx.Text;
                        tbx.Visibility = Visibility.Collapsed;
                        pbx.Visibility = Visibility.Visible;
                    }
                }
            }
            finally
            {
                _isUpdatingNodeDetails = prevFlag;
            }
        }

        // 小眼睛：Gemini API Key 显示/隐藏切换（供 XAML Click 使用）
        private void Button_Toggle_GeminiApiKey_Click(object sender, RoutedEventArgs e)
        {
            // 抑制节点详情事件与自动保存，避免在切换可见性时触发重型保存导致卡顿
            var prevFlag = _isUpdatingNodeDetails;
            _isUpdatingNodeDetails = true;
            try
            {
                if (this.FindName("PasswordBox_GeminiApiKey") is PasswordBox pbx && this.FindName("TextBox_GeminiApiKey_Plain") is TextBox tbx)
                {
                    if (pbx.Visibility == Visibility.Visible)
                    {
                        tbx.Text = pbx.Password;
                        pbx.Visibility = Visibility.Collapsed;
                        tbx.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        pbx.Password = tbx.Text;
                        tbx.Visibility = Visibility.Collapsed;
                        pbx.Visibility = Visibility.Visible;
                    }
                }
            }
            finally
            {
                _isUpdatingNodeDetails = prevFlag;
            }
        }

        // 判断事件源是否在 Gemini 渠道列表内，用于在列表级事件中对 Gemini 文本编辑应用去抖保存
        private bool IsInGeminiList(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Name == "ListView_GeminiNodes")
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// 让渠道列表内的控件（复选框/文本框/下拉/滑条）任何变更都触发立即保存
        /// </summary>
        private void AttachImmediateSaveForChannelLists()
        {
            // OpenAI 渠道列表
            if (this.FindName("ListView_OpenAINodes") is ListView lvOpenAI)
            {
                lvOpenAI.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(ChannelList_TextChanged), true);
                lvOpenAI.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(ChannelList_PasswordChanged), true);
                lvOpenAI.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(ChannelList_SelectionChanged), true);
                lvOpenAI.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(ChannelList_Click), true);
                lvOpenAI.AddHandler(Slider.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(ChannelList_ValueChanged), true);
            }
            // Gemini 渠道列表
            if (this.FindName("ListView_GeminiNodes") is ListView lvGemini)
            {
                lvGemini.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(ChannelList_TextChanged), true);
                lvGemini.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(ChannelList_PasswordChanged), true);
                lvGemini.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(ChannelList_SelectionChanged), true);
                lvGemini.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(ChannelList_Click), true);
                lvGemini.AddHandler(Slider.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(ChannelList_ValueChanged), true);
            }
        }

        // 统一转发为保存：OpenAI/Gemini 列表中的 TextBox 使用去抖，体验一致
        private void ChannelList_TextChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleSecretSave();
        }
        private void ChannelList_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // 统一去抖保存：OpenAI/Gemini 列表密码输入，体验一致
            ScheduleSecretSave();
        }
        private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleAutoSave(true);
        private void ChannelList_Click(object sender, RoutedEventArgs e) => ScheduleAutoSave(true);
        private void ChannelList_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ScheduleAutoSave(true);

        // 处理可编辑 OpenAI 模型下拉框的文本提交（失焦/关闭下拉/回车）
        private void OpenAIModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingNodeDetails) return;
            var node = GetSelectedOpenAINode();
            if (node == null) return;
            if (sender is ComboBox cb)
            {
                node.Model = cb.Text;
                RefreshOpenAINodesList();
                SaveSettings();
            }
        }

        private void OpenAIModel_DropDownClosed(object sender, EventArgs e)
        {
            OpenAIModel_LostFocus(sender, null);
        }

        private void OpenAIModel_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenAIModel_LostFocus(sender, null);
                e.Handled = true;
            }
        }

        private void ComboBox_ASR_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateASRProviderPanel();
        }

        private void UpdateASRProviderPanel()
        {
            try
            {
                var comboBox = FindName("ComboBox_ASR_Provider") as ComboBox;
                if (comboBox?.SelectedItem is ComboBoxItem selectedItem)
                {
                    var provider = selectedItem.Tag?.ToString();
                    System.Diagnostics.Debug.WriteLine($"[ASR Provider] 切换到提供商: {provider}");

                    // 先隐藏所有面板
                    var openAIPanel = FindName("Panel_ASR_OpenAI") as StackPanel;
                    var sonioxPanel = FindName("Panel_ASR_Soniox") as StackPanel;
                    var freePanel = FindName("Panel_ASR_Free") as StackPanel;

                    if (openAIPanel != null) openAIPanel.Visibility = Visibility.Collapsed;
                    if (sonioxPanel != null) sonioxPanel.Visibility = Visibility.Collapsed;
                    if (freePanel != null) freePanel.Visibility = Visibility.Collapsed;

                    // 显示对应的面板
                    switch (provider)
                    {
                        case "OpenAI":
                            if (openAIPanel != null)
                            {
                                openAIPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[ASR Provider] 显示OpenAI面板");
                            }
                            break;
                        case "Soniox":
                            if (sonioxPanel != null)
                            {
                                sonioxPanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[ASR Provider] 显示Soniox面板");
                            }
                            break;
                        case "Free":
                            if (freePanel != null)
                            {
                                freePanel.Visibility = Visibility.Visible;
                                System.Diagnostics.Debug.WriteLine("[ASR Provider] 显示Free面板");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ASR Provider] 切换提供商时出错: {ex.Message}");
            }
        }

        private void Button_ASR_Test_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先保存当前设置
                SaveSettings();
                
                // 显示语音输入窗口
                _plugin.ShowVoiceInputWindow();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error testing ASR: {ex.Message}");
                MessageBox.Show($"测试语音输入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<Setting.SonioxModelInfo> _sonioxModels = new List<Setting.SonioxModelInfo>();

        private async void Button_ASR_Soniox_RefreshModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            try
            {
                button.IsEnabled = false;
                button.Content = "刷新中...";

                // 先保存当前设置以获取最新的 API Key 和 BaseUrl
                SaveSettings();

                // 创建临时 ASR 服务来获取模型列表
                var asrService = new Utils.ASRService(_plugin.Settings);
                _sonioxModels = await asrService.FetchSonioxModels();
                asrService.Dispose();

                if (_sonioxModels.Count == 0)
                {
                    MessageBox.Show("未能获取模型列表，请检查 API Key 和网络连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 更新模型下拉框
                var comboBox = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Model");
                var currentModel = comboBox.Text;
                comboBox.Items.Clear();

                foreach (var model in _sonioxModels)
                {
                    var item = new ComboBoxItem
                    {
                        Content = $"{model.Id} ({model.Name})",
                        Tag = model.Id
                    };
                    comboBox.Items.Add(item);
                }

                // 恢复之前选择的模型
                bool found = false;
                foreach (ComboBoxItem item in comboBox.Items)
                {
                    if (item.Tag?.ToString() == currentModel)
                    {
                        comboBox.SelectedItem = item;
                        found = true;
                        break;
                    }
                }

                if (!found && comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }

                MessageBox.Show($"成功获取 {_sonioxModels.Count} 个模型", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing Soniox models: {ex.Message}");
                MessageBox.Show($"刷新模型列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = "刷新模型列表";
            }
        }

        private void ComboBox_ASR_Soniox_Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var comboBox = sender as ComboBox;
                if (comboBox == null) return;

                string selectedModelId = "";
                if (comboBox.SelectedItem is ComboBoxItem item)
                {
                    selectedModelId = item.Tag?.ToString() ?? "";
                }
                else
                {
                    selectedModelId = comboBox.Text;
                }

                if (string.IsNullOrEmpty(selectedModelId)) return;

                // 查找对应的模型信息
                var modelInfo = _sonioxModels.FirstOrDefault(m => m.Id == selectedModelId);
                if (modelInfo == null || modelInfo.Languages.Count == 0) return;

                // 更新语言下拉框
                var languageComboBox = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Language");
                if (languageComboBox == null) return;

                var currentLanguage = "";
                if (languageComboBox.SelectedItem is ComboBoxItem currentItem)
                {
                    currentLanguage = currentItem.Tag?.ToString() ?? "";
                }

                languageComboBox.Items.Clear();

                foreach (var lang in modelInfo.Languages)
                {
                    var langItem = new ComboBoxItem
                    {
                        Content = $"{lang.Code} ({lang.Name})",
                        Tag = lang.Code
                    };
                    languageComboBox.Items.Add(langItem);
                }

                // 尝试恢复之前选择的语言
                bool found = false;
                foreach (ComboBoxItem langItem in languageComboBox.Items)
                {
                    if (langItem.Tag?.ToString() == currentLanguage)
                    {
                        languageComboBox.SelectedItem = langItem;
                        found = true;
                        break;
                    }
                }

                // 如果没找到，尝试选择英语或中文
                if (!found)
                {
                    foreach (ComboBoxItem langItem in languageComboBox.Items)
                    {
                        var code = langItem.Tag?.ToString() ?? "";
                        if (code == "en" || code == "zh")
                        {
                            languageComboBox.SelectedItem = langItem;
                            found = true;
                            break;
                        }
                    }
                }

                // 如果还是没找到，选择第一个
                if (!found && languageComboBox.Items.Count > 0)
                {
                    languageComboBox.SelectedIndex = 0;
                }

                // 触发保存
                Control_SelectionChanged(sender, e);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating Soniox language list: {ex.Message}");
            }
        }

        private async void InitializeSonioxLanguages()
        {
            try
            {
                var languageComboBox = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Language");
                if (languageComboBox == null) return;

                // 如果已经有 API Key，尝试自动加载模型列表
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.ASR.Soniox.ApiKey))
                {
                    var asrService = new Utils.ASRService(_plugin.Settings);
                    _sonioxModels = await asrService.FetchSonioxModels();
                    asrService.Dispose();

                    if (_sonioxModels.Count > 0)
                    {
                        // 更新模型下拉框
                        var modelComboBox = (ComboBox)this.FindName("ComboBox_ASR_Soniox_Model");
                        if (modelComboBox != null)
                        {
                            var currentModel = modelComboBox.Text;
                            modelComboBox.Items.Clear();

                            foreach (var model in _sonioxModels)
                            {
                                var item = new ComboBoxItem
                                {
                                    Content = $"{model.Id} ({model.Name})",
                                    Tag = model.Id
                                };
                                modelComboBox.Items.Add(item);
                            }

                            // 恢复之前选择的模型
                            bool found = false;
                            foreach (ComboBoxItem item in modelComboBox.Items)
                            {
                                if (item.Tag?.ToString() == currentModel)
                                {
                                    modelComboBox.SelectedItem = item;
                                    found = true;
                                    break;
                                }
                            }

                            if (!found && !string.IsNullOrEmpty(currentModel))
                            {
                                modelComboBox.Text = currentModel;
                            }
                        }

                        // 根据当前选择的模型更新语言列表
                        var selectedModelId = _plugin.Settings.ASR.Soniox.Model;
                        var modelInfo = _sonioxModels.FirstOrDefault(m => m.Id == selectedModelId);
                        
                        if (modelInfo != null && modelInfo.Languages.Count > 0)
                        {
                            languageComboBox.Items.Clear();

                            foreach (var lang in modelInfo.Languages)
                            {
                                var langItem = new ComboBoxItem
                                {
                                    Content = $"{lang.Code} ({lang.Name})",
                                    Tag = lang.Code
                                };
                                languageComboBox.Items.Add(langItem);
                            }

                            // 尝试选中保存的语言
                            if (!string.IsNullOrEmpty(_plugin.Settings.ASR.Language))
                            {
                                bool found = false;
                                foreach (ComboBoxItem item in languageComboBox.Items)
                                {
                                    if (item.Tag?.ToString() == _plugin.Settings.ASR.Language)
                                    {
                                        languageComboBox.SelectedItem = item;
                                        found = true;
                                        break;
                                    }
                                }

                                // 如果没找到，尝试选择英语或中文
                                if (!found)
                                {
                                    foreach (ComboBoxItem item in languageComboBox.Items)
                                    {
                                        var code = item.Tag?.ToString() ?? "";
                                        if (code == "en" || code == "zh")
                                        {
                                            languageComboBox.SelectedItem = item;
                                            found = true;
                                            break;
                                        }
                                    }
                                }

                                // 如果还是没找到，选择第一个
                                if (!found && languageComboBox.Items.Count > 0)
                                {
                                    languageComboBox.SelectedIndex = 0;
                                }
                            }
                        }

                        Logger.Log($"ASR: Initialized Soniox with {_sonioxModels.Count} models");
                    }
                }
                else
                {
                    // 没有 API Key，显示提示信息
                    languageComboBox.Items.Clear();
                    var placeholderItem = new ComboBoxItem
                    {
                        Content = "请先配置 API Key 并刷新模型列表",
                        IsEnabled = false
                    };
                    languageComboBox.Items.Add(placeholderItem);
                    languageComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error initializing Soniox languages: {ex.Message}");
                // 静默失败，用户可以手动点击刷新按钮
            }
        }

        private void LoadRecordingDevices()
        {
            try
            {
                var comboBox = (ComboBox)this.FindName("ComboBox_ASR_RecordingDevice");
                if (comboBox == null) return;

                comboBox.Items.Clear();

                var deviceCount = NAudio.Wave.WaveInEvent.DeviceCount;
                
                if (deviceCount == 0)
                {
                    var item = new ComboBoxItem
                    {
                        Content = "未找到录音设备",
                        Tag = 0,
                        IsEnabled = false
                    };
                    comboBox.Items.Add(item);
                    comboBox.SelectedIndex = 0;
                    return;
                }

                for (int i = 0; i < deviceCount; i++)
                {
                    var capabilities = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                    var item = new ComboBoxItem
                    {
                        Content = $"设备 #{i}: {capabilities.ProductName}",
                        Tag = i
                    };
                    comboBox.Items.Add(item);

                    if (i == _plugin.Settings.ASR.RecordingDeviceNumber)
                    {
                        comboBox.SelectedItem = item;
                    }
                }

                if (comboBox.SelectedItem == null && comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }

                Logger.Log($"ASR Settings: Loaded {deviceCount} recording device(s)");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading recording devices: {ex.Message}");
            }
        }

        private void Button_ASR_RefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadRecordingDevices();
                MessageBox.Show("设备列表已刷新", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing devices: {ex.Message}");
                MessageBox.Show($"刷新设备列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_ASR_CaptureHotkey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Log("ASR: Opening hotkey capture window...");
                
                // 创建并显示捕获窗口
                var captureWindow = new HotkeyCapture
                {
                    Owner = this
                };
                
                var result = captureWindow.ShowDialog();
                
                if (result == true && captureWindow.IsCaptured)
                {
                    // 保存捕获的快捷键
                    _plugin.Settings.ASR.HotkeyModifiers = captureWindow.CapturedModifiers;
                    _plugin.Settings.ASR.HotkeyKey = captureWindow.CapturedKey;
                    
                    Logger.Log($"ASR: Hotkey captured - Modifiers: {captureWindow.CapturedModifiers}, Key: {captureWindow.CapturedKey}");
                    
                    // 保存设置
                    SaveSettings();
                    
                    // 更新显示
                    UpdateHotkeyDisplay();
                    
                    Logger.Log("ASR: Hotkey saved successfully");
                }
                else
                {
                    Logger.Log("ASR: Hotkey capture cancelled");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error capturing hotkey: {ex.Message}");
                MessageBox.Show($"捕获快捷键失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_ASR_ResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Log("ASR: Resetting hotkey to default...");
                
                // 还原为默认快捷键: Win + Alt + V
                _plugin.Settings.ASR.HotkeyModifiers = "Win+Alt";
                _plugin.Settings.ASR.HotkeyKey = "V";
                
                // 保存设置
                SaveSettings();
                
                // 更新显示
                UpdateHotkeyDisplay();
                
                Logger.Log("ASR: Hotkey reset to default (Win+Alt+V)");
                
                MessageBox.Show(
                    LanguageHelper.Get("ASR.HotkeyResetSuccess", _plugin.Settings.Language) ?? "快捷键已还原为默认值: Win + Alt + V",
                    LanguageHelper.Get("Success", _plugin.Settings.Language) ?? "成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"ASR: Error resetting hotkey: {ex.Message}");
                MessageBox.Show($"还原快捷键失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateHotkeyDisplay()
        {
            var textBox = (TextBox)this.FindName("TextBox_ASR_HotkeyDisplay");
            if (textBox != null)
            {
                var modifiers = _plugin.Settings.ASR.HotkeyModifiers;
                var key = _plugin.Settings.ASR.HotkeyKey;
                
                if (string.IsNullOrEmpty(modifiers) && string.IsNullOrEmpty(key))
                {
                    textBox.Text = LanguageHelper.Get("ASR.NoHotkey", _plugin.Settings.Language) ?? "未设置";
                }
                else if (string.IsNullOrEmpty(modifiers))
                {
                    textBox.Text = key;
                }
                else
                {
                    textBox.Text = $"{modifiers} + {key}";
                }
            }
        }
    }
}
