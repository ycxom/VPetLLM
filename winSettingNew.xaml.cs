using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPetLLM.Utils;

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
            ((Slider)this.FindName("Slider_Ollama_Temperature")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_OpenAI_Temperature")).ValueChanged += Control_ValueChanged;
            ((Slider)this.FindName("Slider_Gemini_Temperature")).ValueChanged += Control_ValueChanged;

            // Add event handlers for all other controls
            ((ComboBox)this.FindName("ComboBox_Provider")).SelectionChanged += Control_SelectionChanged;
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
        }

        private void Control_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveSettings();
        private void Control_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettings();
        private void Control_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();
        private void Control_Click(object sender, RoutedEventArgs e) => SaveSettings();

        private void LoadSettings()
        {
            ((ComboBox)this.FindName("ComboBox_Provider")).ItemsSource = Enum.GetValues(typeof(Setting.LLMType));
            ((ComboBox)this.FindName("ComboBox_Provider")).SelectedItem = _plugin.Settings.Provider;
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
            ((DataGrid)this.FindName("DataGrid_Plugins")).ItemsSource = _plugin.Plugins;
        }

        private void SaveSettings()
        {
            var providerComboBox = (ComboBox)this.FindName("ComboBox_Provider");
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

        private void Button_RefreshOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ollamaCore = new OllamaChatCore(_plugin.Settings.Ollama, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = ollamaCore.RefreshModels();
                ((ComboBox)this.FindName("ComboBox_OllamaModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_OllamaModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_OllamaModel")).SelectedIndex = 0;
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
                var openAICore = new OpenAIChatCore(_plugin.Settings.OpenAI, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = openAICore.RefreshModels();
                ((ComboBox)this.FindName("ComboBox_OpenAIModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_OpenAIModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_OpenAIModel")).SelectedIndex = 0;
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
                var geminiCore = new GeminiChatCore(_plugin.Settings.Gemini, _plugin.Settings, _plugin.MW, _plugin.ActionProcessor);
                var models = geminiCore.RefreshModels();
                ((ComboBox)this.FindName("ComboBox_GeminiModel")).ItemsSource = models;
                if (models.Count > 0 && string.IsNullOrEmpty(((ComboBox)this.FindName("ComboBox_GeminiModel")).Text))
                    ((ComboBox)this.FindName("ComboBox_GeminiModel")).SelectedIndex = 0;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"刷新Gemini模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_ClearContext_Click(object sender, RoutedEventArgs e) { _plugin.ChatCore?.ClearContext(); }
        private void Button_EditContext_Click(object sender, RoutedEventArgs e)
        {
            var contextEditor = new winContextEditor(_plugin);
            contextEditor.Show();
        }
        private void Button_CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var logBox = (ListBox)this.FindName("LogBox");
            var sb = new System.Text.StringBuilder();
            foreach (var item in logBox.Items)
            {
                sb.AppendLine(item.ToString());
            }
            Clipboard.SetText(sb.ToString());
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

        private void Button_LoadPlugins_Click(object sender, RoutedEventArgs e)
        {
            _plugin.LoadPlugins();
            ((ListBox)this.FindName("ListBox_Plugins")).ItemsSource = null;
            var pluginDisplayList = new List<string>();
            foreach (var plugin in _plugin.Plugins)
            {
                pluginDisplayList.Add($"{plugin.Name}【{plugin.Description}】");
            }
            ((ListBox)this.FindName("ListBox_Plugins")).ItemsSource = pluginDisplayList;
        }

        private void Button_UnloadPlugin_Click(object sender, RoutedEventArgs e)
        {
            var dataGrid = (DataGrid)this.FindName("DataGrid_Plugins");
            if (dataGrid.SelectedItem is IVPetLLMPlugin selectedPlugin)
            {
                _plugin.UnloadPlugin(selectedPlugin);
                if (System.IO.File.Exists(selectedPlugin.FilePath))
                {
                    System.IO.File.Delete(selectedPlugin.FilePath);
                }
                dataGrid.ItemsSource = null;
                dataGrid.ItemsSource = _plugin.Plugins;
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
                var dataGrid = (DataGrid)this.FindName("DataGrid_Plugins");
                dataGrid.ItemsSource = null;
                dataGrid.ItemsSource = _plugin.Plugins;
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
    }
}