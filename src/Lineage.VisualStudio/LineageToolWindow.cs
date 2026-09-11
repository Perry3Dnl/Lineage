using System;
using System.Runtime.InteropServices;
using Lineage;
using LineageIde;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LineageVisualStudio
{
    [Guid(PackageGuids.LineageWindowString)]
    public sealed class LineageToolWindow : ToolWindowPane
    {
        public LineageToolWindow()
            : base(null)
        {
            try
            {
                Caption = "Lineage";
                var panel = new LineagePanel(LineageIdeHost.Ensure());
                panel.NavigateToSource += node =>
                {
                    var package = LineagePackage.Instance;
                    if (package != null)
                    {
                        package.JoinableTaskFactory.RunAsync(async () =>
                        {
                            try
                            {
                                await SourceNavigation.OpenAsync(node);
                            }
                            catch (Exception ex)
                            {
                                LineagePackage.Log(ex);
                            }
                        });
                    }
                };
                Content = panel;
            }
            catch (Exception ex)
            {
                LineagePackage.Log(ex);
                Caption = "Lineage";
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Lineage failed to load:\n" + ex.Message,
                    Margin = new System.Windows.Thickness(12),
                    TextWrapping = System.Windows.TextWrapping.Wrap
                };
            }
        }
    }
}
