using System;
using Lineage;

namespace Lineage.Instrumentation
{
    internal static class KnownOperations
    {
        public static bool IsTraceOrFocus(string typeName, string methodName)
        {
            if (methodName != "Trace" && methodName != "Focus")
            {
                return false;
            }

            return typeName == "Lineage" || typeName == "LineageExtensions" || typeName.EndsWith(".Lineage", StringComparison.Ordinal) || typeName.EndsWith(".LineageExtensions", StringComparison.Ordinal);
        }

        public static bool TryClassify(string methodName, out OperationKind kind, out string label)
        {
            switch (methodName)
            {
                case "Find":
                case "First":
                case "FirstOrDefault":
                case "Single":
                case "SingleOrDefault":
                case "Last":
                case "LastOrDefault":
                case "TryGetValue":
                    kind = OperationKind.Search;
                    label = methodName;
                    return true;
                case "Select":
                case "Where":
                case "SelectMany":
                    kind = OperationKind.Selection;
                    label = methodName;
                    return true;
                case "Sum":
                case "Average":
                case "Min":
                case "Max":
                case "Count":
                case "LongCount":
                case "Aggregate":
                    kind = OperationKind.Aggregate;
                    label = methodName;
                    return true;
                case "Trim":
                case "ToUpperInvariant":
                case "ToUpper":
                case "ToLowerInvariant":
                case "ToLower":
                case "Concat":
                case "Parse":
                case "TryParse":
                    kind = OperationKind.Transformation;
                    label = methodName;
                    return true;
                default:
                    kind = OperationKind.ExternalCall;
                    label = methodName;
                    return false;
            }
        }

        public static bool IsCompilerGeneratedName(string name)
        {
            return !string.IsNullOrEmpty(name) && (name.IndexOf('<') >= 0 || name.IndexOf("DisplayClass", StringComparison.Ordinal) >= 0);
        }

        public static bool IsDelegateTypeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name == "Predicate`1" || name.StartsWith("Func`", StringComparison.Ordinal) || name == "Action" || name.StartsWith("Action`", StringComparison.Ordinal) || name.StartsWith("Converter`", StringComparison.Ordinal);
        }
    }
}
