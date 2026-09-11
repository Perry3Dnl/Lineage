namespace Lineage
{
    public sealed class LocationInfo
    {
        public int LocationId { get; set; }
        public EventKind Kind { get; set; }
        public OperationKind Operation { get; set; }
        public string MethodName { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string LocalName { get; set; }
        public string CallName { get; set; }
        public string ReportLabel { get; set; }
        public bool IsOpaque { get; set; }

        public LocationInfo()
        {
            MethodName = string.Empty;
            File = string.Empty;
            LocalName = string.Empty;
            CallName = string.Empty;
            ReportLabel = string.Empty;
        }
    }
}
