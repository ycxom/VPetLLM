using System.Windows;
using System.Windows.Controls;
using VPetLLM.Utils;

namespace VPetLLM.UI.Controls
{
    public partial class TouchFeedbackSettingsControl : UserControl
    {
        private readonly VPetLLM _plugin;

        public TouchFeedbackSettingsControl(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            LoadSettings();
            UpdateLanguage();
        }

        private void LoadSettings()
        {
            var settings = _plugin.Settings.TouchFeedback;
            EnableTouchFeedbackCheckBox.IsChecked = settings.EnableTouchFeedback;
            TouchCooldownSlider.Value = settings.TouchCooldown;
            UpdateCooldownText();
            
            // 根据主开关状态控制详细设置面板
            UpdateDetailSettingsVisibility(settings.EnableTouchFeedback);
        }

        private void UpdateLanguage()
        {
            var langCode = _plugin.Settings.Language;
            
            // 更新界面文本
            EnableTouchFeedbackCheckBox.Content = LanguageHelper.Get("TouchFeedback.Enable", langCode, "身体交互反馈");
            
            // 更新说明文本
            var descriptionTextBlock = this.FindName("DescriptionTextBlock") as TextBlock;
            if (descriptionTextBlock != null)
            {
                descriptionTextBlock.Text = LanguageHelper.Get("TouchFeedback.EnableDescription", langCode, 
                    "启用后，触摸VPet的头部或身体时会触发智能反馈");
            }
            
            // 更新冷却时间标签
            var cooldownLabel = this.FindName("CooldownLabel") as TextBlock;
            if (cooldownLabel != null)
            {
                cooldownLabel.Text = LanguageHelper.Get("TouchFeedback.TouchCooldown", langCode, "触摸冷却时间");
            }
            
            // 更新说明文本
            var cooldownDescription = this.FindName("CooldownDescription") as TextBlock;
            if (cooldownDescription != null)
            {
                cooldownDescription.Text = LanguageHelper.Get("TouchFeedback.CooldownDescription", langCode, 
                    "冷却时间可防止过于频繁的交互，建议设置在1-3秒之间。");
            }
        }

        private void UpdateCooldownText()
        {
            var langCode = _plugin.Settings.Language;
            var unit = LanguageHelper.Get("TouchFeedback.CooldownUnit", langCode, "ms");
            TouchCooldownValueText.Text = $"{TouchCooldownSlider.Value:F0}{unit}";
        }

        private void EnableTouchFeedbackCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.TouchFeedback.EnableTouchFeedback = true;
            UpdateDetailSettingsVisibility(true);
            Logger.Log("身体交互反馈已启用");
        }

        private void EnableTouchFeedbackCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.TouchFeedback.EnableTouchFeedback = false;
            UpdateDetailSettingsVisibility(false);
            Logger.Log("身体交互反馈已禁用");
        }

        /// <summary>
        /// 根据主开关状态更新详细设置面板的可见性和启用状态
        /// </summary>
        private void UpdateDetailSettingsVisibility(bool isEnabled)
        {
            if (DetailSettingsPanel != null)
            {
                DetailSettingsPanel.IsEnabled = isEnabled;
                DetailSettingsPanel.Opacity = isEnabled ? 1.0 : 0.5;
            }
        }

        private void TouchCooldownSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TouchCooldownValueText != null)
            {
                var value = (int)e.NewValue;
                _plugin.Settings.TouchFeedback.TouchCooldown = value;
                UpdateCooldownText();
            }
        }

        /// <summary>
        /// 公共方法，供外部调用以刷新语言
        /// </summary>
        public void RefreshLanguage()
        {
            // 强制刷新语言设置
            UpdateLanguage();
            // 同时更新冷却时间文本
            UpdateCooldownText();
        }
    }
}