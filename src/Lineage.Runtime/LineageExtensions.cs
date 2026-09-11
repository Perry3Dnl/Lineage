using Lineage.Internal;

namespace Lineage
{
    public static class LineageExtensions
    {
        public static T Trace<T>(this T value)
        {
            if (!LineageSettings.IsEnabled)
            {
                return value;
            }

            Recorder.RememberFocusValue(value);
            Recorder.CompleteFocus(LineageTrigger.ManualTrace());
            return value;
        }
    }
}
