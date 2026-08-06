using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VPetLLM.Services;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 截图选区窗口 - 支持多屏幕、冻结底图、像素级放大镜
    ///
    /// ⚠ 授权边界：本窗口为独立编写的实现，仅借鉴 ShareX 公开的交互做法，未复制其源代码。
    /// ShareX 以 GPL 发布，与本项目的宽松协议不兼容，禁止从 ShareX 粘贴代码进来。
    /// 详见 README「第三方致谢与引用说明」。
    ///
    /// 做法参考 ShareX 的区域截图：进入时先把整个虚拟桌面抓成一张位图铺满窗口，
    /// 之后所有交互（选区、放大镜、取色、最终裁剪）都基于这张冻结的底图。
    /// 这样做的好处：
    /// 1. 选区期间画面不会变动，所见即所得；
    /// 2. 放大镜可以逐像素采样，不必反复抓屏；
    /// 3. 确认时直接裁剪底图，省掉了原先 Hide() + Sleep(100) 再抓屏的时序 hack。
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

        // 放大镜参数：采样 15x15 物理像素，放大到 140x140 的方框内
        private const int MagnifierSamplePixels = 15;
        private const double MagnifierBoxSize = 140;
        private const double MagnifierMargin = 24;

        private System.Windows.Point _startPoint;
        // 鼠标最后位置，用于把提示条/放大镜摆到「当前所在的那块屏幕」上
        private System.Windows.Point _lastMousePoint;
        private Rect _lastScreenRect = Rect.Empty;
        private bool _isSelecting;
        private bool _hasDragged;
        private double _selectionLeft;
        private double _selectionTop;
        private double _selectionWidth;
        private double _selectionHeight;

        /// <summary>未选中任何窗口时的默认提示</summary>
        private const string DefaultHint = "移动鼠标自动识别窗口，单击即可捕获；拖动可自由框选（Shift 正方形 / Ctrl+A 全屏 / 方向键微调）";

        // 马赛克方块边长（物理像素）
        private const int MosaicBlockSize = 12;

        // 冻结的桌面底图及其在物理像素下的边界
        private BitmapSource? _frozen;
        private System.Drawing.Rectangle _virtualBounds;

        /// <summary>标注工具</summary>
        private enum EditTool
        {
            /// <summary>无，正常框选</summary>
            None,
            /// <summary>马赛克涂抹</summary>
            Mosaic,
            /// <summary>画矩形框</summary>
            Rectangle
        }

        // 画框参数
        private const int RectangleStrokeWidth = 3;
        private static readonly byte[] RectangleColorBgra = { 0x23, 0x11, 0xE8, 0xFF }; // #E81123 醒目红

        // 标注编辑层：只有真正落笔时才从 _frozen 复制一份可写位图，
        // 不画就不为一整屏 RGBA 掏这份内存
        private WriteableBitmap? _editable;
        private EditTool _activeTool = EditTool.None;
        private bool _toolDragging;
        private System.Windows.Point _toolStart;
        private double _toolLeft, _toolTop, _toolWidth, _toolHeight;
        // 撤销栈存的是涂抹前的原始像素副本，大区域一笔就是几十 MB，
        // 所以按总字节数封顶，超了就丢最早的几笔
        private const long UndoBudgetBytes = 64L * 1024 * 1024;
        private readonly List<(Int32Rect region, byte[] pixels)> _undoStack = new();

        /// <summary>
        /// 当前用于采样、裁剪、输出的图像：涂抹过就是编辑层，否则是原始冻结底图
        /// </summary>
        private BitmapSource? CurrentImage => _editable ?? _frozen;

        // 自动窗口识别。
        // _highlightRect 是「预览高亮」的当前显示范围，与选区状态严格分离：
        // 它永远不会自己变成选区，必须经由一次真实单击才会写进 _selection*。
        private List<DetectedWindow>? _windows;
        private DetectedWindow? _hoverWindow;
        private Rect _hoverRect;
        private Rect _highlightRect;

        // 悬停矩形的过渡动画（对应 ShareX 的 RectangleAnimation，同样 200ms）
        private static readonly TimeSpan HoverAnimationDuration = TimeSpan.FromMilliseconds(200);
        private Rect _animFrom, _animTo, _animCurrent;
        private DateTime _animStartTime;
        private bool _animRunning;

        public winScreenshotCapture() : this(null)
        {
        }

        /// <param name="reason">发起截图的原因（AI 主动请求时展示给用户，便于其判断是否授权）</param>
        public winScreenshotCapture(string? reason)
        {
            InitializeComponent();

            FreezeDesktop();
            SetWindowToVirtualDesktop();

            if (!string.IsNullOrWhiteSpace(reason))
            {
                ReasonText.Text = reason;
                ReasonText.Visibility = Visibility.Visible;
            }

            Loaded += OnLoaded;
            // CompositionTarget.Rendering 是静态事件，不摘会把窗口一直吊在内存里
            Closed += (s, e) => StopHoverAnimation();
            SizeChanged += (s, e) => LayoutOverlay();
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseRightButtonDown += OnMouseRightButtonDown;
            MouseLeave += (s, e) => Magnifier.Visibility = Visibility.Collapsed;
            KeyDown += OnKeyDown;

            BtnMosaic.Click += (s, e) => SetTool(_activeTool == EditTool.Mosaic ? EditTool.None : EditTool.Mosaic);
            BtnRect.Click += (s, e) => SetTool(_activeTool == EditTool.Rectangle ? EditTool.None : EditTool.Rectangle);
            BtnUndo.Click += (s, e) => UndoLastEdit();
            BtnRedo.Click += (s, e) => ResetSelection();
            BtnCopy.Click += (s, e) => CopySelectionToClipboard();
            BtnSave.Click += (s, e) => SaveSelectionToFile();
            BtnCancel.Click += (s, e) => Cancel("用户点击取消");
            BtnConfirm.Click += (s, e) => CaptureSelectedRegion();
        }

        /// <summary>
        /// 抓取整个虚拟桌面作为冻结底图
        /// </summary>
        private void FreezeDesktop()
        {
            _frozen = ScreenCapture.CaptureVirtualDesktopAsBitmap(out _virtualBounds);

            if (_frozen is null)
            {
                // 抓不到底图就没有可选的画面，硬撑下去只会给用户一块全黑遮罩；
                // 记下来，等 Loaded 时按取消收场
                Logger.Log("winScreenshotCapture: 冻结桌面失败，将直接取消本次截图");
                return;
            }

            FrozenScreen.Source = _frozen;
            Logger.Log($"winScreenshotCapture: 冻结底图 {_virtualBounds.Width}x{_virtualBounds.Height}，" +
                       $"原点 ({_virtualBounds.Left},{_virtualBounds.Top})");
        }

        /// <summary>
        /// 设置窗口以覆盖整个虚拟桌面
        /// </summary>
        private void SetWindowToVirtualDesktop()
        {
            if (_virtualBounds.Width <= 0 || _virtualBounds.Height <= 0)
            {
                _virtualBounds = GetVirtualBoundsFallback();
            }

            // 物理像素 → WPF 单位
            var source = PresentationSource.FromVisual(Application.Current.MainWindow);
            double dpiX = 1.0, dpiY = 1.0;
            if (source?.CompositionTarget is not null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            Left = _virtualBounds.Left / dpiX;
            Top = _virtualBounds.Top / dpiY;
            Width = _virtualBounds.Width / dpiX;
            Height = _virtualBounds.Height / dpiY;

            Logger.Log($"winScreenshotCapture: 窗口范围 Left={Left}, Top={Top}, Width={Width}, Height={Height}");
        }

        private static System.Drawing.Rectangle GetVirtualBoundsFallback()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                minX = Math.Min(minX, b.Left);
                minY = Math.Min(minY, b.Top);
                maxX = Math.Max(maxX, b.Right);
                maxY = Math.Max(maxY, b.Bottom);
            }
            if (minX == int.MaxValue) return System.Drawing.Rectangle.Empty;
            return new System.Drawing.Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_frozen is null)
            {
                Cancel("冻结桌面失败");
                return;
            }

            Activate();
            Focus();

            // 先取一次真实光标位置：否则 _lastMousePoint 还是 (0,0)，
            // 提示条会先闪现在虚拟桌面原点那块屏幕上，鼠标一动才跳过来
            try
            {
                _lastMousePoint = Mouse.GetPosition(OverlayCanvas);
                _lastScreenRect = GetScreenRectAt(_lastMousePoint);
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 取初始光标位置失败: {ex.Message}");
            }

            LayoutOverlay();
            FadeIn(HintPanel);
            StartWindowDetection();
        }

        /// <summary>
        /// 后台枚举窗口。ShareX 同样是丢到 Task 里做的——遍历几十个窗口 + DWM 查询
        /// 有几十毫秒开销，不能卡在遮罩显示之前。
        /// </summary>
        private void StartWindowDetection()
        {
            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            Task.Run(() =>
            {
                var windows = WindowDetector.Enumerate(self);
                Dispatcher.BeginInvoke(new Action(() => _windows = windows));
            });
        }

        /// <summary>
        /// 底图铺满画布、遮罩几何重算、提示条居中
        /// </summary>
        private void LayoutOverlay()
        {
            var w = OverlayCanvas.ActualWidth;
            var h = OverlayCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            FrozenScreen.Width = w;
            FrozenScreen.Height = h;

            UpdateDimMask();
            PositionHintPanel();
        }

        /// <summary>
        /// 把提示条摆在「鼠标所在那块屏幕」的顶部居中。
        /// 按整个虚拟桌面居中的话，双屏时它会跑到两屏接缝处，甚至整条落在另一块屏上。
        /// </summary>
        private void PositionHintPanel()
        {
            var w = OverlayCanvas.ActualWidth;
            var h = OverlayCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var screen = GetScreenRectAt(_lastMousePoint);

            HintPanel.Measure(new System.Windows.Size(screen.Width, screen.Height));
            var size = HintPanel.DesiredSize;

            Canvas.SetLeft(HintPanel, Math.Max(screen.Left, screen.Left + ((screen.Width - size.Width) / 2)));
            Canvas.SetTop(HintPanel, screen.Top + 60);
        }

        /// <summary>
        /// 遮罩 = 整屏矩形 EXCLUDE 亮区。
        /// 亮区优先取真实选区；没有选区时才退而显示窗口高亮的预览范围。
        /// </summary>
        private void UpdateDimMask()
        {
            var w = OverlayCanvas.ActualWidth;
            var h = OverlayCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var full = new RectangleGeometry(new Rect(0, 0, w, h));
            Rect? hole = null;

            if (SelectionRect.Visibility == Visibility.Visible && _selectionWidth > 0 && _selectionHeight > 0)
            {
                hole = new Rect(_selectionLeft, _selectionTop, _selectionWidth, _selectionHeight);
            }
            else if (WindowHighlight.Visibility == Visibility.Visible && _highlightRect.Width > 0 && _highlightRect.Height > 0)
            {
                hole = _highlightRect;
            }

            DimMask.Data = hole is null
                ? full
                : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole.Value));
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点在功能栏上时不要当成「重新框选」——按钮的点击事件会照常冒泡到窗口
            if (IsWithinToolbar(e.OriginalSource as DependencyObject)) return;

            // 马赛克模式下，拖动是涂抹而不是重新框选
            if (_activeTool != EditTool.None)
            {
                BeginToolDrag(e.GetPosition(OverlayCanvas));
                return;
            }

            // 停掉过渡动画并隐藏高亮框，但保留 _hoverWindow：
            // 如果这一下只是单击没拖动，抬手时还要靠它把整窗采纳下来
            StopHoverAnimation();
            WindowHighlight.Visibility = Visibility.Collapsed;
            _highlightRect = Rect.Empty;

            _startPoint = e.GetPosition(OverlayCanvas);
            Toolbar.Visibility = Visibility.Collapsed;
            _isSelecting = true;
            _hasDragged = false;

            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, _startPoint.X);
            Canvas.SetTop(SelectionRect, _startPoint.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            _selectionWidth = 0;
            _selectionHeight = 0;

            HintPanel.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = e.GetPosition(OverlayCanvas);
            _lastMousePoint = currentPoint;

            // 光标换到另一块屏幕时，提示条要跟过去
            var screenNow = GetScreenRectAt(currentPoint);
            if (screenNow != _lastScreenRect)
            {
                _lastScreenRect = screenNow;
                if (HintPanel.Visibility == Visibility.Visible) PositionHintPanel();
            }

            if (_toolDragging)
            {
                UpdateToolPreview(currentPoint);
                Magnifier.Visibility = Visibility.Collapsed;
                return;
            }

            if (_isSelecting)
            {
                // 一旦真的动起来，这次交互就归框选所有，不再回落到「单击选窗口」
                if (!_hasDragged &&
                    (Math.Abs(currentPoint.X - _startPoint.X) > DragThreshold ||
                     Math.Abs(currentPoint.Y - _startPoint.Y) > DragThreshold))
                {
                    _hasDragged = true;
                    ClearHighlight();
                }

                double dx = currentPoint.X - _startPoint.X;
                double dy = currentPoint.Y - _startPoint.Y;

                // 按住 Shift 约束为正方形，方向仍跟随光标所在象限
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    double side = Math.Min(Math.Abs(dx), Math.Abs(dy));
                    dx = Math.Sign(dx) * side;
                    dy = Math.Sign(dy) * side;
                }

                _selectionLeft = Math.Min(_startPoint.X, _startPoint.X + dx);
                _selectionTop = Math.Min(_startPoint.Y, _startPoint.Y + dy);
                _selectionWidth = Math.Abs(dx);
                _selectionHeight = Math.Abs(dy);

                Canvas.SetLeft(SelectionRect, _selectionLeft);
                Canvas.SetTop(SelectionRect, _selectionTop);
                SelectionRect.Width = _selectionWidth;
                SelectionRect.Height = _selectionHeight;

                UpdateDimMask();
            }
            else if (_activeTool == EditTool.None && Toolbar.Visibility != Visibility.Visible)
            {
                // 还没框选时，跟随光标高亮光标下的窗口
                UpdateWindowHover(currentPoint);
            }

            // 选区已定、功能栏出来之后就收起放大镜：此时用户是在够按钮，不是在对准像素
            if (_activeTool != EditTool.None || (!_isSelecting && Toolbar.Visibility == Visibility.Visible))
            {
                Magnifier.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateMagnifier(currentPoint);
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_toolDragging)
            {
                EndToolDrag();
                return;
            }

            if (!_isSelecting) return;

            _isSelecting = false;
            ReleaseMouseCapture();

            // 单击判定同时看两个条件：过程中没越过拖动阈值，且抬起点就在按下点附近。
            // 后者不依赖 MouseMove 的投递情况，快速拖动漏帧时也不会被误判成单击。
            var upPoint = e.GetPosition(OverlayCanvas);
            bool isClick = !_hasDragged
                           && Math.Abs(upPoint.X - _startPoint.X) <= DragThreshold
                           && Math.Abs(upPoint.Y - _startPoint.Y) <= DragThreshold;

            Logger.Log($"winScreenshotCapture: 抬手 isClick={isClick}, hasDragged={_hasDragged}, " +
                       $"位移=({upPoint.X - _startPoint.X:F1},{upPoint.Y - _startPoint.Y:F1}), " +
                       $"选区={_selectionWidth:F0}x{_selectionHeight:F0}");

            // 只要有过拖动，就一律按框选处理——哪怕框得很小，也是用户的意图
            if (!isClick)
            {
                ClearHighlight();

                if (HasUsableSelection())
                {
                    ShowToolbar();
                }
                else
                {
                    // 拖出来的区域小到无法使用，回到等待框选，绝不退化成整窗。
                    // 先复位再闪提示——ResetSelection 会把提示文字刷回默认值
                    ResetSelection();
                    FlashHint("选区太小，请重新框选");
                }
                return;
            }

            // 真正的单击：采纳光标下的窗口
            if (_hoverWindow is not null && _hoverRect.Width > 3 && _hoverRect.Height > 3)
            {
                var adopted = _hoverRect;
                var title = _hoverWindow.Title;
                bool isClient = _hoverWindow.IsClientArea;

                ClearHighlight();
                ApplySelectionRect(adopted);
                SelectionRect.Visibility = Visibility.Visible;
                Logger.Log($"winScreenshotCapture: 单击采纳窗口「{title}」{(isClient ? "（客户区）" : "")}");
                ShowToolbar();
                return;
            }

            ResetSelection();
        }

        /// <summary>
        /// 在顶部提示条上短暂显示一条消息
        /// </summary>
        private void FlashHint(string message)
        {
            HintText.Text = message;
            HintPanel.Visibility = Visibility.Visible;
            LayoutOverlay();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!HasUsableSelection() && _activeTool == EditTool.None)
                {
                    HintText.Text = DefaultHint;
                    LayoutOverlay();
                }
            };
            timer.Start();
        }

        // ==================== 自动窗口识别 ====================

        /// <summary>
        /// 命中光标下最上层的窗口并高亮。切换目标时走过渡动画，而不是硬切。
        /// </summary>
        private void UpdateWindowHover(System.Windows.Point canvasPoint)
        {
            if (_windows is null) return;

            var (px, py) = CanvasToPixel(canvasPoint);
            var found = WindowDetector.FindAt(_windows, _virtualBounds.Left + px, _virtualBounds.Top + py);

            if (found is null)
            {
                if (_hoverWindow is not null)
                {
                    ClearHighlight();
                    HintText.Text = DefaultHint;
                }
                return;
            }

            if (ReferenceEquals(found, _hoverWindow)) return;

            var previous = _highlightRect;
            _hoverWindow = found;
            _hoverRect = ScreenRectToCanvas(found.Bounds);

            WindowHighlight.Visibility = Visibility.Visible;
            // 从当前正在显示的矩形出发，动画中途换目标也能接得上
            StartHoverAnimation(previous.Width > 0 ? (_animRunning ? _animCurrent : previous) : _hoverRect, _hoverRect);

            var label = string.IsNullOrWhiteSpace(found.Title) ? "窗口" : found.Title;
            if (label.Length > 40) label = label.Substring(0, 40) + "…";
            HintText.Text = $"单击捕获「{label}」{(found.IsClientArea ? "的内容区" : "")}" +
                            $"（{found.Bounds.Width}×{found.Bounds.Height}），或拖动自由框选";
            LayoutOverlay();
        }

        /// <summary>
        /// 取包含指定画布坐标的那块显示器，返回其在画布坐标系下的矩形。
        /// 画布覆盖的是整个虚拟桌面，直接拿画布尺寸当边界会让浮层被推到
        /// 「虚拟桌面之内、但当前物理屏幕之外」的位置——多屏时就看不见了。
        /// </summary>
        private Rect GetScreenRectAt(System.Windows.Point canvasPoint)
        {
            try
            {
                var (px, py) = CanvasToPixel(canvasPoint);
                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point(_virtualBounds.Left + px, _virtualBounds.Top + py));
                var rect = ScreenRectToCanvas(screen.Bounds);
                if (rect.Width > 0 && rect.Height > 0) return rect;
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 取所在显示器失败: {ex.Message}");
            }

            // 兜底：整块画布
            return new Rect(0, 0, OverlayCanvas.ActualWidth, OverlayCanvas.ActualHeight);
        }

        private Rect ScreenRectToCanvas(System.Drawing.Rectangle bounds)
        {
            double scaleX = ScaleX(), scaleY = ScaleY();
            if (scaleX <= 0 || scaleY <= 0) return Rect.Empty;

            double left = (bounds.Left - _virtualBounds.Left) / scaleX;
            double top = (bounds.Top - _virtualBounds.Top) / scaleY;
            double width = bounds.Width / scaleX;
            double height = bounds.Height / scaleY;

            // 夹到画布内：窗口可能有一部分在虚拟桌面之外
            double right = Math.Min(left + width, OverlayCanvas.ActualWidth);
            double bottom = Math.Min(top + height, OverlayCanvas.ActualHeight);
            left = Math.Max(0, left);
            top = Math.Max(0, top);

            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        // ==================== 悬停过渡动画 ====================

        /// <summary>
        /// 逐帧插值，和 ShareX 的 RectangleAnimation 是同一个思路：
        /// 选区边框和变暗遮罩必须在同一帧里一起更新，交给 WPF 的属性动画反而会脱节。
        /// </summary>
        private void StartHoverAnimation(Rect from, Rect to)
        {
            _animFrom = from;
            _animTo = to;
            _animCurrent = from;
            _animStartTime = DateTime.UtcNow;

            if (!_animRunning)
            {
                _animRunning = true;
                CompositionTarget.Rendering += OnHoverAnimationTick;
            }
        }

        private void StopHoverAnimation()
        {
            if (!_animRunning) return;
            _animRunning = false;
            CompositionTarget.Rendering -= OnHoverAnimationTick;
        }

        private void OnHoverAnimationTick(object? sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _animStartTime).TotalMilliseconds / HoverAnimationDuration.TotalMilliseconds;
            t = Math.Clamp(t, 0, 1);

            // 缓出：起步快、收尾稳，切换窗口时比线性更跟手
            double eased = 1 - Math.Pow(1 - t, 3);

            _animCurrent = new Rect(
                Lerp(_animFrom.X, _animTo.X, eased),
                Lerp(_animFrom.Y, _animTo.Y, eased),
                Lerp(_animFrom.Width, _animTo.Width, eased),
                Lerp(_animFrom.Height, _animTo.Height, eased));

            // 只动高亮预览，绝不碰选区状态
            ApplyHighlightRect(_animCurrent);

            if (t >= 1)
            {
                StopHoverAnimation();
            }
        }

        /// <summary>
        /// 套用窗口高亮预览。注意它写的是 _highlightRect / WindowHighlight，
        /// 与 _selection* 完全无关——这正是「拖动被识别成整窗」那类 bug 的根治点。
        /// </summary>
        private void ApplyHighlightRect(Rect rect)
        {
            _highlightRect = rect;

            Canvas.SetLeft(WindowHighlight, rect.X);
            Canvas.SetTop(WindowHighlight, rect.Y);
            WindowHighlight.Width = rect.Width;
            WindowHighlight.Height = rect.Height;

            UpdateDimMask();
        }

        /// <summary>
        /// 收起窗口高亮预览
        /// </summary>
        private void ClearHighlight()
        {
            StopHoverAnimation();
            _hoverWindow = null;
            _hoverRect = Rect.Empty;
            _highlightRect = Rect.Empty;
            WindowHighlight.Visibility = Visibility.Collapsed;
            UpdateDimMask();
        }

        private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

        /// <summary>
        /// 把一个矩形套用到「真正的选区」上。只有用户的明确操作（拖动、单击采纳窗口、
        /// Ctrl+A、方向键微调）才应调用它——窗口高亮预览不得走这条路。
        /// </summary>
        private void ApplySelectionRect(Rect rect)
        {
            _selectionLeft = rect.X;
            _selectionTop = rect.Y;
            _selectionWidth = rect.Width;
            _selectionHeight = rect.Height;

            Canvas.SetLeft(SelectionRect, _selectionLeft);
            Canvas.SetTop(SelectionRect, _selectionTop);
            SelectionRect.Width = _selectionWidth;
            SelectionRect.Height = _selectionHeight;

            UpdateDimMask();
        }

        /// <summary>
        /// 元素出现时的淡入，避免功能栏/放大镜"啪"地弹出来
        /// </summary>
        private static void FadeIn(UIElement element, double milliseconds = 140)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
            element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
        }

        /// <summary>
        /// 回到「等待框选」状态
        /// </summary>
        private void ResetSelection()
        {
            SetTool(EditTool.None);
            // 清掉悬停目标，否则光标停在同一个窗口上时会被 ReferenceEquals 短路，高亮不回来
            ClearHighlight();

            _isSelecting = false;
            _selectionWidth = 0;
            _selectionHeight = 0;
            SelectionRect.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            UpdateDimMask();

            HintText.Text = DefaultHint;
            HintPanel.Visibility = Visibility.Visible;
            LayoutOverlay();
        }

        /// <summary>
        /// 把功能栏贴到选区右下角；下方放不下就翻到上方，再放不下就压在选区内侧
        /// </summary>
        private void ShowToolbar()
        {
            HintPanel.Visibility = Visibility.Collapsed;
            ToolbarSizeText.Text = $"{PixelLength(_selectionWidth, true)} × {PixelLength(_selectionHeight, false)}";

            bool wasHidden = Toolbar.Visibility != Visibility.Visible;
            Toolbar.Visibility = Visibility.Visible;
            if (wasHidden) FadeIn(Toolbar);
            Toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = Toolbar.DesiredSize;

            const double gap = 8;

            // 以选区右下角所在的那块屏幕为界，而不是整个虚拟桌面：
            // 否则多屏时功能栏会被摆到相邻屏幕上，甚至落在物理屏幕之外
            var screen = GetScreenRectAt(new System.Windows.Point(
                _selectionLeft + _selectionWidth, _selectionTop + _selectionHeight));

            double x = _selectionLeft + _selectionWidth - size.Width;
            x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));

            double y = _selectionTop + _selectionHeight + gap;
            if (y + size.Height > screen.Bottom)
            {
                // 下方放不下就翻到选区上方
                y = _selectionTop - gap - size.Height;
                if (y < screen.Top)
                {
                    // 上下都放不下（选区几乎占满该屏），压在选区内侧底部
                    y = Math.Max(screen.Top, _selectionTop + _selectionHeight - size.Height - gap);
                }
            }

            Canvas.SetLeft(Toolbar, x);
            Canvas.SetTop(Toolbar, y);
        }

        private bool IsWithinToolbar(DependencyObject? source)
        {
            while (source is not null)
            {
                if (ReferenceEquals(source, Toolbar)) return true;
                // GetParent 只接受 Visual/Visual3D，遇到其它节点就停下
                if (source is not Visual && source is not System.Windows.Media.Media3D.Visual3D) return false;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 右键取消，和 ShareX 一致
            Cancel("用户右键取消");
        }

        /// <summary>
        /// 选区是否可用。阈值必须放得很低：这里是 WPF 单位，150% 缩放下
        /// 每 1 单位就是 1.5 物理像素，卡到 10 会让「框一个小图标」这种正常操作直接落空。
        /// </summary>
        private bool HasUsableSelection() => _selectionWidth >= 3 && _selectionHeight >= 3;

        /// <summary>判定「单击」而非「拖动」的位移阈值（WPF 单位）</summary>
        private const double DragThreshold = 3.0;

        // 方向键微调步长，对应 ShareX 的 MoveSpeedMinimum / MoveSpeedMaximum
        private const double NudgeSpeedSlow = 1.0;
        private const double NudgeSpeedFast = 10.0;

        // ==================== 马赛克 ====================

        /// <summary>
        /// 切换标注工具。同一个按钮再点一次即退出，切到另一个工具会自动顶掉前一个。
        /// </summary>
        private void SetTool(EditTool tool)
        {
            if (tool != EditTool.None && !HasUsableSelection()) return;

            _activeTool = tool;
            _toolDragging = false;
            ShapePreview.Visibility = Visibility.Collapsed;

            // ToolButton 模板把 Background 做了 TemplateBinding，直接改属性即可当作选中态
            BtnMosaic.Background = tool == EditTool.Mosaic
                ? new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x0A))
                : Brushes.Transparent;
            BtnRect.Background = tool == EditTool.Rectangle
                ? new SolidColorBrush(Color.FromRgb(0xC8, 0x1A, 0x2A))
                : Brushes.Transparent;

            // 预览框的配色跟随工具，落笔前就能看出画出来是什么
            if (tool == EditTool.Rectangle)
            {
                ShapePreview.Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
                ShapePreview.Fill = Brushes.Transparent;
                ShapePreview.StrokeThickness = 2;
            }
            else
            {
                ShapePreview.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x00));
                ShapePreview.Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC4, 0x00));
                ShapePreview.StrokeThickness = 1;
            }

            Cursor = tool == EditTool.None ? Cursors.Cross : Cursors.Pen;

            HintText.Text = tool switch
            {
                EditTool.Mosaic => "在选区内拖动涂抹要遮盖的内容，Ctrl+Z 撤销；再次点击「马赛克」退出",
                EditTool.Rectangle => "在选区内拖动画出标记框，Ctrl+Z 撤销；再次点击「画框」退出",
                _ => DefaultHint
            };
            HintPanel.Visibility = tool == EditTool.None ? Visibility.Collapsed : Visibility.Visible;
            if (tool != EditTool.None) LayoutOverlay();
        }

        private void BeginToolDrag(System.Windows.Point start)
        {
            _toolDragging = true;
            _toolStart = start;
            _toolLeft = start.X;
            _toolTop = start.Y;
            _toolWidth = 0;
            _toolHeight = 0;

            ShapePreview.Visibility = Visibility.Visible;
            Canvas.SetLeft(ShapePreview, start.X);
            Canvas.SetTop(ShapePreview, start.Y);
            ShapePreview.Width = 0;
            ShapePreview.Height = 0;
            CaptureMouse();
        }

        private void UpdateToolPreview(System.Windows.Point current)
        {
            _toolLeft = Math.Min(_toolStart.X, current.X);
            _toolTop = Math.Min(_toolStart.Y, current.Y);
            _toolWidth = Math.Abs(current.X - _toolStart.X);
            _toolHeight = Math.Abs(current.Y - _toolStart.Y);

            Canvas.SetLeft(ShapePreview, _toolLeft);
            Canvas.SetTop(ShapePreview, _toolTop);
            ShapePreview.Width = _toolWidth;
            ShapePreview.Height = _toolHeight;
        }

        private void EndToolDrag()
        {
            _toolDragging = false;
            ReleaseMouseCapture();
            ShapePreview.Visibility = Visibility.Collapsed;

            if (_toolWidth < 2 || _toolHeight < 2) return;

            double scaleX = ScaleX(), scaleY = ScaleY();
            var region = new Int32Rect(
                (int)Math.Round(_toolLeft * scaleX),
                (int)Math.Round(_toolTop * scaleY),
                (int)Math.Round(_toolWidth * scaleX),
                (int)Math.Round(_toolHeight * scaleY));

            switch (_activeTool)
            {
                case EditTool.Mosaic:
                    ApplyEdit(region, (px, w, h, stride) => Pixelate(px, w, h, stride, MosaicBlockSize));
                    break;
                case EditTool.Rectangle:
                    ApplyEdit(region, DrawRectangleOutline);
                    break;
            }
        }

        /// <summary>
        /// 懒创建可写编辑层。统一转成 Bgra32，避免不同来源位图的像素格式差异
        /// 让后续的逐字节运算算错通道。
        /// </summary>
        private bool EnsureEditableBitmap()
        {
            if (_editable is not null) return true;
            if (_frozen is null) return false;

            try
            {
                var normalized = _frozen.Format == PixelFormats.Bgra32
                    ? _frozen
                    : new FormatConvertedBitmap(_frozen, PixelFormats.Bgra32, null, 0);

                _editable = new WriteableBitmap(normalized);
                FrozenScreen.Source = _editable;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 创建马赛克编辑层失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>像素改写委托：就地修改 BGRA 缓冲区</summary>
        private delegate void PixelEdit(byte[] pixels, int width, int height, int stride);

        /// <summary>
        /// 把一次标注落到编辑层上。区域会被夹到选区之内——
        /// 选区外的画面根本不会被输出，画它没有意义，还会让用户误以为遮住了什么。
        /// 马赛克和画框共用这条路径，因此撤销栈也是同一份。
        /// </summary>
        private void ApplyEdit(Int32Rect region, PixelEdit edit)
        {
            if (!EnsureEditableBitmap()) return;

            var bounds = GetSelectionRegion();
            int left = Math.Max(region.X, bounds.X);
            int top = Math.Max(region.Y, bounds.Y);
            int right = Math.Min(region.X + region.Width, bounds.X + bounds.Width);
            int bottom = Math.Min(region.Y + region.Height, bounds.Y + bounds.Height);

            if (right - left < 1 || bottom - top < 1)
            {
                FlashToolbarMessage("请在选区内操作");
                return;
            }

            var target = new Int32Rect(left, top, right - left, bottom - top);

            try
            {
                int stride = target.Width * 4;
                var pixels = new byte[stride * target.Height];
                _editable!.CopyPixels(target, pixels, stride, 0);

                // 先存原样再改，撤销直接写回
                PushUndo(target, (byte[])pixels.Clone());

                edit(pixels, target.Width, target.Height, stride);

                _editable.WritePixels(target, pixels, stride, 0);
                Logger.Log($"winScreenshotCapture: 已应用标注 {_activeTool} {target.Width}x{target.Height}");
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 应用标注失败: {ex.Message}");
                FlashToolbarMessage("操作失败");
            }
        }

        /// <summary>
        /// 沿缓冲区边缘画一圈实心边框。线宽超过区域一半时自动收窄，
        /// 否则小框会被画成实心块。
        /// </summary>
        private static void DrawRectangleOutline(byte[] pixels, int width, int height, int stride)
        {
            int thickness = Math.Max(1, Math.Min(RectangleStrokeWidth, Math.Min(width, height) / 2));

            void SetPixel(int x, int y)
            {
                int i = (y * stride) + (x * 4);
                pixels[i] = RectangleColorBgra[0];
                pixels[i + 1] = RectangleColorBgra[1];
                pixels[i + 2] = RectangleColorBgra[2];
                pixels[i + 3] = RectangleColorBgra[3];
            }

            for (int t = 0; t < thickness; t++)
            {
                for (int x = 0; x < width; x++)
                {
                    SetPixel(x, t);
                    SetPixel(x, height - 1 - t);
                }
                for (int y = 0; y < height; y++)
                {
                    SetPixel(t, y);
                    SetPixel(width - 1 - t, y);
                }
            }
        }

        /// <summary>
        /// 块平均：每个 block 内所有像素取均值后整块填平
        /// </summary>
        private static void Pixelate(byte[] pixels, int width, int height, int stride, int block)
        {
            for (int by = 0; by < height; by += block)
            {
                int blockH = Math.Min(block, height - by);
                for (int bx = 0; bx < width; bx += block)
                {
                    int blockW = Math.Min(block, width - bx);

                    long b = 0, g = 0, r = 0, a = 0;
                    for (int y = 0; y < blockH; y++)
                    {
                        int rowStart = (by + y) * stride + bx * 4;
                        for (int x = 0; x < blockW; x++)
                        {
                            int i = rowStart + x * 4;
                            b += pixels[i];
                            g += pixels[i + 1];
                            r += pixels[i + 2];
                            a += pixels[i + 3];
                        }
                    }

                    int count = blockW * blockH;
                    byte avgB = (byte)(b / count), avgG = (byte)(g / count);
                    byte avgR = (byte)(r / count), avgA = (byte)(a / count);

                    for (int y = 0; y < blockH; y++)
                    {
                        int rowStart = (by + y) * stride + bx * 4;
                        for (int x = 0; x < blockW; x++)
                        {
                            int i = rowStart + x * 4;
                            pixels[i] = avgB;
                            pixels[i + 1] = avgG;
                            pixels[i + 2] = avgR;
                            pixels[i + 3] = avgA;
                        }
                    }
                }
            }
        }

        private void PushUndo(Int32Rect region, byte[] pixels)
        {
            _undoStack.Add((region, pixels));

            long total = 0;
            for (int i = _undoStack.Count - 1; i >= 0; i--)
            {
                total += _undoStack[i].pixels.LongLength;
                if (total > UndoBudgetBytes)
                {
                    // 从最早的一笔开始丢，保住最近的若干步
                    _undoStack.RemoveRange(0, i + 1);
                    break;
                }
            }
        }

        private void UndoLastEdit()
        {
            if (_undoStack.Count == 0 || _editable is null)
            {
                FlashToolbarMessage("没有可撤销的涂抹");
                return;
            }

            try
            {
                var (region, pixels) = _undoStack[^1];
                _undoStack.RemoveAt(_undoStack.Count - 1);
                _editable.WritePixels(region, pixels, region.Width * 4, 0);
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 撤销涂抹失败: {ex.Message}");
            }
        }

        // ==================== 放大镜 ====================

        /// <summary>
        /// 更新跟随光标的像素放大镜：显示放大后的邻域、屏幕坐标、选区尺寸和当前像素颜色
        /// </summary>
        private void UpdateMagnifier(System.Windows.Point canvasPoint)
        {
            if (_frozen is null)
            {
                Magnifier.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var (px, py) = CanvasToPixel(canvasPoint);

                // 采样窗口贴边时整体平移，保证始终取满 15x15，避免边缘处放大倍率突变
                int half = MagnifierSamplePixels / 2;
                int originX = Math.Clamp(px - half, 0, Math.Max(0, _frozen.PixelWidth - MagnifierSamplePixels));
                int originY = Math.Clamp(py - half, 0, Math.Max(0, _frozen.PixelHeight - MagnifierSamplePixels));
                int sampleW = Math.Min(MagnifierSamplePixels, _frozen.PixelWidth);
                int sampleH = Math.Min(MagnifierSamplePixels, _frozen.PixelHeight);

                // 从当前图像取样，涂过的马赛克在放大镜里也能看到
                MagnifierImage.Source = new CroppedBitmap(CurrentImage!, new Int32Rect(originX, originY, sampleW, sampleH));

                // 十字准星对准真正被采样的那个像素（贴边时不在正中）
                double cellW = MagnifierBoxSize / sampleW;
                double cellH = MagnifierBoxSize / sampleH;
                CrosshairV.Width = Math.Max(1, cellW);
                CrosshairV.Margin = new Thickness((px - originX) * cellW, 0, 0, 0);
                CrosshairH.Height = Math.Max(1, cellH);
                CrosshairH.Margin = new Thickness(0, (py - originY) * cellH, 0, 0);

                MagPosText.Text = $"X:{_virtualBounds.Left + px}  Y:{_virtualBounds.Top + py}";
                MagSizeText.Text = HasUsableSelection() || _isSelecting
                    ? $"{PixelLength(_selectionWidth, true)} × {PixelLength(_selectionHeight, false)}"
                    : "拖动以选择";

                var color = GetPixelColor(px, py);
                if (color.HasValue)
                {
                    ColorSwatch.Background = new SolidColorBrush(color.Value);
                    MagColorText.Text = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                }

                PositionMagnifier(canvasPoint);
                if (Magnifier.Visibility != Visibility.Visible)
                {
                    Magnifier.Visibility = Visibility.Visible;
                    FadeIn(Magnifier, 110);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 放大镜更新失败: {ex.Message}");
                Magnifier.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 把放大镜放在光标右下方；贴近屏幕边缘时翻转，保证不被裁掉
        /// </summary>
        private void PositionMagnifier(System.Windows.Point canvasPoint)
        {
            Magnifier.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = Magnifier.DesiredSize;

            // 同样以光标所在屏幕为界，避免放大镜跨到相邻屏或被推出物理屏幕
            var screen = GetScreenRectAt(canvasPoint);

            double x = canvasPoint.X + MagnifierMargin;
            double y = canvasPoint.Y + MagnifierMargin;

            if (x + size.Width > screen.Right)
                x = canvasPoint.X - MagnifierMargin - size.Width;
            if (y + size.Height > screen.Bottom)
                y = canvasPoint.Y - MagnifierMargin - size.Height;

            Canvas.SetLeft(Magnifier, Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)));
            Canvas.SetTop(Magnifier, Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));
        }

        private System.Windows.Media.Color? GetPixelColor(int px, int py)
        {
            try
            {
                var img = CurrentImage;
                if (img is null || px < 0 || py < 0 || px >= img.PixelWidth || py >= img.PixelHeight) return null;

                var pixel = new byte[4];
                new CroppedBitmap(img, new Int32Rect(px, py, 1, 1)).CopyPixels(pixel, 4, 0);
                // CreateBitmapSourceFromHBitmap 产出的是 BGRA 排列
                return System.Windows.Media.Color.FromRgb(pixel[2], pixel[1], pixel[0]);
            }
            catch
            {
                return null;
            }
        }

        // ==================== 坐标换算 ====================

        /// <summary>
        /// 画布坐标（WPF 单位）→ 冻结底图的像素坐标。
        /// 缩放比直接由底图尺寸与画布尺寸相除得到，比查询 DPI 更可靠，
        /// 多显示器混合 DPI 时也不会算偏。
        /// </summary>
        private (int x, int y) CanvasToPixel(System.Windows.Point p)
        {
            double scaleX = ScaleX(), scaleY = ScaleY();
            int px = (int)Math.Round(p.X * scaleX);
            int py = (int)Math.Round(p.Y * scaleY);
            return (Math.Clamp(px, 0, Math.Max(0, (_frozen?.PixelWidth ?? 1) - 1)),
                    Math.Clamp(py, 0, Math.Max(0, (_frozen?.PixelHeight ?? 1) - 1)));
        }

        private double ScaleX() =>
            _frozen is not null && OverlayCanvas.ActualWidth > 0 ? _frozen.PixelWidth / OverlayCanvas.ActualWidth : 1.0;

        private double ScaleY() =>
            _frozen is not null && OverlayCanvas.ActualHeight > 0 ? _frozen.PixelHeight / OverlayCanvas.ActualHeight : 1.0;

        private int PixelLength(double canvasLength, bool horizontal) =>
            (int)Math.Round(canvasLength * (horizontal ? ScaleX() : ScaleY()));

        // ==================== 确认 / 取消 ====================

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);

            if (e.Key == Key.Escape)
            {
                // 涂抹模式下 Esc 先退出涂抹，不要一步把整个截图也取消掉
                if (_activeTool != EditTool.None)
                {
                    SetTool(EditTool.None);
                    ShowToolbar();
                    return;
                }
                Cancel("用户按下 Escape");
            }
            else if (e.Key == Key.Enter)
            {
                if (HasUsableSelection())
                {
                    CaptureSelectedRegion();
                }
            }
            else if (ctrl && e.Key == Key.C)
            {
                CopySelectionToClipboard();
            }
            else if (ctrl && e.Key == Key.S)
            {
                SaveSelectionToFile();
            }
            else if (ctrl && e.Key == Key.Z)
            {
                UndoLastEdit();
            }
            else if (ctrl && e.Key == Key.A)
            {
                SelectEntireCanvas();
            }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                // 方向键微调选区。ShareX 用 Shift 切换到大步长，这里沿用同样的手感。
                bool shift = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
                NudgeSelection(e.Key, shift ? NudgeSpeedFast : NudgeSpeedSlow);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Ctrl+A：整屏作为选区
        /// </summary>
        private void SelectEntireCanvas()
        {
            if (_activeTool != EditTool.None) return;

            ClearHighlight();

            SelectionRect.Visibility = Visibility.Visible;
            ApplySelectionRect(new Rect(0, 0, OverlayCanvas.ActualWidth, OverlayCanvas.ActualHeight));
            ShowToolbar();
        }

        /// <summary>
        /// 按方向键平移整个选区，并夹在画布内。
        /// 有选区时才生效——没选区时按方向键不该有任何动静。
        /// </summary>
        private void NudgeSelection(Key key, double step)
        {
            if (_activeTool != EditTool.None || !HasUsableSelection()) return;

            double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };

            StopHoverAnimation();

            double maxX = Math.Max(0, OverlayCanvas.ActualWidth - _selectionWidth);
            double maxY = Math.Max(0, OverlayCanvas.ActualHeight - _selectionHeight);

            ApplySelectionRect(new Rect(
                Math.Clamp(_selectionLeft + dx, 0, maxX),
                Math.Clamp(_selectionTop + dy, 0, maxY),
                _selectionWidth,
                _selectionHeight));

            // 选区动了，功能栏要跟着走，否则会脱节地留在原地
            if (Toolbar.Visibility == Visibility.Visible) ShowToolbar();
        }

        private void Cancel(string reason)
        {
            Logger.Log($"winScreenshotCapture: 截图已取消（{reason}）");
            CaptureCancelled?.Invoke(this, EventArgs.Empty);
            Close();
        }

        /// <summary>
        /// 选区在冻结底图上的像素矩形。四舍五入后可能越界，必须夹紧，
        /// 否则 CroppedBitmap 会直接抛异常。
        /// </summary>
        private Int32Rect GetSelectionRegion()
        {
            double scaleX = ScaleX(), scaleY = ScaleY();
            int x = (int)Math.Round(_selectionLeft * scaleX);
            int y = (int)Math.Round(_selectionTop * scaleY);
            int width = (int)Math.Round(_selectionWidth * scaleX);
            int height = (int)Math.Round(_selectionHeight * scaleY);

            x = Math.Clamp(x, 0, _frozen!.PixelWidth - 1);
            y = Math.Clamp(y, 0, _frozen.PixelHeight - 1);
            width = Math.Clamp(width, 1, _frozen.PixelWidth - x);
            height = Math.Clamp(height, 1, _frozen.PixelHeight - y);

            return new Int32Rect(x, y, width, height);
        }

        /// <summary>
        /// 复制到剪贴板。不关窗口——确认才是主操作，复制只是顺手，
        /// 关掉会让 AI 发起的那条请求莫名其妙地变成「用户取消」。
        /// </summary>
        private void CopySelectionToClipboard()
        {
            if (_frozen is null || !HasUsableSelection()) return;

            try
            {
                Clipboard.SetImage(new CroppedBitmap(CurrentImage!, GetSelectionRegion()));
                FlashToolbarMessage("已复制到剪贴板");
                Logger.Log("winScreenshotCapture: 选区已复制到剪贴板");
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 复制到剪贴板失败: {ex.Message}");
                FlashToolbarMessage("复制失败");
            }
        }

        /// <summary>
        /// 另存为 PNG。同样不关窗口。
        /// </summary>
        private void SaveSelectionToFile()
        {
            if (_frozen is null || !HasUsableSelection()) return;

            // 保存对话框会被 Topmost 的遮罩压住，先让出置顶
            bool wasTopmost = Topmost;
            Topmost = false;

            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG 图片|*.png",
                    DefaultExt = ".png",
                    FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                };

                if (dialog.ShowDialog(this) != true) return;

                var cropped = new CroppedBitmap(CurrentImage!, GetSelectionRegion());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));

                using (var fs = File.Create(dialog.FileName))
                {
                    encoder.Save(fs);
                }

                FlashToolbarMessage("已保存");
                Logger.Log($"winScreenshotCapture: 选区已保存到 {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 保存截图失败: {ex.Message}");
                FlashToolbarMessage("保存失败");
            }
            finally
            {
                Topmost = wasTopmost;
                Activate();
            }
        }

        /// <summary>
        /// 在功能栏的尺寸位置上短暂显示一条反馈，随后恢复尺寸显示
        /// </summary>
        private void FlashToolbarMessage(string message)
        {
            ToolbarSizeText.Text = message;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (HasUsableSelection())
                {
                    ToolbarSizeText.Text = $"{PixelLength(_selectionWidth, true)} × {PixelLength(_selectionHeight, false)}";
                }
            };
            timer.Start();
        }

        private void CaptureSelectedRegion()
        {
            try
            {
                if (_frozen is null)
                {
                    Logger.Log("winScreenshotCapture: 无冻结底图，无法裁剪");
                    Cancel("无冻结底图");
                    return;
                }

                if (!HasUsableSelection()) return;

                var region = GetSelectionRegion();
                int width = region.Width, height = region.Height;

                // 先隐藏窗口再回调，避免宿主随后弹出的编辑器被本窗口的 Topmost 压住
                Hide();

                var imageData = ScreenCapture.CropToPng(CurrentImage!, region);
                if (imageData is null || imageData.Length == 0)
                {
                    Logger.Log("winScreenshotCapture: 裁剪结果为空");
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                    Close();
                    return;
                }

                Logger.Log($"winScreenshotCapture: 已截取 {width}x{height}，{imageData.Length} 字节");
                ScreenshotCaptured?.Invoke(this, imageData);
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"winScreenshotCapture: 截图失败: {ex.Message}");
                CaptureCancelled?.Invoke(this, EventArgs.Empty);
                Close();
            }
        }
    }
}
