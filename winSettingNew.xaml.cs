using System;
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
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            ComboBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
            TextBox_GeminiUrl.Text = _plugin.Settings.Gemini.Url;
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
            _plugin.Settings.Gemini.ApiKey = TextBox_GeminiApiKey.Text;
            _plugin.Settings.Gemini.Model = ComboBox_GeminiModel.Text;
            _plugin.Settings.Gemini.Url = TextBox_GeminiUrl.Text;
            _plugin.Settings.Save();
            Logger.Log("Settings saved to file.");
            _plugin.ChatCore = null;
            switch (_plugin.Settings.Provider)
            {
                case Setting.LLMType.Ollama:
                    _plugin.ChatCore = new Core.OllamaChatCore(_plugin.Settings.Ollama);
                    Logger.Log("Chat core set to Ollama.");
                    break;
                case Setting.LLMType.OpenAI:
                    _plugin.ChatCore = new Core.OpenAIChatCore(_plugin.Settings.OpenAI);
                    Logger.Log("Chat core set to OpenAI.");
                    break;
                case Setting.LLMType.Gemini:
                    _plugin.ChatCore = new Core.GeminiChatCore(_plugin.Settings.Gemini);
                    Logger.Log("Chat core set to Gemini.");
                    break;
            }
            Close();
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
            if (_plugin.ChatCore is Core.OllamaChatCore ollamaCore)
            {
                try
                {
                    ComboBox_OllamaModel.ItemsSource = ollamaCore.GetModels();
                    Logger.Log("Ollama models refreshed.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to refresh Ollama models: {ex.Message}");
                }
            }
            else
            {
                Logger.Log("Cannot refresh Ollama models: chat core is not Ollama.");
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
            if (_plugin.ChatCore is Core.OpenAIChatCore openAICore)
            {
                try
                {
                    ComboBox_OpenAIModel.ItemsSource = openAICore.GetModels();
                    Logger.Log("OpenAI models refreshed.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to refresh OpenAI models: {ex.Message}");
                }
            }
            else
            {
                Logger.Log("Cannot refresh OpenAI models: chat core is not OpenAI.");
            }
        }

        private void Button_RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ChatCore is Core.GeminiChatCore geminiCore)
            {
                try
                {
                    ComboBox_GeminiModel.ItemsSource = geminiCore.GetModels();
                    Logger.Log("Gemini models refreshed.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to refresh Gemini models: {ex.Message}");
                }
            }
            else
            {
                Logger.Log("Cannot refresh Gemini models: chat core is not Gemini.");
            }
        }

        private void Button_RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBox_Provider.SelectedItem == null) return;

            switch ((Setting.LLMType)ComboBox_Provider.SelectedItem)
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
            LoadSettings();
        }
    }
}