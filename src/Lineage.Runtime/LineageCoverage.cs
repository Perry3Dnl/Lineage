namespace Lineage
{
    public sealed class LineageCoverage
    {
        public bool EventsDropped { get; }
        public long EventCount { get; }
        public int OpaqueNodeCount { get; }
        public bool HasUnknownProvenance { get; }

        public LineageCoverage(bool eventsDropped, long eventCount, int opaqueNodeCount, bool hasUnknownProvenance)
        {
            EventsDropped = eventsDropped;
            EventCount = eventCount;
            OpaqueNodeCount = opaqueNodeCount;
            HasUnknownProvenance = hasUnknownProvenance;
        }
    }

    public sealed class LineageEdge
    {
        public int From { get; }
        public int To { get; }

        public LineageEdge(int from, int to)
        {
            From = from;
            To = to;
        }
    }
}
