using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lineage;

namespace LineageIde
{
    public sealed class LineageNodeEventArgs : EventArgs
    {
        public LineageNodeEventArgs(LineageNode node)
        {
            Node = node;
        }

        public LineageNode Node { get; }
    }

    public partial class LineageFlowView : UserControl
    {
        private static readonly Brush LinkBrush = Freeze(0x6A, 0x6A, 0x70);
        private static readonly Brush LinkSelectedBrush = Freeze(0x00, 0x7A, 0xCC);

        public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
            nameof(Graph),
            typeof(LineageFlowGraph),
            typeof(LineageFlowView),
            new PropertyMetadata(LineageFlowGraph.Empty, OnGraphChanged));

        public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
            nameof(SelectedNode),
            typeof(LineageNode),
            typeof(LineageFlowView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

        public LineageFlowView()
        {
            InitializeComponent();
        }

        public LineageFlowGraph Graph
        {
            get { return (LineageFlowGraph)GetValue(GraphProperty); }
            set { SetValue(GraphProperty, value); }
        }

        public LineageNode SelectedNode
        {
            get { return (LineageNode)GetValue(SelectedNodeProperty); }
            set { SetValue(SelectedNodeProperty, value); }
        }

        public event EventHandler<LineageNodeEventArgs> NodeActivated;

        private static void OnGraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = d as LineageFlowView;
            if (view != null)
            {
                view.ApplyGraph();
            }
        }

        private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = d as LineageFlowView;
            if (view != null)
            {
                view.ApplySelection(false);
            }
        }

        private void ApplyGraph()
        {
            var graph = Graph ?? LineageFlowGraph.Empty;
            Surface.Width = Math.Max(graph.Width, 1);
            Surface.Height = Math.Max(graph.Height, 1);
            LinksLayer.Width = Surface.Width;
            LinksLayer.Height = Surface.Height;
            ApplySelection(true);
            Dispatcher.BeginInvoke(new Action(ScrollSelectedIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplySelection(bool scroll)
        {
            var graph = Graph;
            if (graph != null)
            {
                graph.SetSelected(SelectedNode);
            }

            DrawLinks();
            if (scroll)
            {
                ScrollSelectedIntoView();
            }
        }

        private void DrawLinks()
        {
            LinksLayer.Children.Clear();
            var graph = Graph;
            if (graph == null)
            {
                return;
            }

            var selectedId = SelectedNode != null ? SelectedNode.ValueId : 0;
            for (var i = 0; i < graph.Links.Count; i++)
            {
                var link = graph.Links[i];
                var highlight = selectedId != 0
                    && (link.From.Node.ValueId == selectedId || link.To.Node.ValueId == selectedId);
                var brush = highlight ? LinkSelectedBrush : LinkBrush;
                var thickness = highlight ? 2.4 : 1.3;
                LinksLayer.Children.Add(CreateConnector(link, brush, thickness));
                LinksLayer.Children.Add(CreateArrow(link, brush));
            }
        }

        private static Path CreateConnector(LineageFlowLink link, Brush brush, double thickness)
        {
            var x1 = link.From.X + link.From.Width / 2;
            var y1 = link.From.Y + link.From.Height;
            var x2 = link.To.X + link.To.Width / 2;
            var y2 = link.To.Y;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                if (Math.Abs(x1 - x2) < 0.5)
                {
                    ctx.LineTo(new Point(x2, y2 - 9), true, true);
                }
                else
                {
                    var mid = y1 + (y2 - y1) / 2;
                    ctx.LineTo(new Point(x1, mid), true, true);
                    ctx.LineTo(new Point(x2, mid), true, true);
                    ctx.LineTo(new Point(x2, y2 - 9), true, true);
                }
            }

            geo.Freeze();
            return new Path
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
        }

        private static Polygon CreateArrow(LineageFlowLink link, Brush brush)
        {
            var x = link.To.X + link.To.Width / 2;
            var y = link.To.Y;
            return new Polygon
            {
                Points = new PointCollection
                {
                    new Point(x, y + 1),
                    new Point(x - 6, y - 9),
                    new Point(x + 6, y - 9)
                },
                Fill = brush,
                IsHitTestVisible = false
            };
        }

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            var item = element != null ? element.DataContext as LineageFlowItem : null;
            if (item == null)
            {
                return;
            }

            SelectedNode = item.Node;
            var handler = NodeActivated;
            if (handler != null)
            {
                handler(this, new LineageNodeEventArgs(item.Node));
            }

            e.Handled = true;
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ScrollSelectedIntoView()
        {
            var graph = Graph;
            if (graph == null || SelectedNode == null || Scroller.ViewportWidth <= 0)
            {
                return;
            }

            var item = graph.Find(SelectedNode.ValueId);
            if (item == null)
            {
                return;
            }

            var x = item.X + item.Width / 2 - Scroller.ViewportWidth / 2;
            var y = item.Y + item.Height / 2 - Scroller.ViewportHeight / 2;
            Scroller.ScrollToHorizontalOffset(Math.Max(0, x));
            Scroller.ScrollToVerticalOffset(Math.Max(0, y));
        }

        private static Brush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
