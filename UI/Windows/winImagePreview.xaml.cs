using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VPetLLM.Utils;

namespace VPetLLM.UI.Windows
{
    public partial class winImagePreview : Window
    {
        public winImagePreview(byte[] imageData)
        {
            InitializeComponent();
            LoadImage(imageData);
            
            // 在代码中注册 KeyDown 事件以支持 Escape 关闭
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                }
            };
        }

        private void LoadImage(byte[] imageData)
        {
            try
            {
                if (imageData == null || imageData.Length == 0)
                {
                    Logger.Log("图像预览: 无图像数据");
                    return;
                }

                using var ms = new MemoryStream(imageData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreview.Source = bitmap;
            }
            catch (Exception ex)
            {
                Logger.Log($"图像预览加载失败: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
