using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 截图编辑窗口 - 用于预览截图、编辑提示词并发送
    /// </summary>
    public partial class winScreenshotEditor : Window
    {
        /// <summary>本次要一并发送的所有截图，按加入顺序排列</summary>
        private readonly List<byte[]> _images = new();
        /// <summary>主预览当前显示的是第几张</summary>
        private int _currentIndex;
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
            if (imageData is null) throw new ArgumentNullException(nameof(imageData));
            _images.Add(imageData);

            RefreshImages();

            // 设置默认提示词
            TextBoxPrompt.Text = "";
            UpdatePlaceholder();
            TextBoxPrompt.TextChanged += (s, e) => UpdatePlaceholder();
            TextBoxPrompt.Focus();
        }

        /// <summary>
        /// 按当前图片列表刷新主预览、缩略图条和计数文字
        /// </summary>
        private void RefreshImages()
        {
            try
            {
                if (_images.Count == 0)
                {
                    ImagePreview.Source = null;
                    ButtonRemoveImage.Visibility = Visibility.Collapsed;
                    ThumbnailBar.Visibility = Visibility.Collapsed;
                    TextImageCount.Text = "";
                    return;
                }

                _currentIndex = Math.Clamp(_currentIndex, 0, _images.Count - 1);

                ImagePreview.Source = Decode(_images[_currentIndex]);
                ButtonRemoveImage.Visibility = Visibility.Visible;

                // 单张时缩略图条没有意义，省下这块空间给主预览
                ThumbnailBar.Visibility = _images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                TextImageCount.Text = _images.Count > 1 ? $"共 {_images.Count} 张" : "";

                BuildThumbnails();
                Logger.Log($"Screenshot editor: {_images.Count} 张图片，当前预览第 {_currentIndex + 1} 张");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing images in editor: {ex.Message}");
            }
        }

        private static BitmapImage? Decode(byte[] data, int decodeWidth = 0)
        {
            if (data is null || data.Length == 0) return null;

            using var ms = new MemoryStream(data);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // 缩略图按目标宽度解码，避免每张都把整幅位图读进内存
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 重建缩略图条。点击缩略图切换主预览，右上角小叉删除该张。
        /// </summary>
        private void BuildThumbnails()
        {
            ThumbnailList.Items.Clear();
            if (_images.Count <= 1) return;

            for (int i = 0; i < _images.Count; i++)
            {
                int index = i;
                bool isCurrent = index == _currentIndex;

                var thumb = new Image
                {
                    Source = Decode(_images[index], 120),
                    Stretch = Stretch.UniformToFill,
                    Width = 76,
                    Height = 48
                };

                var frame = new Border
                {
                    Child = thumb,
                    BorderBrush = new SolidColorBrush(isCurrent
                        ? Color.FromRgb(0x00, 0xBF, 0xFF)
                        : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(isCurrent ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = $"第 {index + 1} 张，点击预览"
                };
                frame.MouseLeftButtonUp += (s, e) =>
                {
                    _currentIndex = index;
                    RefreshImages();
                };

                var close = new Button
                {
                    Content = "×",
                    Width = 16,
                    Height = 16,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 8, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "移除这张"
                };
                close.Click += (s, e) =>
                {
                    _images.RemoveAt(index);
                    if (_currentIndex >= _images.Count) _currentIndex = Math.Max(0, _images.Count - 1);
                    RefreshImages();
                };

                ThumbnailList.Items.Add(new Grid { Children = { frame, close } });
            }
        }

        /// <summary>
        /// 「继续截图」：把编辑器藏起来再唤起选区窗口，
        /// 否则编辑器自身会被截进去。
        /// </summary>
        private async void ButtonAddShot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Hide();
                // 让隐藏真正生效再抓屏，否则冻结底图里还留着编辑器
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                await Task.Delay(120);

                var shot = await _plugin.RequestScreenshotFromUserAsync("再截一张，与已有截图一并发送", 60);

                if (shot is not null && shot.Length > 0)
                {
                    _images.Add(shot);
                    _currentIndex = _images.Count - 1;
                    Logger.Log($"Screenshot editor: 追加第 {_images.Count} 张截图");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error adding screenshot: {ex.Message}");
            }
            finally
            {
                Show();
                Activate();
                RefreshImages();
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
            // 删的是当前预览的这一张，不是清空全部
            if (_images.Count > 0)
            {
                _images.RemoveAt(_currentIndex);
            }
            RefreshImages();
            Logger.Log($"Screenshot editor: 移除一张，剩余 {_images.Count} 张");
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
                    Images = _images.ToList(),
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
            _images.Clear();
        }
    }

    /// <summary>
    /// 截图发送事件参数
    /// </summary>
    public class ScreenshotSendEventArgs : EventArgs
    {
        /// <summary>
        /// 本次一并发送的所有图像（用户可能全部删除，此时为空列表）
        /// </summary>
        public List<byte[]> Images { get; set; } = new();

        /// <summary>
        /// 兼容旧调用方：取第一张
        /// </summary>
        public byte[]? ImageData => Images.Count > 0 ? Images[0] : null;

        /// <summary>
        /// 用户输入的提示词
        /// </summary>
        public string Prompt { get; set; } = "";
    }
}
