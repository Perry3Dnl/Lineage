using System.Windows;
using System.Windows.Controls;
using Lineage;

namespace LineageIde
{
    public partial class LineagePanel : UserControl
    {
        public LineageSession Session { get; }

        public LineagePanel()
            : this(new LineageSession(Application.Current != null ? Application.Current.Dispatcher : System.Windows.Threading.Dispatcher.CurrentDispatcher))
        {
        }

        public LineagePanel(LineageSession session)
        {
            Session = session;
            DataContext = Session;
            InitializeComponent();
            Session.StartListening();
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            Session.ShowPrevious();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            Session.ShowNext();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Session.Clear();
        }

        private void Raw_Click(object sender, RoutedEventArgs e)
        {
            Session.ShowRaw = RawToggle.IsChecked == true;
        }

        private void Flow_NodeActivated(object sender, LineageNodeEventArgs e)
        {
            if (e == null || e.Node == null)
            {
                return;
            }

            Session.Selected = e.Node;
            OpenNode(e.Node);
        }

        private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = (sender as ListBox)?.SelectedItem as ReportHistoryItem;
            if (item != null)
            {
                Session.Load(item.Report);
            }
        }

        private void OpenSource_Click(object sender, RoutedEventArgs e)
        {
            OpenNode(Session.Selected ?? (Session.Current != null ? Session.Current.Focus : null));
        }

        private void OpenNode(LineageNode node)
        {
            if (node == null)
            {
                return;
            }

            var navigate = NavigateToSource;
            if (navigate != null)
            {
                navigate(node);
            }
        }

        public event System.Action<LineageNode> NavigateToSource;
    }
}
