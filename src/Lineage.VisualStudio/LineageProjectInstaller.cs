using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LineageVisualStudio
{
    internal static class LineageProjectInstaller
    {
        public static void Install(IServiceProvider provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = provider.GetService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                throw new InvalidOperationException("Visual Studio is not ready.");
            }

            var projectPath = SelectedProjectPath(dte);
            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
            {
                throw new InvalidOperationException("Select a project, then try again.");
            }

            var nupkg = FindNupkg();
            if (string.IsNullOrEmpty(nupkg))
            {
                throw new InvalidOperationException("Could not find Lineage.nupkg next to the extension. Rebuild the Lineage bundle and reinstall the VSIX.");
            }

            var version = VersionFromNupkg(nupkg);
            var solutionDir = !string.IsNullOrEmpty(dte.Solution.FullName)
                ? Path.GetDirectoryName(dte.Solution.FullName)
                : Path.GetDirectoryName(projectPath);
            var feed = Path.Combine(solutionDir, "lineage-packages");
            Directory.CreateDirectory(feed);
            File.Copy(nupkg, Path.Combine(feed, Path.GetFileName(nupkg)), true);
            WriteNugetConfig(solutionDir, "lineage-packages");
            AddPackageReference(projectPath, version);

            VsShellUtilities.ShowMessageBox(
                provider,
                "Added Lineage " + version + " to " + Path.GetFileName(projectPath) + "."
                    + Environment.NewLine + Environment.NewLine
                    + "Restore the project, then rebuild and debug.",
                "Lineage",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private static string SelectedProjectPath(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte.SelectedItems != null && dte.SelectedItems.Count > 0)
            {
                var selected = dte.SelectedItems.Item(1);
                if (selected != null && selected.Project != null && !string.IsNullOrEmpty(selected.Project.FullName))
                {
                    return selected.Project.FullName;
                }
            }

            if (dte.Solution != null && dte.Solution.Projects != null && dte.Solution.Projects.Count > 0)
            {
                for (var i = 1; i <= dte.Solution.Projects.Count; i++)
                {
                    var project = dte.Solution.Projects.Item(i);
                    if (project != null && !string.IsNullOrEmpty(project.FullName) && File.Exists(project.FullName))
                    {
                        return project.FullName;
                    }
                }
            }

            return null;
        }

        private static string FindNupkg()
        {
            var dir = Path.GetDirectoryName(typeof(LineagePackage).Assembly.Location);
            while (!string.IsNullOrEmpty(dir))
            {
                var packages = Path.Combine(dir, "Packages");
                if (Directory.Exists(packages))
                {
                    var matches = Directory.GetFiles(packages, "Lineage*.nupkg");
                    if (matches.Length > 0)
                    {
                        return matches[0];
                    }
                }

                var direct = Directory.GetFiles(dir, "Lineage*.nupkg");
                if (direct.Length > 0)
                {
                    return direct[0];
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static string VersionFromNupkg(string nupkg)
        {
            var name = Path.GetFileNameWithoutExtension(nupkg);
            var match = Regex.Match(name, @"\d+\.\d+\.\d+");
            return match.Success ? match.Value : "0.1.0";
        }

        private static void WriteNugetConfig(string directory, string feedRelative)
        {
            var path = Path.Combine(directory, "nuget.config");
            var xml = new XmlDocument();
            if (File.Exists(path))
            {
                xml.Load(path);
            }
            else
            {
                xml.LoadXml("<configuration><packageSources></packageSources></configuration>");
            }

            var sources = xml.SelectSingleNode("/configuration/packageSources") as XmlElement;
            if (sources == null)
            {
                var configuration = xml.DocumentElement ?? xml.AppendChild(xml.CreateElement("configuration")) as XmlElement;
                sources = xml.CreateElement("packageSources");
                configuration.AppendChild(sources);
            }

            var existing = sources.SelectNodes("add[@key='Lineage']");
            if (existing != null && existing.Count > 0)
            {
                existing[0].Attributes["value"].Value = feedRelative;
            }
            else
            {
                var add = xml.CreateElement("add");
                add.SetAttribute("key", "Lineage");
                add.SetAttribute("value", feedRelative);
                sources.AppendChild(add);
            }

            xml.Save(path);
        }

        private static void AddPackageReference(string projectPath, string version)
        {
            var text = File.ReadAllText(projectPath);
            if (Regex.IsMatch(text, @"<PackageReference\s+Include=""Lineage""", RegexOptions.IgnoreCase))
            {
                text = Regex.Replace(
                    text,
                    @"<PackageReference\s+Include=""Lineage""[^/]*/>",
                    "<PackageReference Include=\"Lineage\" Version=\"" + version + "\" />",
                    RegexOptions.IgnoreCase);
                text = Regex.Replace(
                    text,
                    @"<PackageReference\s+Include=""Lineage""[^>]*>.*?</PackageReference>",
                    "<PackageReference Include=\"Lineage\" Version=\"" + version + "\" />",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                File.WriteAllText(projectPath, text, Encoding.UTF8);
                return;
            }

            var itemGroup = "<ItemGroup>" + Environment.NewLine
                + "    <PackageReference Include=\"Lineage\" Version=\"" + version + "\" />" + Environment.NewLine
                + "  </ItemGroup>" + Environment.NewLine;
            if (text.Contains("</Project>"))
            {
                text = text.Replace("</Project>", "  " + itemGroup + "</Project>");
            }
            else
            {
                text += Environment.NewLine + itemGroup;
            }

            File.WriteAllText(projectPath, text, Encoding.UTF8);
        }
    }
}
