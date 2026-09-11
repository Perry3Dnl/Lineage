using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using Lineage;

namespace LineageIde
{
    public sealed class LineageFlowGraph
    {
        public const double NodeWidth = 220;
        public const double NodeHeight = 88;
        public const double BindingLineHeight = 16;
        public const double HorizontalGap = 22;
        public const double VerticalGap = 36;
        public const double Padding = 18;

        public static readonly LineageFlowGraph Empty = new LineageFlowGraph(
            new LineageFlowItem[0],
            new LineageFlowLink[0],
            0,
            0);

        public IReadOnlyList<LineageFlowItem> Items { get; }
        public IReadOnlyList<LineageFlowLink> Links { get; }
        public double Width { get; }
        public double Height { get; }

        public LineageFlowGraph(
            IReadOnlyList<LineageFlowItem> items,
            IReadOnlyList<LineageFlowLink> links,
            double width,
            double height)
        {
            Items = items ?? new LineageFlowItem[0];
            Links = links ?? new LineageFlowLink[0];
            Width = width;
            Height = height;
        }

        public void SetSelected(LineageNode node)
        {
            var id = node != null ? node.ValueId : 0;
            for (var i = 0; i < Items.Count; i++)
            {
                Items[i].IsSelected = Items[i].Node.ValueId == id;
            }
        }

        public LineageFlowItem Find(int valueId)
        {
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].Node.ValueId == valueId)
                {
                    return Items[i];
                }
            }

            return null;
        }

        public static LineageFlowGraph Build(IReadOnlyList<LineageNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return Empty;
            }

            var byId = new Dictionary<int, LineageNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node == null || node.ValueId <= 0 || byId.ContainsKey(node.ValueId))
                {
                    continue;
                }

                byId[node.ValueId] = node;
            }

            if (byId.Count == 0)
            {
                return Empty;
            }

            var children = new Dictionary<int, List<int>>();
            foreach (var pair in byId)
            {
                children[pair.Key] = new List<int>();
            }

            foreach (var pair in byId)
            {
                var parents = pair.Value.Parents;
                for (var p = 0; p < parents.Count; p++)
                {
                    List<int> list;
                    if (children.TryGetValue(parents[p], out list))
                    {
                        list.Add(pair.Key);
                    }
                }
            }

            var visibleIds = new HashSet<int>();
            foreach (var pair in byId)
            {
                if (IsValueChange(pair.Value, byId, children))
                {
                    visibleIds.Add(pair.Key);
                }
            }

            if (visibleIds.Count == 0)
            {
                foreach (var pair in byId)
                {
                    visibleIds.Add(pair.Key);
                }
            }

            var itemsById = new Dictionary<int, LineageFlowItem>();
            var items = new List<LineageFlowItem>(visibleIds.Count);
            var order = 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node == null || !visibleIds.Contains(node.ValueId) || itemsById.ContainsKey(node.ValueId))
                {
                    continue;
                }

                var incoming = VisibleParents(node, byId, visibleIds);
                var via = HiddenArithmetic(node, byId, visibleIds);
                var item = new LineageFlowItem(node, order++, incoming, via);
                itemsById[node.ValueId] = item;
                items.Add(item);
            }

            if (items.Count == 0)
            {
                return Empty;
            }

            var ranks = new Dictionary<int, int>();
            var visiting = new HashSet<int>();
            var maxRank = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var rank = RankOfVisible(items[i].Node.ValueId, byId, visibleIds, ranks, visiting);
                if (rank > maxRank)
                {
                    maxRank = rank;
                }
            }

            var layers = new List<LineageFlowItem>[maxRank + 1];
            for (var r = 0; r <= maxRank; r++)
            {
                layers[r] = new List<LineageFlowItem>();
            }

            for (var i = 0; i < items.Count; i++)
            {
                layers[ranks[items[i].Node.ValueId]].Add(items[i]);
            }

            SortLayers(layers);

            var layerHeights = new double[layers.Length];
            var widest = 1;
            for (var r = 0; r < layers.Length; r++)
            {
                var layer = layers[r];
                if (layer.Count > widest)
                {
                    widest = layer.Count;
                }

                var layerHeight = NodeHeight;
                for (var i = 0; i < layer.Count; i++)
                {
                    if (layer[i].Height > layerHeight)
                    {
                        layerHeight = layer[i].Height;
                    }
                }

                layerHeights[r] = layerHeight;
            }

            var width = Padding * 2 + widest * NodeWidth + Math.Max(0, widest - 1) * HorizontalGap;
            var height = Padding * 2;
            for (var r = 0; r < layers.Length; r++)
            {
                height += layerHeights[r];
                if (r > 0)
                {
                    height += VerticalGap;
                }
            }

            var y = Padding;
            for (var r = 0; r < layers.Length; r++)
            {
                var layer = layers[r];
                var rowWidth = layer.Count * NodeWidth + Math.Max(0, layer.Count - 1) * HorizontalGap;
                var startX = (width - rowWidth) / 2;
                for (var i = 0; i < layer.Count; i++)
                {
                    layer[i].X = startX + i * (NodeWidth + HorizontalGap);
                    layer[i].Y = y;
                }

                y += layerHeights[r] + VerticalGap;
            }

            var links = new List<LineageFlowLink>();
            for (var i = 0; i < items.Count; i++)
            {
                var child = items[i];
                var parents = VisibleParents(child.Node, byId, visibleIds);
                for (var p = 0; p < parents.Count; p++)
                {
                    LineageFlowItem parent;
                    if (itemsById.TryGetValue(parents[p].ValueId, out parent))
                    {
                        links.Add(new LineageFlowLink(parent, child));
                    }
                }
            }

            return new LineageFlowGraph(items, links, width, height);
        }

        private static bool IsValueChange(
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            Dictionary<int, List<int>> children)
        {
            if (node.Kind == EventKind.Focus || node.Category == ReportCategory.Focus)
            {
                return true;
            }

            if (node.Category == ReportCategory.Search || node.Operation == OperationKind.Search)
            {
                return true;
            }

            if (node.Category == ReportCategory.FrameworkBoundary)
            {
                return false;
            }

            if (IsEmptyValue(node) && node.Category != ReportCategory.Search)
            {
                return false;
            }

            if (IsUnnamedArithmetic(node) && HasChildWithSameValue(node, byId, children))
            {
                return false;
            }

            if (node.Parents == null || node.Parents.Count == 0)
            {
                return !IsEmptyValue(node);
            }

            if (node.Parents.Count == 1)
            {
                LineageNode parent;
                if (byId.TryGetValue(node.Parents[0], out parent) && SameValue(parent, node) && !IsUnnamedArithmetic(parent))
                {
                    var parentName = parent.DisplayName ?? string.Empty;
                    var nodeName = node.DisplayName ?? string.Empty;
                    if ((parentName == "rawQuantity" || parentName.IndexOf("ReadLine", StringComparison.Ordinal) >= 0)
                        && parentName != nodeName)
                    {
                        return true;
                    }

                    if (IsNumericValue(node) && (parent.Kind == EventKind.FieldRead || parent.Kind == EventKind.FieldWrite
                        || parent.Category == ReportCategory.Search || parent.Category == ReportCategory.PropertyRead
                        || parent.Category == ReportCategory.PropertyWrite))
                    {
                        return true;
                    }

                    return false;
                }
            }

            return true;
        }

        private static bool HasChildWithSameValue(
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            Dictionary<int, List<int>> children)
        {
            List<int> ids;
            if (!children.TryGetValue(node.ValueId, out ids))
            {
                return false;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                LineageNode child;
                if (byId.TryGetValue(ids[i], out child) && SameValue(node, child) && !IsUnnamedArithmetic(child))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<LineageNode> VisibleParents(
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            HashSet<int> visibleIds)
        {
            var result = new List<LineageNode>();
            var seen = new HashSet<int>();
            CollectVisibleParents(node, byId, visibleIds, seen, result);
            return result;
        }

        private static void CollectVisibleParents(
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            HashSet<int> visibleIds,
            HashSet<int> seen,
            List<LineageNode> into)
        {
            if (node == null || node.Parents == null)
            {
                return;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                var id = node.Parents[i];
                if (!seen.Add(id))
                {
                    continue;
                }

                LineageNode parent;
                if (!byId.TryGetValue(id, out parent))
                {
                    continue;
                }

                if (visibleIds.Contains(id))
                {
                    if (!ContainsNode(into, parent))
                    {
                        into.Add(parent);
                    }

                    continue;
                }

                CollectVisibleParents(parent, byId, visibleIds, seen, into);
            }
        }

        private static bool ContainsNode(List<LineageNode> list, LineageNode node)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].ValueId == node.ValueId)
                {
                    return true;
                }
            }

            return false;
        }

        private static LineageNode HiddenArithmetic(
            LineageNode node,
            Dictionary<int, LineageNode> byId,
            HashSet<int> visibleIds)
        {
            if (node.Parents == null || node.Parents.Count != 1)
            {
                return null;
            }

            LineageNode parent;
            if (!byId.TryGetValue(node.Parents[0], out parent))
            {
                return null;
            }

            if (!visibleIds.Contains(parent.ValueId) && IsUnnamedArithmetic(parent))
            {
                return parent;
            }

            return null;
        }

        private static int RankOfVisible(
            int id,
            Dictionary<int, LineageNode> byId,
            HashSet<int> visibleIds,
            Dictionary<int, int> ranks,
            HashSet<int> visiting)
        {
            int rank;
            if (ranks.TryGetValue(id, out rank))
            {
                return rank;
            }

            LineageNode node;
            if (!byId.TryGetValue(id, out node) || !visiting.Add(id))
            {
                ranks[id] = 0;
                return 0;
            }

            rank = 0;
            var parents = VisibleParents(node, byId, visibleIds);
            for (var i = 0; i < parents.Count; i++)
            {
                var parentRank = RankOfVisible(parents[i].ValueId, byId, visibleIds, ranks, visiting);
                if (parentRank + 1 > rank)
                {
                    rank = parentRank + 1;
                }
            }

            visiting.Remove(id);
            ranks[id] = rank;
            return rank;
        }

        private static bool IsUnnamedArithmetic(LineageNode node)
        {
            var name = node.DisplayName ?? string.Empty;
            switch (name)
            {
                case "op":
                case "add":
                case "sub":
                case "mul":
                case "div":
                case "rem":
                case "+":
                case "-":
                case "×":
                case "÷":
                    return true;
                default:
                    return name.IndexOf(" × ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" ÷ ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" + ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" − ", StringComparison.Ordinal) >= 0;
            }
        }

        private static bool IsEmptyValue(LineageNode node)
        {
            var text = NormalizedValue(node);
            return text.Length == 0 || text == "[not captured]" || text == "[lineage unavailable]" || text == "[unavailable]";
        }

        private static bool SameValue(LineageNode a, LineageNode b)
        {
            var left = NormalizedValue(a);
            var right = NormalizedValue(b);
            return left.Length > 0 && left == right
                && left != "[not captured]"
                && left != "[lineage unavailable]";
        }

        private static string NormalizedValue(LineageNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var text = node.FormatStepValue() ?? string.Empty;
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                return text.Substring(1, text.Length - 2);
            }

            return text;
        }

        private static bool IsNumericValue(LineageNode node)
        {
            var text = NormalizedValue(node);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var digits = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch >= '0' && ch <= '9')
                {
                    digits++;
                    continue;
                }

                if (ch != '-' && ch != '.' && ch != ',')
                {
                    return false;
                }
            }

            return digits > 0;
        }

        private static void SortLayers(List<LineageFlowItem>[] layers)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                for (var r = 1; r < layers.Length; r++)
                {
                    SortByBarycenter(layers[r], true, layers[r - 1]);
                }

                for (var r = layers.Length - 2; r >= 0; r--)
                {
                    SortByBarycenter(layers[r], false, layers[r + 1]);
                }
            }
        }

        private static void SortByBarycenter(
            List<LineageFlowItem> layer,
            bool useParents,
            List<LineageFlowItem> related)
        {
            var index = new Dictionary<int, int>();
            for (var i = 0; i < related.Count; i++)
            {
                index[related[i].Node.ValueId] = i;
            }

            layer.Sort((a, b) =>
            {
                var cmp = Barycenter(a, useParents, index).CompareTo(Barycenter(b, useParents, index));
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
        }

        private static double Barycenter(LineageFlowItem item, bool useParents, Dictionary<int, int> index)
        {
            var ids = useParents ? item.Node.Parents : item.Node.Children;
            var sum = 0.0;
            var count = 0;
            for (var i = 0; i < ids.Count; i++)
            {
                int position;
                if (index.TryGetValue(ids[i], out position))
                {
                    sum += position;
                    count++;
                }
            }

            return count == 0 ? item.Order : sum / count;
        }
    }

    public sealed class LineageFlowLink
    {
        public LineageFlowLink(LineageFlowItem from, LineageFlowItem to)
        {
            From = from;
            To = to;
        }

        public LineageFlowItem From { get; }
        public LineageFlowItem To { get; }
    }

    public sealed class LineageFlowItem : INotifyPropertyChanged
    {
        private static readonly Brush OriginBrush = Freeze(0x6A, 0x99, 0x55);
        private static readonly Brush AssignmentBrush = Freeze(0x56, 0x9C, 0xD6);
        private static readonly Brush PropertyBrush = Freeze(0x9C, 0xDC, 0xFE);
        private static readonly Brush CallBrush = Freeze(0xC5, 0x86, 0xC0);
        private static readonly Brush TransformBrush = Freeze(0x4E, 0xC9, 0xB0);
        private static readonly Brush SearchBrush = Freeze(0xDC, 0xDC, 0xAA);
        private static readonly Brush AggregateBrush = Freeze(0xCE, 0x91, 0x78);
        private static readonly Brush MergeBrush = Freeze(0xD7, 0xBA, 0x7D);
        private static readonly Brush FocusBrush = Freeze(0xF1, 0xC2, 0x32);
        private static readonly Brush BoundaryBrush = Freeze(0x80, 0x80, 0x80);
        private static readonly Brush DefaultBrush = Freeze(0xD4, 0xD4, 0xD4);

        private bool _selected;

        public LineageFlowItem(LineageNode node, int order)
            : this(node, order, null, null)
        {
        }

        public LineageFlowItem(LineageNode node, int order, IList<LineageNode> incoming, LineageNode via)
        {
            Node = node;
            Order = order;
            Name = DisplayName(node);
            var after = FormatAfter(node);
            var before = FormatBefore(incoming);
            var how = FormatHow(node, incoming, via);
            if (string.IsNullOrEmpty(before) || before == after)
            {
                ValueText = after;
            }
            else
            {
                ValueText = before + " → " + after;
            }

            HowText = how;
            Bindings = BuildBindings(node, incoming, after);
            CategoryText = CategoryLabel(node.Category);
            NameCaption = "what";
            ValueCaption = "change";
            HowCaption = "how";
            LocationText = FormatLocation(node);
            HintText = BuildHint(node, before, after, how, Bindings);
            Accent = AccentFor(node.Category);
            IsFocus = node.Kind == EventKind.Focus || node.Category == ReportCategory.Focus;
            Width = LineageFlowGraph.NodeWidth;
            Height = NodeHeightFor(Bindings.Count);
        }

        public LineageNode Node { get; }
        public int Order { get; }
        public string Name { get; }
        public string ValueText { get; }
        public string HowText { get; }
        public IReadOnlyList<LineageFlowBinding> Bindings { get; }
        public string CategoryText { get; }
        public string NameCaption { get; }
        public string ValueCaption { get; }
        public string HowCaption { get; }
        public string LocationText { get; }
        public string HintText { get; }
        public Brush Accent { get; }
        public bool IsFocus { get; }
        public bool CanNavigate => Node != null && (!string.IsNullOrEmpty(Node.File) || !string.IsNullOrEmpty(Node.Method));
        public double Width { get; }
        public double Height { get; }
        public double X { get; set; }
        public double Y { get; set; }

        public bool IsSelected
        {
            get { return _selected; }
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                var handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private static string DisplayName(LineageNode node)
        {
            var name = string.IsNullOrEmpty(node.DisplayName) ? node.Label : node.DisplayName;
            if (string.IsNullOrEmpty(name))
            {
                name = node.Kind.ToString();
            }

            return name;
        }

        private static IReadOnlyList<LineageFlowBinding> BuildBindings(
            LineageNode node,
            IList<LineageNode> incoming,
            string after)
        {
            var list = new List<LineageFlowBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (incoming != null)
            {
                for (var i = 0; i < incoming.Count; i++)
                {
                    AddBinding(list, seen, DisplayName(incoming[i]), incoming[i].FormatStepValue());
                }
            }

            AddBinding(list, seen, DisplayName(node), after);
            return list;
        }

        private static void AddBinding(List<LineageFlowBinding> list, HashSet<string> seen, string name, string value)
        {
            if (string.IsNullOrEmpty(name) || IsExpressionName(name) || IsNoiseName(name))
            {
                return;
            }

            if (string.IsNullOrEmpty(value) || value == "[not captured]" || value == "[unavailable]" || value == "[lineage unavailable]")
            {
                return;
            }

            var key = name + "\0" + value;
            if (!seen.Add(key))
            {
                return;
            }

            list.Add(new LineageFlowBinding(name, value));
        }

        private static bool IsNoiseName(string name)
        {
            switch (name)
            {
                case "local":
                case "field":
                case "constant":
                case "op":
                case "mul":
                case "div":
                case "add":
                case "sub":
                case "Trace":
                case "Exception":
                    return true;
                default:
                    return false;
            }
        }

        private static double NodeHeightFor(int bindingCount)
        {
            var extra = Math.Max(0, bindingCount - 1) * LineageFlowGraph.BindingLineHeight;
            return LineageFlowGraph.NodeHeight + extra;
        }

        private static string FormatAfter(LineageNode node)
        {
            if (node.IsOpaque || node.Category == ReportCategory.FrameworkBoundary)
            {
                return "[lineage unavailable]";
            }

            return node.FormatStepValue();
        }

        private static string FormatBefore(IList<LineageNode> incoming)
        {
            if (incoming == null || incoming.Count == 0)
            {
                return string.Empty;
            }

            return incoming[0].FormatStepValue();
        }

        private static string FormatHow(LineageNode node, IList<LineageNode> incoming, LineageNode via)
        {
            if ((incoming == null || incoming.Count == 0) && (node.Category == ReportCategory.Origin || LooksLikeInput(node)))
            {
                return LooksLikeInput(node) ? "Console.ReadLine()" : "starting value";
            }

            if (incoming != null && incoming.Count == 1 && LooksLikeInput(incoming[0]))
            {
                return "parsed";
            }

            if (node.Category == ReportCategory.Search || node.Operation == OperationKind.Search)
            {
                return "lookup";
            }

            if (node.Kind == EventKind.Focus)
            {
                return "traced result";
            }

            var opNode = via ?? node;
            if (via != null && IsExpressionName(via.DisplayName))
            {
                return via.DisplayName;
            }

            if (IsExpressionName(node.DisplayName) && (incoming == null || incoming.Count < 2))
            {
                return node.DisplayName;
            }

            var symbol = OperatorOf(opNode);
            if (incoming != null && incoming.Count >= 2)
            {
                return symbol + " " + incoming[1].FormatStepValue();
            }

            if (via != null)
            {
                return OperatorOf(via) + (incoming != null && incoming.Count == 1
                    ? " " + incoming[0].FormatStepValue()
                    : string.Empty);
            }

            if (!string.IsNullOrEmpty(symbol) && symbol != "×" && incoming != null && incoming.Count == 1)
            {
                return symbol;
            }

            var name = DisplayName(node);
            if (name.IndexOf("Find ", StringComparison.Ordinal) == 0)
            {
                return "lookup";
            }

            return Humanize(name);
        }

        private static bool IsExpressionName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.IndexOf(" × ", StringComparison.Ordinal) >= 0
                || name.IndexOf(" ÷ ", StringComparison.Ordinal) >= 0
                || name.IndexOf(" + ", StringComparison.Ordinal) >= 0
                || name.IndexOf(" − ", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeInput(LineageNode node)
        {
            var name = DisplayName(node);
            return name.IndexOf("ReadLine", StringComparison.Ordinal) >= 0
                || name == "rawQuantity";
        }

        private static string OperatorOf(LineageNode node)
        {
            var name = node.DisplayName ?? string.Empty;
            if (name.IndexOf('÷') >= 0 || name == "div")
            {
                return "÷";
            }

            if (name.IndexOf('−') >= 0 || name == "sub" || name == "-")
            {
                return "−";
            }

            if (name.IndexOf('×') >= 0 || name == "mul" || name == "op")
            {
                return "×";
            }

            if (name.IndexOf(" + ", StringComparison.Ordinal) >= 0 || name == "add" || name == "+")
            {
                return "+";
            }

            if (name.IndexOf(" - ", StringComparison.Ordinal) >= 0)
            {
                return "−";
            }

            return "×";
        }

        private static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            if (name == "rawQuantity" || name.IndexOf("ReadLine", StringComparison.Ordinal) >= 0)
            {
                return "Console.ReadLine()";
            }

            return name;
        }

        private static string BuildHint(
            LineageNode node,
            string before,
            string after,
            string how,
            IReadOnlyList<LineageFlowBinding> bindings)
        {
            var change = string.IsNullOrEmpty(before) || before == after
                ? after
                : before + " became " + after;
            var reason = string.IsNullOrEmpty(how) ? CategoryHint(node.Category) : how;
            var names = string.Empty;
            if (bindings != null && bindings.Count > 0)
            {
                var parts = new string[bindings.Count];
                for (var i = 0; i < bindings.Count; i++)
                {
                    parts[i] = bindings[i].Text;
                }

                names = " Variables: " + string.Join(", ", parts) + ".";
            }

            var click = !string.IsNullOrEmpty(node.File)
                ? "  Click to open " + FormatLocation(node) + "."
                : "  Click to open the source for this step.";
            return change + " because " + reason + "." + names + click;
        }

        private static string FormatLocation(LineageNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.File))
            {
                return "no source";
            }

            var name = Path.GetFileName(node.File);
            if (string.IsNullOrEmpty(name))
            {
                name = node.File;
            }

            return node.Line > 0 ? name + ":" + node.Line : name;
        }

        private static string CategoryLabel(ReportCategory category)
        {
            switch (category)
            {
                case ReportCategory.Origin:
                    return "Input";
                case ReportCategory.Assignment:
                    return "Set";
                case ReportCategory.PropertyRead:
                    return "Read";
                case ReportCategory.PropertyWrite:
                    return "Wrote";
                case ReportCategory.MethodCall:
                    return "Called";
                case ReportCategory.Transformation:
                    return "Changed";
                case ReportCategory.Search:
                    return "Found";
                case ReportCategory.Aggregate:
                    return "Reduced";
                case ReportCategory.Merge:
                    return "Combined";
                case ReportCategory.Focus:
                    return "Result";
                case ReportCategory.FrameworkBoundary:
                    return "Hidden";
                case ReportCategory.Comparison:
                    return "Compared";
                case ReportCategory.Branch:
                    return "Branched";
                default:
                    return "Step";
            }
        }

        private static string CategoryHint(ReportCategory category)
        {
            switch (category)
            {
                case ReportCategory.Origin:
                    return "A starting value entered the lineage.";
                case ReportCategory.Assignment:
                    return "A local variable was stored.";
                case ReportCategory.PropertyRead:
                    return "A property was read.";
                case ReportCategory.PropertyWrite:
                    return "A property was written.";
                case ReportCategory.MethodCall:
                    return "A method was called.";
                case ReportCategory.Transformation:
                    return "The value was transformed.";
                case ReportCategory.Search:
                    return "A lookup found this value.";
                case ReportCategory.Aggregate:
                    return "Several values were reduced together.";
                case ReportCategory.Merge:
                    return "Several values were combined.";
                case ReportCategory.Focus:
                    return "This is the traced result.";
                case ReportCategory.FrameworkBoundary:
                    return "Lineage could not follow this step.";
                default:
                    return "A recorded step in the value's history.";
            }
        }

        private static Brush AccentFor(ReportCategory category)
        {
            switch (category)
            {
                case ReportCategory.Origin:
                    return OriginBrush;
                case ReportCategory.Assignment:
                    return AssignmentBrush;
                case ReportCategory.PropertyRead:
                case ReportCategory.PropertyWrite:
                    return PropertyBrush;
                case ReportCategory.MethodCall:
                    return CallBrush;
                case ReportCategory.Transformation:
                    return TransformBrush;
                case ReportCategory.Search:
                    return SearchBrush;
                case ReportCategory.Aggregate:
                    return AggregateBrush;
                case ReportCategory.Merge:
                    return MergeBrush;
                case ReportCategory.Focus:
                    return FocusBrush;
                case ReportCategory.FrameworkBoundary:
                    return BoundaryBrush;
                default:
                    return DefaultBrush;
            }
        }

        private static Brush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class LineageFlowBinding
    {
        public LineageFlowBinding(string name, string value)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string Name { get; }
        public string Value { get; }
        public string Text
        {
            get { return Name + " = " + Value; }
        }
    }
}
