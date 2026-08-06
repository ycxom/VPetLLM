using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace VPetLLM.Services
{
    /// <summary>
    /// 截图时命中的候选窗口
    /// </summary>
    public sealed class DetectedWindow
    {
        public IntPtr Handle { get; init; }
        /// <summary>物理像素下的屏幕矩形</summary>
        public Rectangle Bounds { get; init; }
        public string Title { get; init; } = "";
        /// <summary>true 表示这是窗口的客户区（去掉标题栏和边框）</summary>
        public bool IsClientArea { get; init; }
    }

    /// <summary>
    /// 自动窗口识别。
    ///
    /// ⚠ 授权边界：本类为独立编写的实现，只借鉴了 ShareX 公开的「调哪些 API、滤哪几类窗口」
    /// 这类功能性做法，未复制其任何源代码。ShareX 以 GPL 发布，与本项目的宽松协议方向不兼容，
    /// 因此禁止从 ShareX 仓库粘贴代码到本文件。详见 README「第三方致谢与引用说明」。
    ///
    /// 做法参考 ShareX 的 WindowsRectangleList：
    /// EnumWindows 的回调顺序天然是 z-order 从上到下，所以命中测试取第一个包含光标的窗口
    /// 就是「最上层的那个」，不需要自己排序。
    ///
    /// 过滤规则同样照搬 ShareX：不可见、被 DWM cloak（虚拟桌面上的其它桌面、UWP 挂起窗口）、
    /// 以及 WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE 的浮层都要排除——那些不是用户想截的「真窗口」。
    /// </summary>
    public static class WindowDetector
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        /// <summary>枚举耗时上限，卡死的窗口不该拖住整个截图流程</summary>
        private const int EnumerateTimeoutMs = 5000;

        // NVIDIA GeForce Overlay 的常驻透明窗口，覆盖全屏且永远命中，必须排除
        private static readonly string[] IgnoredClassNames = { "CEF-OSC-WIDGET" };

        /// <summary>
        /// 枚举当前可见窗口，按 z-order 从上到下返回
        /// </summary>
        /// <param name="ignoreHandle">要跳过的窗口句柄（截图遮罩自己）</param>
        public static List<DetectedWindow> Enumerate(IntPtr ignoreHandle)
        {
            var result = new List<DetectedWindow>();
            var watch = Stopwatch.StartNew();

            try
            {
                EnumWindows((hWnd, _) =>
                {
                    if (watch.ElapsedMilliseconds > EnumerateTimeoutMs) return false;

                    try
                    {
                        if (hWnd == ignoreHandle) return true;
                        if (!IsWindowVisible(hWnd)) return true;
                        if (IsCloaked(hWnd)) return true;

                        var className = GetClassNameOf(hWnd);
                        if (IgnoredClassNames.Any(n => string.Equals(n, className, StringComparison.OrdinalIgnoreCase)))
                            return true;

                        var exStyle = GetExStyle(hWnd);
                        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_NOACTIVATE) != 0)
                            return true;

                        var bounds = GetWindowBounds(hWnd);
                        if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                        var title = GetWindowTitle(hWnd);

                        // 客户区单独作为一条候选（和整窗矩形不同才有意义）：
                        // 让用户能只截内容区、去掉标题栏和边框
                        var client = GetClientBounds(hWnd);
                        if (client.Width > 0 && client.Height > 0 && client != bounds)
                        {
                            result.Add(new DetectedWindow
                            {
                                Handle = hWnd,
                                Bounds = client,
                                Title = title,
                                IsClientArea = true
                            });
                        }

                        result.Add(new DetectedWindow
                        {
                            Handle = hWnd,
                            Bounds = bounds,
                            Title = title
                        });
                    }
                    catch
                    {
                        // 单个窗口查询失败不影响整体枚举
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Log($"WindowDetector: 枚举窗口失败: {ex.Message}");
            }

            Logger.Log($"WindowDetector: 识别到 {result.Count} 个候选区域，耗时 {watch.ElapsedMilliseconds}ms");
            return result;
        }

        /// <summary>
        /// 取光标下最上层的候选。列表顺序即 z-order，所以第一个命中的就是最上层。
        /// </summary>
        public static DetectedWindow? FindAt(List<DetectedWindow>? windows, int screenX, int screenY)
        {
            if (windows is null) return null;

            foreach (var window in windows)
            {
                if (window.Bounds.Contains(screenX, screenY)) return window;
            }
            return null;
        }

        /// <summary>
        /// 优先用 DWM 的扩展边框，它排除了 Win10+ 窗口周围那圈不可见的阴影余量；
        /// DWM 不可用时退回 GetWindowRect。
        /// </summary>
        private static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            if (IsDwmEnabled() &&
                DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
            {
                var rect = frame.ToRectangle();
                if (rect.Width > 0 && rect.Height > 0) return rect;
            }

            return GetWindowRect(hWnd, out RECT r) ? r.ToRectangle() : Rectangle.Empty;
        }

        private static Rectangle GetClientBounds(IntPtr hWnd)
        {
            if (!GetClientRect(hWnd, out RECT r)) return Rectangle.Empty;

            var topLeft = new POINT { X = r.Left, Y = r.Top };
            if (!ClientToScreen(hWnd, ref topLeft)) return Rectangle.Empty;

            return new Rectangle(topLeft.X, topLeft.Y, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static bool IsCloaked(IntPtr hWnd)
        {
            if (!IsDwmEnabled()) return false;
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        private static bool IsDwmEnabled()
        {
            try { return DwmIsCompositionEnabled(out bool enabled) == 0 && enabled; }
            catch { return false; }
        }

        private static long GetExStyle(IntPtr hWnd) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64()
                : GetWindowLong(hWnd, GWL_EXSTYLE);

        private static string GetClassNameOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return "";

            var sb = new StringBuilder(length + 1);
            return GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        #region Native

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() => new(Left, Top, Right - Left, Bottom - Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled(out bool enabled);

        #endregion
    }
}
