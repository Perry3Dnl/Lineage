using System.Diagnostics;
using System.Windows;
using Lineage;
using LineageIde;

namespace LineageViewer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = "Lineage";
            Panel.NavigateToSource += OpenSource;
        }

        private static void OpenSource(LineageNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.File))
            {
                return;
            }

            if (TryOpenEditor(node.File, node.Line))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = node.File,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private static bool TryOpenEditor(string file, int line)
        {
            var target = line > 0 ? file + ":" + line : file;
            var args = "-g \"" + target + "\"";
            return TryStart("cursor", args) || TryStart("code", args);
        }

        private static bool TryStart(string fileName, string arguments)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true
                });
                return process != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
