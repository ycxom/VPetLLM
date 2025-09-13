using System.Windows;
using VPetLLM.Utils;

namespace VPetLLM.UI.Windows
{
    public partial class winUninstallConfirm : Window
    {
        public bool DoNotShowAgain => CheckBox_DoNotShowAgain.IsChecked == true;

        public winUninstallConfirm()
        {
            InitializeComponent();
            UpdateUIForLanguage();
        }

        private void Button_Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Button_No_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void UpdateUIForLanguage()
        {
            var langCode = VPetLLM.Instance.Settings.Language;
            Title = LanguageHelper.Get("UninstallConfirm.Title", langCode);
            TextBlock_Message.Text = LanguageHelper.Get("UninstallConfirm.Message", langCode);
            CheckBox_DoNotShowAgain.Content = LanguageHelper.Get("UninstallConfirm.DoNotShowAgain", langCode);
            Button_Yes.Content = LanguageHelper.Get("UninstallConfirm.Yes", langCode);
            Button_No.Content = LanguageHelper.Get("UninstallConfirm.No", langCode);
        }
    }
}