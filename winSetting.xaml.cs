using System;
using System.Windows;

namespace VPetLLM
{
    public partial class winSetting : Window
    {
        private readonly VPetLLM _plugin;

        public winSetting(VPetLLM plugin)
        {
            _plugin = plugin;
            InitializeComponent();
            ComboBox_Provider.ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            LoadSettings();
        }

        private void LoadSettings()
        {
            ComboBox_Provider.SelectedItem = _plugin.Settings.Provider;
            TextBox_OllamaUrl.Text = _plugin.Settings.Ollama.Url;
            ComboBox_OllamaModel.Text = _plugin.Settings.Ollama.Model;
            TextBox_OpenAIApiKey.Text = _plugin.Settings.OpenAI.ApiKey;
            TextBox_OpenAIModel.Text = _plugin.Settings.OpenAI.Model;
            TextBox_OpenAIUrl.Text = _plugin.Settings.OpenAI.Url;
            TextBox_GeminiApiKey.Text = _plugin.Settings.Gemini.ApiKey;
            TextBox_GeminiModel.Text = _plugin.Settings.Gemini.Model;
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Provider = (Setting.LLMType)ComboBox_Provider.SelectedItem;
            _plugin.Settings.Ollama.Url = TextBox_OllamaUrl.Text;
            _plugin.Settings.Ollama.Model = ComboBox_OllamaModel.Text;
            _plugin.Settings.OpenAI.ApiKey = TextBox_OpenAIApiKey.Text;
            _plugin.Settings.OpenAI.Model = TextBox_OpenAIModel.Text;
            _plugin.Settings.OpenAI.Url = TextBox_OpenAIUrl.Text;
            _plugin.Settings.Gemini.ApiKey = TextBox_GeminiApiKey.Text;
            _plugin.Settings.Gemini.Model = TextBox_GeminiModel.Text;
            _plugin.Settings.Save();
            _plugin.ChatCore = null;
            switch (_plugin.Settings.Provider)
            {
                case Setting.LLMType.Ollama:
                    _plugin.ChatCore = new OllamaChatCore(_plugin.Settings.Ollama);
                    break;
                case Setting.LLMType.OpenAI:
                    _plugin.ChatCore = new OpenAIChatCore(_plugin.Settings.OpenAI);
                    break;
                case Setting.LLMType.Gemini:
                    _plugin.ChatCore = new GeminiChatCore(_plugin.Settings.Gemini);
                    break;
            }
            Close();
        }
    }
}