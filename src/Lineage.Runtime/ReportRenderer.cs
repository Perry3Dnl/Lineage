using System.Collections.Generic;
using System.Text;

namespace Lineage
{
    internal static class ReportRenderer
    {
        private const string IncompleteHistory = "[provenance history incomplete — older events were dropped or truncated]";

        public static string Render(LineageReport report, bool raw)
        {
            if (report == null)
            {
                return "(empty lineage)";
            }

            var nodes = raw ? report.RawNodes : report.Nodes;
            if (nodes == null || nodes.Count == 0)
            {
                return report.EventsDropped ? IncompleteHistory + "\n\n(empty lineage)" : "(empty lineage)";
            }

            if (raw)
            {
                var linear = RenderLinear(nodes, true);
                return report.EventsDropped ? IncompleteHistory + "\n\n" + linear : linear;
            }

            var byId = Index(nodes);
            var roots = new List<LineageNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Parents.Count == 0)
                {
                    roots.Add(nodes[i]);
                }
            }

            if (roots.Count == 0)
            {
                roots.Add(nodes[0]);
            }

            var sb = new StringBuilder();
            if (report.EventsDropped)
            {
                sb.AppendLine(IncompleteHistory);
                sb.AppendLine();
            }

            if (report.Trigger != null && report.Trigger.Kind != LineageTriggerKind.None)
            {
                sb.Append("Trigger: ");
                sb.Append(report.Trigger.Title);
                if (!string.IsNullOrEmpty(report.Trigger.Detail))
                {
                    sb.Append(" (");
                    sb.Append(report.Trigger.Detail);
                    sb.Append(')');
                }

                sb.AppendLine();
                if (report.Focus != null)
                {
                    sb.Append("Focus: ");
                    sb.AppendLine(report.Focus.Label);
                }

                sb.AppendLine();
            }

            var visited = new HashSet<int>();
            for (var i = 0; i < roots.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                }

                WriteTree(sb, roots[i], byId, visited, "", true, true);
            }

            return sb.ToString().TrimEnd();
        }

        private static string RenderLinear(IReadOnlyList<LineageNode> nodes, bool diagnostic)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("    ↓");
                }

                var node = nodes[i];
                if (diagnostic)
                {
                    sb.Append("Node ");
                    sb.Append(node.ValueId);
                    sb.Append("  ");
                    sb.Append(node.Category);
                    sb.Append("  ");
                }

                sb.Append(node.TitleWithValue());
                if (node.IsOpaque || node.Category == ReportCategory.FrameworkBoundary)
                {
                    sb.AppendLine();
                    sb.Append("    [lineage unavailable]");
                }
            }

            return sb.ToString();
        }

        private static Dictionary<int, LineageNode> Index(IReadOnlyList<LineageNode> nodes)
        {
            var map = new Dictionary<int, LineageNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].ValueId > 0)
                {
                    map[nodes[i].ValueId] = nodes[i];
                }
            }

            return map;
        }

        private static void WriteTree(
            StringBuilder sb,
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            HashSet<int> visited,
            string prefix,
            bool isRoot,
            bool isLast)
        {
            if (node == null || !visited.Add(node.ValueId))
            {
                return;
            }

            if (!isRoot)
            {
                sb.Append(prefix);
                sb.Append(isLast ? "└── " : "├── ");
            }

            sb.Append(node.TitleWithValue());
            if (node.IsOpaque || node.Category == ReportCategory.FrameworkBoundary)
            {
                sb.AppendLine();
                sb.Append(prefix);
                sb.Append(isRoot ? "" : (isLast ? "    " : "│   "));
                sb.Append("[lineage unavailable]");
            }

            var kids = new List<LineageNode>();
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                LineageNode child;
                if (byId.TryGetValue(children[i], out child) && !visited.Contains(child.ValueId))
                {
                    kids.Add(child);
                }
            }

            if (node.Parents.Count >= 2 && kids.Count == 0)
            {
                sb.AppendLine();
                return;
            }

            sb.AppendLine();
            if (isRoot && kids.Count == 1 && kids[0].Parents.Count < 2)
            {
                sb.AppendLine("    ↓");
                WriteTree(sb, kids[0], byId, visited, "", true, true);
                return;
            }

            var childPrefix = isRoot ? "" : prefix + (isLast ? "    " : "│   ");
            for (var i = 0; i < kids.Count; i++)
            {
                var last = i == kids.Count - 1;
                if (isRoot && kids.Count == 1)
                {
                    sb.AppendLine("    ↓");
                    WriteTree(sb, kids[i], byId, visited, childPrefix, true, true);
                }
                else
                {
                    WriteTree(sb, kids[i], byId, visited, childPrefix, false, last);
                }
            }
        }
    }
}
