namespace Lineage
{
    /// <summary>
    /// A compact runtime fact. Id is the occurrence identity, Value is the captured
    /// value representation, and LocationId resolves source/variable metadata later.
    /// </summary>
    public struct LineageStep
    {
        public int Id;
        public string Value;
        public int LocationId;
        public EventKind Kind;

        public LineageStep(int id, string value, int locationId, EventKind kind)
        {
            Id = id;
            Value = value;
            LocationId = locationId;
            Kind = kind;
        }
    }
}
