namespace Lineage
{
    public struct LineageEvent
    {
        public int LocationId;
        public int FrameId;
        public int ValueId;
        public int Parent0;
        public int Parent1;
        public EventKind Kind;

        // These fields are populated only when raw provenance is hydrated for a report.
        public string Value;
        public string TypeName;
        public LineageValueKind ValueKind;
    }
}
