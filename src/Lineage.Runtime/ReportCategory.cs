namespace Lineage
{
    public enum ReportCategory : byte
    {
        None = 0,
        Origin = 1,
        Assignment = 2,
        PropertyRead = 3,
        PropertyWrite = 4,
        MethodCall = 5,
        Transformation = 6,
        Search = 7,
        Aggregate = 8,
        Comparison = 9,
        Branch = 10,
        FrameworkBoundary = 11,
        Merge = 12,
        Focus = 13,
        Trigger = 14
    }
}
