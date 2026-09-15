namespace Lineage
{
    /// <summary>
    /// A compact runtime fact. Id is the occurrence identity and LocationId resolves
    /// source/variable metadata later. First-class value types are stored as a semantic
    /// kind plus two fixed payload words; Value is used only when a kind needs textual
    /// auxiliary data or for legacy reference previews.
    /// </summary>
    public struct LineageStep
    {
        public int Id;
        public string Value;
        public int LocationId;
        public EventKind Kind;
        public string TypeName;
        public LineageValueKind ValueKind;
        public long ValueData0;
        public long ValueData1;

        public LineageStep(int id, string value, int locationId, EventKind kind)
        {
            Id = id;
            Value = value;
            LocationId = locationId;
            Kind = kind;
            TypeName = null;
            ValueKind = string.IsNullOrEmpty(value) ? LineageValueKind.None : LineageValueKind.LegacyText;
            ValueData0 = 0;
            ValueData1 = 0;
        }

        internal string FormatValue()
        {
            return LineageValueCodec.Format(ValueKind, ValueData0, ValueData1, Value);
        }
    }
}
