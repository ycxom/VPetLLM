using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core;

namespace VPetLLM
{
    public partial class winSettingNew : Window
    {
        private readonly VPetLLM _plugin;

        public winSettingNew(VPetLLM plugin)
        {
            _plugin = plugin;
            InitializeComponent();
            LoadSettings();
            Slider_Ollama_Temperature.ValueChanged += Slider_Temperature_ValueChanged;
            Slider_OpenAI_Temperature.ValueChanged += Slider_Temperature_ValueChanged;
            Slider_Gemini_Temperature.ValueChanged += Slider_Temperature_ValueChanged;
        }

        private void LoadSettings()
        {
            // LLM 设置
            ComboBox_Provider.ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            ComboBox_Provider.SelectedItem = _plugin.Settings.Provider;
            TextBox_AiName.Text = _plugin.Settings.AiName;
            TextBox_UserName.Text = _plugin.Settings.UserName;
            TextBox_Role.Text = _plugin.Settings.Role;
            TextBox_OllamaUrl.Text = _plugin.Settings.Ollama.Url;
            ComboBox_OllamaModel.Text = _plugin.Settings.Ollama.Model;
            TextBox_OpenAIApiKey.Text = _plugin.Settings.OpenAI.ApiKey;
            ComboBox_OpenAIModel.Text = _plugin.Settings.OpenAI.Model;
            TextBox_OpenAIUrl.Text = _plugin.Settings.OpenAI.Url;
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            ComboBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
            TextBox_GeminiUrl.Text = _plugin.Settings.Gemini.Url;

            // 高级选项
            CheckBox_KeepContext.IsChecked = _plugin.Settings.KeepContext;
            CheckBox_EnableChatHistory.IsChecked = _plugin.Settings.EnableChatHistory;
            CheckBox_SeparateChatByProvider.IsChecked = _plugin.Settings.SeparateChatByProvider;
            CheckBox_AutoMigrateChatHistory.IsChecked = _plugin.Settings.AutoMigrateChatHistory;
            CheckBox_EnableAction.IsChecked = _plugin.Settings.EnableAction;
            CheckBox_EnableBuy.IsChecked = _plugin.Settings.EnableBuy;
            CheckBox_EnableState.IsChecked = _plugin.Settings.EnableState;
            CheckBox_EnableActionExecution.IsChecked = _plugin.Settings.EnableActionExecution;
            CheckBox_EnableMove.IsChecked = _plugin.Settings.EnableMove;
            CheckBox_EnableTime.IsChecked = _plugin.Settings.EnableTime;
            CheckBox_LogAutoScroll.IsChecked = _plugin.Settings.LogAutoScroll;
            TextBox_MaxLogCount.Text = _plugin.Settings.MaxLogCount.ToString();
            
            CheckBox_Ollama_EnableAdvanced.IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            Slider_Ollama_Temperature.Value = _plugin.Settings.Ollama.Temperature;
            TextBlock_Ollama_TemperatureValue.Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            TextBox_Ollama_MaxTokens.Text = _plugin.Settings.Ollama.MaxTokens.ToString();

            CheckBox_OpenAI_EnableAdvanced.IsChecked = _plugin.Settings.OpenAI.EnableAdvanced;
            Slider_OpenAI_Temperature.Value = _plugin.Settings.OpenAI.Temperature;
            TextBlock_OpenAI_TemperatureValue.Text = _plugin.Settings.OpenAI.Temperature.ToString("F2");
            TextBox_OpenAI_MaxTokens.Text = _plugin.Settings.OpenAI.MaxTokens.ToString();

            CheckBox_Gemini_EnableAdvanced.IsChecked = _plugin.Settings.Gemini.EnableAdvanced;
            Slider_Gemini_Temperature.Value = _plugin.Settings.Gemini.Temperature;
            TextBlock_Gemini_TemperatureValue.Text = _plugin.Settings.Gemini.Temperature.ToString("F2");
            TextBox_Gemini_MaxTokens.Text = _plugin.Settings.Gemini.MaxTokens.ToString();
            LogBox.ItemsSource = Logger.Logs;
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            var oldProvider = _plugin.Settings.Provider;
            var newProvider = (Setting.LLMType)ComboBox_Provider.SelectedItem;

            // LLM 设置
            _plugin.Settings.Provider = newProvider;
            _plugin.Settings.AiName = TextBox_AiName.Text;
            _plugin.Settings.UserName = TextBox_UserName.Text;
            _plugin.Settings.Role = TextBox_Role.Text;
            _plugin.Settings.Ollama.Url = TextBox_OllamaUrl.Text;
            _plugin.Settings.Ollama.Model = ComboBox_OllamaModel.Text;
            _plugin.Settings.OpenAI.ApiKey = TextBox_OpenAIApiKey.Text;
            _plugin.Settings.OpenAI.Model = ComboBox_OpenAIModel.Text;
            _plugin.Settings.OpenAI.Url = TextBox_OpenAIUrl.Text;
            _plugin.Settings.Gemini.ApiKey = TextBox_GeminiApiKey.Text;
            _plugin.Settings.Gemini.Model = ComboBox_GeminiModel.Text;
            _plugin.Settings.Gemini.Url = TextBox_GeminiUrl.Text;

            // 高级选项
            _plugin.Settings.KeepContext = CheckBox_KeepContext.IsChecked ?? true;
            _plugin.Settings.EnableChatHistory = CheckBox_EnableChatHistory.IsChecked ?? true;
            _plugin.Settings.SeparateChatByProvider = CheckBox_SeparateChatByProvider.IsChecked ?? true;
            _plugin.Settings.AutoMigrateChatHistory = CheckBox_AutoMigrateChatHistory.IsChecked ?? true;
            _plugin.Settings.EnableAction = CheckBox_EnableAction.IsChecked ?? true;
            _plugin.Settings.EnableBuy = CheckBox_EnableBuy.IsChecked ?? true;
            _plugin.Settings.EnableState = CheckBox_EnableState.IsChecked ?? true;
            _plugin.Settings.EnableActionExecution = CheckBox_EnableActionExecution.IsChecked ?? true;
            _plugin.Settings.EnableMove = CheckBox_EnableMove.IsChecked ?? true;
            _plugin.Settings.EnableTime = CheckBox_EnableTime.IsChecked ?? true;
            _plugin.Settings.LogAutoScroll = CheckBox_LogAutoScroll.IsChecked ?? true;
            if (int.TryParse(TextBox_MaxLogCount.Text, out int maxLogCount))
                _plugin.Settings.MaxLogCount = maxLogCount;
            
            _plugin.Settings.Ollama.EnableAdvanced = CheckBox_Ollama_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.Ollama.Temperature = Slider_Ollama_Temperature.Value;
            if (int.TryParse(TextBox_Ollama_MaxTokens.Text, out int ollamaMaxTokens))
                _plugin.Settings.Ollama.MaxTokens = ollamaMaxTokens;

            _plugin.Settings.OpenAI.EnableAdvanced = CheckBox_OpenAI_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.OpenAI.Temperature = Slider_OpenAI_Temperature.Value;
            if (int.TryParse(TextBox_OpenAI_MaxTokens.Text, out int openAIMaxTokens))
                _plugin.Settings.OpenAI.MaxTokens = openAIMaxTokens;

            _plugin.Settings.Gemini.EnableAdvanced = CheckBox_Gemini_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.Gemini.Temperature = Slider_Gemini_Temperature.Value;
            if (int.TryParse(TextBox_Gemini_MaxTokens.Text, out int geminiMaxTokens))
                _plugin.Settings.Gemini.MaxTokens = geminiMaxTokens;

            _plugin.Settings.Save();

            if (oldProvider != newProvider)
            {
                var oldHistory = _plugin.ChatCore.GetChatHistory();
                IChatCore newChatCore = newProvider switch
                {
                    Setting.LLMType.Ollama => new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW),
                    Setting.LLMType.OpenAI => new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW),
                    Setting.LLMType.Gemini => new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW),
                    _ => throw new NotImplementedException()
                };
                if (_plugin.Settings.EnableChatHistory && oldHistory != null)
                {
                    newChatCore.SetChatHistory(oldHistory);
                }
                _plugin.UpdateChatCore(newChatCore);
            }
            TextBlock_Unsaved.Visibility = Visibility.Collapsed;
        }

        private void Slider_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender == Slider_Ollama_Temperature)
                TextBlock_Ollama_TemperatureValue.Text = e.NewValue.ToString("F2");
            else if (sender == Slider_OpenAI_Temperature)
                TextBlock_OpenAI_TemperatureValue.Text = e.NewValue.ToString("F2");
            else if (sender == Slider_Gemini_Temperature)
                TextBlock_Gemini_TemperatureValue.Text = e.NewValue.ToString("F2");
        }

        private void Button_RestoreDefaults_Click(object sender, RoutedEventArgs e) { }
        private void ComboBox_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        
        private void Button_RefreshOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ollamaCore = new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW);
                var models = ollamaCore.RefreshModels();
                ComboBox_OllamaModel.ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(ComboBox_OllamaModel.Text))
                    ComboBox_OllamaModel.SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"刷新Ollama模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void Button_RefreshOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openAICore = new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW);
                var models = openAICore.RefreshModels();
                ComboBox_OpenAIModel.ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(ComboBox_OpenAIModel.Text))
                    ComboBox_OpenAIModel.SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"刷新OpenAI模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void Button_RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var geminiCore = new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW);
                var models = geminiCore.RefreshModels();
                ComboBox_GeminiModel.ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(ComboBox_GeminiModel.Text))
                    ComboBox_GeminiModel.SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"刷新Gemini模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void Button_ClearContext_Click(object sender, RoutedEventArgs e) { _plugin.ChatCore?.ClearContext(); }
        private void Button_EditContext_Click(object sender, RoutedEventArgs e)
        {
            var contextEditor = new winContextEditor(_plugin.ChatCore);
            contextEditor.Show();
        }
        private void Button_CopyLog_Click(object sender, RoutedEventArgs e) { }
        private void Button_ClearLog_Click(object sender, RoutedEventArgs e) { }
    }
}