using System.Windows;
using System.Windows.Controls;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// GPT-SoVITS TTS 相关的事件处理方法（winSettingNew 的部分类）
    /// </summary>
    public partial class winSettingNew
    {
        // 存储当前加载的模型数据
        private Dictionary<string, Dictionary<string, List<string>>> _currentModels = new Dictionary<string, Dictionary<string, List<string>>>();

        /// <summary>
        /// API 模式选择变化事件
        /// </summary>
        private void ComboBox_TTS_GPTSoVITS_ApiMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 初始化时 _plugin 可能为空，跳过
            if (_plugin is null || _plugin.Settings?.TTS?.GPTSoVITS is null)
                return;

            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var mode = selectedItem.Tag?.ToString();
                var isApiV2 = mode == "ApiV2";

                // 切换面板可见性
                var webUIPanel = (StackPanel)this.FindName("Panel_TTS_GPTSoVITS_WebUI");
                var apiV2Panel = (StackPanel)this.FindName("Panel_TTS_GPTSoVITS_ApiV2");

                if (webUIPanel is not null)
                    webUIPanel.Visibility = isApiV2 ? Visibility.Collapsed : Visibility.Visible;
                if (apiV2Panel is not null)
                    apiV2Panel.Visibility = isApiV2 ? Visibility.Visible : Visibility.Collapsed;

                // 保存设置
                _plugin.Settings.TTS.GPTSoVITS.ApiMode = isApiV2
                    ? Setting.GPTSoVITSApiMode.ApiV2
                    : Setting.GPTSoVITSApiMode.WebUI;
                ScheduleAutoSave();
            }
        }

        /// <summary>
        /// 应用 GPT 模型权重按钮点击事件
        /// </summary>
        private async void Button_TTS_GPTSoVITS_ApplyGptWeights_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            try
            {
                StartButtonLoadingAnimation(button);

                var weightsPath = ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_GptWeightsPath"))?.Text;
                if (string.IsNullOrWhiteSpace(weightsPath))
                {
                    MessageBox.Show("请输入 GPT 模型权重路径", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var ttsCore = new GPTSoVITSTTSCore(_plugin.Settings);
                var success = await ttsCore.SetGptWeightsAsync(weightsPath);

                if (success)
                {
                    _plugin.Settings.TTS.GPTSoVITS.GptWeightsPath = weightsPath;
                    ScheduleAutoSave();
                    MessageBox.Show("GPT 模型切换成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("GPT 模型切换失败，请检查路径是否正确", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"应用 GPT 模型权重失败: {ex.Message}");
                MessageBox.Show($"应用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        /// <summary>
        /// 应用 SoVITS 模型权重按钮点击事件
        /// </summary>
        private async void Button_TTS_GPTSoVITS_ApplySovitsWeights_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            try
            {
                StartButtonLoadingAnimation(button);

                var weightsPath = ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_SovitsWeightsPath"))?.Text;
                if (string.IsNullOrWhiteSpace(weightsPath))
                {
                    MessageBox.Show("请输入 SoVITS 模型权重路径", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var ttsCore = new GPTSoVITSTTSCore(_plugin.Settings);
                var success = await ttsCore.SetSovitsWeightsAsync(weightsPath);

                if (success)
                {
                    _plugin.Settings.TTS.GPTSoVITS.SovitsWeightsPath = weightsPath;
                    ScheduleAutoSave();
                    MessageBox.Show("SoVITS 模型切换成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("SoVITS 模型切换失败，请检查路径是否正确", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"应用 SoVITS 模型权重失败: {ex.Message}");
                MessageBox.Show($"应用失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        /// <summary>
        /// 检测版本按钮点击事件
        /// </summary>
        private async void Button_TTS_GPTSoVITS_DetectVersion_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            try
            {
                StartButtonLoadingAnimation(button);

                var baseUrl = ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl")).Text;
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    MessageBox.Show("请先输入 API 地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 临时创建 TTS Core 实例来获取版本信息
                // 使用当前设置的副本，只修改 BaseUrl
                var tempSettings = _plugin.Settings;
                var originalBaseUrl = tempSettings.TTS.GPTSoVITS.BaseUrl;
                tempSettings.TTS.GPTSoVITS.BaseUrl = baseUrl;
                var ttsCore = new GPTSoVITSTTSCore(tempSettings);
                // 恢复原始 BaseUrl
                tempSettings.TTS.GPTSoVITS.BaseUrl = originalBaseUrl;

                var versions = await ttsCore.GetSupportedVersionsAsync();

                if (versions is null || versions.Count == 0)
                {
                    MessageBox.Show("无法获取版本信息，请检查 API 地址是否正确", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 更新版本下拉框
                var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");
                versionComboBox.Items.Clear();

                foreach (var version in versions)
                {
                    var item = new ComboBoxItem
                    {
                        Content = version,
                        Tag = version
                    };
                    versionComboBox.Items.Add(item);
                }

                // 选择最新版本（通常是最后一个）
                if (versionComboBox.Items.Count > 0)
                {
                    versionComboBox.SelectedIndex = versionComboBox.Items.Count - 1;
                }

                MessageBox.Show($"检测到 {versions.Count} 个支持的版本", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"检测版本失败: {ex.Message}");
                MessageBox.Show($"检测版本失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        /// <summary>
        /// 版本选择变化事件
        /// </summary>
        private async void ComboBox_TTS_GPTSoVITS_Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var version = selectedItem.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(version))
                    return;

                try
                {
                    var baseUrl = ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl")).Text;
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        return;

                    // 自动加载该版本的模型
                    var tempSettings = _plugin.Settings;
                    var originalBaseUrl = tempSettings.TTS.GPTSoVITS.BaseUrl;
                    var originalVersion = tempSettings.TTS.GPTSoVITS.Version;
                    tempSettings.TTS.GPTSoVITS.BaseUrl = baseUrl;
                    tempSettings.TTS.GPTSoVITS.Version = version;
                    var ttsCore = new GPTSoVITSTTSCore(tempSettings);
                    // 恢复原始值
                    tempSettings.TTS.GPTSoVITS.BaseUrl = originalBaseUrl;
                    tempSettings.TTS.GPTSoVITS.Version = originalVersion;

                    _currentModels = await ttsCore.GetModelsAsync(version);

                    // 更新模型下拉框
                    var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
                    modelComboBox.Items.Clear();

                    if (_currentModels is not null && _currentModels.Count > 0)
                    {
                        foreach (var modelName in _currentModels.Keys)
                        {
                            var item = new ComboBoxItem
                            {
                                Content = modelName,
                                Tag = modelName
                            };
                            modelComboBox.Items.Add(item);
                        }

                        // 选择第一个模型
                        if (modelComboBox.Items.Count > 0)
                        {
                            modelComboBox.SelectedIndex = 0;
                        }
                    }

                    // 保存版本设置
                    _plugin.Settings.TTS.GPTSoVITS.Version = version;
                    ScheduleAutoSave();
                }
                catch (Exception ex)
                {
                    Logger.Log($"加载模型列表失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 刷新模型按钮点击事件
        /// </summary>
        private async void Button_TTS_GPTSoVITS_RefreshModels_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            try
            {
                StartButtonLoadingAnimation(button);

                var baseUrl = ((TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl")).Text;
                var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    MessageBox.Show("请先输入 API 地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (versionComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    var version = selectedItem.Tag?.ToString();
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        MessageBox.Show("请先选择版本", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var tempSettings = _plugin.Settings;
                    var originalBaseUrl = tempSettings.TTS.GPTSoVITS.BaseUrl;
                    var originalVersion = tempSettings.TTS.GPTSoVITS.Version;
                    tempSettings.TTS.GPTSoVITS.BaseUrl = baseUrl;
                    tempSettings.TTS.GPTSoVITS.Version = version;
                    var ttsCore = new GPTSoVITSTTSCore(tempSettings);
                    // 恢复原始值
                    tempSettings.TTS.GPTSoVITS.BaseUrl = originalBaseUrl;
                    tempSettings.TTS.GPTSoVITS.Version = originalVersion;

                    _currentModels = await ttsCore.GetModelsAsync(version);

                    var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
                    modelComboBox.Items.Clear();

                    if (_currentModels is not null && _currentModels.Count > 0)
                    {
                        foreach (var modelName in _currentModels.Keys)
                        {
                            var item = new ComboBoxItem
                            {
                                Content = modelName,
                                Tag = modelName
                            };
                            modelComboBox.Items.Add(item);
                        }

                        if (modelComboBox.Items.Count > 0)
                        {
                            modelComboBox.SelectedIndex = 0;
                        }

                        MessageBox.Show($"成功加载 {_currentModels.Count} 个模型", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("未找到可用模型", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"刷新模型失败: {ex.Message}");
                MessageBox.Show($"刷新模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButtonLoadingAnimation(button);
            }
        }

        /// <summary>
        /// 模型选择变化事件
        /// </summary>
        private void ComboBox_TTS_GPTSoVITS_Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var modelName = selectedItem.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(modelName))
                    return;

                // 更新语言下拉框
                var languageComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Language");
                var emotionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Emotion");

                languageComboBox.Items.Clear();
                emotionComboBox.Items.Clear();

                if (_currentModels is not null && _currentModels.ContainsKey(modelName))
                {
                    var languages = _currentModels[modelName];

                    // 添加语言选项
                    foreach (var lang in languages.Keys)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = lang,
                            Tag = lang
                        };
                        languageComboBox.Items.Add(item);
                    }

                    // 选择第一个语言
                    if (languageComboBox.Items.Count > 0)
                    {
                        languageComboBox.SelectedIndex = 0;

                        // 获取第一个语言的情感列表
                        var firstLang = languages.Keys.First();
                        if (languages.ContainsKey(firstLang))
                        {
                            foreach (var emotion in languages[firstLang])
                            {
                                var emotionItem = new ComboBoxItem
                                {
                                    Content = emotion,
                                    Tag = emotion
                                };
                                emotionComboBox.Items.Add(emotionItem);
                            }

                            if (emotionComboBox.Items.Count > 0)
                            {
                                emotionComboBox.SelectedIndex = 0;
                            }
                        }

                        // 保存 PromptLanguage（从模型数据中获取）
                        _plugin.Settings.TTS.GPTSoVITS.PromptLanguage = firstLang;
                    }
                }

                // 保存模型设置
                _plugin.Settings.TTS.GPTSoVITS.ModelName = modelName;
                ScheduleAutoSave();
            }
        }

        /// <summary>
        /// 语言选择变化事件
        /// </summary>
        private void ComboBox_TTS_GPTSoVITS_Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var language = selectedItem.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(language))
                    return;

                // 获取当前选择的模型
                var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
                var modelName = modelComboBox?.Text;

                if (string.IsNullOrWhiteSpace(modelName))
                    return;

                // 更新情感下拉框
                var emotionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Emotion");
                emotionComboBox.Items.Clear();

                if (_currentModels is not null &&
                    _currentModels.ContainsKey(modelName) &&
                    _currentModels[modelName].ContainsKey(language))
                {
                    foreach (var emotion in _currentModels[modelName][language])
                    {
                        var emotionItem = new ComboBoxItem
                        {
                            Content = emotion,
                            Tag = emotion
                        };
                        emotionComboBox.Items.Add(emotionItem);
                    }

                    if (emotionComboBox.Items.Count > 0)
                    {
                        emotionComboBox.SelectedIndex = 0;
                    }
                }

                // 保存 PromptLanguage
                _plugin.Settings.TTS.GPTSoVITS.PromptLanguage = language;
                ScheduleAutoSave();
            }
        }

        /// <summary>
        /// 加载 GPT-SoVITS 设置到 UI
        /// </summary>
        private void LoadGPTSoVITSSettings()
        {
            if (_plugin.Settings.TTS.GPTSoVITS is null)
            {
                _plugin.Settings.TTS.GPTSoVITS = new Setting.GPTSoVITSTTSSetting();
            }

            var settings = _plugin.Settings.TTS.GPTSoVITS;

            // 加载 API 模式
            var apiModeComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_ApiMode");
            if (apiModeComboBox is not null)
            {
                var modeTag = settings.ApiMode == Setting.GPTSoVITSApiMode.ApiV2 ? "ApiV2" : "WebUI";
                foreach (ComboBoxItem item in apiModeComboBox.Items)
                {
                    if (item.Tag?.ToString() == modeTag)
                    {
                        apiModeComboBox.SelectedItem = item;
                        break;
                    }
                }

                // 设置面板可见性
                var isApiV2 = settings.ApiMode == Setting.GPTSoVITSApiMode.ApiV2;
                var webUIPanel = (StackPanel)this.FindName("Panel_TTS_GPTSoVITS_WebUI");
                var apiV2Panel = (StackPanel)this.FindName("Panel_TTS_GPTSoVITS_ApiV2");
                if (webUIPanel is not null)
                    webUIPanel.Visibility = isApiV2 ? Visibility.Collapsed : Visibility.Visible;
                if (apiV2Panel is not null)
                    apiV2Panel.Visibility = isApiV2 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 加载基本设置
            var baseUrlTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl");
            if (baseUrlTextBox is not null)
            {
                baseUrlTextBox.Text = settings.BaseUrl;
            }

            // 加载 API v2 专用设置
            LoadGPTSoVITSApiV2Settings(settings);

            var cutPuncTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_CutPunc");
            if (cutPuncTextBox is not null)
            {
                cutPuncTextBox.Text = settings.CutPunc;
            }

            var topKTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopK");
            if (topKTextBox is not null)
            {
                topKTextBox.Text = settings.TopK.ToString();
            }

            var topPTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopP");
            if (topPTextBox is not null)
            {
                topPTextBox.Text = settings.TopP.ToString();
            }

            var temperatureTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Temperature");
            if (temperatureTextBox is not null)
            {
                temperatureTextBox.Text = settings.Temperature.ToString();
            }

            var speedTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Speed");
            if (speedTextBox is not null)
            {
                speedTextBox.Text = settings.Speed.ToString();
            }

            // 加载版本
            var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");
            if (versionComboBox is not null)
            {
                foreach (ComboBoxItem item in versionComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.Version)
                    {
                        versionComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 加载模型名称（如果有）
            var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
            if (modelComboBox is not null && !string.IsNullOrWhiteSpace(settings.ModelName))
            {
                modelComboBox.Text = settings.ModelName;
            }

            // 加载情感（如果有）
            var emotionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Emotion");
            if (emotionComboBox is not null && !string.IsNullOrWhiteSpace(settings.Emotion))
            {
                emotionComboBox.Text = settings.Emotion;
            }

            // 加载目标文本语言（向后兼容：将语言代码转换为完整名称）
            var textLangComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLanguage");
            if (textLangComboBox is not null)
            {
                var textLang = ConvertLanguageCodeToName(settings.TextLanguage);
                foreach (ComboBoxItem item in textLangComboBox.Items)
                {
                    if (item.Tag?.ToString() == textLang)
                    {
                        textLangComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 加载文本切分方法
            var splitMethodComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_SplitMethod");
            if (splitMethodComboBox is not null)
            {
                var splitMethod = string.IsNullOrWhiteSpace(settings.TextSplitMethod) ? "按标点符号切" : settings.TextSplitMethod;
                foreach (ComboBoxItem item in splitMethodComboBox.Items)
                {
                    if (item.Tag?.ToString() == splitMethod)
                    {
                        splitMethodComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 将语言代码转换为完整语言名称（向后兼容）
        /// </summary>
        private string ConvertLanguageCodeToName(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode))
                return "中文";

            return langCode switch
            {
                // 旧格式语言代码
                "zh" => "中文",
                "yue" => "粤语",
                "en" => "英文",
                "ja" => "日文",
                "ko" => "韩文",

                // 新格式完整语言名称（直接返回）
                "中文" => "中文",
                "粤语" => "粤语",
                "英文" => "英文",
                "日文" => "日文",
                "韩文" => "韩文",
                "中英混合" => "中英混合",
                "粤英混合" => "粤英混合",
                "日英混合" => "日英混合",
                "韩英混合" => "韩英混合",
                "多语种混合" => "多语种混合",
                "多语种混合(粤语)" => "多语种混合(粤语)",

                _ => "中文"
            };
        }

        /// <summary>
        /// 保存 GPT-SoVITS 设置
        /// </summary>
        private void SaveGPTSoVITSSettings()
        {
            // 保存基本设置
            var baseUrlTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BaseUrl");
            if (baseUrlTextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.BaseUrl = baseUrlTextBox.Text;
            }

            var cutPuncTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_CutPunc");
            if (cutPuncTextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.CutPunc = cutPuncTextBox.Text;
            }

            var topKTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopK");
            if (topKTextBox is not null && int.TryParse(topKTextBox.Text, out int topK))
            {
                _plugin.Settings.TTS.GPTSoVITS.TopK = topK;
            }

            var topPTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_TopP");
            if (topPTextBox is not null && double.TryParse(topPTextBox.Text, out double topP))
            {
                _plugin.Settings.TTS.GPTSoVITS.TopP = topP;
            }

            var temperatureTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Temperature");
            if (temperatureTextBox is not null && double.TryParse(temperatureTextBox.Text, out double temperature))
            {
                _plugin.Settings.TTS.GPTSoVITS.Temperature = temperature;
            }

            var speedTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_Speed");
            if (speedTextBox is not null && double.TryParse(speedTextBox.Text, out double speed))
            {
                _plugin.Settings.TTS.GPTSoVITS.Speed = speed;
            }

            // 版本
            var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");
            if (versionComboBox?.SelectedItem is ComboBoxItem versionItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.Version = versionItem.Tag?.ToString() ?? "v4";
            }

            // 模型
            var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
            if (modelComboBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.ModelName = modelComboBox.Text;
            }

            // 目标文本语言
            var textLangComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLanguage");
            if (textLangComboBox?.SelectedItem is ComboBoxItem textLangItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.TextLanguage = textLangItem.Tag?.ToString() ?? "中文";
            }

            // 情感
            var emotionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Emotion");
            if (emotionComboBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.Emotion = emotionComboBox.Text;
            }

            // 文本切分方法
            var splitMethodComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_SplitMethod");
            if (splitMethodComboBox?.SelectedItem is ComboBoxItem splitMethodItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.TextSplitMethod = splitMethodItem.Tag?.ToString() ?? "按标点符号切";
            }

            // PromptLanguage 从语言下拉框获取（这是模型支持的语言）
            var languageComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Language");
            if (languageComboBox?.SelectedItem is ComboBoxItem langItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.PromptLanguage = langItem.Tag?.ToString() ?? "中文";
            }

            // 保存 API v2 专用设置
            SaveGPTSoVITSApiV2Settings();
        }

        /// <summary>
        /// 加载 API v2 专用设置
        /// </summary>
        private void LoadGPTSoVITSApiV2Settings(Setting.GPTSoVITSTTSSetting settings)
        {
            // 参考音频路径
            var refAudioPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_RefAudioPath");
            if (refAudioPathTextBox is not null)
            {
                refAudioPathTextBox.Text = settings.RefAudioPath;
            }

            // 提示文本
            var promptTextV2TextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_PromptTextV2");
            if (promptTextV2TextBox is not null)
            {
                promptTextV2TextBox.Text = settings.PromptTextV2;
            }

            // 提示语言
            var promptLangV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_PromptLangV2");
            if (promptLangV2ComboBox is not null)
            {
                foreach (ComboBoxItem item in promptLangV2ComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.PromptLangV2)
                    {
                        promptLangV2ComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 合成文本语言
            var textLangV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLangV2");
            if (textLangV2ComboBox is not null)
            {
                foreach (ComboBoxItem item in textLangV2ComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.TextLangV2)
                    {
                        textLangV2ComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 文本切分方法
            var textSplitMethodV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextSplitMethodV2");
            if (textSplitMethodV2ComboBox is not null)
            {
                foreach (ComboBoxItem item in textSplitMethodV2ComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.TextSplitMethodV2)
                    {
                        textSplitMethodV2ComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 输出格式
            var mediaTypeComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_MediaType");
            if (mediaTypeComboBox is not null)
            {
                foreach (ComboBoxItem item in mediaTypeComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.MediaType)
                    {
                        mediaTypeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 批处理大小
            var batchSizeTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BatchSize");
            if (batchSizeTextBox is not null)
            {
                batchSizeTextBox.Text = settings.BatchSize.ToString();
            }

            // 采样步数
            var sampleStepsTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_SampleSteps");
            if (sampleStepsTextBox is not null)
            {
                sampleStepsTextBox.Text = settings.SampleSteps.ToString();
            }

            // 重复惩罚
            var repetitionPenaltyTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_RepetitionPenalty");
            if (repetitionPenaltyTextBox is not null)
            {
                repetitionPenaltyTextBox.Text = settings.RepetitionPenalty.ToString();
            }

            // 流式模式
            var streamingModeComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_StreamingMode");
            if (streamingModeComboBox is not null)
            {
                foreach (ComboBoxItem item in streamingModeComboBox.Items)
                {
                    if (item.Tag?.ToString() == settings.StreamingMode.ToString())
                    {
                        streamingModeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // 超采样
            var superSamplingCheckBox = (CheckBox)this.FindName("CheckBox_TTS_GPTSoVITS_SuperSampling");
            if (superSamplingCheckBox is not null)
            {
                superSamplingCheckBox.IsChecked = settings.SuperSampling;
            }

            // GPT 模型权重路径
            var gptWeightsPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_GptWeightsPath");
            if (gptWeightsPathTextBox is not null)
            {
                gptWeightsPathTextBox.Text = settings.GptWeightsPath;
            }

            // SoVITS 模型权重路径
            var sovitsWeightsPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_SovitsWeightsPath");
            if (sovitsWeightsPathTextBox is not null)
            {
                sovitsWeightsPathTextBox.Text = settings.SovitsWeightsPath;
            }
        }

        /// <summary>
        /// 保存 API v2 专用设置
        /// </summary>
        private void SaveGPTSoVITSApiV2Settings()
        {
            // 参考音频路径
            var refAudioPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_RefAudioPath");
            if (refAudioPathTextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.RefAudioPath = refAudioPathTextBox.Text;
            }

            // 提示文本
            var promptTextV2TextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_PromptTextV2");
            if (promptTextV2TextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.PromptTextV2 = promptTextV2TextBox.Text;
            }

            // 提示语言
            var promptLangV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_PromptLangV2");
            if (promptLangV2ComboBox?.SelectedItem is ComboBoxItem promptLangItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.PromptLangV2 = promptLangItem.Tag?.ToString() ?? "zh";
            }

            // 合成文本语言
            var textLangV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLangV2");
            if (textLangV2ComboBox?.SelectedItem is ComboBoxItem textLangItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.TextLangV2 = textLangItem.Tag?.ToString() ?? "zh";
            }

            // 文本切分方法
            var textSplitMethodV2ComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextSplitMethodV2");
            if (textSplitMethodV2ComboBox?.SelectedItem is ComboBoxItem splitMethodItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.TextSplitMethodV2 = splitMethodItem.Tag?.ToString() ?? "cut5";
            }

            // 输出格式
            var mediaTypeComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_MediaType");
            if (mediaTypeComboBox?.SelectedItem is ComboBoxItem mediaTypeItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.MediaType = mediaTypeItem.Tag?.ToString() ?? "wav";
            }

            // 批处理大小
            var batchSizeTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_BatchSize");
            if (batchSizeTextBox is not null && int.TryParse(batchSizeTextBox.Text, out int batchSize))
            {
                _plugin.Settings.TTS.GPTSoVITS.BatchSize = batchSize;
            }

            // 采样步数
            var sampleStepsTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_SampleSteps");
            if (sampleStepsTextBox is not null && int.TryParse(sampleStepsTextBox.Text, out int sampleSteps))
            {
                _plugin.Settings.TTS.GPTSoVITS.SampleSteps = sampleSteps;
            }

            // 重复惩罚
            var repetitionPenaltyTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_RepetitionPenalty");
            if (repetitionPenaltyTextBox is not null && double.TryParse(repetitionPenaltyTextBox.Text, out double repetitionPenalty))
            {
                _plugin.Settings.TTS.GPTSoVITS.RepetitionPenalty = repetitionPenalty;
            }

            // 流式模式
            var streamingModeComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_StreamingMode");
            if (streamingModeComboBox?.SelectedItem is ComboBoxItem streamingModeItem)
            {
                if (int.TryParse(streamingModeItem.Tag?.ToString(), out int streamingMode))
                {
                    _plugin.Settings.TTS.GPTSoVITS.StreamingMode = streamingMode;
                }
            }

            // 超采样
            var superSamplingCheckBox = (CheckBox)this.FindName("CheckBox_TTS_GPTSoVITS_SuperSampling");
            if (superSamplingCheckBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.SuperSampling = superSamplingCheckBox.IsChecked ?? false;
            }

            // GPT 模型权重路径
            var gptWeightsPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_GptWeightsPath");
            if (gptWeightsPathTextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.GptWeightsPath = gptWeightsPathTextBox.Text;
            }

            // SoVITS 模型权重路径
            var sovitsWeightsPathTextBox = (TextBox)this.FindName("TextBox_TTS_GPTSoVITS_SovitsWeightsPath");
            if (sovitsWeightsPathTextBox is not null)
            {
                _plugin.Settings.TTS.GPTSoVITS.SovitsWeightsPath = sovitsWeightsPathTextBox.Text;
            }
        }
    }
}
