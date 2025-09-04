using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LinePutScript.Localization.WPF;
using VPetLLM.Core;
using System.Windows.Media;

namespace VPetLLM
{
    public partial class winSettingNew : Window
    {
        private readonly VPetLLM _plugin;
        private bool isModified = false;

        public winSettingNew(VPetLLM plugin)
        {
            _plugin = plugin;
            InitializeComponent();
            
            // 跟随VPet的语言设置
            // LocalizeCore 方法需要进一步调查正确用法
            
            // 注册温度滑块值变化事件
            Slider_OpenAI_Temperature.ValueChanged += Setting_Changed;
            Slider_Gemini_Temperature.ValueChanged += Setting_Changed;
            Slider_Ollama_Temperature.ValueChanged += Setting_Changed;

            // 注册文本框和复选框的更改事件
            foreach (var textBox in FindVisualChildren<TextBox>(this))
            {
                textBox.TextChanged += Setting_Changed;
            }
            foreach (var comboBox in FindVisualChildren<ComboBox>(this))
            {
                comboBox.SelectionChanged += Setting_Changed;
            }
            foreach (var checkBox in FindVisualChildren<CheckBox>(this))
            {
                checkBox.Checked += Setting_Changed;
                checkBox.Unchecked += Setting_Changed;
            }

            Logger.Log("Setting window opened.");
            ComboBox_Provider.ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            LogBox.ItemsSource = Logger.Logs;
            LoadSettings();
        }

        private void LoadSettings()
        {
            isModified = false;
            TextBlock_Unsaved.Visibility = Visibility.Collapsed;
            Logger.Log("Loading settings.");
            ComboBox_Provider.SelectedItem = _plugin.Settings.Provider;
            TextBox_OllamaUrl.Text = _plugin.Settings.Ollama.Url;
            ComboBox_OllamaModel.Text = _plugin.Settings.Ollama.Model;
            if (_plugin.ChatCore is Core.OllamaChatCore ollamaCore)
            {
                // 不再自动获取模型列表，改为用户手动刷新
                ComboBox_OllamaModel.ItemsSource = new List<string>();
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
            
            // 加载聊天历史保存设置
            CheckBox_EnableChatHistory.IsChecked = _plugin.Settings.EnableChatHistory;
            CheckBox_SeparateChatByProvider.IsChecked = _plugin.Settings.SeparateChatByProvider;
            CheckBox_AutoMigrateChatHistory.IsChecked = _plugin.Settings.AutoMigrateChatHistory;
            
            // 加载日志设置
            CheckBox_LogAutoScroll.IsChecked = _plugin.Settings.LogAutoScroll;
            TextBox_MaxLogCount.Text = _plugin.Settings.MaxLogCount.ToString();
            
            // 初始化Ollama高级配置
            CheckBox_Ollama_EnableAdvanced.IsChecked = _plugin.Settings.Ollama.EnableAdvanced;
            Slider_Ollama_Temperature.Value = _plugin.Settings.Ollama.Temperature;
            TextBlock_Ollama_TemperatureValue.Text = _plugin.Settings.Ollama.Temperature.ToString("F2");
            TextBox_Ollama_MaxTokens.Text = _plugin.Settings.Ollama.MaxTokens.ToString();
            
            // 不再自动刷新模型列表，改为用户手动刷新
            // 初始化时只显示空列表，用户需要手动点击刷新按钮
            ComboBox_GeminiModel.ItemsSource = new List<string>();
            Logger.Log("Settings loaded.");
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("Saving settings.");
            isModified = false;
            TextBlock_Unsaved.Visibility = Visibility.Collapsed;
            
            // 在保存设置之前获取旧的提供商值
            var oldProvider = _plugin.Settings.Provider;
            var newProvider = (Setting.LLMType)ComboBox_Provider.SelectedItem;
            
            // 更新设置
            _plugin.Settings.Provider = newProvider;
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
            
            // 保存聊天历史保存设置
            _plugin.Settings.EnableChatHistory = CheckBox_EnableChatHistory.IsChecked ?? true;
            _plugin.Settings.SeparateChatByProvider = CheckBox_SeparateChatByProvider.IsChecked ?? true;
            _plugin.Settings.AutoMigrateChatHistory = CheckBox_AutoMigrateChatHistory.IsChecked ?? true;

            // 保存日志设置
            _plugin.Settings.LogAutoScroll = CheckBox_LogAutoScroll.IsChecked ?? true;
            if (int.TryParse(TextBox_MaxLogCount.Text, out int maxLogCount) && maxLogCount > 0)
            {
                _plugin.Settings.MaxLogCount = maxLogCount;
            }

            // 应用日志设置 - 现在Logger类直接从Setting.Instance读取设置值
            
            // 保存当前聊天历史记录（如果启用）
            if (_plugin.ChatCore != null && _plugin.Settings.EnableChatHistory)
            {
                _plugin.ChatCore.SaveHistory();
            }

            _plugin.Settings.Save();
            Logger.Log("Settings saved to file.");
            
            if (oldProvider != newProvider)
            {
                Logger.Log($"Provider changed from {oldProvider} to {newProvider}");
                
                // 保存当前历史记录以便后续恢复
                var oldHistory = _plugin.ChatCore?.GetChatHistory();
                
                IChatCore? newChatCore = null;
                switch (newProvider)
                {
                    case Setting.LLMType.Ollama:
                        newChatCore = new Core.OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings);
                        Logger.Log("New chat core created for Ollama.");
                        break;
                    case Setting.LLMType.OpenAI:
                        newChatCore = new Core.OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings);
                        Logger.Log("New chat core created for OpenAI.");
                        break;
                    case Setting.LLMType.Gemini:
                        newChatCore = new Core.GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings);
                        Logger.Log("New chat core created for Gemini.");
                        break;
                }
                
                // 如果启用了聊天历史记录，尝试恢复历史记录
                if (_plugin.Settings.EnableChatHistory && oldHistory != null && oldHistory.Count > 0)
                {
                    newChatCore?.SetChatHistory(oldHistory);
                    Logger.Log($"Restored {oldHistory.Count} messages from previous provider");
                }
                
                // 更新主类的ChatCore实例
                _plugin.UpdateChatCore(newChatCore);
                Logger.Log("Chat core instance updated in main plugin.");
            }
            else
            {
                Logger.Log("Provider unchanged, keeping current chat core instance.");
            }
            // 保存后不关闭界面，方便调试
            Logger.Log("Settings saved successfully. Window remains open for debugging.");
        }

        private void ComboBox_Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
                var models = ollamaCore.RefreshModels();
                ComboBox_OllamaModel.ItemsSource = models;
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
                var models = openAICore.RefreshModels();
                ComboBox_OpenAIModel.ItemsSource = models;
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
                var models = geminiCore.RefreshModels();
                ComboBox_GeminiModel.ItemsSource = models;
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
            isModified = false;
            TextBlock_Unsaved.Visibility = Visibility.Collapsed;

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
                // 不再自动获取模型列表，改为用户手动刷新
                ComboBox_OllamaModel.ItemsSource = new List<string>();
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
            
            // 加载聊天历史保存设置
            CheckBox_EnableChatHistory.IsChecked = _plugin.Settings.EnableChatHistory;
            CheckBox_SeparateChatByProvider.IsChecked = _plugin.Settings.SeparateChatByProvider;
            CheckBox_AutoMigrateChatHistory.IsChecked = _plugin.Settings.AutoMigrateChatHistory;
            
            // 保持当前选择的提供商不变
            ComboBox_Provider.SelectedItem = currentProvider;
            Logger.Log("Settings loaded without changing provider.");
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (this.IsLoaded)
            {
                isModified = true;
                TextBlock_Unsaved.Visibility = Visibility.Visible;
            }
        }
        private void Slider_OpenAI_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.IsLoaded)
            {
                TextBlock_OpenAI_TemperatureValue.Text = e.NewValue.ToString("F2");
                Setting_Changed(sender, e);
            }
        }

        private void Slider_Gemini_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.IsLoaded)
            {
                TextBlock_Gemini_TemperatureValue.Text = e.NewValue.ToString("F2");
                Setting_Changed(sender, e);
            }
        }

        private void Slider_Ollama_Temperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.IsLoaded)
            {
                TextBlock_Ollama_TemperatureValue.Text = e.NewValue.ToString("F2");
                Setting_Changed(sender, e);
            }
        }

        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T)
                    {
                        yield return (T)child;
                    }

                    foreach (T childOfChild in FindVisualChildren<T>(child))
                    {
                        yield return childOfChild;
                    }
                }
            }
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

        private void Button_EditContext_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ChatCore != null)
            {
                // 打开上下文编辑窗口
                var contextEditor = new ContextEditorWindow(_plugin.ChatCore);
                contextEditor.ShowDialog();
                Logger.Log("Context editor opened.");
            }
            else
            {
                Logger.Log("No active chat core to edit context.");
            }
        }
    }
}