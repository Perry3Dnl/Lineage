using Lineage.Internal;

namespace Lineage
{
    public static class Lineage
    {
        public static LineageReport LastReport
        {
            get { return LineageState.LastReport; }
        }

        public static LineageReport Focus<T>(T value)
        {
            if (!LineageSettings.IsEnabled)
            {
                return new LineageReport(new LineageNode[0]);
            }

            Recorder.RememberFocusValue(value);
            return Recorder.CompleteFocus(LineageTrigger.ManualTrace());
        }
    }
}
