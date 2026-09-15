namespace Lineage
{
    /// <summary>
    /// Semantic kind of a captured runtime value. These values are deliberately stable
    /// because they are also persisted in the cold provenance journal.
    /// </summary>
    public enum LineageValueKind : byte
    {
        None = 0,
        Null = 1,
        Boolean = 2,
        Char = 3,
        SByte = 4,
        Byte = 5,
        Int16 = 6,
        UInt16 = 7,
        Int32 = 8,
        UInt32 = 9,
        Int64 = 10,
        UInt64 = 11,
        NativeInt = 12,
        NativeUInt = 13,
        Single = 14,
        Double = 15,
        Decimal = 16,
        Enum = 17,
        DateTime = 18,
        DateTimeOffset = 19,
        TimeSpan = 20,
        Guid = 21,
        Half = 22,
        Int128 = 23,
        UInt128 = 24,
        DateOnly = 25,
        TimeOnly = 26,
        Struct = 27,
        ValueTuple = 28,

        // Compatibility bucket for values that still use the old preview mechanism.
        // Reference-type support is intentionally not part of the first-class value-type
        // model, but keeping this bucket avoids regressing existing v0.1 behavior.
        LegacyText = 250
    }
}
