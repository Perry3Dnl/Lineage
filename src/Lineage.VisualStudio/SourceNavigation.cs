using System;
using System.IO;
using Lineage;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LineageVisualStudio
{
    internal static class SourceNavigation
    {
        public static async System.Threading.Tasks.Task OpenAsync(LineageNode node)
        {
            if (node == null)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var file = ResolveFile(node);
            if (string.IsNullOrEmpty(file))
            {
                return;
            }

            VsShellUtilities.OpenDocument(
                ServiceProvider.GlobalProvider,
                file,
                Guid.Empty,
                out _,
                out _,
                out var frame);

            if (frame == null)
            {
                return;
            }

            frame.Show();
            if (node.Line <= 0)
            {
                return;
            }

            var view = VsShellUtilities.GetTextView(frame);
            if (view == null)
            {
                return;
            }

            var line = Math.Max(node.Line - 1, 0);
            view.SetCaretPos(line, 0);
            view.CenterLines(line, 1);
        }

        private static string ResolveFile(LineageNode node)
        {
            if (!string.IsNullOrEmpty(node.File) && File.Exists(node.File))
            {
                return node.File;
            }

            var name = !string.IsNullOrEmpty(node.File)
                ? Path.GetFileName(node.File)
                : TypeFileName(node.Method);
            if (string.IsNullOrEmpty(name))
            {
                return node.File;
            }

            var found = FindInSolution(name);
            return !string.IsNullOrEmpty(found) ? found : node.File;
        }

        private static string TypeFileName(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return null;
            }

            var end = method.IndexOf("::", StringComparison.Ordinal);
            if (end <= 0)
            {
                return null;
            }

            var start = method.LastIndexOf('.', end - 1);
            var type = start >= 0 && start < end - 1
                ? method.Substring(start + 1, end - start - 1)
                : method.Substring(0, end);
            return string.IsNullOrEmpty(type) ? null : type + ".cs";
        }

        private static string FindInSolution(string fileName)
        {
            try
            {
                var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                if (solution == null)
                {
                    return null;
                }

                string dir;
                solution.GetSolutionInfo(out dir, out _, out _);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return null;
                }

                return FindFile(dir, fileName);
            }
            catch
            {
                return null;
            }
        }

        private static string FindFile(string directory, string fileName)
        {
            foreach (var path in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
            {
                if (path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                return path;
            }

            return null;
        }
    }
}
