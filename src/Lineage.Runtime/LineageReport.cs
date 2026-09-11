using System;
using System.Collections.Generic;

namespace Lineage
{
    public sealed class LineageReport
    {
        public string ReportId { get; }
        public DateTime Timestamp { get; }
        public LineageTrigger Trigger { get; }
        public LineageNode Focus { get; }
        public IReadOnlyList<LineageNode> Origins { get; }
        public IReadOnlyList<LineageNode> Nodes { get; }
        public IReadOnlyList<LineageNode> RawNodes { get; }
        public IReadOnlyList<LineageEdge> Edges { get; }
        public string Scope { get; }
        public LineageCoverage Coverage { get; }
        public bool EventsDropped => Coverage != null && Coverage.EventsDropped;
        public long EventCount => Coverage != null ? Coverage.EventCount : 0;
        public long ProduceTicks { get; }

        public LineageReport(IReadOnlyList<LineageNode> nodes, bool eventsDropped = false, long eventCount = 0, long produceTicks = 0)
            : this(
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow,
                LineageTrigger.None,
                nodes,
                nodes,
                eventsDropped,
                eventCount,
                produceTicks,
                "default")
        {
        }

        public LineageReport(
            string reportId,
            DateTime timestamp,
            LineageTrigger trigger,
            IReadOnlyList<LineageNode> nodes,
            IReadOnlyList<LineageNode> rawNodes,
            bool eventsDropped,
            long eventCount,
            long produceTicks,
            string scope)
        {
            ReportId = reportId ?? string.Empty;
            Timestamp = timestamp;
            Trigger = trigger ?? LineageTrigger.None;
            Nodes = nodes ?? new LineageNode[0];
            RawNodes = rawNodes ?? Nodes;
            ProduceTicks = produceTicks;
            Scope = scope ?? string.Empty;
            AssignChildren(Nodes);
            AssignChildren(RawNodes);
            Edges = BuildEdges(Nodes);
            Origins = FindOrigins(Nodes);
            Focus = FindFocus(Nodes);
            var opaque = 0;
            var unknown = false;
            for (var i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].IsOpaque || Nodes[i].Category == ReportCategory.FrameworkBoundary)
                {
                    opaque++;
                    unknown = true;
                }
            }

            Coverage = new LineageCoverage(eventsDropped, eventCount, opaque, unknown);
        }

        public override string ToString()
        {
            return ReportRenderer.Render(this, false);
        }

        public string ToDiagnosticString()
        {
            return ReportRenderer.Render(this, true);
        }

        private static void AssignChildren(IReadOnlyList<LineageNode> nodes)
        {
            if (nodes == null)
            {
                return;
            }

            var map = new Dictionary<int, List<int>>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var id = nodes[i].ValueId;
                if (id > 0 && !map.ContainsKey(id))
                {
                    map[id] = new List<int>();
                }
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                var child = nodes[i];
                var parents = child.Parents;
                for (var p = 0; p < parents.Count; p++)
                {
                    List<int> list;
                    if (map.TryGetValue(parents[p], out list) && !list.Contains(child.ValueId))
                    {
                        list.Add(child.ValueId);
                    }
                }
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                List<int> list;
                nodes[i].Children = map.TryGetValue(nodes[i].ValueId, out list) ? (IReadOnlyList<int>)list : (IReadOnlyList<int>)new int[0];
            }
        }

        private static IReadOnlyList<LineageEdge> BuildEdges(IReadOnlyList<LineageNode> nodes)
        {
            var edges = new List<LineageEdge>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                for (var p = 0; p < node.Parents.Count; p++)
                {
                    edges.Add(new LineageEdge(node.Parents[p], node.ValueId));
                }
            }

            return edges;
        }

        private static IReadOnlyList<LineageNode> FindOrigins(IReadOnlyList<LineageNode> nodes)
        {
            var origins = new List<LineageNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Parents.Count == 0 && nodes[i].Kind != EventKind.Focus)
                {
                    origins.Add(nodes[i]);
                }
            }

            return origins;
        }

        private static LineageNode FindFocus(IReadOnlyList<LineageNode> nodes)
        {
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                if (nodes[i].Kind == EventKind.Focus)
                {
                    return nodes[i];
                }
            }

            return nodes.Count > 0 ? nodes[nodes.Count - 1] : null;
        }
    }
}
