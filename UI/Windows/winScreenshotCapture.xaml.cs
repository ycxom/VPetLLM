using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VPetLLM.Utils.System;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 截图捕获窗口 - 支持多屏幕
    /// </summary>
    public partial class winScreenshotCapture : Window
    {
        /// <summary>
        /// 截图完成事件
        /// </summary>
        public event EventHandler<byte[]>? ScreenshotCaptured;

        /// <summary>
        /// 截图取消事件
        /// </summary>
        public event EventHandler? CaptureCancelled;

        private System.Windows.Point _startPoint;
        private bool _isSelecting;
        private double _selectionLeft;
        private double _selectionTop;
        private double _selectionWidth;
        private double _selectionHeight;

        // 虚拟桌面边界（所有屏幕的组合区域）
        private int _virtualLeft;
        private int _virtualTop;
        private int _virtualWidth;
        private int _virtualHeight;

        public winScreenshotCapture()
        {
            InitializeComponent();

            // 计算虚拟桌面边界（覆盖所有屏幕）
            CalculateVirtualDesktopBounds();

            // 设置窗口位置和大小以覆盖所有屏幕
            SetWindowToVirtualDesktop();

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            KeyDown += OnKeyDown;
        }

        /// <summary>
        /// 计算虚拟桌面边界（所有屏幕的组合区域）
        /// </summary>
        private void CalculateVirtualDesktopBounds()
        {
            // 使用 System.Windows.Forms.Screen 获取所有屏幕信息
            var screens = System.Windows.Forms.Screen.AllScreens;

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;

            foreach (var screen in screens)
            {
                var bounds = screen.Bounds;
                minX = Math.Min(minX, bounds.Left);
                minY = Math.Min(minY, bounds.Top);
                maxX = Math.Max(maxX, bounds.Right);
                maxY = Math.Max(maxY, bounds.Bottom);
            }

            _virtualLeft = minX;
            _virtualTop = minY;
            _virtualWidth = maxX - minX;
            _virtualHeight = maxY - minY;

            Logger.Log($"Virtual desktop bounds: Left={_virtualLeft}, Top={_virtualTop}, Width={_virtualWidth}, Height={_virtualHeight}, Screens={screens.Length}");
        }

        /// <summary>
        /// 设置窗口以覆盖整个虚拟桌面
        /// </summary>
        private void SetWindowToVirtualDesktop()
        {
            // 获取 DPI 缩放因子
            var source = PresentationSource.FromVisual(Application.Current.MainWindow);
            double dpiX = 1.0, dpiY = 1.0;
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            // 将物理像素转换为 WPF 单位
            Left = _virtualLeft / dpiX;
            Top = _virtualTop / dpiY;
            Width = _virtualWidth / dpiX;
            Height = _virtualHeight / dpiY;

            Logger.Log($"Window set to: Left={Left}, Top={Top}, Width={Width}, Height={Height}");
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(OverlayCanvas);
            _isSelecting = true;

            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, _startPoint.X);
            Canvas.SetTop(SelectionRect, _startPoint.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            HintText.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isSelecting) return;

            var currentPoint = e.GetPosition(OverlayCanvas);

            _selectionLeft = Math.Min(_startPoint.X, currentPoint.X);
            _selectionTop = Math.Min(_startPoint.Y, currentPoint.Y);
            _selectionWidth = Math.Abs(currentPoint.X - _startPoint.X);
            _selectionHeight = Math.Abs(currentPoint.Y - _startPoint.Y);

            Canvas.SetLeft(SelectionRect, _selectionLeft);
            Canvas.SetTop(SelectionRect, _selectionTop);
            SelectionRect.Width = _selectionWidth;
            SelectionRect.Height = _selectionHeight;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelecting) return;

            _isSelecting = false;
            ReleaseMouseCapture();

            if (_selectionWidth > 10 && _selectionHeight > 10)
            {
                HintText.Text = "按 Enter 确认截图，按 Escape 取消";
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                SelectionRect.Visibility = Visibility.Collapsed;
                HintText.Text = "拖动鼠标选择截图区域，按 Enter 确认，按 Escape 取消";
                HintText.Visibility = Visibility.Visible;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Logger.Log("Screenshot capture cancelled by user");
                CaptureCancelled?.Invoke(this, EventArgs.Empty);
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                if (_selectionWidth > 10 && _selectionHeight > 10)
                {
                    CaptureSelectedRegion();
                }
            }
        }

        private void CaptureSelectedRegion()
        {
            try
            {
                // 隐藏窗口以便截图
                Hide();
                System.Threading.Thread.Sleep(100);

                // 获取屏幕 DPI 缩放
                var source = PresentationSource.FromVisual(Application.Current.MainWindow);
                double dpiX = 1.0, dpiY = 1.0;
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                // 计算实际像素坐标（相对于虚拟桌面原点）
                int x = _virtualLeft + (int)(_selectionLeft * dpiX);
                int y = _virtualTop + (int)(_selectionTop * dpiY);
                int width = (int)(_selectionWidth * dpiX);
                int height = (int)(_selectionHeight * dpiY);

                // 确保尺寸有效
                if (width <= 0 || height <= 0)
                {
                    Logger.Log("Invalid selection size");
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                    Close();
                    return;
                }

                // 截取屏幕
                using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                }

                // 转换为字节数组
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                var imageData = ms.ToArray();

                Logger.Log($"Screenshot captured: {width}x{height}, size: {imageData.Length} bytes");
                ScreenshotCaptured?.Invoke(this, imageData);
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error capturing screenshot: {ex.Message}");
                CaptureCancelled?.Invoke(this, EventArgs.Empty);
                Close();
            }
        }
    }
}
