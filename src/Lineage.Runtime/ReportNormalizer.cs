using System;
using System.Collections.Generic;

namespace Lineage
{
    internal static class ReportNormalizer
    {
        public static IReadOnlyList<LineageNode> Apply(IReadOnlyList<LineageNode> raw)
        {
            if (raw == null || raw.Count == 0)
            {
                return new LineageNode[0];
            }

            var works = new List<Work>(raw.Count);
            var byId = new Dictionary<int, Work>();
            for (var i = 0; i < raw.Count; i++)
            {
                var work = Work.From(raw[i]);
                if (work.ValueId <= 0 || work.Kind == EventKind.MethodEntry)
                {
                    continue;
                }

                works.Add(work);
                byId[work.ValueId] = work;
            }

            DropUnknownParents(works, byId);
            AnnotateSearch(works, byId);
            CollapseStringConstruction(works, byId);
            FoldConstants(works, byId);
            InheritAssignmentSites(works, byId);
            SuppressNoise(works, byId);
            DedupProperties(works, byId);
            FoldPassThroughLocals(works, byId);
            FoldValueCopies(works, byId);
            PrefixProperties(works, byId);
            RelabelNumericCopies(works, byId);
            PromoteInputStores(works, byId);
            RelabelArithmetic(works, byId);
            RelabelCalls(works, byId);
            MarkMerges(works, byId);
            DropUnchanged(works, byId);
            Reconnect(works, byId);
            InheritFocusLocation(works, byId);

            var visible = new List<Work>();
            for (var i = 0; i < works.Count; i++)
            {
                if (!works[i].Suppressed)
                {
                    visible.Add(works[i]);
                }
            }

            var ordered = TopoSort(visible);
            var nodes = new LineageNode[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                nodes[i] = ordered[i].ToNode();
            }

            return nodes;
        }

        private static void DropUnknownParents(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var parents = works[i].Parents;
                for (var p = parents.Count - 1; p >= 0; p--)
                {
                    if (!byId.ContainsKey(parents[p]))
                    {
                        parents.RemoveAt(p);
                    }
                }
            }
        }

        private static void AnnotateSearch(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var find = works[i];
                if (find.Operation != OperationKind.Search)
                {
                    continue;
                }

                var result = ChildAssignmentName(find, works);
                var resultNode = ChildAssignment(find, works);
                var key = BestSearchKey(find, works, byId);
                var field = RelatedFieldName(works, find, byId);
                var keyText = key != null ? (string.IsNullOrEmpty(key.Preview) ? key.Title : Unquote(key.Preview)) : null;
                if (resultNode != null && (string.IsNullOrEmpty(resultNode.Preview) || LooksNumeric(resultNode)))
                {
                    var namePreview = FieldPreview(works, resultNode, field);
                    if (string.IsNullOrEmpty(namePreview))
                    {
                        namePreview = FieldPreview(works, resultNode, "Name");
                    }

                    if (!string.IsNullOrEmpty(namePreview))
                    {
                        resultNode.Preview = Unquote(namePreview);
                    }
                }

                if (string.IsNullOrEmpty(find.Preview) && resultNode != null && !string.IsNullOrEmpty(resultNode.Preview))
                {
                    find.Preview = resultNode.Preview;
                }

                var resultPreview = resultNode != null ? resultNode.Preview : find.Preview;
                if (!string.IsNullOrEmpty(resultPreview) && !string.IsNullOrEmpty(keyText)
                    && Unquote(resultPreview) == Unquote(keyText) && (field == "Name" || field == "field"))
                {
                    find.Title = string.IsNullOrEmpty(result) ? "Find" : "Find " + result;
                }
                else if (!string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(field) && !string.IsNullOrEmpty(keyText))
                {
                    find.Title = "Find " + result + " where " + field + " == " + keyText;
                }
                else if (!string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(keyText))
                {
                    find.Title = "Find " + result + " using " + keyText;
                }
                else
                {
                    find.Title = ShortCall(find);
                    if (string.IsNullOrEmpty(find.Title))
                    {
                        find.Title = "Find";
                    }
                }

                find.Category = ReportCategory.Search;

                for (var p = find.Parents.Count - 1; p >= 0; p--)
                {
                    Work parent;
                    if (!byId.TryGetValue(find.Parents[p], out parent))
                    {
                        continue;
                    }

                    if (parent.Category == ReportCategory.Assignment && parent.Parents.Count == 0)
                    {
                        parent.Suppressed = true;
                    }
                }
            }
        }

        private static void CollapseStringConstruction(List<Work> works, Dictionary<int, Work> byId)
        {
            var concat = new HashSet<int>();
            for (var i = 0; i < works.Count; i++)
            {
                if (IsStringGlue(works[i]))
                {
                    concat.Add(works[i].ValueId);
                }
            }

            if (concat.Count == 0)
            {
                return;
            }

            for (var i = 0; i < works.Count; i++)
            {
                var sink = works[i];
                if (sink.Suppressed || sink.Kind == EventKind.Focus || !Touches(sink, concat))
                {
                    continue;
                }

                if (!IsStringSink(sink, works, concat))
                {
                    continue;
                }

                var terminals = new List<int>();
                var seen = new HashSet<int>();
                CollectTerminals(sink, byId, concat, terminals, seen);
                if (terminals.Count < 2)
                {
                    continue;
                }

                for (var t = 0; t < terminals.Count; t++)
                {
                    seen.Remove(terminals[t]);
                }

                seen.Remove(sink.ValueId);
                foreach (var id in seen)
                {
                    Work glue;
                    if (byId.TryGetValue(id, out glue) && glue != sink)
                    {
                        glue.Suppressed = true;
                    }
                }

                sink.Parents.Clear();
                for (var t = 0; t < terminals.Count; t++)
                {
                    if (!sink.Parents.Contains(terminals[t]))
                    {
                        sink.Parents.Add(terminals[t]);
                    }
                }

                sink.Category = ReportCategory.Merge;
            }
        }

        private static void FoldConstants(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || (node.Kind != EventKind.Constant && node.Category != ReportCategory.Origin))
                {
                    continue;
                }

                if (node.Kind != EventKind.Constant && !IsBareLiteral(node))
                {
                    continue;
                }

                var children = ChildrenOf(node.ValueId, works);
                if (children.Count != 1)
                {
                    continue;
                }

                var child = children[0];
                if (child.Kind == EventKind.Focus || IsArithmetic(child))
                {
                    continue;
                }

                InheritLiteralSite(child, node);

                if (!SamePreview(node, child) && !string.IsNullOrEmpty(child.Title) && child.Title != "constant")
                {
                    node.Suppressed = true;
                    ReplaceParent(child, node.ValueId, node.Parents);
                    continue;
                }

                if (!string.IsNullOrEmpty(child.Title) && child.Title != "constant")
                {
                    if (string.IsNullOrEmpty(child.Preview))
                    {
                        child.Preview = node.Preview;
                    }

                    node.Suppressed = true;
                    ReplaceParent(child, node.ValueId, node.Parents);
                }
            }
        }

        private static void InheritAssignmentSites(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || node.Kind != EventKind.FieldWrite)
                {
                    continue;
                }

                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (!byId.TryGetValue(node.Parents[p], out parent) || parent.Suppressed)
                    {
                        continue;
                    }

                    if (parent.Kind == EventKind.Constant || IsBareLiteral(parent))
                    {
                        InheritLiteralSite(node, parent);
                        break;
                    }
                }
            }
        }

        private static void InheritLiteralSite(Work destination, Work literal)
        {
            if (destination == null || literal == null || literal.Line <= 0)
            {
                return;
            }

            if (destination.Kind != EventKind.FieldWrite)
            {
                return;
            }

            if (!string.IsNullOrEmpty(literal.File))
            {
                destination.File = literal.File;
            }

            destination.Line = literal.Line;
        }

        private static void SuppressNoise(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed)
                {
                    continue;
                }

                if (node.Category == ReportCategory.Search || node.Kind == EventKind.Focus)
                {
                    continue;
                }

                if (IsFrameworkNoise(node) && !IsUnknownBoundary(node, works, byId))
                {
                    node.Suppressed = true;
                    continue;
                }

                if (node.Operation == OperationKind.Comparison || node.Category == ReportCategory.Comparison || node.Title == "compare")
                {
                    node.Suppressed = true;
                    continue;
                }

                if (node.Kind == EventKind.Branch)
                {
                    node.Suppressed = true;
                    continue;
                }

                if (IsNullConstant(node))
                {
                    node.Suppressed = true;
                    continue;
                }

                if (IsCompilerLocal(node))
                {
                    node.Suppressed = true;
                    continue;
                }

                if (IsToString(node) || IsConcat(node) || IsTryParse(node))
                {
                    node.Suppressed = true;
                }
            }

            InsertUnknownGaps(works, byId);
        }

        private static void InsertUnknownGaps(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || !node.Opaque || IsFrameworkNoise(node) || IsTryParse(node) || IsParseCall(node))
                {
                    continue;
                }

                var developerParent = HasDeveloperNeighbor(node.Parents, byId);
                var children = ChildrenOf(node.ValueId, works);
                var developerChild = false;
                for (var c = 0; c < children.Count; c++)
                {
                    if (!children[c].Suppressed && !children[c].Opaque)
                    {
                        developerChild = true;
                        break;
                    }
                }

                if (developerParent && developerChild)
                {
                    node.Title = "[lineage unavailable]";
                    node.Preview = null;
                    node.Category = ReportCategory.FrameworkBoundary;
                    node.Opaque = false;
                }
            }
        }

        private static void DedupProperties(List<Work> works, Dictionary<int, Work> byId)
        {
            var keep = new Dictionary<string, Work>();
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed)
                {
                    continue;
                }

                if (node.Kind != EventKind.FieldRead && node.Kind != EventKind.FieldWrite)
                {
                    continue;
                }

                var key = PropertyKey(node) + "|" + (node.Preview ?? string.Empty);
                Work existing;
                if (keep.TryGetValue(key, out existing))
                {
                    node.Suppressed = true;
                    Retarget(works, node.ValueId, existing.ValueId);
                }
                else
                {
                    keep[key] = node;
                }
            }
        }

        private static void FoldPassThroughLocals(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || node.Kind != EventKind.LocalStore || node.Parents.Count != 1)
                {
                    continue;
                }

                Work parent;
                if (!byId.TryGetValue(node.Parents[0], out parent) || parent.Suppressed)
                {
                    continue;
                }

                var property = parent.Kind == EventKind.FieldRead || parent.Kind == EventKind.FieldWrite;
                if (!property || !SameMeaning(parent, node))
                {
                    continue;
                }

                if (IsNamedStore(node) && LooksNumeric(node))
                {
                    continue;
                }

                node.Suppressed = true;
                Retarget(works, node.ValueId, parent.ValueId);
            }
        }

        private static void FoldValueCopies(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || node.Kind == EventKind.Focus)
                {
                    continue;
                }

                if (node.Kind != EventKind.LocalStore && node.Kind != EventKind.FieldRead && node.Kind != EventKind.Argument)
                {
                    continue;
                }

                if (node.Category == ReportCategory.Merge || node.Operation == OperationKind.Search || node.Operation == OperationKind.Aggregate)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(node.Preview))
                {
                    continue;
                }

                var target = CopySource(node, works, byId);
                if (target == null || target.ValueId == node.ValueId)
                {
                    continue;
                }

                if (IsNamedStore(node) && !IsNamedStore(target))
                {
                    continue;
                }

                if (IsNamedStore(node) && IsNamedStore(target) && !string.Equals(node.Title, target.Title, StringComparison.Ordinal))
                {
                    continue;
                }

                node.Suppressed = true;
                Retarget(works, node.ValueId, target.ValueId);
            }
        }

        private static Work CopySource(Work node, List<Work> works, Dictionary<int, Work> byId)
        {
            if (node.Parents.Count == 1)
            {
                Work parent;
                if (byId.TryGetValue(node.Parents[0], out parent) && !parent.Suppressed && SameMeaning(parent, node))
                {
                    return parent;
                }
            }

            if (node.Kind != EventKind.FieldRead)
            {
                return null;
            }

            var field = PropertyName(node);
            if (string.IsNullOrEmpty(field))
            {
                return null;
            }

            Work write = null;
            for (var i = 0; i < works.Count; i++)
            {
                var candidate = works[i];
                if (candidate.Suppressed || candidate.Kind != EventKind.FieldWrite)
                {
                    continue;
                }

                if (PropertyName(candidate) != field || candidate.Preview != node.Preview)
                {
                    continue;
                }

                if (write == null || candidate.ValueId > write.ValueId)
                {
                    write = candidate;
                }
            }

            return write;
        }

        private static void PrefixProperties(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || (node.Kind != EventKind.FieldRead && node.Kind != EventKind.FieldWrite))
                {
                    continue;
                }

                var field = PropertyName(node);
                if (string.IsNullOrEmpty(field))
                {
                    continue;
                }

                Work owner = null;
                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (byId.TryGetValue(node.Parents[p], out parent) && !parent.Suppressed && parent.Kind == EventKind.LocalStore)
                    {
                        owner = parent;
                        break;
                    }
                }

                if (owner != null && !string.IsNullOrEmpty(owner.Title) && owner.Title.IndexOf('.') < 0)
                {
                    node.Title = owner.Title + "." + field;
                }
                else
                {
                    node.Title = field;
                }

                node.Category = node.Kind == EventKind.FieldWrite ? ReportCategory.PropertyWrite : ReportCategory.PropertyRead;
            }
        }

        private static void RelabelNumericCopies(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || !LooksNumeric(node))
                {
                    continue;
                }

                if (!IsCompilerLocal(node) && node.Title != "field")
                {
                    continue;
                }

                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (!byId.TryGetValue(node.Parents[p], out parent) || parent.Suppressed)
                    {
                        continue;
                    }

                    if (parent.Kind != EventKind.FieldRead && parent.Kind != EventKind.FieldWrite
                        && parent.Category != ReportCategory.PropertyRead && parent.Category != ReportCategory.PropertyWrite)
                    {
                        continue;
                    }

                    var prop = PropertyName(parent);
                    if (string.IsNullOrEmpty(prop) || prop == "field" || prop == "local")
                    {
                        continue;
                    }

                    node.Title = ToLocalName(prop);
                    break;
                }
            }
        }

        private static string ToLocalName(string name)
        {
            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static void RelabelCalls(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || node.Kind != EventKind.Call)
                {
                    continue;
                }

                if (node.Operation == OperationKind.Search || node.Operation == OperationKind.Aggregate)
                {
                    continue;
                }

                if (node.Parents.Count == 1)
                {
                    Work parent;
                    if (byId.TryGetValue(node.Parents[0], out parent) && !parent.Suppressed)
                    {
                        var method = ShortCall(node);
                        if (ShouldQualifyCall(method) && method.IndexOf('(') < 0)
                        {
                            node.Title = method + "(" + parent.Title + ")";
                        }
                    }
                }
            }
        }

        private static void MarkMerges(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed)
                {
                    continue;
                }

                var count = 0;
                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (byId.TryGetValue(node.Parents[p], out parent) && !parent.Suppressed)
                    {
                        count++;
                    }
                }

                if (count >= 2 && node.Kind != EventKind.Focus && !IsArithmetic(node) && node.Category != ReportCategory.Transformation)
                {
                    node.Category = ReportCategory.Merge;
                }
            }
        }

        private static void InheritFocusLocation(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Kind != EventKind.Focus || (!string.IsNullOrEmpty(node.File) && node.Line > 0))
                {
                    continue;
                }

                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (!byId.TryGetValue(node.Parents[p], out parent) || string.IsNullOrEmpty(parent.File))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(node.File))
                    {
                        node.File = parent.File;
                    }

                    if (node.Line <= 0)
                    {
                        node.Line = parent.Line;
                    }

                    break;
                }
            }
        }

        private static void PromoteInputStores(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || node.Kind != EventKind.LocalStore || !IsNamedStore(node))
                {
                    continue;
                }

                if (!HasDirectInputParent(node, byId, new HashSet<int>()))
                {
                    continue;
                }

                if (FindNamedSourceStore(node, byId, new HashSet<int>()) == null)
                {
                    node.Category = ReportCategory.Origin;
                    var typed = FindTypedInput(node, byId, new HashSet<int>());
                    if (typed != null && typed.Line > 0)
                    {
                        node.File = typed.File;
                        node.Line = typed.Line;
                    }
                }

                var guard = 0;
                while (guard++ < 8)
                {
                    var changed = false;
                    for (var p = node.Parents.Count - 1; p >= 0; p--)
                    {
                        Work parent;
                        if (!byId.TryGetValue(node.Parents[p], out parent) || parent.Suppressed)
                        {
                            continue;
                        }

                        if (!IsInputCall(parent))
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(node.Preview) && !string.IsNullOrEmpty(parent.Preview))
                        {
                            node.Preview = Unquote(parent.Preview) ?? parent.Preview;
                        }

                        parent.Suppressed = true;
                        ReplaceParent(node, parent.ValueId, parent.Parents);
                        changed = true;
                    }

                    if (!changed)
                    {
                        break;
                    }
                }
            }
        }

        private static void RelabelArithmetic(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || !IsArithmetic(node))
                {
                    continue;
                }

                var symbol = ArithmeticSymbol(ShortCall(node));
                var parts = new List<string>();
                for (var p = 0; p < node.Parents.Count; p++)
                {
                    Work parent;
                    if (!byId.TryGetValue(node.Parents[p], out parent) || parent.Suppressed)
                    {
                        continue;
                    }

                    var text = ParentDisplay(parent);
                    if (!string.IsNullOrEmpty(text) && !parts.Contains(text))
                    {
                        parts.Add(text);
                    }
                }

                if (parts.Count >= 2)
                {
                    node.Title = parts[0] + " " + symbol + " " + parts[1];
                }
                else if (parts.Count == 1)
                {
                    node.Title = symbol + " " + parts[0];
                }
                else
                {
                    node.Title = symbol;
                }

                node.Category = ReportCategory.Transformation;
                node.Operation = OperationKind.Transformation;
            }
        }

        private static void DropUnchanged(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var changed = false;
                for (var i = 0; i < works.Count; i++)
                {
                    if (DropUnchangedNode(works[i], works, byId))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        private static bool DropUnchangedNode(Work node, List<Work> works, Dictionary<int, Work> byId)
        {
            if (node.Suppressed || node.Kind == EventKind.Focus)
            {
                return false;
            }

            if (node.Category == ReportCategory.Search || node.Operation == OperationKind.Search)
            {
                return false;
            }

            if (node.Category == ReportCategory.FrameworkBoundary)
            {
                var kids = ChildrenOf(node.ValueId, works);
                var keep = false;
                for (var c = 0; c < kids.Count; c++)
                {
                    if (kids[c].Kind == EventKind.Focus)
                    {
                        keep = true;
                        break;
                    }
                }

                if (!keep)
                {
                    node.Suppressed = true;
                    return true;
                }

                return false;
            }

            if (node.Kind == EventKind.Constant && FeedsOnlyArithmetic(node, works))
            {
                node.Suppressed = true;
                return true;
            }

            if (IsTryParse(node) || IsParseCall(node))
            {
                node.Suppressed = true;
                return true;
            }

            if (LooksNumeric(node) && (node.Kind == EventKind.FieldWrite || (node.Kind == EventKind.LocalStore && IsNamedStore(node))))
            {
                return false;
            }

            if (string.IsNullOrEmpty(node.Preview))
            {
                if (IsNoiseTitle(node))
                {
                    node.Suppressed = true;
                    return true;
                }

                if (node.Kind == EventKind.LocalStore && IsOnlySearchChild(node, works))
                {
                    node.Suppressed = true;
                    SuppressEmptyAncestors(node, byId);
                    return true;
                }

                return false;
            }

            if (node.Parents.Count != 1)
            {
                return false;
            }

            Work parent;
            if (!byId.TryGetValue(node.Parents[0], out parent) || parent.Suppressed)
            {
                return false;
            }

            if (!SameMeaning(parent, node))
            {
                return false;
            }

            if (node.Category == ReportCategory.Origin && parent.Category != ReportCategory.Origin)
            {
                return false;
            }

            if (IsNamedStore(node) && (IsArithmetic(parent) || !IsNamedStore(parent)))
            {
                if (parent.Operation != OperationKind.Search && parent.Category != ReportCategory.Search)
                {
                    return false;
                }
            }

            if (IsNamedStore(node) && IsNamedStore(parent) && parent.Category == ReportCategory.Origin)
            {
                return false;
            }

            node.Suppressed = true;
            Retarget(works, node.ValueId, parent.ValueId);
            return true;
        }

        private static void SuppressEmptyAncestors(Work node, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(node.Parents[i], out parent) || parent.Suppressed)
                {
                    continue;
                }

                if (parent.Kind == EventKind.Focus || !string.IsNullOrEmpty(parent.Preview))
                {
                    continue;
                }

                parent.Suppressed = true;
                SuppressEmptyAncestors(parent, byId);
            }
        }

        private static bool IsOnlySearchChild(Work node, List<Work> works)
        {
            var children = ChildrenOf(node.ValueId, works);
            if (children.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (children[i].Operation != OperationKind.Search && children[i].Category != ReportCategory.Search)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FeedsOnlyArithmetic(Work node, List<Work> works)
        {
            var children = ChildrenOf(node.ValueId, works);
            if (children.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (!IsArithmetic(children[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNoiseTitle(Work node)
        {
            var title = node.Title ?? string.Empty;
            return title == "field" || title == "local" || title == "constant" || title == "op" || title == "compare";
        }

        private static bool HasDirectInputParent(Work node, Dictionary<int, Work> byId, HashSet<int> seen)
        {
            if (!seen.Add(node.ValueId))
            {
                return false;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(node.Parents[i], out parent) || parent.Suppressed)
                {
                    continue;
                }

                if (IsInputCall(parent))
                {
                    return true;
                }

                if (parent.Kind == EventKind.LocalStore && (SameMeaning(parent, node) || IsInputCall(parent))
                    && HasDirectInputParent(parent, byId, seen))
                {
                    return true;
                }
            }

            return false;
        }

        private static Work FindNamedSourceStore(Work node, Dictionary<int, Work> byId, HashSet<int> seen)
        {
            if (!seen.Add(node.ValueId))
            {
                return null;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(node.Parents[i], out parent) || parent.Suppressed)
                {
                    continue;
                }

                if (parent.Kind == EventKind.LocalStore && IsNamedStore(parent) && parent.ValueId != node.ValueId)
                {
                    return parent;
                }

                if (IsInputCall(parent))
                {
                    var deeper = FindNamedSourceStore(parent, byId, seen);
                    if (deeper != null)
                    {
                        return deeper;
                    }
                }
            }

            return null;
        }

        private static Work FindTypedInput(Work node, Dictionary<int, Work> byId, HashSet<int> seen)
        {
            Work readLine = null;
            Work store = null;
            CollectTypedInput(node, node, byId, seen, ref readLine, ref store);
            return readLine ?? store;
        }

        private static void CollectTypedInput(Work origin, Work current, Dictionary<int, Work> byId, HashSet<int> seen, ref Work readLine, ref Work store)
        {
            if (!seen.Add(current.ValueId))
            {
                return;
            }

            for (var i = 0; i < current.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(current.Parents[i], out parent))
                {
                    continue;
                }

                if (IsReadLine(parent) && parent.Line > 0)
                {
                    if (readLine == null || parent.Line < readLine.Line)
                    {
                        readLine = parent;
                    }
                }
                else if (parent.Kind == EventKind.LocalStore
                    && parent.ValueId != origin.ValueId
                    && parent.Line > 0
                    && IsNamedStore(parent)
                    && !IsParseCall(parent)
                    && !IsTryParse(parent)
                    && SameDeclaringMethod(origin, parent))
                {
                    if (store == null || parent.Line < store.Line)
                    {
                        store = parent;
                    }
                }

                CollectTypedInput(origin, parent, byId, seen, ref readLine, ref store);
            }
        }

        private static bool SameDeclaringMethod(Work a, Work b)
        {
            if (string.IsNullOrEmpty(a.Method) || string.IsNullOrEmpty(b.Method))
            {
                return true;
            }

            return string.Equals(a.Method, b.Method, StringComparison.Ordinal);
        }

        private static bool IsReadLine(Work node)
        {
            var name = ShortCall(node);
            if (name == "ReadLine" || name == "Console.ReadLine")
            {
                return true;
            }

            var title = node.Title ?? string.Empty;
            return title.IndexOf("ReadLine", StringComparison.Ordinal) >= 0;
        }

        private static bool IsInputCall(Work node)
        {
            var name = ShortCall(node);
            if (name == "ReadLine" || name == "Console.ReadLine" || name == "Parse" || name == "TryParse" || name == "ParseQuantity")
            {
                return true;
            }

            var title = node.Title ?? string.Empty;
            return title.IndexOf("ReadLine", StringComparison.Ordinal) >= 0;
        }

        private static bool IsNamedStore(Work node)
        {
            if (node.Kind != EventKind.LocalStore)
            {
                return false;
            }

            var title = node.Title ?? string.Empty;
            if (string.IsNullOrEmpty(title) || IsNoiseTitle(node) || IsArithmetic(node) || IsCompilerLocal(node))
            {
                return false;
            }

            return title.IndexOf('(') < 0;
        }

        private static bool IsArithmetic(Work node)
        {
            return IsArithmeticName(ShortCall(node)) || IsArithmeticName(node.Title);
        }

        private static bool IsArithmeticName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

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
                case "%":
                    return true;
                default:
                    return name.IndexOf(" × ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" ÷ ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" + ", StringComparison.Ordinal) >= 0
                        || name.IndexOf(" − ", StringComparison.Ordinal) >= 0;
            }
        }

        private static string ArithmeticSymbol(string name)
        {
            switch (name)
            {
                case "add":
                case "+":
                    return "+";
                case "sub":
                case "-":
                case "−":
                    return "−";
                case "div":
                case "÷":
                    return "÷";
                case "rem":
                case "%":
                    return "%";
                default:
                    return "×";
            }
        }

        private static string ParentDisplay(Work parent)
        {
            if (!string.IsNullOrEmpty(parent.Preview))
            {
                return Unquote(parent.Preview);
            }

            if (!string.IsNullOrEmpty(parent.Title) && parent.Title != "constant")
            {
                return parent.Title;
            }

            return string.Empty;
        }

        private static bool IsTryParse(Work node)
        {
            var name = ShortCall(node);
            return name == "TryParse" || (node.Title != null && node.Title.IndexOf("TryParse", StringComparison.Ordinal) >= 0);
        }

        private static bool IsParseCall(Work node)
        {
            var name = ShortCall(node);
            return name == "Parse" || name == "ParseQuantity";
        }

        private static void Reconnect(List<Work> works, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed)
                {
                    continue;
                }

                var resolved = new List<int>();
                var stack = new Stack<int>();
                var seen = new HashSet<int>();
                for (var p = node.Parents.Count - 1; p >= 0; p--)
                {
                    stack.Push(node.Parents[p]);
                }

                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    Work parent;
                    if (!byId.TryGetValue(id, out parent))
                    {
                        continue;
                    }

                    if (!parent.Suppressed)
                    {
                        if (!resolved.Contains(id))
                        {
                            resolved.Add(id);
                        }

                        continue;
                    }

                    for (var p = parent.Parents.Count - 1; p >= 0; p--)
                    {
                        stack.Push(parent.Parents[p]);
                    }
                }

                node.Parents.Clear();
                node.Parents.AddRange(resolved);
            }
        }

        private static List<Work> TopoSort(List<Work> works)
        {
            var byId = new Dictionary<int, Work>();
            var index = new Dictionary<int, int>();
            for (var i = 0; i < works.Count; i++)
            {
                byId[works[i].ValueId] = works[i];
                index[works[i].ValueId] = i;
            }

            var indegree = new Dictionary<int, int>();
            var children = new Dictionary<int, List<int>>();
            for (var i = 0; i < works.Count; i++)
            {
                indegree[works[i].ValueId] = 0;
                children[works[i].ValueId] = new List<int>();
            }

            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                for (var p = 0; p < node.Parents.Count; p++)
                {
                    var pid = node.Parents[p];
                    if (!byId.ContainsKey(pid))
                    {
                        continue;
                    }

                    children[pid].Add(node.ValueId);
                    indegree[node.ValueId] = indegree[node.ValueId] + 1;
                }
            }

            var searchAncestors = SearchAncestors(works, byId);
            var mergeIds = new HashSet<int>();
            for (var i = 0; i < works.Count; i++)
            {
                if (works[i].Category == ReportCategory.Merge)
                {
                    mergeIds.Add(works[i].ValueId);
                }
            }

            var ready = new List<int>();
            foreach (var pair in indegree)
            {
                if (pair.Value == 0)
                {
                    ready.Add(pair.Key);
                }
            }

            var ordered = new List<Work>();
            while (ready.Count > 0)
            {
                var pick = 0;
                var best = int.MaxValue;
                for (var r = 0; r < ready.Count; r++)
                {
                    var score = Rank(byId[ready[r]], searchAncestors, mergeIds);
                    if (score < best || (score == best && ready[r] < ready[pick]))
                    {
                        best = score;
                        pick = r;
                    }
                }

                var id = ready[pick];
                ready.RemoveAt(pick);
                ordered.Add(byId[id]);
                var kids = children[id];
                for (var k = 0; k < kids.Count; k++)
                {
                    var child = kids[k];
                    indegree[child] = indegree[child] - 1;
                    if (indegree[child] == 0)
                    {
                        ready.Add(child);
                    }
                }
            }

            if (ordered.Count < works.Count)
            {
                for (var i = 0; i < works.Count; i++)
                {
                    if (!ordered.Contains(works[i]))
                    {
                        ordered.Add(works[i]);
                    }
                }
            }

            return ordered;
        }

        private static int Rank(Work node, HashSet<int> searchAncestors, HashSet<int> mergeIds)
        {
            if (node.Kind == EventKind.Focus)
            {
                return 4;
            }

            if (mergeIds.Contains(node.ValueId))
            {
                return 3;
            }

            if (node.Parents.Count == 0 && !searchAncestors.Contains(node.ValueId))
            {
                return 2;
            }

            if (searchAncestors.Contains(node.ValueId))
            {
                return 0;
            }

            return 1;
        }

        private static HashSet<int> SearchAncestors(List<Work> works, Dictionary<int, Work> byId)
        {
            var set = new HashSet<int>();
            for (var i = 0; i < works.Count; i++)
            {
                if (works[i].Operation != OperationKind.Search && works[i].Category != ReportCategory.Search)
                {
                    continue;
                }

                var stack = new Stack<int>();
                stack.Push(works[i].ValueId);
                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    if (!set.Add(id))
                    {
                        continue;
                    }

                    Work node;
                    if (!byId.TryGetValue(id, out node))
                    {
                        continue;
                    }

                    for (var p = 0; p < node.Parents.Count; p++)
                    {
                        stack.Push(node.Parents[p]);
                    }
                }
            }

            return set;
        }

        private static void CollectTerminals(Work node, Dictionary<int, Work> byId, HashSet<int> concat, List<int> terminals, HashSet<int> seen)
        {
            if (!seen.Add(node.ValueId))
            {
                return;
            }

            if (node.Parents.Count == 0)
            {
                if (!IsStringGlue(node) && !IsBareLiteral(node) && !node.Suppressed)
                {
                    terminals.Add(node.ValueId);
                }

                return;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(node.Parents[i], out parent))
                {
                    continue;
                }

                if (IsStringGlue(parent) || concat.Contains(parent.ValueId) || IsBareLiteral(parent) || IsToString(parent) || IsCompilerLocal(parent))
                {
                    CollectTerminals(parent, byId, concat, terminals, seen);
                    continue;
                }

                if (parent.Kind == EventKind.LocalStore && Touches(parent, concat) && !IsStringSink(parent, ToList(byId), concat))
                {
                    CollectTerminals(parent, byId, concat, terminals, seen);
                    continue;
                }

                terminals.Add(parent.ValueId);
                seen.Add(parent.ValueId);
            }
        }

        private static List<Work> ToList(Dictionary<int, Work> byId)
        {
            var list = new List<Work>(byId.Count);
            foreach (var pair in byId)
            {
                list.Add(pair.Value);
            }

            return list;
        }

        private static bool Touches(Work node, HashSet<int> concat)
        {
            if (concat.Contains(node.ValueId) || IsStringGlue(node))
            {
                return true;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                if (concat.Contains(node.Parents[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStringSink(Work node, List<Work> works, HashSet<int> concat)
        {
            if (node.Kind != EventKind.LocalStore && node.Category != ReportCategory.Assignment && node.Category != ReportCategory.Merge)
            {
                return false;
            }

            var children = ChildrenOf(node.ValueId, works);
            if (children.Count == 0)
            {
                return true;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (children[i].Kind == EventKind.Focus)
                {
                    return true;
                }

                if (IsStringGlue(children[i]) || concat.Contains(children[i].ValueId))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<Work> ChildrenOf(int id, List<Work> works)
        {
            var list = new List<Work>();
            for (var i = 0; i < works.Count; i++)
            {
                if (!works[i].Suppressed && works[i].Parents.Contains(id))
                {
                    list.Add(works[i]);
                }
            }

            return list;
        }

        private static void ReplaceParent(Work node, int from, List<int> replacements)
        {
            node.Parents.Remove(from);
            for (var i = 0; i < replacements.Count; i++)
            {
                if (replacements[i] > 0 && replacements[i] != node.ValueId && !node.Parents.Contains(replacements[i]))
                {
                    node.Parents.Add(replacements[i]);
                }
            }
        }

        private static void Retarget(List<Work> works, int from, int to)
        {
            if (from == to)
            {
                return;
            }

            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (!node.Parents.Contains(from))
                {
                    continue;
                }

                node.Parents.Remove(from);
                if (to > 0 && to != node.ValueId && !node.Parents.Contains(to))
                {
                    node.Parents.Add(to);
                }
            }
        }

        private static bool ShouldQualifyCall(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return false;
            }

            switch (method)
            {
                case "Trim":
                case "ToUpper":
                case "ToUpperInvariant":
                case "ToLower":
                case "ToLowerInvariant":
                case "Concat":
                case "ToString":
                case "ReadLine":
                case "Console.ReadLine":
                case "Parse":
                case "TryParse":
                case "add":
                case "sub":
                case "mul":
                case "div":
                case "rem":
                case "op":
                    return false;
                default:
                    return true;
            }
        }

        private static string ChildAssignmentName(Work find, List<Work> works)
        {
            var child = ChildAssignment(find, works);
            return child != null ? child.Title : null;
        }

        private static Work ChildAssignment(Work find, List<Work> works)
        {
            var children = ChildrenOf(find.ValueId, works);
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i].Kind == EventKind.LocalStore)
                {
                    return children[i];
                }
            }

            return children.Count > 0 ? children[0] : null;
        }

        private static Work FirstNamedParent(Work node, Dictionary<int, Work> byId)
        {
            Work withValue = null;
            Work namedStore = null;
            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (!byId.TryGetValue(node.Parents[i], out parent))
                {
                    continue;
                }

                if (parent.Suppressed)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(parent.Preview) && parent.Preview != "null")
                {
                    if (withValue == null || parent.Kind == EventKind.LocalStore)
                    {
                        withValue = parent;
                    }
                }

                if (namedStore == null && parent.Kind == EventKind.LocalStore && !string.IsNullOrEmpty(parent.Title) && parent.Title != "constant")
                {
                    namedStore = parent;
                }
            }

            if (withValue != null)
            {
                return withValue;
            }

            if (namedStore != null)
            {
                return namedStore;
            }

            for (var i = 0; i < node.Parents.Count; i++)
            {
                Work parent;
                if (byId.TryGetValue(node.Parents[i], out parent) && !string.IsNullOrEmpty(parent.Title) && parent.Title != "constant")
                {
                    return parent;
                }
            }

            return null;
        }

        private static Work BestSearchKey(Work find, List<Work> works, Dictionary<int, Work> byId)
        {
            Work skuLiteral = null;
            Work letterLiteral = null;
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Suppressed || (node.Kind != EventKind.Constant && node.Kind != EventKind.LocalStore))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(node.Preview) || node.Preview == "null" || !HasLetter(node.Preview))
                {
                    continue;
                }

                if (LooksLikeSku(node.Preview))
                {
                    skuLiteral = node;
                    break;
                }

                if (letterLiteral == null && node.Kind == EventKind.Constant)
                {
                    letterLiteral = node;
                }
            }

            if (skuLiteral != null)
            {
                return skuLiteral;
            }

            var named = FirstNamedParent(find, byId);
            if (named != null && !string.IsNullOrEmpty(named.Preview) && named.Preview != "null" && HasLetter(named.Preview) && named.Title != "catalog")
            {
                return named;
            }

            return letterLiteral ?? named;
        }

        private static bool LooksLikeSku(string preview)
        {
            var text = Unquote(preview);
            if (string.IsNullOrEmpty(text) || text.Length > 16)
            {
                return false;
            }

            var letters = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text[i]))
                {
                    letters++;
                    if (!char.IsUpper(text[i]))
                    {
                        return false;
                    }
                }
            }

            return letters > 0;
        }

        private static string RelatedFieldName(List<Work> works, Work find, Dictionary<int, Work> byId)
        {
            string sku = null;
            string other = null;
            string name = null;
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if (node.Kind != EventKind.FieldRead && node.Kind != EventKind.FieldWrite)
                {
                    continue;
                }

                var field = PropertyName(node);
                if (string.IsNullOrEmpty(field) || field == "Status" || field == "field" || field == "local")
                {
                    continue;
                }

                if (field == "Sku")
                {
                    sku = field;
                }
                else if (field == "Name")
                {
                    name = field;
                }
                else if (other == null)
                {
                    other = field;
                }
            }

            return sku ?? name ?? other ?? "Sku";
        }

        private static string FieldPreview(List<Work> works, Work owner, string field)
        {
            for (var i = 0; i < works.Count; i++)
            {
                var node = works[i];
                if ((node.Kind == EventKind.FieldRead || node.Kind == EventKind.FieldWrite) && PropertyName(node) == field && !string.IsNullOrEmpty(node.Preview))
                {
                    return node.Preview;
                }
            }

            return null;
        }

        private static bool HasDeveloperNeighbor(List<int> ids, Dictionary<int, Work> byId)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                Work node;
                if (byId.TryGetValue(ids[i], out node) && !node.Suppressed && !node.Opaque && !IsFrameworkNoise(node))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnknownBoundary(Work node, List<Work> works, Dictionary<int, Work> byId)
        {
            if (!node.Opaque || IsFrameworkNoise(node))
            {
                return false;
            }

            return HasDeveloperNeighbor(node.Parents, byId) && ChildrenOf(node.ValueId, works).Count > 0;
        }

        private static bool IsFrameworkNoise(Work node)
        {
            var name = node.InfoCall ?? node.Title ?? string.Empty;
            if (name.IndexOf(".ctor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (name.IndexOf('`') >= 0)
            {
                return true;
            }

            if (name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool IsCompilerLocal(Work node)
        {
            var name = node.Title ?? string.Empty;
            if (string.IsNullOrEmpty(name) || name == "local" || name == "constant")
            {
                return node.Kind == EventKind.LocalStore || name == "local";
            }

            if (name.StartsWith("CS$", StringComparison.Ordinal) || name.IndexOf('<') >= 0)
            {
                return true;
            }

            if (name.StartsWith("local", StringComparison.Ordinal) && name.Length > 5 && char.IsDigit(name[5]))
            {
                return true;
            }

            return false;
        }

        private static bool IsConcat(Work node)
        {
            return string.Equals(node.InfoCall, "Concat", StringComparison.Ordinal) || (node.Title != null && node.Title.StartsWith("Concat", StringComparison.Ordinal));
        }

        private static bool IsToString(Work node)
        {
            return string.Equals(node.InfoCall, "ToString", StringComparison.Ordinal) || node.Title == "ToString" || (node.Title != null && node.Title.StartsWith("ToString", StringComparison.Ordinal));
        }

        private static bool IsStringGlue(Work node)
        {
            return IsConcat(node) || IsToString(node) || IsBareLiteral(node);
        }

        private static bool IsBareLiteral(Work node)
        {
            if (node.Kind != EventKind.Constant)
            {
                return false;
            }

            if (string.IsNullOrEmpty(node.Title) || node.Title == "constant")
            {
                return true;
            }

            if (node.Title.Length >= 2 && node.Title[0] == '"')
            {
                return true;
            }

            var preview = node.Preview ?? string.Empty;
            if (preview.Length >= 2 && preview[0] == '"' && !HasLetter(preview))
            {
                return true;
            }

            return false;
        }

        private static bool IsNullConstant(Work node)
        {
            return node.Kind == EventKind.Constant && (node.Preview == "null" || node.Title == "null");
        }

        private static bool HasLetter(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksNumeric(Work node)
        {
            var text = NormalizePreview(node.Preview);
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

        private static bool SamePreview(Work a, Work b)
        {
            if (string.IsNullOrEmpty(a.Preview) || string.IsNullOrEmpty(b.Preview))
            {
                return string.IsNullOrEmpty(a.Preview) && string.IsNullOrEmpty(b.Preview);
            }

            return a.Preview == b.Preview;
        }

        private static bool SameMeaning(Work a, Work b)
        {
            var left = NormalizePreview(a.Preview);
            var right = NormalizePreview(b.Preview);
            return left.Length > 0 && left == right;
        }

        private static string NormalizePreview(string preview)
        {
            if (string.IsNullOrEmpty(preview))
            {
                return string.Empty;
            }

            return Unquote(preview);
        }

        private static string PropertyKey(Work node)
        {
            return node.Kind + ":" + PropertyName(node);
        }

        private static string PropertyName(Work node)
        {
            if (node.InfoLocal != null && node.InfoLocal.IndexOf('<') < 0 && node.InfoLocal != "local")
            {
                return node.InfoLocal;
            }

            var title = node.Title ?? string.Empty;
            var dot = title.LastIndexOf('.');
            if (dot >= 0 && dot < title.Length - 1)
            {
                title = title.Substring(dot + 1);
            }

            var eq = title.IndexOf(" = ", StringComparison.Ordinal);
            if (eq > 0)
            {
                title = title.Substring(0, eq);
            }

            return title;
        }

        private static string ShortCall(Work node)
        {
            var name = node.InfoCall;
            if (string.IsNullOrEmpty(name))
            {
                name = node.Title;
            }

            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var eq = name.IndexOf(" = ", StringComparison.Ordinal);
            if (eq > 0)
            {
                name = name.Substring(0, eq);
            }

            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1)
            {
                name = name.Substring(dot + 1);
            }

            if (name.EndsWith("()", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - 2);
            }

            return name;
        }

        private static string Unquote(string preview)
        {
            if (preview != null && preview.Length >= 2 && preview[0] == '"' && preview[preview.Length - 1] == '"')
            {
                return preview.Substring(1, preview.Length - 2);
            }

            return preview;
        }

        internal sealed class Work
        {
            public int ValueId;
            public int LocationId;
            public EventKind Kind;
            public OperationKind Operation;
            public ReportCategory Category;
            public List<int> Parents = new List<int>();
            public string Title;
            public string Preview;
            public bool Opaque;
            public bool Suppressed;
            public string InfoCall;
            public string InfoLocal;
            public string File;
            public int Line;
            public string Method;
            public string TypeName;
            public ValueAvailability Availability;

            public static Work From(LineageNode node)
            {
                var info = MetadataRegistry.Get(node.LocationId);
                var work = new Work
                {
                    ValueId = node.ValueId,
                    LocationId = node.LocationId,
                    Kind = node.Kind,
                    Operation = node.Operation,
                    Category = Classify(node),
                    Title = BaseName(node, info),
                    Preview = string.IsNullOrEmpty(node.Value) ? null : node.Value,
                    Opaque = node.IsOpaque,
                    InfoCall = info != null ? info.CallName : null,
                    InfoLocal = info != null ? info.LocalName : null,
                    File = !string.IsNullOrEmpty(node.File) ? node.File : (info != null ? info.File : null),
                    Line = node.Line != 0 ? node.Line : (info != null ? info.Line : 0),
                    Method = !string.IsNullOrEmpty(node.Method) ? node.Method : (info != null ? info.MethodName : null),
                    TypeName = node.TypeName,
                    Availability = node.ValueAvailability
                };
                var parents = node.Parents ?? LineageNode.NormalizeParents(node.Parent0, node.Parent1, null);
                for (var i = 0; i < parents.Count; i++)
                {
                    if (parents[i] > 0)
                    {
                        work.Parents.Add(parents[i]);
                    }
                }

                return work;
            }

            public LineageNode ToNode()
            {
                return new LineageNode(
                    ValueId,
                    LocationId,
                    Kind,
                    Operation,
                    Category,
                    0,
                    0,
                    Parents,
                    ReportFormatter.Combine(Title, Preview),
                    Opaque && Category != ReportCategory.FrameworkBoundary,
                    Preview,
                    File,
                    Line,
                    Method,
                    null,
                    TypeName,
                    Availability);
            }
        }

        internal static ReportCategory Classify(LineageNode node)
        {
            if (node.Kind == EventKind.Focus)
            {
                return ReportCategory.Focus;
            }

            if (node.Operation == OperationKind.Search)
            {
                return ReportCategory.Search;
            }

            if (node.Operation == OperationKind.Aggregate)
            {
                return ReportCategory.Aggregate;
            }

            if (node.Operation == OperationKind.Comparison || node.Kind == EventKind.Branch)
            {
                return node.Kind == EventKind.Branch ? ReportCategory.Branch : ReportCategory.Comparison;
            }

            if (node.Kind == EventKind.FieldRead)
            {
                return ReportCategory.PropertyRead;
            }

            if (node.Kind == EventKind.FieldWrite)
            {
                return ReportCategory.PropertyWrite;
            }

            if (node.Kind == EventKind.LocalStore || node.Kind == EventKind.Argument)
            {
                return ReportCategory.Assignment;
            }

            if (node.Kind == EventKind.Constant || node.Kind == EventKind.Origin)
            {
                return ReportCategory.Origin;
            }

            if (node.IsOpaque)
            {
                return ReportCategory.FrameworkBoundary;
            }

            if (node.Operation == OperationKind.Transformation)
            {
                return ReportCategory.Transformation;
            }

            if (node.Kind == EventKind.Call)
            {
                return ReportCategory.MethodCall;
            }

            return ReportCategory.None;
        }

        private static string BaseName(LineageNode node, LocationInfo info)
        {
            var label = node.Label ?? string.Empty;
            var eq = label.IndexOf(" = ", StringComparison.Ordinal);
            if (eq > 0)
            {
                return label.Substring(0, eq);
            }

            if (node.Kind == EventKind.Focus)
            {
                return "Trace";
            }

            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.ReportLabel) && info.ReportLabel.IndexOf('<') < 0)
                {
                    return info.ReportLabel;
                }

                if (!string.IsNullOrEmpty(info.LocalName) && info.LocalName.IndexOf('<') < 0)
                {
                    return info.LocalName;
                }

                if (!string.IsNullOrEmpty(info.CallName))
                {
                    if (info.Operation == OperationKind.Search || info.Operation == OperationKind.Aggregate || info.CallName.IndexOf('(') >= 0)
                    {
                        return info.CallName;
                    }

                    return info.CallName + "()";
                }
            }

            return label;
        }
    }
}
