using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lineage
{
    public static class ReportJson
    {
        public static string Serialize(LineageReport report)
        {
            if (report == null)
            {
                return "null";
            }

            var sb = new StringBuilder();
            sb.Append('{');
            Write(sb, "reportId", report.ReportId);
            sb.Append(',');
            Write(sb, "timestamp", report.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"trigger\":");
            WriteTrigger(sb, report.Trigger);
            sb.Append(',');
            Write(sb, "scope", report.Scope);
            sb.Append(",\"coverage\":{");
            WriteRaw(sb, "eventsDropped", report.Coverage != null && report.Coverage.EventsDropped);
            sb.Append(',');
            WriteRaw(sb, "eventCount", report.EventCount);
            sb.Append(',');
            WriteRaw(sb, "opaqueNodeCount", report.Coverage != null ? report.Coverage.OpaqueNodeCount : 0);
            sb.Append(',');
            WriteRaw(sb, "hasUnknownProvenance", report.Coverage != null && report.Coverage.HasUnknownProvenance);
            sb.Append("},\"focusId\":");
            sb.Append(report.Focus != null ? report.Focus.ValueId.ToString(CultureInfo.InvariantCulture) : "0");
            sb.Append(",\"nodes\":");
            WriteNodes(sb, report.Nodes);
            sb.Append(",\"rawNodes\":");
            WriteNodes(sb, report.RawNodes);
            sb.Append(",\"edges\":[");
            for (var i = 0; i < report.Edges.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"from\":");
                sb.Append(report.Edges[i].From.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"to\":");
                sb.Append(report.Edges[i].To.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        public static LineageReport Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null")
            {
                return new LineageReport(new LineageNode[0]);
            }

            var reader = new Reader(json);
            reader.SkipWs();
            return ReadReport(reader);
        }

        private static void WriteNodes(StringBuilder sb, IReadOnlyList<LineageNode> nodes)
        {
            sb.Append('[');
            if (nodes != null)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteNode(sb, nodes[i]);
                }
            }

            sb.Append(']');
        }

        private static void WriteNode(StringBuilder sb, LineageNode node)
        {
            sb.Append('{');
            WriteRaw(sb, "id", node.ValueId);
            sb.Append(',');
            WriteRaw(sb, "locationId", node.LocationId);
            sb.Append(',');
            Write(sb, "kind", node.Kind.ToString());
            sb.Append(',');
            Write(sb, "operation", node.Operation.ToString());
            sb.Append(',');
            Write(sb, "category", node.Category.ToString());
            sb.Append(',');
            Write(sb, "displayName", node.DisplayName);
            sb.Append(',');
            Write(sb, "label", node.Label);
            sb.Append(',');
            Write(sb, "value", node.Value);
            sb.Append(',');
            Write(sb, "valueAvailability", node.ValueAvailability.ToString());
            sb.Append(',');
            Write(sb, "type", node.TypeName);
            sb.Append(',');
            Write(sb, "file", node.File);
            sb.Append(',');
            WriteRaw(sb, "line", node.Line);
            sb.Append(',');
            Write(sb, "method", node.Method);
            sb.Append(',');
            Write(sb, "assembly", node.Assembly);
            sb.Append(',');
            WriteRaw(sb, "opaque", node.IsOpaque);
            sb.Append(",\"parents\":[");
            WriteIds(sb, node.Parents);
            sb.Append("],\"children\":[");
            WriteIds(sb, node.Children);
            sb.Append("]}");
        }

        private static void WriteTrigger(StringBuilder sb, LineageTrigger trigger)
        {
            trigger = trigger ?? LineageTrigger.None;
            sb.Append('{');
            Write(sb, "kind", trigger.Kind.ToString());
            sb.Append(',');
            Write(sb, "title", trigger.Title);
            sb.Append(',');
            Write(sb, "detail", trigger.Detail);
            sb.Append(',');
            Write(sb, "file", trigger.File);
            sb.Append(',');
            WriteRaw(sb, "line", trigger.Line);
            sb.Append('}');
        }

        private static void WriteIds(StringBuilder sb, IReadOnlyList<int> ids)
        {
            if (ids == null)
            {
                return;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(ids[i].ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void Write(StringBuilder sb, string name, string value)
        {
            sb.Append('"');
            sb.Append(name);
            sb.Append("\":");
            Quote(sb, value);
        }

        private static void WriteRaw(StringBuilder sb, string name, long value)
        {
            sb.Append('"');
            sb.Append(name);
            sb.Append("\":");
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteRaw(StringBuilder sb, string name, bool value)
        {
            sb.Append('"');
            sb.Append(name);
            sb.Append("\":");
            sb.Append(value ? "true" : "false");
        }

        private static void Quote(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\');
                    sb.Append(c);
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
        }

        private static LineageReport ReadReport(Reader reader)
        {
            reader.Expect('{');
            string reportId = string.Empty;
            var timestamp = DateTime.UtcNow;
            var trigger = LineageTrigger.None;
            var scope = "default";
            var dropped = false;
            long eventCount = 0;
            IReadOnlyList<LineageNode> nodes = new LineageNode[0];
            IReadOnlyList<LineageNode> raw = null;
            while (!reader.Take('}'))
            {
                var key = reader.ReadString();
                reader.Expect(':');
                if (key == "reportId")
                {
                    reportId = reader.ReadString();
                }
                else if (key == "timestamp")
                {
                    DateTime parsed;
                    DateTime.TryParse(reader.ReadString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
                    timestamp = parsed == default(DateTime) ? DateTime.UtcNow : parsed;
                }
                else if (key == "trigger")
                {
                    trigger = ReadTrigger(reader);
                }
                else if (key == "scope")
                {
                    scope = reader.ReadString();
                }
                else if (key == "coverage")
                {
                    ReadCoverage(reader, out dropped, out eventCount);
                }
                else if (key == "nodes")
                {
                    nodes = ReadNodes(reader);
                }
                else if (key == "rawNodes")
                {
                    raw = ReadNodes(reader);
                }
                else
                {
                    reader.SkipValue();
                }

                reader.Take(',');
            }

            return new LineageReport(reportId, timestamp, trigger, nodes, raw ?? nodes, dropped, eventCount, 0, scope);
        }

        private static void ReadCoverage(Reader reader, out bool dropped, out long eventCount)
        {
            dropped = false;
            eventCount = 0;
            reader.Expect('{');
            while (!reader.Take('}'))
            {
                var key = reader.ReadString();
                reader.Expect(':');
                if (key == "eventsDropped")
                {
                    dropped = reader.ReadBoolean();
                }
                else if (key == "eventCount")
                {
                    eventCount = reader.ReadLong();
                }
                else
                {
                    reader.SkipValue();
                }

                reader.Take(',');
            }
        }

        private static LineageTrigger ReadTrigger(Reader reader)
        {
            reader.Expect('{');
            var kind = LineageTriggerKind.None;
            var title = string.Empty;
            var detail = string.Empty;
            var file = string.Empty;
            var line = 0;
            while (!reader.Take('}'))
            {
                var key = reader.ReadString();
                reader.Expect(':');
                if (key == "kind")
                {
                    Enum.TryParse(reader.ReadString(), out kind);
                }
                else if (key == "title")
                {
                    title = reader.ReadString();
                }
                else if (key == "detail")
                {
                    detail = reader.ReadString();
                }
                else if (key == "file")
                {
                    file = reader.ReadString();
                }
                else if (key == "line")
                {
                    line = (int)reader.ReadLong();
                }
                else
                {
                    reader.SkipValue();
                }

                reader.Take(',');
            }

            return new LineageTrigger(kind, title, detail, file, line);
        }

        private static IReadOnlyList<LineageNode> ReadNodes(Reader reader)
        {
            var list = new List<LineageNode>();
            reader.Expect('[');
            while (!reader.Take(']'))
            {
                list.Add(ReadNode(reader));
                reader.Take(',');
            }

            return list;
        }

        private static LineageNode ReadNode(Reader reader)
        {
            reader.Expect('{');
            var id = 0;
            var locationId = 0;
            var kind = EventKind.None;
            var operation = OperationKind.None;
            var category = ReportCategory.None;
            var label = string.Empty;
            var value = string.Empty;
            var file = string.Empty;
            var line = 0;
            var method = string.Empty;
            var assembly = string.Empty;
            var typeName = string.Empty;
            var opaque = false;
            var availability = ValueAvailability.NotCaptured;
            IReadOnlyList<int> parents = new int[0];
            while (!reader.Take('}'))
            {
                var key = reader.ReadString();
                reader.Expect(':');
                if (key == "id")
                {
                    id = (int)reader.ReadLong();
                }
                else if (key == "locationId")
                {
                    locationId = (int)reader.ReadLong();
                }
                else if (key == "kind")
                {
                    Enum.TryParse(reader.ReadString(), out kind);
                }
                else if (key == "operation")
                {
                    Enum.TryParse(reader.ReadString(), out operation);
                }
                else if (key == "category")
                {
                    Enum.TryParse(reader.ReadString(), out category);
                }
                else if (key == "label")
                {
                    label = reader.ReadString();
                }
                else if (key == "value")
                {
                    value = reader.ReadString();
                }
                else if (key == "valueAvailability")
                {
                    Enum.TryParse(reader.ReadString(), out availability);
                }
                else if (key == "type")
                {
                    typeName = reader.ReadString();
                }
                else if (key == "file")
                {
                    file = reader.ReadString();
                }
                else if (key == "line")
                {
                    line = (int)reader.ReadLong();
                }
                else if (key == "method")
                {
                    method = reader.ReadString();
                }
                else if (key == "assembly")
                {
                    assembly = reader.ReadString();
                }
                else if (key == "opaque")
                {
                    opaque = reader.ReadBoolean();
                }
                else if (key == "parents")
                {
                    parents = reader.ReadIntArray();
                }
                else
                {
                    reader.SkipValue();
                }

                reader.Take(',');
            }

            return new LineageNode(id, locationId, kind, operation, category, 0, 0, parents, label, opaque, value, file, line, method, assembly, typeName, availability);
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _i;

            public Reader(string text)
            {
                _text = text ?? string.Empty;
            }

            public void SkipWs()
            {
                while (_i < _text.Length && char.IsWhiteSpace(_text[_i]))
                {
                    _i++;
                }
            }

            public void Expect(char c)
            {
                SkipWs();
                if (_i >= _text.Length || _text[_i] != c)
                {
                    throw new FormatException("Expected " + c);
                }

                _i++;
                SkipWs();
            }

            public bool Take(char c)
            {
                SkipWs();
                if (_i < _text.Length && _text[_i] == c)
                {
                    _i++;
                    SkipWs();
                    return true;
                }

                return false;
            }

            public string ReadString()
            {
                SkipWs();
                if (TakeLiteral("null"))
                {
                    return string.Empty;
                }

                Expect('"');
                var sb = new StringBuilder();
                while (_i < _text.Length)
                {
                    var c = _text[_i++];
                    if (c == '"')
                    {
                        break;
                    }

                    if (c == '\\' && _i < _text.Length)
                    {
                        var n = _text[_i++];
                        sb.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                SkipWs();
                return sb.ToString();
            }

            public long ReadLong()
            {
                SkipWs();
                var start = _i;
                if (_i < _text.Length && _text[_i] == '-')
                {
                    _i++;
                }

                while (_i < _text.Length && char.IsDigit(_text[_i]))
                {
                    _i++;
                }

                long value;
                long.TryParse(_text.Substring(start, _i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                SkipWs();
                return value;
            }

            public bool ReadBoolean()
            {
                SkipWs();
                if (TakeLiteral("true"))
                {
                    return true;
                }

                TakeLiteral("false");
                return false;
            }

            public IReadOnlyList<int> ReadIntArray()
            {
                var list = new List<int>();
                Expect('[');
                while (!Take(']'))
                {
                    list.Add((int)ReadLong());
                    Take(',');
                }

                return list;
            }

            public void SkipValue()
            {
                SkipWs();
                if (_i >= _text.Length)
                {
                    return;
                }

                var c = _text[_i];
                if (c == '"')
                {
                    ReadString();
                    return;
                }

                if (c == '{')
                {
                    _i++;
                    var depth = 1;
                    while (_i < _text.Length && depth > 0)
                    {
                        if (_text[_i] == '"')
                        {
                            ReadString();
                            continue;
                        }

                        if (_text[_i] == '{')
                        {
                            depth++;
                        }
                        else if (_text[_i] == '}')
                        {
                            depth--;
                        }

                        _i++;
                    }

                    SkipWs();
                    return;
                }

                if (c == '[')
                {
                    _i++;
                    var depth = 1;
                    while (_i < _text.Length && depth > 0)
                    {
                        if (_text[_i] == '"')
                        {
                            ReadString();
                            continue;
                        }

                        if (_text[_i] == '[')
                        {
                            depth++;
                        }
                        else if (_text[_i] == ']')
                        {
                            depth--;
                        }

                        _i++;
                    }

                    SkipWs();
                    return;
                }

                while (_i < _text.Length && _text[_i] != ',' && _text[_i] != '}' && _text[_i] != ']')
                {
                    _i++;
                }

                SkipWs();
            }

            private bool TakeLiteral(string literal)
            {
                SkipWs();
                if (_i + literal.Length <= _text.Length && string.CompareOrdinal(_text, _i, literal, 0, literal.Length) == 0)
                {
                    _i += literal.Length;
                    SkipWs();
                    return true;
                }

                return false;
            }
        }
    }
}
