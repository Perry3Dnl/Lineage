using System.Collections.Generic;
using Lineage;

namespace Lineage.Instrumentation
{
    internal sealed class MetadataBuilder
    {
        private readonly List<LocationInfo> _infos = new List<LocationInfo>();
        private int _nextId = 1;

        public int Add(
            EventKind kind,
            OperationKind operation,
            string methodName,
            string file,
            int line,
            string localName,
            string callName,
            string reportLabel,
            bool opaque)
        {
            var id = _nextId++;
            _infos.Add(new LocationInfo
            {
                LocationId = id,
                Kind = kind,
                Operation = operation,
                MethodName = methodName ?? string.Empty,
                File = file ?? string.Empty,
                Line = line,
                LocalName = localName ?? string.Empty,
                CallName = callName ?? string.Empty,
                ReportLabel = reportLabel ?? string.Empty,
                IsOpaque = opaque
            });
            return id;
        }

        public IReadOnlyList<LocationInfo> Infos => _infos;
    }
}
