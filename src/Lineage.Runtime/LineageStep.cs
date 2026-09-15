namespace Lineage
{
    /// <summary>
    /// A compact runtime fact. The step id identifies one produced value occurrence;
    /// source/variable information is resolved later through LocationId metadata.
    /// </summary>
    public struct LineageStep
    {
        public int Id;
        public int LocationId;
        public EventKind Kind;

        public LineageStep(int id, int locationId, EventKind kind)
        {
            Id = id;
            LocationId = locationId;
            Kind = kind;
        }
    }
}
