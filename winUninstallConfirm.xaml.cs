using System.Windows;

namespace VPetLLM
{
    public partial class winUninstallConfirm : Window
    {
        public bool DoNotShowAgain => CheckBox_DoNotShowAgain.IsChecked == true;

        public winUninstallConfirm()
        {
            InitializeComponent();
        }

        private void Button_Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Button_No_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}