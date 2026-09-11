using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lineage;

namespace LineageIde
{
    public sealed class LineageTreeItem
    {
        public LineageTreeItem(LineageNode node)
        {
            Node = node;
            Name = string.IsNullOrEmpty(node.DisplayName) ? node.Label : node.DisplayName;
            ValueText = node.FormatStepValue();
            Caption = node.TitleWithValue();
            Children = new ObservableCollection<LineageTreeItem>();
        }

        public LineageNode Node { get; }
        public string Name { get; }
        public string ValueText { get; }
        public string Caption { get; }
        public ObservableCollection<LineageTreeItem> Children { get; }

        public static IList<LineageTreeItem> Build(IReadOnlyList<LineageNode> nodes)
        {
            var result = new List<LineageTreeItem>();
            if (nodes == null || nodes.Count == 0)
            {
                return result;
            }

            var byId = new Dictionary<int, LineageNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].ValueId > 0)
                {
                    byId[nodes[i].ValueId] = nodes[i];
                }
            }

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

            var visited = new HashSet<int>();
            for (var i = 0; i < roots.Count; i++)
            {
                var item = From(roots[i], byId, visited);
                if (item != null)
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static LineageTreeItem From(LineageNode node, Dictionary<int, LineageNode> byId, HashSet<int> visited)
        {
            if (node == null || !visited.Add(node.ValueId))
            {
                return null;
            }

            var item = new LineageTreeItem(node);
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                LineageNode child;
                if (byId.TryGetValue(children[i], out child) && !visited.Contains(child.ValueId))
                {
                    var kid = From(child, byId, visited);
                    if (kid != null)
                    {
                        item.Children.Add(kid);
                    }
                }
            }

            return item;
        }
    }
}
