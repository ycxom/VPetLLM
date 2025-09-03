using System;
using System.Windows;
using System.Windows.Controls;
using LinePutScript.Localization.WPF;
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
            
            // 跟随VPet的语言设置
            // LocalizeCore 方法需要进一步调查正确用法
            
            // 注册温度滑块值变化事件
            Slider_OpenAI_Temperature.ValueChanged += Slider_OpenAI_Temperature_ValueChanged;
            Slider_Gemini_Temperature.ValueChanged += Slider_Gemini_Temperature_ValueChanged;
            Slider_Ollama_Temperature.ValueChanged += Slider_Ollama_Temperature_ValueChanged;
            
            Logger.Log("Setting window opened.");
            ComboBox_Provider.ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            LogBox.ItemsSource = Logger.Logs;
            LoadSettings();
        }

        private void LoadSettings()
        {
            Logger.Log("Loading settings.");
            ComboBox_Provider.SelectedItem = _plugin.Settings.Provider;
            TextBox_OllamaUrl.Text = _plugin.Settings.Ollama.Url;
            ComboBox_OllamaModel.Text = _plugin.Settings.Ollama.Model;
            if (_plugin.ChatCore is Core.OllamaChatCore ollamaCore)
            {
                ComboBox_OllamaModel.ItemsSource = ollamaCore.GetModels();
            }
            TextBox_OpenAIApiKey.Text = _plugin.Settings.OpenAI.ApiKey;
            ComboBox_OpenAIModel.Text = _plugin.Settings.OpenAI.Model;
            TextBox_OpenAIUrl.Text = _plugin.Settings.OpenAI.Url;
            
            // 初始化OpenAI高级配置
            CheckBox_OpenAI_EnableAdvanced.IsChecked = _plugin.Settings.OpenAI.EnableAdvanced;
            Slider_OpenAI_Temperature.Value = _plugin.Settings.OpenAI.Temperature;
            TextBlock_OpenAI_TemperatureValue.Text = _plugin.Settings.OpenAI.Temperature.ToString("F2");
            TextBox_OpenAI_MaxTokens.Text = _plugin.Settings.OpenAI.MaxTokens.ToString();
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            ComboBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
            TextBox_GeminiUrl.Text = _plugin.Settings.Gemini.Url;
            
            // 初始化Gemini高级配置
            CheckBox_Gemini_EnableAdvanced.IsChecked = _plugin.Settings.Gemini.EnableAdvanced;
            Slider_Gemini_Temperature.Value = _plugin.Settings.Gemini.Temperature;
            TextBlock_Gemini_TemperatureValue.Text = _plugin.Settings.Gemini.Temperature.ToString("F2");
            TextBox_Gemini_MaxTokens.Text = _plugin.Settings.Gemini.MaxTokens.ToString();
            
            // 加载角色设定
            TextBox_Role.Text = _plugin.Settings.Role;
            
            // 初始化Ollama高级配置
            CheckBox_Ollama_EnableAdvanced.IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            Slider_Ollama_Temperature.Value = _plugin.Settings.Ollama.Temperature;
            TextBlock_Ollama_TemperatureValue.Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            TextBox_Ollama_MaxTokens.Text = _plugin.Settings.Ollama.MaxTokens.ToString();
            
            // 加载角色设定
            TextBox_Role.Text = _plugin.Settings.Role;
            
            // 尝试自动刷新Gemini模型列表
            try
            {
                var geminiSettings = new Setting.GeminiSetting
                {
                    ApiKey = TextBox_GeminiApiKey.Text,
                    Url = TextBox_GeminiUrl.Text
                };
                var geminiCore = new Core.GeminiChatCore(geminiSettings, _plugin.Settings);
                ComboBox_GeminiModel.ItemsSource = geminiCore.GetModels();
            }
            catch
            {
                // 如果刷新失败，静默忽略，用户仍然可以手动刷新
            }
            UpdateProviderVisibility();
            Logger.Log("Settings loaded.");
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("Saving settings.");
            _plugin.Settings.Provider = (Setting.LLMType)ComboBox_Provider.SelectedItem;
            _plugin.Settings.Ollama.Url = TextBox_OllamaUrl.Text;
            _plugin.Settings.Ollama.Model = ComboBox_OllamaModel.Text;
            _plugin.Settings.OpenAI.ApiKey = TextBox_OpenAIApiKey.Text;
            _plugin.Settings.OpenAI.Model = ComboBox_OpenAIModel.Text;
            _plugin.Settings.OpenAI.Url = TextBox_OpenAIUrl.Text;
            
            // 保存OpenAI高级配置
            _plugin.Settings.OpenAI.EnableAdvanced = CheckBox_OpenAI_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.OpenAI.Temperature = Slider_OpenAI_Temperature.Value;
            if (int.TryParse(TextBox_OpenAI_MaxTokens.Text, out int maxTokens))
            {
                _plugin.Settings.OpenAI.MaxTokens = maxTokens;
            }
            _plugin.Settings.Gemini.ApiKey = TextBox_GeminiApiKey.Text;
            _plugin.Settings.Gemini.Model = ComboBox_GeminiModel.Text;
            _plugin.Settings.Gemini.Url = TextBox_GeminiUrl.Text;
            
            // 保存Gemini高级配置
            _plugin.Settings.Gemini.EnableAdvanced = CheckBox_Gemini_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.Gemini.Temperature = Slider_Gemini_Temperature.Value;
            if (int.TryParse(TextBox_Gemini_MaxTokens.Text, out int geminiMaxTokens))
            {
                _plugin.Settings.Gemini.MaxTokens = geminiMaxTokens;
            }
            
            // 保存Ollama高级配置
            _plugin.Settings.Ollama.EnableAdvanced = CheckBox_Ollama_EnableAdvanced.IsChecked ?? false;
            _plugin.Settings.Ollama.Temperature = Slider_Ollama_Temperature.Value;
            if (int.TryParse(TextBox_Ollama_MaxTokens.Text, out int ollamaMaxTokens))
            {
                _plugin.Settings.Ollama.MaxTokens = ollamaMaxTokens;
            }
            
            // 保存角色设定
            _plugin.Settings.Role = TextBox_Role.Text;
            
            _plugin.Settings.Save();
            Logger.Log("Settings saved to file.");
            _plugin.ChatCore = null;
            switch (_plugin.Settings.Provider)
            {
                case Setting.LLMType.Ollama:
                    _plugin.ChatCore = new Core.OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings);
                    Logger.Log("Chat core set to Ollama.");
                    break;
                case Setting.LLMType.OpenAI:
                    _plugin.ChatCore = new Core.OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings);
                    Logger.Log("Chat core set to OpenAI.");
                    break;
                case Setting.LLMType.Gemini:
                    _plugin.ChatCore = new Core.GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings);
                    Logger.Log("Chat core set to Gemini.");
                    break;
            }
            // 保存后不关闭界面，方便调试
            Logger.Log("Settings saved successfully. Window remains open for debugging.");
        }

        private void ComboBox_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProviderVisibility();
        }

        private void UpdateProviderVisibility()
        {
            Grid_Ollama.Visibility = Visibility.Collapsed;
            Grid_OpenAI.Visibility = Visibility.Collapsed;
            Grid_Gemini.Visibility = Visibility.Collapsed;
            Button_RefreshOllamaModels.IsEnabled = false;
            Button_RefreshOpenAIModels.IsEnabled = false;
            Button_RefreshGeminiModels.IsEnabled = false;

            if (ComboBox_Provider.SelectedItem == null) return;

            switch ((Setting.LLMType)ComboBox_Provider.SelectedItem)
            {
                case Setting.LLMType.Ollama:
                    Grid_Ollama.Visibility = Visibility.Visible;
                    Button_RefreshOllamaModels.IsEnabled = true;
                    break;
                case Setting.LLMType.OpenAI:
                    Grid_OpenAI.Visibility = Visibility.Visible;
                    Button_RefreshOpenAIModels.IsEnabled = true;
                    break;
                case Setting.LLMType.Gemini:
                    Grid_Gemini.Visibility = Visibility.Visible;
                    Button_RefreshGeminiModels.IsEnabled = true;
                    break;
            }
            Logger.Log($"Provider view changed to {ComboBox_Provider.SelectedItem}.");
        }

        private void Button_RefreshOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 创建临时的OllamaChatCore实例来获取模型列表
                var ollamaSettings = new Setting.OllamaSetting
                {
                    Url = TextBox_OllamaUrl.Text
                };
                var ollamaCore = new Core.OllamaChatCore(ollamaSettings, _plugin.Settings);
                ComboBox_OllamaModel.ItemsSource = ollamaCore.GetModels();
                Logger.Log("Ollama models refreshed.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to refresh Ollama models: {ex.Message}");
            }
        }

        private void Button_CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var logText = string.Join(Environment.NewLine, Logger.Logs);
            Clipboard.SetText(logText);
            Logger.Log("Log copied to clipboard.");
        }

        private void Button_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            Logger.Logs.Clear();
            Logger.Log("Log cleared.");
        }

        private void Button_RefreshOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 创建临时的OpenAIChatCore实例来获取模型列表
                var openAISettings = new Setting.OpenAISetting
                {
                    ApiKey = TextBox_OpenAIApiKey.Text,
                    Url = TextBox_OpenAIUrl.Text
                };
                var openAICore = new Core.OpenAIChatCore(openAISettings, _plugin.Settings);
                ComboBox_OpenAIModel.ItemsSource = openAICore.GetModels();
                Logger.Log("OpenAI models refreshed.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to refresh OpenAI models: {ex.Message}");
            }
        }

        private void Button_RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 创建临时的GeminiChatCore实例来获取模型列表
                var geminiSettings = new Setting.GeminiSetting
                {
                    ApiKey = TextBox_GeminiApiKey.Text,
                    Url = TextBox_GeminiUrl.Text
                };
                var geminiCore = new Core.GeminiChatCore(geminiSettings, _plugin.Settings);
                ComboBox_GeminiModel.ItemsSource = geminiCore.GetModels();
                Logger.Log("Gemini models refreshed.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to refresh Gemini models: {ex.Message}");
            }
        }

        private void Button_RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBox_Provider.SelectedItem == null) return;

            // 保存当前选择的提供商
            var currentProvider = (Setting.LLMType)ComboBox_Provider.SelectedItem;

            switch (currentProvider)
            {
                case Setting.LLMType.Ollama:
                    _plugin.Settings.Ollama = new Setting.OllamaSetting();
                    Logger.Log("Ollama settings restored to defaults.");
                    break;
                case Setting.LLMType.OpenAI:
                    _plugin.Settings.OpenAI = new Setting.OpenAISetting();
                    Logger.Log("OpenAI settings restored to defaults.");
                    break;
                case Setting.LLMType.Gemini:
                    _plugin.Settings.Gemini = new Setting.GeminiSetting();
                    Logger.Log("Gemini settings restored to defaults.");
                    break;
            }
            
            // 重新加载设置但不重新设置提供商选择
            LoadSettingsWithoutChangingProvider(currentProvider);
        }

        private void LoadSettingsWithoutChangingProvider(Setting.LLMType currentProvider)
        {
            Logger.Log("Loading settings without changing provider.");
            
            // 只更新特定提供商的设置，不改变当前选择的提供商
            TextBox_OllamaUrl.Text = _plugin.Settings.Ollama.Url;
            ComboBox_OllamaModel.Text = _plugin.Settings.Ollama.Model;
            if (_plugin.ChatCore is Core.OllamaChatCore ollamaCore)
            {
                ComboBox_OllamaModel.ItemsSource = ollamaCore.GetModels();
            }
            
            TextBox_OpenAIApiKey.Text = _plugin.Settings.OpenAI.ApiKey;
            ComboBox_OpenAIModel.Text = _plugin.Settings.OpenAI.Model;
            TextBox_OpenAIUrl.Text = _plugin.Settings.OpenAI.Url;
            
            // 初始化OpenAI高级配置
            CheckBox_OpenAI_EnableAdvanced.IsChecked = _plugin.Settings.OpenAI.EnableAdvanced;
            Slider_OpenAI_Temperature.Value = _plugin.Settings.OpenAI.Temperature;
            TextBlock_OpenAI_TemperatureValue.Text = _plugin.Settings.OpenAI.Temperature.ToString("F2");
            TextBox_OpenAI_MaxTokens.Text = _plugin.Settings.OpenAI.MaxTokens.ToString();
            
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            ComboBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
            TextBox_GeminiUrl.Text = _plugin.Settings.Gemini.Url;
            
            // 初始化Gemini高级配置
            CheckBox_Gemini_EnableAdvanced.IsChecked = _plugin.Settings.Gemini.EnableAdvanced;
            Slider_Gemini_Temperature.Value = _plugin.Settings.Gemini.Temperature;
            TextBlock_Gemini_TemperatureValue.Text = _plugin.Settings.Gemini.Temperature.ToString("F2");
            TextBox_Gemini_MaxTokens.Text = _plugin.Settings.Gemini.MaxTokens.ToString();
            
            // 初始化Ollama高级配置
            CheckBox_Ollama_EnableAdvanced.IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            Slider_Ollama_Temperature.Value = _plugin.Settings.Ollama.Temperature;
            TextBlock_Ollama_TemperatureValue.Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            TextBox_Ollama_MaxTokens.Text = _plugin.Settings.Ollama.MaxTokens.ToString();
            
            // 保持当前选择的提供商不变
            ComboBox_Provider.SelectedItem = currentProvider;
            UpdateProviderVisibility();
            Logger.Log("Settings loaded without changing provider.");
        }

        private void Slider_OpenAI_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TextBlock_OpenAI_TemperatureValue.Text = Slider_OpenAI_Temperature.Value.ToString("F2");
        }

        private void Slider_Gemini_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TextBlock_Gemini_TemperatureValue.Text = Slider_Gemini_Temperature.Value.ToString("F2");
        }

        private void Slider_Ollama_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TextBlock_Ollama_TemperatureValue.Text = Slider_Ollama_Temperature.Value.ToString("F2");
        }

        private void Button_ClearContext_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ChatCore != null)
            {
                _plugin.ChatCore.ClearContext();
                Logger.Log("Context cleared.");
            }
            else
            {
                Logger.Log("No active chat core to clear context.");
            }
        }
    }
}