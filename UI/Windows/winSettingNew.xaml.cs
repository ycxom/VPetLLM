using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Media;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Utils;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows.Threading;

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
        public string SettingActionText { get; set; }
    }

    public partial class winSettingNew : Window
    {
        private readonly VPetLLM _plugin;
        
        // 自动保存相关字段
        private DispatcherTimer? _autoSaveTimer;
        private bool _hasUnsavedChanges = false;
        private readonly object _saveLock = new object();

        public winSettingNew(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;
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
            ((TextBox)this.FindName("TextBox_Gemini_MaxTokens")).TextChanged += Control_TextChanged;

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
            ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).Click += Control_Click;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).Click += Control_Click;

            // Plugin Store Proxy settings
            ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).Click += Control_Click;
            ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).TextChanged += Control_TextChanged;

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
                Dispatcher.BeginInvoke(new Action(() => {
                    UpdateUIForLanguage();
                    // 强制刷新插件列表的列标题
                    if (FindName("DataGrid_Plugins") is DataGrid dataGridPluginsAsync)
                    {
                        var langCodeAsync = _plugin.Settings.Language;
                        if (dataGridPluginsAsync.Columns.Count > 0)
                            dataGridPluginsAsync.Columns[0].Header = LanguageHelper.Get("Plugin.Enabled", langCodeAsync) ?? "启用";
                        if (dataGridPluginsAsync.Columns.Count > 1)
                            dataGridPluginsAsync.Columns[1].Header = LanguageHelper.Get("Plugin.Icon", langCodeAsync) ?? "图标";
                        if (dataGridPluginsAsync.Columns.Count > 2)
                            dataGridPluginsAsync.Columns[2].Header = LanguageHelper.Get("Plugin.Name", langCodeAsync) ?? "名称";
                        if (dataGridPluginsAsync.Columns.Count > 3)
                            dataGridPluginsAsync.Columns[3].Header = LanguageHelper.Get("Plugin.Action", langCodeAsync) ?? "操作";
                        
                        // 强制刷新DataGrid显示
                        dataGridPluginsAsync.Items.Refresh();
                        dataGridPluginsAsync.UpdateLayout();
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
                    Dispatcher.BeginInvoke(new Action(() => {
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
                    Dispatcher.BeginInvoke(new Action(() => {
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
            ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).IsChecked = _plugin.Settings.Proxy.ForMcp;
            ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).IsChecked = _plugin.Settings.Proxy.ForPlugin;

            // Plugin Store Proxy settings
            if (_plugin.Settings.PluginStore == null)
            {
                _plugin.Settings.PluginStore = new Setting.PluginStoreSetting();
            }
            ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).IsChecked = _plugin.Settings.PluginStore.UseProxy;
            ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).Text = _plugin.Settings.PluginStore.ProxyUrl;
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
            _plugin.Settings.Proxy.ForMcp = ((CheckBox)this.FindName("CheckBox_Proxy_ForMcp")).IsChecked ?? true;
            _plugin.Settings.Proxy.ForPlugin = ((CheckBox)this.FindName("CheckBox_Proxy_ForPlugin")).IsChecked ?? true;

            // Plugin Store Proxy settings
            _plugin.Settings.PluginStore.UseProxy = ((CheckBox)this.FindName("CheckBox_PluginStore_UseProxy")).IsChecked ?? true;
            _plugin.Settings.PluginStore.ProxyUrl = ((TextBox)this.FindName("TextBox_PluginStore_ProxyUrl")).Text;

            _plugin.Settings.Save();

            if (oldProvider != newProvider)
            {
                var oldHistory = _plugin.ChatCore.GetChatHistory();
                IChatCore newChatCore = newProvider switch
                {
                    Setting.LLMType.Ollama => new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.OpenAI => new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
                    Setting.LLMType.Gemini => new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor),
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
                StopButtonLoadingAnimation(button);
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
                bool isEnabled = checkBox.IsChecked ?? false;
                item.IsEnabled = isEnabled;
                item.LocalPlugin.Enabled = isEnabled;

                if (isEnabled)
                {
                    item.LocalPlugin.Initialize(_plugin);
                    _plugin.ChatCore?.AddPlugin(item.LocalPlugin);
                    Logger.Log($"Plugin '{item.LocalPlugin.Name}' has been enabled and initialized.");
                }
                else
                {
                    item.LocalPlugin.Unload();
                    _plugin.ChatCore?.RemovePlugin(item.LocalPlugin);
                    Logger.Log($"Plugin '{item.LocalPlugin.Name}' has been disabled and unloaded.");
                }
                _plugin.SavePluginStates();
                Logger.Log("Plugin states have been saved.");
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            // 停止并清理定时器
            _autoSaveTimer?.Stop();
            _autoSaveTimer = null;
            
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
            
            button.IsEnabled = true;
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
                if (dataGridTools.Columns.Count >= 5)
                {
                    dataGridTools.Columns[0].Header = LanguageHelper.Get("Tools.Name", langCode);
                    dataGridTools.Columns[1].Header = LanguageHelper.Get("Tools.URL", langCode);
                    dataGridTools.Columns[2].Header = LanguageHelper.Get("Tools.ApiKey", langCode);
                    dataGridTools.Columns[3].Header = LanguageHelper.Get("Tools.Description", langCode);
                    dataGridTools.Columns[4].Header = LanguageHelper.Get("Tools.Enabled", langCode);
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
            if (FindName("DataGrid_Plugins") is DataGrid dataGridPlugins)
            {
                // 修复列标题的多语言显示，与XAML中的列顺序保持一致
                if (dataGridPlugins.Columns.Count > 0)
                    dataGridPlugins.Columns[0].Header = LanguageHelper.Get("Plugin.Enabled", langCode) ?? "启用";
                if (dataGridPlugins.Columns.Count > 1)
                    dataGridPlugins.Columns[1].Header = LanguageHelper.Get("Plugin.Icon", langCode) ?? "图标";
                if (dataGridPlugins.Columns.Count > 2)
                    dataGridPlugins.Columns[2].Header = LanguageHelper.Get("Plugin.Name", langCode) ?? "名称";
                if (dataGridPlugins.Columns.Count > 3)
                    dataGridPlugins.Columns[3].Header = LanguageHelper.Get("Plugin.Action", langCode) ?? "操作";
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
            if (FindName("Label_GeminiApiKey") is Label labelGeminiApiKey) labelGeminiApiKey.Content = LanguageHelper.Get("Gemini.ApiKey", langCode);
            if (FindName("Label_GeminiModel") is Label labelGeminiModel) labelGeminiModel.Content = LanguageHelper.Get("Gemini.Model", langCode);
            if (FindName("Button_RefreshGeminiModels") is Button buttonRefreshGeminiModels) buttonRefreshGeminiModels.Content = LanguageHelper.Get("Gemini.Refresh", langCode);
            if (FindName("Label_GeminiUrl") is Label labelGeminiUrl) labelGeminiUrl.Content = LanguageHelper.Get("Gemini.ApiAddress", langCode);
            if (FindName("TextBlock_GeminiApiEndpointNote") is TextBlock textBlockGeminiApiEndpointNote) textBlockGeminiApiEndpointNote.Text = LanguageHelper.Get("Gemini.ApiEndpointNote", langCode);
            if (FindName("CheckBox_Gemini_EnableAdvanced") is CheckBox checkBoxGeminiEnableAdvanced) checkBoxGeminiEnableAdvanced.Content = LanguageHelper.Get("Gemini.EnableAdvanced", langCode);
            if (FindName("TextBlock_Gemini_Temperature") is TextBlock textBlockGeminiTemperature) textBlockGeminiTemperature.Text = LanguageHelper.Get("Gemini.Temperature", langCode);
            if (FindName("TextBlock_Gemini_MaxTokens") is TextBlock textBlockGeminiMaxTokens) textBlockGeminiMaxTokens.Text = LanguageHelper.Get("Gemini.MaxTokens", langCode);

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
            if (FindName("CheckBox_Proxy_ForMcp") is CheckBox checkBoxProxyForMcp) checkBoxProxyForMcp.Content = LanguageHelper.Get("Proxy.ForMcp", langCode);
            if (FindName("CheckBox_Proxy_ForPlugin") is CheckBox checkBoxProxyForPlugin) checkBoxProxyForPlugin.Content = LanguageHelper.Get("Proxy.ForPlugin", langCode);

            // Plugin Store Proxy UI
            if (FindName("Label_PluginStore_Proxy") is Label labelPluginStoreProxy) labelPluginStoreProxy.Content = LanguageHelper.Get("PluginStore.ProxySettings", langCode);
            if (FindName("CheckBox_PluginStore_UseProxy") is CheckBox checkBoxPluginStoreUseProxy) checkBoxPluginStoreUseProxy.Content = LanguageHelper.Get("PluginStore.EnableProxy", langCode);
            if (FindName("Label_PluginStore_ProxyUrl") is Label labelPluginStoreProxyUrl) labelPluginStoreProxyUrl.Content = LanguageHelper.Get("PluginStore.ProxyUrl", langCode);
            if (FindName("TextBlock_PluginStore_ProxyUrlNote") is TextBlock textBlockPluginStoreProxyUrlNote) textBlockPluginStoreProxyUrlNote.Text = LanguageHelper.Get("PluginStore.ProxyUrlNote", langCode);
        }

        private async void Button_RefreshPlugins_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            StartButtonLoadingAnimation(button);

            try
            {
                var langCode = _plugin.Settings.Language;
                var pluginItems = new Dictionary<string, UnifiedPluginItem>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, JObject> remotePlugins = new Dictionary<string, JObject>();
                // Load local plugins (both successful and failed)
                _plugin.LoadPlugins();

                // Fetch remote plugin list first to get the correct IDs
                try
                {
                    using (var client = new HttpClient(new HttpClientHandler() { Proxy = GetPluginStoreProxy() }))
                    {
                        var pluginListUrl = GetPluginStoreUrl("https://raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json");
                        var json = await client.GetStringAsync(pluginListUrl);
                        remotePlugins = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(json);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("RefreshPluginStore.Fail", _plugin.Settings.Language, "刷新插件商店失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // First pass: Add all local plugins to the dictionary using the remote key if available
            foreach (var localPlugin in _plugin.Plugins)
            {
                var sha = PluginManager.GetFileSha256(localPlugin.FilePath);
                var remoteEntry = remotePlugins.FirstOrDefault(kvp => kvp.Value["Name"]?.ToString() == localPlugin.Name);
                var id = !string.IsNullOrEmpty(remoteEntry.Key) ? remoteEntry.Key : localPlugin.Name;

                var item = new UnifiedPluginItem
                {
                    IsLocal = true,
                    Id = id,
                    Name = localPlugin.Name,
                    OriginalName = localPlugin.Name,
                    Author = localPlugin.Author,
                    Description = localPlugin.Description,
                    IsEnabled = localPlugin.Enabled,
                    ActionText = LanguageHelper.Get("Plugin.UnloadPlugin", langCode),
                    Icon = "\uE8A5", // Folder icon
                    LocalPlugin = localPlugin,
                    Version = sha,
                    LocalFilePath = localPlugin.FilePath
                };
                if (localPlugin is IActionPlugin && localPlugin.Parameters.Contains("setting"))
                {
                    item.HasSettingAction = true;
                    item.SettingActionText = LanguageHelper.Get("Plugin.Setting", langCode);
                }
                pluginItems[id] = item;
            }
            foreach (var failedPlugin in _plugin.FailedPlugins)
            {
                var remoteEntry = remotePlugins.FirstOrDefault(kvp => kvp.Value["Name"]?.ToString() == failedPlugin.Name);
                var id = !string.IsNullOrEmpty(remoteEntry.Key) ? remoteEntry.Key : failedPlugin.Name;
                pluginItems[id] = new UnifiedPluginItem
                {
                    IsFailed = true,
                    IsLocal = true,
                    Id = id,
                    Name = $"{failedPlugin.Name} ({LanguageHelper.Get("Plugin.Outdated", langCode)})",
                    OriginalName = failedPlugin.Name,
                    Author = failedPlugin.Author,
                    Description = $"{LanguageHelper.Get("Plugin.LoadFailed", langCode)}: {failedPlugin.Description}",
                    IsEnabled = false,
                    ActionText = LanguageHelper.Get("Plugin.Delete", langCode),
                    Icon = "\uE783", // Error icon
                    FailedPlugin = failedPlugin,
                    Version = LanguageHelper.Get("Plugin.UnknownVersion", langCode),
                    LocalFilePath = failedPlugin.FilePath
                };
            }

            // Second pass: Update with remote info or add new remote plugins
            foreach (var item in remotePlugins)
            {
                var id = item.Key;
                var remoteInfo = item.Value;
                var remoteSha256 = remoteInfo["SHA256"]?.ToString() ?? string.Empty;

                if (pluginItems.TryGetValue(id, out var existingItem))
                {
                    // Plugin exists locally, check for update
                    existingItem.RemoteVersion = remoteSha256;
                    existingItem.FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty;
                    existingItem.SHA256 = remoteSha256;

                    bool isUpdatable = !string.IsNullOrEmpty(existingItem.Version) &&
                                       !string.IsNullOrEmpty(remoteSha256) &&
                                       !existingItem.Version.Equals(remoteSha256, StringComparison.OrdinalIgnoreCase);

                    if (isUpdatable)
                    {
                        existingItem.IsUpdatable = true;
                        existingItem.UpdateAvailableText = LanguageHelper.Get("Plugin.UpdateAvailable", langCode);
                        existingItem.ActionText = LanguageHelper.Get("Plugin.Update", langCode);
                    }
                }
                else
                {
                    // Plugin is only remote, add it for installation
                    var des = remoteInfo["Description"]?.ToObject<Dictionary<string, string>>();
                    var description = des != null ? (des.TryGetValue(langCode, out var d) ? d : (des.TryGetValue("en", out var enD) ? enD : string.Empty)) : string.Empty;

                    pluginItems[id] = new UnifiedPluginItem
                    {
                        IsLocal = false,
                        Id = id,
                        Name = remoteInfo["Name"]?.ToString() ?? string.Empty,
                        Author = remoteInfo["Author"]?.ToString() ?? string.Empty,
                        Description = description,
                        IsEnabled = false,
                        ActionText = LanguageHelper.Get("PluginStore.Install", langCode),
                        Icon = "\uE753", // Cloud download icon
                        FileUrl = remoteInfo["File"]?.ToString() ?? string.Empty,
                        SHA256 = remoteSha256,
                        RemoteVersion = remoteSha256
                    };
                }
            }

            // Final pass to mark local-only plugins and source
            foreach (var item in pluginItems.Values)
            {
                if (item.IsLocal)
                {
                    if (string.IsNullOrEmpty(item.RemoteVersion))
                    {
                        item.Description = $"({LanguageHelper.Get("Plugin.LocalOnly", langCode)}) {item.Description}";
                    }
                    else
                    {
                        item.Icon = "\uE955"; // Cloud with checkmark
                        var localSource = LanguageHelper.Get("Plugin.Source.Local", langCode);
                        item.Description = $"({localSource}) {item.Description}";
                    }
                }
                else
                {
                    var cloudSource = LanguageHelper.Get("Plugin.Source.Cloud", langCode);
                    item.Description = $"({cloudSource}) {item.Description}";
                }

                if (item.IsLocal)
                {
                    item.UninstallActionText = LanguageHelper.Get("Plugin.Uninstall", langCode);
                }

                if (item.IsUpdatable)
                {
                    item.Icon = "\uE948"; // Sync icon
                }
            }

                ((DataGrid)this.FindName("DataGrid_Plugins")).ItemsSource = pluginItems.Values.ToList();
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
                using (var client = new HttpClient(new HttpClientHandler() { Proxy = GetPluginStoreProxy() }))
                {
                    var downloadUrl = GetPluginStoreUrl(plugin.FileUrl);
                    var data = await client.GetByteArrayAsync(downloadUrl);
                    using (var sha256 = SHA256.Create())
                    {
                        var hash = sha256.ComputeHash(data);
                        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                        if (hashString != plugin.SHA256.ToLowerInvariant())
                        {
                            MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("InstallPlugin.FileValidationError", _plugin.Settings.Language, "文件校验失败，请稍后重试"),
                                ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    var pluginDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "Plugin");
                    if (!Directory.Exists(pluginDir))
                    {
                        Directory.CreateDirectory(pluginDir);
                    }
                    var filePath = Path.Combine(pluginDir, $"{plugin.Id}.dll");
                    File.WriteAllBytes(filePath, data);
                    
                    // Force reload plugins to recognize the new dll
                    _plugin.LoadPlugins();
                    
                    // Now refresh the UI
                    Button_RefreshPlugins_Click(this, new RoutedEventArgs());

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
                    actionPlugin.Function("action(setting)");
                }
            }
        }

        private string GetPluginStoreUrl(string originalUrl)
        {
            var pluginStoreSettings = _plugin.Settings.PluginStore;
            if (pluginStoreSettings != null && pluginStoreSettings.UseProxy && !string.IsNullOrEmpty(pluginStoreSettings.ProxyUrl))
            {
                // 移除原URL中的协议部分，然后拼接代理地址
                var uri = new Uri(originalUrl);
                var pathAndQuery = originalUrl.Substring(uri.Scheme.Length + 3); // 移除 "https://" 或 "http://"
                return $"{pluginStoreSettings.ProxyUrl.TrimEnd('/')}/{pathAndQuery}";
            }
            return originalUrl;
        }

        private System.Net.IWebProxy GetPluginStoreProxy()
        {
            var pluginStoreSettings = _plugin.Settings.PluginStore;
            
            // 插件商店代理独立于常规代理设置，不受CheckBox_Proxy_IsEnabled约束
            // 如果插件商店代理启用且设置了代理地址，但不是URL拼接类型的代理
            if (pluginStoreSettings != null && pluginStoreSettings.UseProxy && !string.IsNullOrEmpty(pluginStoreSettings.ProxyUrl))
            {
                var proxyUrl = pluginStoreSettings.ProxyUrl.Trim();
                
                // 如果是URL拼接类型的代理（如ghfast.top），不使用HttpClient代理
                if (proxyUrl.StartsWith("http://") || proxyUrl.StartsWith("https://"))
                {
                    // 检查是否是GitHub加速服务（URL拼接类型）
                    if (proxyUrl.Contains("ghfast.top") || proxyUrl.Contains("github") || proxyUrl.Contains("raw.githubusercontent"))
                    {
                        return null; // 使用URL拼接方式
                    }
                    
                    // 其他HTTP/HTTPS代理，使用传统代理方式
                    return new System.Net.WebProxy(proxyUrl);
                }
                else
                {
                    // 假设是 IP:Port 格式的代理
                    return new System.Net.WebProxy($"http://{proxyUrl}");
                }
            }
            
            return null;
        }
    }
}
