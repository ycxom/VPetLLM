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
            TextBox_OpenAIApiKey.Text = _plugin.Settings.OpenAI.ApiKey;
            TextBox_OpenAIModel.Text = _plugin.Settings.OpenAI.Model;
            TextBox_OpenAIUrl.Text = _plugin.Settings.OpenAI.Url;
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            TextBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
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
            _plugin.Settings.OpenAI.Model = TextBox_OpenAIModel.Text;
            _plugin.Settings.OpenAI.Url = TextBox_OpenAIUrl.Text;
            _plugin.Settings.Gemini.ApiKey = TextBox_GeminiApiKey.Text;
            _plugin.Settings.Gemini.Model = TextBox_GeminiModel.Text;
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

            if (ComboBox_Provider.SelectedItem == null) return;

            switch ((Setting.LLMType)ComboBox_Provider.SelectedItem)
            {
                case Setting.LLMType.Ollama:
                    Grid_Ollama.Visibility = Visibility.Visible;
                    break;
                case Setting.LLMType.OpenAI:
                    Grid_OpenAI.Visibility = Visibility.Visible;
                    break;
                case Setting.LLMType.Gemini:
                    Grid_Gemini.Visibility = Visibility.Visible;
                    break;
            }
            Logger.Log($"Provider view changed to {ComboBox_Provider.SelectedItem}.");
        }
    }
}