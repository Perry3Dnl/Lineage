using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lineage
{
    public static class MetadataCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LIN2");

        public static byte[] Write(IReadOnlyList<LocationInfo> infos)
        {
            using (var ms = new MemoryStream())
            {
                Write(ms, infos);
                return ms.ToArray();
            }
        }

        public static void Write(Stream stream, IReadOnlyList<LocationInfo> infos)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(infos.Count);
                for (var i = 0; i < infos.Count; i++)
                {
                    var info = infos[i];
                    writer.Write(info.LocationId);
                    writer.Write((byte)info.Kind);
                    writer.Write((byte)info.Operation);
                    writer.Write(info.Line);
                    writer.Write(info.IsOpaque);
                    WriteString(writer, info.MethodName);
                    WriteString(writer, info.File);
                    WriteString(writer, info.LocalName);
                    WriteString(writer, info.CallName);
                    WriteString(writer, info.ReportLabel);
                }
            }
        }

        public static List<LocationInfo> Read(Stream stream)
        {
            var result = new List<LocationInfo>();
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                {
                    return result;
                }

                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    result.Add(new LocationInfo
                    {
                        LocationId = reader.ReadInt32(),
                        Kind = (EventKind)reader.ReadByte(),
                        Operation = (OperationKind)reader.ReadByte(),
                        Line = reader.ReadInt32(),
                        IsOpaque = reader.ReadBoolean(),
                        MethodName = ReadString(reader),
                        File = ReadString(reader),
                        LocalName = ReadString(reader),
                        CallName = ReadString(reader),
                        ReportLabel = ReadString(reader)
                    });
                }
            }

            return result;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value ?? string.Empty);
        }

        private static string ReadString(BinaryReader reader)
        {
            return reader.ReadString();
        }
    }
}
