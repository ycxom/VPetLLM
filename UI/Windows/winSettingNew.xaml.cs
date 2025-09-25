using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        private bool _hasUnsavedChanges = false;
        private readonly object _saveLock = new object();

        // TTS服务
        private TTSService? _ttsService;

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
                Button_RefreshPlugins_Click(this, new RoutedEventArgs());
            };
            ((Slider)this.FindName("Slider_Ollama_Temperature")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_OpenAI_Temperature")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_Gemini_Temperature")).ValueChanged += Control_ValueChanged;
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
            ((TextBox)this.FindName("TextBox_OpenAIApiKey")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_OpenAIModel")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_OpenAIUrl")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_GeminiApiKey")).TextChanged += Control_TextChanged;

            ((ComboBox)this.FindName("ComboBox_GeminiModel")).SelectionChanged += Control_SelectionChanged;

            ((TextBox)this.FindName("TextBox_GeminiUrl")).TextChanged += Control_TextChanged;

            ((CheckBox)this.FindName("CheckBox_KeepContext")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableChatHistory")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_SeparateChatByProvider")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableAction")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableBuy")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableState")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableActionExecution")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableMove")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableTime")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_LogAutoScroll")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_MaxLogCount")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_Ollama_MaxTokens")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_OpenAI_EnableAdvanced")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_OpenAI_MaxTokens")).TextChanged += Control_TextChanged;
            ((CheckBox)this.FindName("CheckBox_Gemini_EnableAdvanced")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Free_EnableAdvanced")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_Gemini_MaxTokens")).TextChanged += Control_TextChanged;
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
            ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).Click += Control_Click;

            // Plugin Store Proxy settings
            ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).TextChanged += Control_TextChanged;

            // TTS settings
            ((CheckBox)this.FindName("CheckBox_TTS_IsEnabled")).Click += Control_Click;
            ((ComboBox)this.FindName("ComboBox_TTS_Provider")).SelectionChanged += Control_SelectionChanged;
            ((CheckBox)this.FindName("CheckBox_TTS_OnlyPlayAIResponse")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_TTS_URL_BaseUrl")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_URL_Voice")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_URL_RequestMethod")).SelectionChanged += Control_SelectionChanged;
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_BaseUrl")).TextChanged += Control_TextChanged;
            ((TextBox)this.FindName("TextBox_TTS_OpenAI_ApiKey")).TextChanged += Control_TextChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Model")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Voice")).SelectionChanged += Control_SelectionChanged;
            ((ComboBox)this.FindName("ComboBox_TTS_OpenAI_Format")).SelectionChanged += Control_SelectionChanged;

            // DIY TTS 按钮事件绑定 - 已在 XAML 中绑定，无需重复绑定

            ((Button)this.FindName("Button_RefreshPlugins")).Click += Button_RefreshPlugins_Click;

            // 初始化自动保存定时器
            InitializeAutoSaveTimer();
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
                    ((TextBlock)this.FindName("TextBlock_Gemini_TemperatureValue")).Text = slider.Value.ToString("F2");
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
                // 立即保存语言设置
                SaveSettings();
                // 重新加载语言资源
                LanguageHelper.ReloadLanguages();
                // 立即更新UI（同步调用）
                UpdateUIForLanguage();
                // 使用Dispatcher确保在下一个UI周期再次更新界面，以防有遗漏
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateUIForLanguage();
                    
                    // 重新初始化TouchFeedbackSettingsControl以确保语言更新
                    InitializeTouchFeedbackSettings();
                    
                    // 强制刷新插件列表的列标题
                    if (FindName("DataGrid_Plugins") is DataGrid dataGridPluginsAsync)
                    {
                        var langCodeAsync = _plugin.Settings.Language;
                        if (dataGridPluginsAsync.Columns.Count > 0)
                            dataGridPluginsAsync.Columns[0].Header = LanguageHelper.Get("Plugin.Enabled", langCodeAsync) ?? "启用";
                        if (dataGridPluginsAsync.Columns.Count > 1)
                            dataGridPluginsAsync.Columns[1].Header = LanguageHelper.Get("Plugin.Status", langCodeAsync);
                        if (dataGridPluginsAsync.Columns.Count > 2)
                            dataGridPluginsAsync.Columns[2].Header = LanguageHelper.Get("Plugin.Name", langCodeAsync) ?? "插件信息";
                        if (dataGridPluginsAsync.Columns.Count > 3)
                            dataGridPluginsAsync.Columns[3].Header = LanguageHelper.Get("Plugin.Description", langCodeAsync) ?? "描述";
                        if (dataGridPluginsAsync.Columns.Count > 4)
                            dataGridPluginsAsync.Columns[4].Header = LanguageHelper.Get("Plugin.Action", langCodeAsync) ?? "操作";

                        // 重新刷新插件列表以更新多语言文本
                        Button_RefreshPlugins_Click(this, new RoutedEventArgs());
                    }

                    // 强制更新Tab标题
                    if (FindName("Tab_Plugin") is TabItem tabPlugin)
                    {
                        var langCodeForTab = _plugin.Settings.Language;
                        tabPlugin.Header = LanguageHelper.Get("Settings.Plugin", langCodeForTab) ?? "插件";
                    }

                    // 强制刷新整个窗口
                    this.UpdateLayout();
                }), System.Windows.Threading.DispatcherPriority.Background);
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
                    "TextBox_OllamaUrl", "TextBox_OpenAIUrl", "TextBox_GeminiUrl"
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
            ((TextBox)this.FindName("TextBox_OllamaUrl")).Text = _plugin.Settings.Ollama.Url;
            ((ComboBox)this.FindName("ComboBox_OllamaModel")).Text = _plugin.Settings.Ollama.Model;
            ((TextBox)this.FindName("TextBox_OpenAIApiKey")).Text = _plugin.Settings.OpenAI.ApiKey;
            ((ComboBox)this.FindName("ComboBox_OpenAIModel")).Text = _plugin.Settings.OpenAI.Model;
            ((TextBox)this.FindName("TextBox_OpenAIUrl")).Text = _plugin.Settings.OpenAI.Url;
            ((TextBox)this.FindName("TextBox_GeminiApiKey")).Text = _plugin.Settings.Gemini.ApiKey;
            ((ComboBox)this.FindName("ComboBox_GeminiModel")).Text = _plugin.Settings.Gemini.Model;
            ((TextBox)this.FindName("TextBox_GeminiUrl")).Text = _plugin.Settings.Gemini.Url;

            ((CheckBox)this.FindName("CheckBox_KeepContext")).IsChecked = _plugin.Settings.KeepContext;
            ((CheckBox)this.FindName("CheckBox_EnableChatHistory")).IsChecked = _plugin.Settings.EnableChatHistory;
            ((CheckBox)this.FindName("CheckBox_SeparateChatByProvider")).IsChecked = _plugin.Settings.SeparateChatByProvider;
            ((CheckBox)this.FindName("CheckBox_EnableAction")).IsChecked = _plugin.Settings.EnableAction;
            ((CheckBox)this.FindName("CheckBox_EnableBuy")).IsChecked = _plugin.Settings.EnableBuy;
            ((CheckBox)this.FindName("CheckBox_EnableState")).IsChecked = _plugin.Settings.EnableState;
            ((CheckBox)this.FindName("CheckBox_EnableActionExecution")).IsChecked = _plugin.Settings.EnableActionExecution;
            ((CheckBox)this.FindName("CheckBox_EnableMove")).IsChecked = _plugin.Settings.EnableMove;
            ((CheckBox)this.FindName("CheckBox_EnableTime")).IsChecked = _plugin.Settings.EnableTime;
            ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).IsChecked = _plugin.Settings.EnableHistoryCompression;
            ((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).Text = _plugin.Settings.HistoryCompressionThreshold.ToString();
            ((CheckBox)this.FindName("CheckBox_LogAutoScroll")).IsChecked = _plugin.Settings.LogAutoScroll;
            ((TextBox)this.FindName("TextBox_MaxLogCount")).Text = _plugin.Settings.MaxLogCount.ToString();
            ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
            ((CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced")).IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            ((Slider)this.FindName("Slider_Ollama_Temperature")).Value = _plugin.Settings.Ollama.Temperature;
            ((TextBlock)this.FindName("TextBlock_Ollama_TemperatureValue")).Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_Ollama_MaxTokens")).Text = _plugin.Settings.Ollama.MaxTokens.ToString();
            ((CheckBox)this.FindName("CheckBox_OpenAI_EnableAdvanced")).IsChecked = _plugin.Settings.OpenAI.EnableAdvanced;
            ((Slider)this.FindName("Slider_OpenAI_Temperature")).Value = _plugin.Settings.OpenAI.Temperature;
            ((TextBlock)this.FindName("TextBlock_OpenAI_TemperatureValue")).Text = _plugin.Settings.OpenAI.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_OpenAI_MaxTokens")).Text = _plugin.Settings.OpenAI.MaxTokens.ToString();
            ((CheckBox)this.FindName("CheckBox_Gemini_EnableAdvanced")).IsChecked = _plugin.Settings.Gemini.EnableAdvanced;
            ((Slider)this.FindName("Slider_Gemini_Temperature")).Value = _plugin.Settings.Gemini.Temperature;
            ((TextBlock)this.FindName("TextBlock_Gemini_TemperatureValue")).Text = _plugin.Settings.Gemini.Temperature.ToString("F2");
            ((TextBox)this.FindName("TextBox_Gemini_MaxTokens")).Text = _plugin.Settings.Gemini.MaxTokens.ToString();
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

            // 初始化TTS服务
            _ttsService = new TTSService(_plugin.Settings.TTS, _plugin.Settings.Proxy);
        }

        private void SaveSettings()
        {
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
            var geminiUrlTextBox = (TextBox)this.FindName("TextBox_GeminiUrl");

            var keepContextCheckBox = (CheckBox)this.FindName("CheckBox_KeepContext");
            var enableChatHistoryCheckBox = (CheckBox)this.FindName("CheckBox_EnableChatHistory");
            var separateChatByProviderCheckBox = (CheckBox)this.FindName("CheckBox_SeparateChatByProvider");
            var enableActionCheckBox = (CheckBox)this.FindName("CheckBox_EnableAction");
            var enableBuyCheckBox = (CheckBox)this.FindName("CheckBox_EnableBuy");
            var enableStateCheckBox = (CheckBox)this.FindName("CheckBox_EnableState");
            var enableActionExecutionCheckBox = (CheckBox)this.FindName("CheckBox_EnableActionExecution");
            var enableMoveCheckBox = (CheckBox)this.FindName("CheckBox_EnableMove");
            var enableTimeCheckBox = (CheckBox)this.FindName("CheckBox_EnableTime");
            var logAutoScrollCheckBox = (CheckBox)this.FindName("CheckBox_LogAutoScroll");
            var maxLogCountTextBox = (TextBox)this.FindName("TextBox_MaxLogCount");
            var ollamaEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_Ollama_EnableAdvanced");
            var ollamaTemperatureSlider = (Slider)this.FindName("Slider_Ollama_Temperature");
            var ollamaMaxTokensTextBox = (TextBox)this.FindName("TextBox_Ollama_MaxTokens");
            var openAIEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_OpenAI_EnableAdvanced");
            var openAITemperatureSlider = (Slider)this.FindName("Slider_OpenAI_Temperature");
            var openAIMaxTokensTextBox = (TextBox)this.FindName("TextBox_OpenAI_MaxTokens");
            var geminiEnableAdvancedCheckBox = (CheckBox)this.FindName("CheckBox_Gemini_EnableAdvanced");
            var geminiTemperatureSlider = (Slider)this.FindName("Slider_Gemini_Temperature");
            var geminiMaxTokensTextBox = (TextBox)this.FindName("TextBox_Gemini_MaxTokens");
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
            _plugin.Settings.OpenAI.ApiKey = openAIApiKeyTextBox.Text;
            _plugin.Settings.OpenAI.Model = openAIModelComboBox.Text;
            _plugin.Settings.OpenAI.Url = openAIUrlTextBox.Text;
            _plugin.Settings.Gemini.ApiKey = geminiApiKeyTextBox.Text;
            _plugin.Settings.Gemini.Model = geminiModelComboBox.Text;
            _plugin.Settings.Gemini.Url = geminiUrlTextBox.Text;

            _plugin.Settings.KeepContext = keepContextCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableChatHistory = enableChatHistoryCheckBox.IsChecked ?? true;
            _plugin.Settings.SeparateChatByProvider = separateChatByProviderCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableAction = enableActionCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableBuy = enableBuyCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableState = enableStateCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableActionExecution = enableActionExecutionCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableMove = enableMoveCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableTime = enableTimeCheckBox.IsChecked ?? true;
            _plugin.Settings.EnableHistoryCompression = ((CheckBox)this.FindName("CheckBox_EnableHistoryCompression")).IsChecked ?? false;
            if (int.TryParse(((TextBox)this.FindName("TextBox_HistoryCompressionThreshold")).Text, out int historyCompressionThreshold))
                _plugin.Settings.HistoryCompressionThreshold = historyCompressionThreshold;
            _plugin.Settings.LogAutoScroll = logAutoScrollCheckBox.IsChecked ?? true;
            if (int.TryParse(maxLogCountTextBox.Text, out int maxLogCount))
                _plugin.Settings.MaxLogCount = maxLogCount;
            _plugin.Settings.Ollama.EnableAdvanced = ollamaEnableAdvancedCheckBox.IsChecked ?? false;
            _plugin.Settings.Ollama.Temperature = ollamaTemperatureSlider.Value;
            if (int.TryParse(ollamaMaxTokensTextBox.Text, out int ollamaMaxTokens))
                _plugin.Settings.Ollama.MaxTokens = ollamaMaxTokens;
            _plugin.Settings.OpenAI.EnableAdvanced = openAIEnableAdvancedCheckBox.IsChecked ?? false;
            _plugin.Settings.OpenAI.Temperature = openAITemperatureSlider.Value;
            if (int.TryParse(openAIMaxTokensTextBox.Text, out int openAIMaxTokens))
                _plugin.Settings.OpenAI.MaxTokens = openAIMaxTokens;
            _plugin.Settings.Gemini.EnableAdvanced = geminiEnableAdvancedCheckBox.IsChecked ?? false;
            _plugin.Settings.Gemini.Temperature = geminiTemperatureSlider.Value;
            if (int.TryParse(geminiMaxTokensTextBox.Text, out int geminiMaxTokens))
                _plugin.Settings.Gemini.MaxTokens = geminiMaxTokens;
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
            _plugin.Settings.Proxy.ForMcp = ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForPlugin = ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).IsChecked ?? true;

            // Plugin Store Proxy settings
            _plugin.Settings.PluginStore.UseProxy = ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).IsChecked ?? true;
            _plugin.Settings.PluginStore.ProxyUrl = ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).Text;

            // TTS settings
            _plugin.Settings.TTS.IsEnabled = ((CheckBox)this.FindName("CheckBox_TTS_IsEnabled")).IsChecked ?? false;
            _plugin.Settings.TTS.OnlyPlayAIResponse = ((CheckBox)this.FindName("CheckBox_TTS_OnlyPlayAIResponse")).IsChecked ?? true;
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

            // 更新TTS服务设置
            _ttsService?.UpdateSettings(_plugin.Settings.TTS, _plugin.Settings.Proxy);
            _plugin.UpdateTTSService();

            _plugin.Settings.Save();

            if (oldProvider != newProvider)
            {
                var oldHistory = _plugin.ChatCore.GetChatHistory();
                IChatCore newChatCore = newProvider switch
                {
                    Setting.LLMType.Ollama => new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.OpenAI => new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Gemini => new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
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
                    Setting.LLMType.Gemini => new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
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
                // 确保在UI线程上停止动画和重置按钮状态
                Dispatcher.Invoke(() =>
                {
                    StopButtonLoadingAnimation(button);

                    // 额外的按钮状态重置
                    if (button != null)
                    {
                        button.IsEnabled = true;
                        button.UpdateLayout();
                    }
                });
            }
        }

        private async void Button_RefreshOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                var openAICore = new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
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
                var geminiCore = new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = await Task.Run(() => geminiCore.RefreshModels());
                ((ComboBox)this.FindName("ComboBox_GeminiModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_GeminiModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_GeminiModel")).SelectedIndex = 0;
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

                Dispatcher.Invoke(() =>
                {
                    ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = null;
                    ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
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
                await Task.Run(() =>
                {
                    if (((DataGrid)this.FindName("DataGrid_Tools")).SelectedItem is Setting.ToolSetting selectedTool)
                    {
                        _plugin.Settings.Tools.Remove(selectedTool);
                    }
                });

                Dispatcher.Invoke(() =>
                {
                    ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = null;
                    ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
                });
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

        /// <summary>
        /// 调度自动保存
        /// </summary>
        /// <param name="immediate">是否立即保存</param>
        private void ScheduleAutoSave(bool immediate = false)
        {
            lock (_saveLock)
            {
                _hasUnsavedChanges = true;

                if (immediate)
                {
                    // 立即保存
                    _autoSaveTimer?.Stop();
                    SaveSettings();
                }
                else
                {
                    // 重启定时器，实现防抖动
                    _autoSaveTimer?.Stop();
                    _autoSaveTimer?.Start();
                }
            }
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
            if (FindName("CheckBox_EnableHistoryCompression") is CheckBox checkBoxEnableHistoryCompression) checkBoxEnableHistoryCompression.Content = LanguageHelper.Get("Advanced_Options.EnableHistoryCompression", langCode);
            if (FindName("TextBlock_HistoryCompressionThreshold") is TextBlock textBlockHistoryCompressionThreshold) textBlockHistoryCompressionThreshold.Text = LanguageHelper.Get("Advanced_Options.HistoryCompressionThreshold", langCode);
            if (FindName("TextBlock_CurrentContextLengthLabel") is TextBlock textBlockCurrentContextLengthLabel) textBlockCurrentContextLengthLabel.Text = LanguageHelper.Get("Advanced_Options.CurrentContextLength", langCode);
            if (FindName("TextBlock_CurrentContextLength") is TextBlock textBlockCurrentContextLength) textBlockCurrentContextLength.Text = _plugin.ChatCore.GetChatHistory().Count.ToString();

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
                if (dataGridTools.Columns.Count >= 4)
                {
                    dataGridTools.Columns[0].Header = LanguageHelper.Get("Tools.Name", langCode) ?? "名称";
                    dataGridTools.Columns[1].Header = LanguageHelper.Get("Tools.URL", langCode) ?? "URL";
                    dataGridTools.Columns[2].Header = LanguageHelper.Get("Tools.ApiKey", langCode) ?? "API Key";
                    dataGridTools.Columns[3].Header = LanguageHelper.Get("Tools.Description", langCode) ?? "描述";
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

        private async void Button_RefreshPlugins_Click(object sender, RoutedEventArgs e)
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
            if (_plugin.Settings.ShowUninstallWarning)
            {
                var confirmDialog = new winUninstallConfirm();
                if (confirmDialog.ShowDialog() == true)
                {
                    if (confirmDialog.DoNotShowAgain)
                    {
                        _plugin.Settings.ShowUninstallWarning = false;
                        _plugin.Settings.Save();
                    }
                }
                else
                {
                    return;
                }
            }

            var pluginNameToFind = plugin.OriginalName ?? plugin.Name;
            var localPlugin = _plugin.Plugins.FirstOrDefault(p => p.FilePath == plugin.LocalFilePath);

            if (localPlugin != null)
            {
                bool uninstalled = await _plugin.UnloadAndTryDeletePlugin(localPlugin);
                if (!uninstalled)
                {
                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {Path.GetFileName(plugin.LocalFilePath)}"),
                                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var failedPlugin = _plugin.FailedPlugins.FirstOrDefault(p => p.Name == pluginNameToFind);
                if (failedPlugin != null)
                {
                    string pluginFilePath = failedPlugin.FilePath;
                    bool deleted = await _plugin.DeletePluginFile(pluginFilePath);
                    if (!deleted)
                    {
                        MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {Path.GetFileName(pluginFilePath)}"),
                                        ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.FindPluginFail", _plugin.Settings.Language, "找不到本地插件实例"),
                                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            Button_RefreshPlugins_Click(this, new RoutedEventArgs());
        }

        private async Task HandleDeletePlugin(UnifiedPluginItem plugin)
        {
            string pluginFilePath = plugin.FailedPlugin.FilePath;
            bool deleted = await _plugin.DeletePluginFile(pluginFilePath);
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

                    if (urlPanel != null) urlPanel.Visibility = Visibility.Collapsed;
                    if (openAIPanel != null) openAIPanel.Visibility = Visibility.Collapsed;
                    if (diyPanel != null) diyPanel.Visibility = Visibility.Collapsed;

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
                // 确保配置文件存在
                var config = Utils.DIYTTSConfig.LoadConfig();
                var configPath = Utils.DIYTTSConfig.GetConfigFilePath();
                var configDir = System.IO.Path.GetDirectoryName(configPath);

                // 打开配置文件夹
                if (System.IO.Directory.Exists(configDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", configDir);
                }
                else
                {
                    MessageBox.Show($"配置文件夹不存在：{configDir}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开配置文件夹失败：{ex.Message}",
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
    }
}
