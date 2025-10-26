using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core.TTSCore;

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
                
                if (versions == null || versions.Count == 0)
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
                Utils.Logger.Log($"检测版本失败: {ex.Message}");
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
                    
                    if (_currentModels != null && _currentModels.Count > 0)
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
                    Utils.Logger.Log($"加载模型列表失败: {ex.Message}");
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
                    
                    if (_currentModels != null && _currentModels.Count > 0)
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
                Utils.Logger.Log($"刷新模型失败: {ex.Message}");
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
                
                if (_currentModels != null && _currentModels.ContainsKey(modelName))
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
                
                if (_currentModels != null && 
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
            if (_plugin.Settings.TTS.GPTSoVITS == null)
            {
                _plugin.Settings.TTS.GPTSoVITS = new Setting.GPTSoVITSTTSSetting();
            }

            var settings = _plugin.Settings.TTS.GPTSoVITS;

            // 加载版本
            var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");
            if (versionComboBox != null)
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
            if (modelComboBox != null && !string.IsNullOrWhiteSpace(settings.ModelName))
            {
                modelComboBox.Text = settings.ModelName;
            }

            // 加载情感（如果有）
            var emotionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Emotion");
            if (emotionComboBox != null && !string.IsNullOrWhiteSpace(settings.Emotion))
            {
                emotionComboBox.Text = settings.Emotion;
            }

            // 加载目标文本语言（向后兼容：将语言代码转换为完整名称）
            var textLangComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_TextLanguage");
            if (textLangComboBox != null)
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
            if (splitMethodComboBox != null)
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
            // 版本
            var versionComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Version");
            if (versionComboBox?.SelectedItem is ComboBoxItem versionItem)
            {
                _plugin.Settings.TTS.GPTSoVITS.Version = versionItem.Tag?.ToString() ?? "v4";
            }

            // 模型
            var modelComboBox = (ComboBox)this.FindName("ComboBox_TTS_GPTSoVITS_Model");
            if (modelComboBox != null)
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
            if (emotionComboBox != null)
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
        }
    }
}
