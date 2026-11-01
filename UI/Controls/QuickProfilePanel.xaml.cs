using System.Windows;
using System.Windows.Controls;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 简易设置和新手引导共用的称呼、性格面板。
    /// </summary>
    public partial class QuickProfilePanel : UserControl
    {
        private Setting? _settings;

        public QuickProfilePanel()
        {
            InitializeComponent();
        }

        public void Load(Setting settings)
        {
            _settings = settings;
            Tb_AiName.Text = settings.AiName;
            Tb_UserName.Text = settings.UserName;
            Cb_FollowName.IsChecked = settings.FollowVPetName;
            Tb_Role.Text = settings.Role;
            Cb_Personality.SelectedIndex = 0;
        }

        public void Save(Setting settings)
        {
            settings.FollowVPetName = Cb_FollowName.IsChecked == true;
            if (!settings.FollowVPetName)
            {
                if (!string.IsNullOrWhiteSpace(Tb_AiName.Text)) settings.AiName = Tb_AiName.Text.Trim();
                if (!string.IsNullOrWhiteSpace(Tb_UserName.Text)) settings.UserName = Tb_UserName.Text.Trim();
            }
            if (!string.IsNullOrWhiteSpace(Tb_Role.Text)) settings.Role = Tb_Role.Text.Trim();
        }

        private string RoleTemplate(string? tag)
        {
            var lang = _settings?.Language ?? "zh-hans";
            return tag switch
            {
                "lively" => LanguageHelper.Get("QuickSetup.RoleLively", lang,
                    "你是一只活泼可爱的桌宠，说话轻快，充满好奇心，喜欢逗主人开心。"),
                "gentle" => LanguageHelper.Get("QuickSetup.RoleGentle", lang,
                    "你是一只温柔体贴的桌宠，说话轻声细语，总是关心主人的心情和身体。"),
                "tsundere" => LanguageHelper.Get("QuickSetup.RoleTsundere", lang,
                    "你是一只傲娇的桌宠，嘴上不饶人，心里却很在意主人，偶尔会不小心流露关心。"),
                _ => _settings?.Role ?? ""
            };
        }

        // 跟随模式下名字由 SyncNames 定时同步，名字和称呼两个框都填了也会被盖掉
        private void FollowName_Changed(object sender, RoutedEventArgs e)
        {
            var editable = Cb_FollowName.IsChecked != true;
            Tb_AiName.IsEnabled = editable;
            Tb_UserName.IsEnabled = editable;
        }

        private void Personality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings is null || Cb_Personality.SelectedItem is not ComboBoxItem item) return;
            Tb_Role.Text = RoleTemplate(item.Tag as string);
        }
    }
}
