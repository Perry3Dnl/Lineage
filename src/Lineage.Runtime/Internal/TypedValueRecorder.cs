namespace Lineage.Internal
{
    /// <summary>
    /// Typed value capture entry point used by injected instrumentation. It intentionally
    /// keeps the same IL call shape as the original Recorder.Remember method so typed
    /// capture can land without adding extra work to every instrumented call site.
    /// </summary>
    public static class TypedValueRecorder
    {
        public static void Remember(object value, int valueId)
        {
            if (!Recorder.IsEnabled() || valueId <= 0)
            {
                return;
            }

            var scope = CaptureScope.Current;
            if (scope == null || scope.Frozen)
            {
                return;
            }

            scope.SetCapturedValue(valueId, LineageValueCodec.Capture(value, null));
            if (value != null)
            {
                scope.SetTypeName(valueId, value.GetType().FullName);
            }
        }
    }
}
