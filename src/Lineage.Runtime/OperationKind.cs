namespace Lineage
{
    public enum OperationKind : byte
    {
        None = 0,
        Assignment = 1,
        Transformation = 2,
        Search = 3,
        Selection = 4,
        Aggregate = 5,
        Comparison = 6,
        Branch = 7,
        ExternalCall = 8,
        Literal = 9,
        Focus = 10,
        Trigger = 11,
        Opaque = 12
    }
}
