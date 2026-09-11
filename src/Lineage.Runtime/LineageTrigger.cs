namespace Lineage
{
    public enum LineageTriggerKind : byte
    {
        None = 0,
        ManualTrace = 1,
        UnhandledException = 2
    }

    public sealed class LineageTrigger
    {
        public LineageTriggerKind Kind { get; }
        public string Title { get; }
        public string Detail { get; }
        public string File { get; }
        public int Line { get; }

        public LineageTrigger(LineageTriggerKind kind, string title, string detail = null, string file = null, int line = 0)
        {
            Kind = kind;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
            File = file ?? string.Empty;
            Line = line;
        }

        public static LineageTrigger ManualTrace(string file = null, int line = 0)
        {
            return new LineageTrigger(LineageTriggerKind.ManualTrace, "Manual Trace", string.Empty, file, line);
        }

        public static LineageTrigger UnhandledException(string exceptionType, string file = null, int line = 0)
        {
            return new LineageTrigger(LineageTriggerKind.UnhandledException, "Unhandled Exception", exceptionType ?? string.Empty, file, line);
        }

        public static readonly LineageTrigger None = new LineageTrigger(LineageTriggerKind.None, string.Empty);
    }
}
