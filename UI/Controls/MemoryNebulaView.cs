using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPetLLM.Core.Services.MemoryGraph;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 记忆关系星云图。节点位置在后台算好（<see cref="MemoryGraphBuilder"/>），这里只负责画和交互：
    /// 拖动平移、滚轮以光标为中心缩放、悬停高亮、单击选中（邻居一起亮，其余压暗）、双击复位。
    ///
    /// 全部自绘、不建子元素：几百个节点各建一个 Ellipse 会让布局和命中测试都慢下来，
    /// 而这个窗口和宠物动画共用一条 UI 线程。画笔、光晕刷子、文字都缓存并冻结，重绘只做坐标变换。
    /// </summary>
    public sealed class MemoryNebulaView : FrameworkElement
    {
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x7A, 0xA2, 0xF7), Color.FromRgb(0xF7, 0x76, 0x8E), Color.FromRgb(0x9E, 0xCE, 0x6A),
            Color.FromRgb(0xE0, 0xAF, 0x68), Color.FromRgb(0xBB, 0x9A, 0xF7), Color.FromRgb(0x7D, 0xCF, 0xFF),
            Color.FromRgb(0xFF, 0x9E, 0x64), Color.FromRgb(0x73, 0xDA, 0xCA), Color.FromRgb(0xC0, 0xCA, 0xF5),
            Color.FromRgb(0xF4, 0xA7, 0xD3), Color.FromRgb(0xB4, 0xF9, 0xF8), Color.FromRgb(0xDD, 0xB6, 0xF2)
        };

        private static readonly Color AnchorColor = Color.FromRgb(0xFF, 0xE8, 0xA3);

        private const int AlwaysLabeledEntities = 40;
        private const double MinScale = 0.08;
        private const double MaxScale = 8;

        private static readonly Brush Background = Freeze(new RadialGradientBrush(
            Color.FromRgb(0x1C, 0x24, 0x44), Color.FromRgb(0x06, 0x08, 0x12))
        {
            Center = new Point(0.5, 0.45),
            GradientOrigin = new Point(0.5, 0.45),
            RadiusX = 0.75,
            RadiusY = 0.75
        });

        private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xF2, 0xF4, 0xFF)));
        private static readonly Brush LabelShadow = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0x05, 0x07, 0x10)));
        private static readonly Pen PersonRing = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1.2));
        private static readonly Pen SelectedRing = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 2.0));
        private static readonly Typeface LabelFace = new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface LabelFaceBold = new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly (double X, double Y, double Size, int Bucket)[] _stars;
        private readonly Brush[] _starBrushes;

        private readonly Dictionary<(int Color, bool Anchor), (Brush Core, Brush Glow)> _nodeBrushes = new();
        private readonly Dictionary<(int Color, int State), Pen> _edgePens = new();
        private readonly Dictionary<(int Node, bool Bold), FormattedText> _labels = new();

        private NebulaGraph? _graph;
        private HashSet<int> _labeled = new();
        private double _maxEdgeWeight = 1;

        private double _scale = 1;
        private Vector _pan;
        private bool _needsFit;

        private int _hover = -1;
        private int _selected = -1;
        private HashSet<int>? _focus;

        private Point _pressAt;
        private Vector _panAtPress;
        private bool _pressed;
        private bool _dragging;

        public event EventHandler<NebulaNode?>? SelectionChanged;

        public NebulaNode? SelectedNode => _graph is not null && _selected >= 0 ? _graph.Nodes[_selected] : null;

        public MemoryNebulaView()
        {
            ClipToBounds = true;
            Cursor = Cursors.Arrow;

            // 星尘固定种子，按归一化坐标存：窗口怎么缩放，星空都还是那一片
            var random = new Random(20261103);
            _stars = new (double, double, double, int)[260];
            for (var i = 0; i < _stars.Length; i++)
                _stars[i] = (random.NextDouble(), random.NextDouble(), 0.35 + random.NextDouble() * 1.1, random.Next(4));
            _starBrushes = new Brush[4];
            for (var i = 0; i < _starBrushes.Length; i++)
                _starBrushes[i] = Freeze(new SolidColorBrush(Color.FromArgb((byte)(40 + i * 35), 0xDD, 0xE6, 0xFF)));

            SizeChanged += (_, _) =>
            {
                if (_needsFit)
                    FitToView();
                InvalidateVisual();
            };
        }

        public void SetGraph(NebulaGraph? graph)
        {
            _graph = graph;
            _labels.Clear();
            _hover = -1;
            _selected = -1;
            _focus = null;
            _maxEdgeWeight = graph is { Edges.Count: > 0 } ? graph.Edges.Max(e => e.Weight) : 1;

            _labeled = graph is null
                ? new HashSet<int>()
                : graph.Nodes.Where(n => n.Kind == NebulaNodeKind.Entity)
                    .OrderByDescending(n => n.IsAnchor).ThenByDescending(n => n.Weight)
                    .Take(AlwaysLabeledEntities)
                    .Select(n => n.Index)
                    .ToHashSet();

            FitToView();
            SelectionChanged?.Invoke(this, null);
        }

        /// <summary>把整张图缩放平移到刚好装进视口。</summary>
        public void FitToView()
        {
            if (_graph is null || _graph.Nodes.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
            {
                _needsFit = true;
                InvalidateVisual();
                return;
            }

            _needsFit = false;
            var minX = _graph.Nodes.Min(n => n.X - n.Radius);
            var maxX = _graph.Nodes.Max(n => n.X + n.Radius);
            var minY = _graph.Nodes.Min(n => n.Y - n.Radius);
            var maxY = _graph.Nodes.Max(n => n.Y + n.Radius);
            var width = Math.Max(1, maxX - minX);
            var height = Math.Max(1, maxY - minY);

            const double margin = 48;
            _scale = Math.Clamp(Math.Min((ActualWidth - 2 * margin) / width, (ActualHeight - 2 * margin) / height), MinScale, 2.5);
            _pan = new Vector(
                ActualWidth / 2 - (minX + width / 2) * _scale,
                ActualHeight / 2 - (minY + height / 2) * _scale);
            InvalidateVisual();
        }

        private Point ToScreen(NebulaNode node) => new(node.X * _scale + _pan.X, node.Y * _scale + _pan.Y);

        private double ScreenRadius(NebulaNode node)
            => Math.Max(node.Kind == NebulaNodeKind.Entity ? 3.5 : 1.6, node.Radius * Math.Sqrt(_scale));

        // ── 绘制 ─────────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Background, null, bounds);

            // 星尘随平移轻微视差，拖动时有纵深感
            var parallax = _pan * 0.04;
            foreach (var (sx, sy, size, bucket) in _stars)
            {
                var x = Wrap(sx * bounds.Width + parallax.X, bounds.Width);
                var y = Wrap(sy * bounds.Height + parallax.Y, bounds.Height);
                dc.DrawEllipse(_starBrushes[bucket], null, new Point(x, y), size, size);
            }

            var graph = _graph;
            if (graph is null || graph.Nodes.Count == 0)
                return;

            var focus = _focus;
            DrawEdges(dc, graph, focus, bounds);

            // 事实先画、实体后画，实体压在上面
            foreach (var node in graph.Nodes.Where(n => n.Kind == NebulaNodeKind.Fact))
                DrawNode(dc, node, focus, bounds);
            foreach (var node in graph.Nodes.Where(n => n.Kind == NebulaNodeKind.Entity))
                DrawNode(dc, node, focus, bounds);

            DrawLabels(dc, graph, focus, bounds);
        }

        private void DrawEdges(DrawingContext dc, NebulaGraph graph, HashSet<int>? focus, Rect bounds)
        {
            // 同一支笔的边合进一个几何体，几百条线只发几次绘制调用
            var groups = new Dictionary<Pen, StreamGeometry>();
            var contexts = new Dictionary<Pen, StreamGeometryContext>();
            var visible = bounds;
            visible.Inflate(40, 40);

            foreach (var edge in graph.Edges)
            {
                var a = graph.Nodes[edge.A];
                var b = graph.Nodes[edge.B];
                var pa = ToScreen(a);
                var pb = ToScreen(b);
                if (!visible.Contains(pa) && !visible.Contains(pb) && !SegmentMayCross(pa, pb, visible))
                    continue;

                int state;
                if (focus is null)
                    state = edge.Weight >= _maxEdgeWeight * 0.35 ? 1 : 0;
                else if (focus.Contains(edge.A) && focus.Contains(edge.B) && (edge.A == FocusCenter || edge.B == FocusCenter))
                    state = 2;
                else
                    state = 3;

                // 边的颜色跟实体一端走；两端都是实体时跟较重的那个
                var colorNode = a.Kind == NebulaNodeKind.Entity && (b.Kind != NebulaNodeKind.Entity || a.Weight >= b.Weight) ? a : b;
                var pen = EdgePen(ColorIndexOf(colorNode), state);
                if (!contexts.TryGetValue(pen, out var ctx))
                {
                    var geometry = new StreamGeometry();
                    ctx = geometry.Open();
                    groups[pen] = geometry;
                    contexts[pen] = ctx;
                }
                ctx.BeginFigure(pa, false, false);
                ctx.LineTo(pb, true, false);
            }

            foreach (var (pen, geometry) in groups)
            {
                contexts[pen].Close();
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }

        private int FocusCenter => _selected >= 0 ? _selected : _hover;

        private void DrawNode(DrawingContext dc, NebulaNode node, HashSet<int>? focus, Rect bounds)
        {
            var p = ToScreen(node);
            var r = ScreenRadius(node);
            var glowR = r * (node.Kind == NebulaNodeKind.Entity ? 3.0 : 2.2);
            if (p.X < -glowR || p.Y < -glowR || p.X > bounds.Width + glowR || p.Y > bounds.Height + glowR)
                return;

            var opacity = 1.0;
            if (focus is not null && !focus.Contains(node.Index))
                opacity = 0.12;
            else if (node.Status == MemoryAtomStatus.Fading)
                opacity = 0.45;

            var (core, glow) = NodeBrushes(ColorIndexOf(node), node.IsAnchor);
            if (opacity < 1)
                dc.PushOpacity(opacity);

            dc.DrawEllipse(glow, null, p, glowR, glowR);
            var ring = node.Index == _selected
                ? SelectedRing
                : node.Kind == NebulaNodeKind.Entity && node.EntityType == MemoryEntityType.Person ? PersonRing : null;
            dc.DrawEllipse(core, ring, p, r, r);

            if (opacity < 1)
                dc.Pop();
        }

        private void DrawLabels(DrawingContext dc, NebulaGraph graph, HashSet<int>? focus, Rect bounds)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var showAllEntities = _scale >= 1.4;

            foreach (var node in graph.Nodes)
            {
                bool show;
                if (focus is not null)
                    show = focus.Contains(node.Index) && (node.Kind == NebulaNodeKind.Entity || node.Index == FocusCenter || _scale >= 1.2);
                else
                    show = node.Kind == NebulaNodeKind.Entity && (showAllEntities || _labeled.Contains(node.Index));
                if (!show && node.Index != _hover)
                    continue;

                var p = ToScreen(node);
                if (p.X < -200 || p.Y < -40 || p.X > bounds.Width + 200 || p.Y > bounds.Height + 40)
                    continue;

                var bold = node.Index == FocusCenter || node.IsAnchor;
                var text = LabelOf(node, bold, dpi);
                var origin = new Point(p.X - text.Width / 2, p.Y + ScreenRadius(node) + 3);

                if (focus is null && node.Kind == NebulaNodeKind.Entity && !node.IsAnchor && node.Index != _hover)
                    dc.PushOpacity(0.55 + 0.45 * Math.Min(1, node.Radius / 20));
                else
                    dc.PushOpacity(1);
                dc.DrawRoundedRectangle(LabelShadow, null,
                    new Rect(origin.X - 4, origin.Y - 1, text.Width + 8, text.Height + 2), 4, 4);
                dc.DrawText(text, origin);
                dc.Pop();
            }
        }

        private FormattedText LabelOf(NebulaNode node, bool bold, double dpi)
        {
            if (_labels.TryGetValue((node.Index, bold), out var cached))
                return cached;

            var label = node.Label.Trim();
            if (node.Kind == NebulaNodeKind.Fact)
            {
                label = label.TrimStart('-', '*', '•', ' ');
                if (label.Length > 22)
                    label = label[..21] + "…";
            }
            else if (label.Length > 16)
            {
                label = label[..15] + "…";
            }

            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                bold ? LabelFaceBold : LabelFace, node.Kind == NebulaNodeKind.Entity ? 12 : 10.5, LabelBrush, dpi);
            _labels[(node.Index, bold)] = text;
            return text;
        }

        private int ColorIndexOf(NebulaNode node) => node.IsAnchor ? -1 : node.Community % Palette.Length;

        private (Brush Core, Brush Glow) NodeBrushes(int colorIndex, bool anchor)
        {
            if (_nodeBrushes.TryGetValue((colorIndex, anchor), out var brushes))
                return brushes;

            var color = colorIndex < 0 ? AnchorColor : Palette[colorIndex];
            var core = Freeze(new RadialGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), color)
            {
                GradientOrigin = new Point(0.35, 0.35),
                Center = new Point(0.4, 0.4),
                RadiusX = 0.75,
                RadiusY = 0.75
            });
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, color.R, color.G, color.B), 0.45));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
            brushes = (core, Freeze(glow));
            _nodeBrushes[(colorIndex, anchor)] = brushes;
            return brushes;
        }

        /// <param name="state">0 普通、1 较重的边、2 聚焦中、3 聚焦时被压暗。</param>
        private Pen EdgePen(int colorIndex, int state)
        {
            if (_edgePens.TryGetValue((colorIndex, state), out var pen))
                return pen;

            var color = colorIndex < 0 ? AnchorColor : Palette[colorIndex];
            var (alpha, thickness) = state switch
            {
                1 => (0x55, 1.1),
                2 => (0xC0, 1.6),
                3 => (0x0C, 0.7),
                _ => (0x26, 0.7)
            };
            pen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb((byte)alpha, color.R, color.G, color.B)), thickness));
            _edgePens[(colorIndex, state)] = pen;
            return pen;
        }

        // ── 交互 ─────────────────────────────────────────────────────────

        private int HitTestNode(Point point)
        {
            var graph = _graph;
            if (graph is null)
                return -1;

            // 实体压在事实上面，先测实体；同类里取最近的
            var best = -1;
            var bestDistance = double.MaxValue;
            foreach (var kind in new[] { NebulaNodeKind.Entity, NebulaNodeKind.Fact })
            {
                foreach (var node in graph.Nodes)
                {
                    if (node.Kind != kind)
                        continue;
                    var p = ToScreen(node);
                    var distance = (p - point).Length;
                    if (distance <= ScreenRadius(node) + 4 && distance < bestDistance)
                    {
                        best = node.Index;
                        bestDistance = distance;
                    }
                }
                if (best >= 0)
                    return best;
            }
            return -1;
        }

        private void UpdateFocus()
        {
            var center = FocusCenter;
            if (_graph is null || center < 0)
            {
                _focus = null;
                return;
            }
            var focus = new HashSet<int> { center };
            foreach (var n in _graph.Nodes[center].Neighbors)
                focus.Add(n);
            _focus = focus;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2)
            {
                FitToView();
                e.Handled = true;
                return;
            }

            _pressAt = e.GetPosition(this);
            _panAtPress = _pan;
            _pressed = true;
            _dragging = false;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var position = e.GetPosition(this);

            if (_pressed)
            {
                var delta = position - _pressAt;
                if (!_dragging && delta.Length > 3)
                {
                    _dragging = true;
                    Cursor = Cursors.SizeAll;
                }
                if (_dragging)
                {
                    _pan = _panAtPress + delta;
                    InvalidateVisual();
                }
                return;
            }

            var hit = HitTestNode(position);
            if (hit != _hover)
            {
                _hover = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
                if (_selected < 0)
                    UpdateFocus();
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_pressed)
                return;

            _pressed = false;
            ReleaseMouseCapture();
            Cursor = _hover >= 0 ? Cursors.Hand : Cursors.Arrow;

            if (!_dragging)
            {
                var hit = HitTestNode(e.GetPosition(this));
                _selected = hit == _selected ? -1 : hit;
                UpdateFocus();
                InvalidateVisual();
                SelectionChanged?.Invoke(this, SelectedNode);
            }
            _dragging = false;
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0 && !_pressed)
            {
                _hover = -1;
                if (_selected < 0)
                    UpdateFocus();
                InvalidateVisual();
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var position = e.GetPosition(this);
            var factor = Math.Pow(1.15, e.Delta / 120.0);
            var scale = Math.Clamp(_scale * factor, MinScale, MaxScale);

            // 光标下那个世界坐标缩放前后保持在原处
            var worldX = (position.X - _pan.X) / _scale;
            var worldY = (position.Y - _pan.Y) / _scale;
            _scale = scale;
            _pan = new Vector(position.X - worldX * _scale, position.Y - worldY * _scale);
            InvalidateVisual();

            // 不吃掉的话外层 TabControl/ScrollViewer 会跟着滚
            e.Handled = true;
        }

        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
            => new PointHitTestResult(this, hitTestParameters.HitPoint);

        // ── 工具 ─────────────────────────────────────────────────────────

        private static double Wrap(double value, double size)
        {
            if (size <= 0)
                return value;
            value %= size;
            return value < 0 ? value + size : value;
        }

        /// <summary>两端都在视口外时粗判一下线段会不会穿过视口（包围盒相交即算）。</summary>
        private static bool SegmentMayCross(Point a, Point b, Rect rect)
            => new Rect(a, b).IntersectsWith(rect);

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
