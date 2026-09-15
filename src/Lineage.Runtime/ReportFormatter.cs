using System;
using System.Collections.Generic;

namespace Lineage
{
    internal static class ReportFormatter
    {
        public static LineageReport Format(
            IReadOnlyList<LineageEvent> events,
            bool dropped,
            long eventCount,
            long produceTicks,
            CaptureScope scope = null,
            LineageTrigger trigger = null)
        {
            var raw = new LineageNode[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                var info = MetadataRegistry.Get(ev.LocationId);
                var operation = info != null ? info.Operation : OperationKind.None;
                var opaque = info != null && info.IsOpaque;
                var preview = ev.Value ?? (scope != null ? scope.GetPreview(ev.ValueId) : null);
                var typeName = ev.TypeName ?? (scope != null ? scope.GetTypeName(ev.ValueId) : null);
                var availability = string.IsNullOrEmpty(preview) ? ValueAvailability.NotCaptured : ValueAvailability.Available;
                var node = new LineageNode(
                    ev.ValueId,
                    ev.LocationId,
                    ev.Kind,
                    operation,
                    ReportNormalizer.Classify(new LineageNode(ev.ValueId, ev.LocationId, ev.Kind, operation, ev.Parent0, ev.Parent1, Label(ev, info, preview, trigger), opaque, preview)),
                    ev.Parent0,
                    ev.Parent1,
                    null,
                    Label(ev, info, preview, trigger),
                    opaque,
                    preview,
                    info != null ? info.File : null,
                    info != null ? info.Line : 0,
                    info != null ? info.MethodName : null,
                    null,
                    typeName,
                    availability);
                node.ValueKind = ev.ValueKind != LineageValueKind.None
                    ? ev.ValueKind
                    : (scope != null ? scope.GetValueKind(ev.ValueId) : LineageValueKind.None);
                raw[i] = node;
            }

            var semantic = ReportNormalizer.Apply(raw);
            return new LineageReport(
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow,
                trigger ?? LineageTrigger.None,
                semantic,
                raw,
                dropped,
                eventCount,
                produceTicks,
                "default");
        }

        internal static string Label(LineageEvent ev, LocationInfo info, string preview, LineageTrigger trigger = null)
        {
            var name = ev.Kind == EventKind.Focus ? FocusName(trigger) : Name(ev, info);
            return Combine(name, preview);
        }

        private static string FocusName(LineageTrigger trigger)
        {
            if (trigger != null && trigger.Kind == LineageTriggerKind.UnhandledException)
            {
                return "Exception";
            }

            return "Trace";
        }

        private static string Name(LineageEvent ev, LocationInfo info)
        {
            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.ReportLabel) && info.ReportLabel.IndexOf('<') < 0)
                {
                    return info.ReportLabel;
                }

                if (!string.IsNullOrEmpty(info.LocalName) && info.LocalName.IndexOf('<') < 0 && !info.LocalName.StartsWith("CS$"))
                {
                    return info.LocalName;
                }

                if (!string.IsNullOrEmpty(info.CallName))
                {
                    return FormatCall(info.CallName, info.Operation);
                }
            }

            switch (ev.Kind)
            {
                case EventKind.Call:
                    return "call";
                case EventKind.Constant:
                    return "constant";
                case EventKind.LocalStore:
                    return "local";
                case EventKind.FieldWrite:
                case EventKind.FieldRead:
                    return "field";
                default:
                    return ev.Kind.ToString();
            }
        }

        internal static string Combine(string name, string preview)
        {
            if (string.IsNullOrEmpty(preview))
            {
                return name ?? string.Empty;
            }

            if (string.IsNullOrEmpty(name) || name == preview || name == "constant")
            {
                return preview;
            }

            if (name.IndexOf(preview, StringComparison.Ordinal) >= 0)
            {
                return name;
            }

            return name + " = " + preview;
        }

        private static string FormatCall(string callName, OperationKind operation)
        {
            if (operation == OperationKind.Search || operation == OperationKind.Aggregate)
            {
                return callName;
            }

            if (callName.EndsWith("()") || callName.IndexOf('(') >= 0)
            {
                return callName;
            }

            return callName + "()";
        }
    }
}
