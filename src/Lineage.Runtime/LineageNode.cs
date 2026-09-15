using System.Collections.Generic;

namespace Lineage
{
    public sealed class LineageNode
    {
        private static readonly int[] EmptyIds = new int[0];

        public int Id => ValueId;
        public int ValueId { get; }
        public int LocationId { get; }
        public EventKind Kind { get; }
        public OperationKind Operation { get; }
        public ReportCategory Category { get; }
        public int Parent0 { get; }
        public int Parent1 { get; }
        public IReadOnlyList<int> Parents { get; }
        public IReadOnlyList<int> Children { get; internal set; }
        public string Label { get; }
        public string DisplayName { get; }
        public string Value { get; }
        public bool IsOpaque { get; }
        public string File { get; }
        public int Line { get; }
        public string Method { get; }
        public string Assembly { get; }
        public string TypeName { get; }
        public ValueAvailability ValueAvailability { get; }
        public LineageValueKind ValueKind { get; internal set; }

        public LineageNode(
            int valueId,
            int locationId,
            EventKind kind,
            OperationKind operation,
            int parent0,
            int parent1,
            string label,
            bool isOpaque,
            string value = null)
            : this(valueId, locationId, kind, operation, ReportCategory.None, parent0, parent1, null, label, isOpaque, value, null, 0, null, null, null, ValueAvailability.NotCaptured)
        {
        }

        public LineageNode(
            int valueId,
            int locationId,
            EventKind kind,
            OperationKind operation,
            ReportCategory category,
            int parent0,
            int parent1,
            IReadOnlyList<int> parents,
            string label,
            bool isOpaque,
            string value = null,
            string file = null,
            int line = 0,
            string method = null,
            string assembly = null,
            string typeName = null,
            ValueAvailability valueAvailability = ValueAvailability.NotCaptured)
        {
            ValueId = valueId;
            LocationId = locationId;
            Kind = kind;
            Operation = operation;
            Category = category;
            Label = label ?? string.Empty;
            DisplayName = StripValue(label);
            IsOpaque = isOpaque;
            Value = value ?? string.Empty;
            File = file ?? string.Empty;
            Line = line;
            Method = method ?? string.Empty;
            Assembly = assembly ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            ValueAvailability = string.IsNullOrEmpty(value) && valueAvailability == ValueAvailability.NotCaptured
                ? ValueAvailability.NotCaptured
                : (valueAvailability == ValueAvailability.NotCaptured && !string.IsNullOrEmpty(value)
                    ? ValueAvailability.Available
                    : valueAvailability);
            Parents = NormalizeParents(parent0, parent1, parents);
            Parent0 = Parents.Count > 0 ? Parents[0] : 0;
            Parent1 = Parents.Count > 1 ? Parents[1] : 0;
            Children = EmptyIds;
            ValueKind = LineageValueKind.None;
        }

        public string FormatStepValue()
        {
            switch (ValueAvailability)
            {
                case ValueAvailability.Redacted:
                    return "[PROTECTED]";
                case ValueAvailability.Unavailable:
                    return "[unavailable]";
                case ValueAvailability.NotCaptured:
                    return string.IsNullOrEmpty(Value) ? "[not captured]" : Value;
                default:
                    return string.IsNullOrEmpty(Value) ? "[not captured]" : Value;
            }
        }

        public string TitleWithValue()
        {
            var name = string.IsNullOrEmpty(DisplayName) ? Label : DisplayName;
            if (string.IsNullOrEmpty(name))
            {
                name = Kind.ToString();
            }

            return name + " = " + FormatStepValue();
        }

        internal static string StripValue(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }

            var eq = label.IndexOf(" = ", System.StringComparison.Ordinal);
            return eq > 0 ? label.Substring(0, eq) : label;
        }

        internal static IReadOnlyList<int> NormalizeParents(int parent0, int parent1, IReadOnlyList<int> parents)
        {
            if (parents != null && parents.Count > 0)
            {
                var copy = new List<int>(parents.Count);
                for (var i = 0; i < parents.Count; i++)
                {
                    var id = parents[i];
                    if (id > 0 && !copy.Contains(id))
                    {
                        copy.Add(id);
                    }
                }

                return copy;
            }

            if (parent0 <= 0 && parent1 <= 0)
            {
                return EmptyIds;
            }

            if (parent1 <= 0 || parent1 == parent0)
            {
                return new[] { parent0 };
            }

            if (parent0 <= 0)
            {
                return new[] { parent1 };
            }

            return new[] { parent0, parent1 };
        }
    }
}
