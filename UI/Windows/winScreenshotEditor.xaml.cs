using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VPetLLM.Utils.System;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 截图编辑窗口 - 用于预览截图、编辑提示词并发送
    /// </summary>
    public partial class winScreenshotEditor : Window
    {
        private byte[]? _imageData;
        private readonly VPetLLM _plugin;

        /// <summary>
        /// 发送事件 - 当用户点击发送时触发
        /// </summary>
        public event EventHandler<ScreenshotSendEventArgs>? SendRequested;

        /// <summary>
        /// 取消事件 - 当用户取消或关闭窗口时触发
        /// </summary>
        public event EventHandler? Cancelled;

        public winScreenshotEditor(VPetLLM plugin, byte[] imageData)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _imageData = imageData ?? throw new ArgumentNullException(nameof(imageData));

            LoadImage();

            // 设置默认提示词
            TextBoxPrompt.Text = "";
            UpdatePlaceholder();
            TextBoxPrompt.TextChanged += (s, e) => UpdatePlaceholder();
            TextBoxPrompt.Focus();
        }

        private void LoadImage()
        {
            try
            {
                if (_imageData == null || _imageData.Length == 0)
                {
                    Logger.Log("No image data to display");
                    return;
                }

                using var ms = new MemoryStream(_imageData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreview.Source = bitmap;
                Logger.Log($"Screenshot editor: Image loaded, size: {_imageData.Length} bytes");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading image in editor: {ex.Message}");
            }
        }

        private void UpdatePlaceholder()
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(TextBoxPrompt.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ButtonRemoveImage_Click(object sender, RoutedEventArgs e)
        {
            _imageData = null;
            ImagePreview.Source = null;
            ButtonRemoveImage.Visibility = Visibility.Collapsed;
            Logger.Log("Screenshot editor: Image removed");
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("Screenshot editor: Cancelled by user");
            Cancelled?.Invoke(this, EventArgs.Empty);
            Close();
        }

        private void ButtonSend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var prompt = TextBoxPrompt.Text?.Trim();

                if (string.IsNullOrEmpty(prompt))
                {
                    prompt = "请描述这张图片的内容。";
                }

                Logger.Log($"Screenshot editor: Sending with prompt: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");

                var args = new ScreenshotSendEventArgs
                {
                    ImageData = _imageData,
                    Prompt = prompt
                };

                SendRequested?.Invoke(this, args);
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error sending screenshot: {ex.Message}");
                MessageBox.Show($"发送失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _imageData = null;
        }
    }

    /// <summary>
    /// 截图发送事件参数
    /// </summary>
    public class ScreenshotSendEventArgs : EventArgs
    {
        /// <summary>
        /// 图像数据（可能为null，如果用户删除了图片）
        /// </summary>
        public byte[]? ImageData { get; set; }

        /// <summary>
        /// 用户输入的提示词
        /// </summary>
        public string Prompt { get; set; } = "";
    }
}
