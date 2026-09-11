using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LineageIde;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace LineageVisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideBindingPath]
    [ProvideToolWindow(typeof(LineageToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideToolWindowVisibility(typeof(LineageToolWindow), UIContextGuids80.Debugging)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.Debugging, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.LineagePackageString)]
    public sealed class LineagePackage : AsyncPackage
    {
        public static LineagePackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;

            try
            {
                LineageIdeHost.Ensure();
                LineageIdeHost.Session.ReportReceived -= OnReportReceived;
                LineageIdeHost.Session.ReportReceived += OnReportReceived;
            }
            catch (Exception ex)
            {
                Log(ex);
            }

            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands != null)
            {
                var id = new CommandID(PackageGuids.LineageCmdSet, PackageIds.LineageWindow);
                commands.AddCommand(new MenuCommand((s, e) => ShowWindow(), id));
                var addId = new CommandID(PackageGuids.LineageCmdSet, PackageIds.AddLineagePackage);
                commands.AddCommand(new MenuCommand((s, e) => AddLineagePackage(), addId));
            }
        }

        private void OnReportReceived(Lineage.LineageReport report)
        {
            _ = JoinableTaskFactory.RunAsync(ShowWindowAsync);
        }

        internal void AddLineagePackage()
        {
            _ = JoinableTaskFactory.RunAsync(AddLineagePackageAsync);
        }

        internal async Task AddLineagePackageAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                LineageProjectInstaller.Install(this);
            }
            catch (Exception ex)
            {
                Log(ex);
                VsShellUtilities.ShowMessageBox(
                    this,
                    ex.Message,
                    "Lineage",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        internal void ShowWindow()
        {
            _ = JoinableTaskFactory.RunAsync(ShowWindowAsync);
        }

        internal async Task ShowWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            await ShowToolWindowAsync(typeof(LineageToolWindow), 0, true, DisposalToken);
        }

        internal static void Log(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lineage");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "package.log"), DateTime.Now + Environment.NewLine + ex + Environment.NewLine);
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LineageIdeHost.DisposeSession();
                Instance = null;
            }

            base.Dispose(disposing);
        }
    }

    internal static class LineageIdeHost
    {
        public static LineageSession Session { get; private set; }

        public static LineageSession Ensure()
        {
            if (Session == null)
            {
                var dispatcher = System.Windows.Application.Current != null
                    ? System.Windows.Application.Current.Dispatcher
                    : System.Windows.Threading.Dispatcher.CurrentDispatcher;
                Session = new LineageSession(dispatcher);
                Session.StartListening();
            }

            return Session;
        }

        public static void DisposeSession()
        {
            if (Session != null)
            {
                Session.Dispose();
                Session = null;
            }
        }
    }

    internal static class PackageGuids
    {
        public const string LineagePackageString = "7c4e9a12-8b3f-4d6e-9c1a-2e5f8d4b0a73";
        public const string LineageWindowString = "5e0b8c77-2a19-4d3e-a6f1-4c9e82b1d5f0";
        public const string LineageCmdSetString = "9a12c44e-1d80-4b6f-b3c2-7e91aa55d018";
        public static readonly Guid LineageCmdSet = new Guid(LineageCmdSetString);
    }

    internal static class PackageIds
    {
        public const int LineageWindow = 0x0100;
        public const int AddLineagePackage = 0x0101;
    }
}
