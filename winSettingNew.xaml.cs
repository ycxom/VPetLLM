using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Utils;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VPetLLM
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
    }

    public partial class winSettingNew : Window
    {
        private readonly VPetLLM _plugin;

        public winSettingNew(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _plugin.SettingWindow = this;
            LoadSettings();
            Closed += Window_Closed;
            Loaded += (s, e) => {
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
            ((CheckBox)this.FindName("CheckBox_AutoMigrateChatHistory")).Click += Control_Click;
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
            ((DataGrid)this.FindName("DataGrid_Plugins")).CellEditEnding += DataGrid_Plugins_CellEditEnding;

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
            
            ((Button)this.FindName("Button_RefreshPlugins")).Click += Button_RefreshPlugins_Click;
            ((Button)this.FindName("Button_ImportPlugin")).Click += Button_ImportPlugin_Click;
        }

        private void Control_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveSettings();
        private void Control_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SaveSettings();
            if (sender == FindName("ComboBox_Language"))
            {
                UpdateUIForLanguage();
            }
        }
        private void Control_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();
        private void Control_Click(object sender, RoutedEventArgs e) => SaveSettings();

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
            ((CheckBox)this.FindName("CheckBox_AutoMigrateChatHistory")).IsChecked = _plugin.Settings.AutoMigrateChatHistory;
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
            var autoMigrateChatHistoryCheckBox = (CheckBox)this.FindName("CheckBox_AutoMigrateChatHistory");
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
            _plugin.Settings.AiName = aiNameTextBox.Text;
            _plugin.Settings.UserName = userNameTextBox.Text;
            _plugin.Settings.FollowVPetName = followVPetNameCheckBox.IsChecked ?? true;
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
            _plugin.Settings.AutoMigrateChatHistory = autoMigrateChatHistoryCheckBox.IsChecked ?? true;
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
        }
        private void ComboBox_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void Button_RefreshOllamaModels_Click(object sender, RoutedEventArgs e)
        {
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
        }

        private async void Button_RefreshOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
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
        }

        private async void Button_RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
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
        private void Button_AddTool_Click(object sender, RoutedEventArgs e)
        {
            var newTool = new Setting.ToolSetting();
            _plugin.Settings.Tools.Add(newTool);
            ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = null;
            ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
        }

        private void Button_DeleteTool_Click(object sender, RoutedEventArgs e)
        {
            if (((DataGrid)this.FindName("DataGrid_Tools")).SelectedItem is Setting.ToolSetting selectedTool)
            {
                _plugin.Settings.Tools.Remove(selectedTool);
                ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = null;
                ((DataGrid)this.FindName("DataGrid_Tools")).ItemsSource = _plugin.Settings.Tools;
            }
        }

        private void Button_ImportPlugin_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "插件文件 (*.dll)|*.dll"
            };
            if (dialog.ShowDialog() == true)
            {
                _plugin.ImportPlugin(dialog.FileName);
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

        private void DataGrid_Plugins_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit)
            {
                if (e.Column is DataGridCheckBoxColumn)
                {
                    var plugin = e.Row.Item as IVPetLLMPlugin;
                    if (plugin != null)
                    {
                        // The binding automatically updates the `Enabled` property.
                        // Now, we need to add/remove the plugin from the ChatCore and save the state.
                        if (plugin.Enabled)
                        {
                            _plugin.ChatCore.AddPlugin(plugin);
                        }
                        else
                        {
                            _plugin.ChatCore.RemovePlugin(plugin);
                        }
                        _plugin.SavePluginStates();
                    }
                }
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _plugin.SettingWindow = null;
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
            if (FindName("CheckBox_AutoMigrateChatHistory") is CheckBox checkBoxAutoMigrateChatHistory) checkBoxAutoMigrateChatHistory.Content = LanguageHelper.Get("LLM_Settings.AutoMigrateChatHistory", langCode);
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

            if (FindName("TextBlock_ToolsDescription") is TextBlock textBlockToolsDescription) textBlockToolsDescription.Text = LanguageHelper.Get("Tools.Description", langCode);
            if (FindName("Button_AddTool") is Button buttonAddTool) buttonAddTool.Content = LanguageHelper.Get("Tools.Add", langCode);
            if (FindName("Button_DeleteTool") is Button buttonDeleteTool) buttonDeleteTool.Content = LanguageHelper.Get("Tools.Delete", langCode);
            if (FindName("DataGrid_Tools") is DataGrid dataGridTools)
            {
                dataGridTools.Columns[0].Header = LanguageHelper.Get("Tools.Name", langCode);
                dataGridTools.Columns[1].Header = LanguageHelper.Get("Tools.URL", langCode);
                dataGridTools.Columns[2].Header = LanguageHelper.Get("Tools.ApiKey", langCode);
                dataGridTools.Columns[3].Header = LanguageHelper.Get("Tools.Enabled", langCode);
            }

            if (FindName("CheckBox_LogAutoScroll") is CheckBox checkBoxLogAutoScroll) checkBoxLogAutoScroll.Content = LanguageHelper.Get("Log.AutoScroll", langCode);
            if (FindName("TextBlock_MaxLogCount") is TextBlock textBlockMaxLogCount) textBlockMaxLogCount.Text = LanguageHelper.Get("Log.MaxLogCount", langCode);
            if (FindName("Button_CopyLog") is Button buttonCopyLog) buttonCopyLog.Content = LanguageHelper.Get("Log.Copy", langCode);
            if (FindName("Button_ClearLog") is Button buttonClearLog) buttonClearLog.Content = LanguageHelper.Get("Log.Clear", langCode);

            if (FindName("TextBlock_HowToUse") is TextBlock howToUseTextBlock)
            {
                howToUseTextBlock.Inlines.Clear();
                howToUseTextBlock.Inlines.Add(new Run(LanguageHelper.Get("Plugin.HowToUse", langCode)));
                var hyperlink = new Hyperlink(new Run("https://github.com/ycxom/VPetLLM_Plugin"));
                hyperlink.NavigateUri = new Uri("https://github.com/ycxom/VPetLLM_Plugin");
                hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                howToUseTextBlock.Inlines.Add(hyperlink);
            }
            if (FindName("Button_RefreshPlugins") is Button buttonRefreshPlugins) buttonRefreshPlugins.Content = LanguageHelper.Get("Plugin.Refresh", langCode);
            if (FindName("Button_ImportPlugin") is Button buttonImportPlugin) buttonImportPlugin.Content = LanguageHelper.Get("Plugin.ImportPlugin", langCode);
            if (FindName("DataGrid_Plugins") is DataGrid dataGridPlugins)
            {
                dataGridPlugins.Columns[0].Header = LanguageHelper.Get("Plugin.Enabled", langCode);
                // Column 1 is icon, no header
                dataGridPlugins.Columns[2].Header = LanguageHelper.Get("Plugin.Name", langCode);
                dataGridPlugins.Columns[3].Header = LanguageHelper.Get("Plugin.Author", langCode);
                dataGridPlugins.Columns[4].Header = LanguageHelper.Get("Plugin.Description", langCode);
                dataGridPlugins.Columns[5].Header = LanguageHelper.Get("Plugin.Action", langCode);
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
        }

        private async void Button_RefreshPlugins_Click(object sender, RoutedEventArgs e)
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
                    var json = await client.GetStringAsync("https://raw.githubusercontent.com/ycxom/VPetLLM_Plugin/refs/heads/main/PluginList.json");
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

                pluginItems[id] = new UnifiedPluginItem
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
                    Version = sha
                };
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
                    Version = LanguageHelper.Get("Plugin.UnknownVersion", langCode)
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

        private async void Button_PluginAction_Click(object sender, RoutedEventArgs e)
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

        private async void Button_UninstallPlugin_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is UnifiedPluginItem plugin)
            {
                await HandleUninstallPlugin(plugin);
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
            var localPlugin = plugin.LocalPlugin ?? _plugin.Plugins.FirstOrDefault(p => p.Name == pluginNameToFind);

            if (localPlugin != null)
            {
                string pluginFilePath = localPlugin.FilePath;
                bool uninstalled = await _plugin.UnloadAndTryDeletePlugin(localPlugin);
                if (!uninstalled)
                {
                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("Uninstall.DeleteFail", _plugin.Settings.Language, $"无法删除插件文件: {Path.GetFileName(pluginFilePath)}"),
                                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var failedPlugin = plugin.FailedPlugin ?? _plugin.FailedPlugins.FirstOrDefault(p => p.Name == pluginNameToFind);
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
                    var data = await client.GetByteArrayAsync(plugin.FileUrl);
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
                    MessageBox.Show(ErrorMessageHelper.GetLocalizedMessage("InstallPlugin.Success", _plugin.Settings.Language, "插件安装成功，请重启桌宠以加载新插件"),
                        ErrorMessageHelper.GetLocalizedTitle("Success", _plugin.Settings.Language, "成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                    Button_RefreshPlugins_Click(this, new RoutedEventArgs());
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ErrorMessageHelper.GetLocalizedError("InstallPlugin.Fail", _plugin.Settings.Language, "安装插件失败", ex),
                    ErrorMessageHelper.GetLocalizedTitle("Error", _plugin.Settings.Language, "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private System.Net.IWebProxy GetPluginStoreProxy()
        {
            var proxySettings = _plugin.Settings.Proxy;
            if (proxySettings != null && proxySettings.IsEnabled)
            {
                bool useProxy = proxySettings.ForAllAPI || proxySettings.ForPlugin;
                if (useProxy)
                {
                    if (proxySettings.FollowSystemProxy)
                    {
                        return System.Net.WebRequest.GetSystemWebProxy();
                    }
                    else if (!string.IsNullOrEmpty(proxySettings.Address))
                    {
                        if (string.IsNullOrEmpty(proxySettings.Protocol))
                        {
                            proxySettings.Protocol = "http";
                        }
                        var protocol = proxySettings.Protocol.ToLower() == "socks" ? "socks5" : "http";
                        return new System.Net.WebProxy(new Uri($"{protocol}://{proxySettings.Address}"));
                    }
                }
            }
            return null;
        }
    }
}
