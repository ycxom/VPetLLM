using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VPetLLM.Services
{
    /// <summary>
    /// 静默屏幕捕获。
    /// 与 winScreenshotCapture 的区别：不弹选区窗口、不需要用户操作，
    /// 供 AI 主动调用「看屏幕」时使用。
    /// </summary>
    public static class ScreenCapture
    {
        /// <summary>
        /// 捕获主屏幕（不含选区 UI）。失败返回 null。
        /// </summary>
        public static byte[]? CapturePrimaryScreen() => Capture(primaryOnly: true);

        /// <summary>
        /// 捕获整个虚拟桌面（所有显示器拼合）。失败返回 null。
        /// </summary>
        public static byte[]? CaptureAllScreens() => Capture(primaryOnly: false);

        private static byte[]? Capture(bool primaryOnly)
        {
            try
            {
                var bounds = GetBounds(primaryOnly);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Logger.Log("ScreenCapture: 屏幕边界无效，放弃截屏");
                    return null;
                }

                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
                }

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                var imageData = ms.ToArray();

                Logger.Log($"ScreenCapture: 已捕获 {bounds.Width}x{bounds.Height}，{imageData.Length} 字节");

                // 与手动截图走同一条压缩策略，避免把整屏原图直接丢给视觉模型
                return Utils.Common.ImageDownscaler.ClampToMaxDimension(imageData);
            }
            catch (Exception ex)
            {
                Logger.Log($"ScreenCapture: 截屏失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 抓取整个虚拟桌面作为 WPF 位图，供选区窗口当作冻结底图使用。
        /// 冻结底图有三个好处：选区期间画面不会变、放大镜可以逐像素采样、
        /// 裁剪时不必再 Hide 窗口重新抓屏（所见即所得）。
        /// </summary>
        /// <param name="bounds">虚拟桌面在物理像素下的边界，用于把画布坐标换算成屏幕坐标</param>
        public static BitmapSource? CaptureVirtualDesktopAsBitmap(out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            try
            {
                var area = GetBounds(primaryOnly: false);
                if (area.Width <= 0 || area.Height <= 0) return null;

                using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(area.Left, area.Top, 0, 0, new System.Drawing.Size(area.Width, area.Height));
                }

                var hBitmap = bitmap.GetHbitmap();
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze(); // 跨线程安全 + 后续裁剪不再复制
                    bounds = area;
                    return source;
                }
                finally
                {
                    // GetHbitmap 的句柄必须手动释放，否则每次截图泄漏一整屏 GDI 对象
                    DeleteObject(hBitmap);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ScreenCapture: 抓取虚拟桌面失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 把 BitmapSource 的指定区域编码为 PNG 字节，并按统一策略压缩尺寸
        /// </summary>
        public static byte[]? CropToPng(BitmapSource source, System.Windows.Int32Rect region)
        {
            try
            {
                var cropped = new CroppedBitmap(source, region);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return Utils.Common.ImageDownscaler.ClampToMaxDimension(ms.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Log($"ScreenCapture: 裁剪截图失败: {ex.Message}");
                return null;
            }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static Rectangle GetBounds(bool primaryOnly)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;

            if (primaryOnly)
            {
                var primary = System.Windows.Forms.Screen.PrimaryScreen ?? screens.FirstOrDefault();
                return primary?.Bounds ?? Rectangle.Empty;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                minX = Math.Min(minX, b.Left);
                minY = Math.Min(minY, b.Top);
                maxX = Math.Max(maxX, b.Right);
                maxY = Math.Max(maxY, b.Bottom);
            }

            if (minX == int.MaxValue) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 当前显示器数量（&gt;1 时 AI 才有必要指定 all）
        /// </summary>
        public static int ScreenCount
        {
            get
            {
                try { return System.Windows.Forms.Screen.AllScreens.Length; }
                catch { return 1; }
            }
        }
    }
}
