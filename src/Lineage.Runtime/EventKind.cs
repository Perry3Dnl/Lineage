namespace Lineage
{
    public enum EventKind : byte
    {
        None = 0,
        MethodEntry = 1,
        Argument = 2,
        Call = 3,
        Return = 4,
        LocalStore = 5,
        LocalLoad = 6,
        FieldRead = 7,
        FieldWrite = 8,
        Branch = 9,
        Focus = 10,
        Constant = 11,
        Origin = 12
    }
}
