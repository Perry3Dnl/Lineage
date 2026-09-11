using System;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Lineage
{
    public static class ReportTransport
    {
        public const string DefaultPipeName = "Lineage.Report";
        public const string LatestFileName = "latest.json";
        private const int ConnectTimeoutMs = 250;
        private const int MaxBytes = 1024 * 1024;

        public static string PipeName
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable("LINEAGE_PIPE");
                return string.IsNullOrEmpty(configured) ? DefaultPipeName : configured;
            }
        }

        public static string ReportDirectory
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable("LINEAGE_REPORT_DIR");
                if (!string.IsNullOrEmpty(configured))
                {
                    return configured;
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Lineage",
                    "reports");
            }
        }

        public static string LatestReportPath => Path.Combine(ReportDirectory, LatestFileName);

        public static bool TryPublish(LineageReport report)
        {
            if (report == null || !LineageSettings.PublishToIde)
            {
                return false;
            }

            try
            {
                var json = ReportJson.Serialize(report);
                var bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length == 0 || bytes.Length > MaxBytes)
                {
                    return false;
                }

                var wroteFile = TryWriteLatest(json);
                var sentPipe = TrySendPipe(bytes);
                return wroteFile || sentPipe;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryReadLatest(out LineageReport report)
        {
            report = null;
            try
            {
                var path = LatestReportPath;
                if (!File.Exists(path))
                {
                    return false;
                }

                var json = File.ReadAllText(path);
                report = ReportJson.Deserialize(json);
                return report != null && report.Nodes != null && report.Nodes.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRead(Stream stream, out string json)
        {
            json = null;
            if (stream == null)
            {
                return false;
            }

            var header = new byte[4];
            if (!ReadExact(stream, header, 4))
            {
                return false;
            }

            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxBytes)
            {
                return false;
            }

            var buffer = new byte[length];
            if (!ReadExact(stream, buffer, length))
            {
                return false;
            }

            json = Encoding.UTF8.GetString(buffer);
            return true;
        }

        private static bool TryWriteLatest(string json)
        {
            try
            {
                var directory = ReportDirectory;
                Directory.CreateDirectory(directory);
                var latest = Path.Combine(directory, LatestFileName);
                var temp = latest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(latest))
                {
                    File.Replace(temp, latest, null);
                }
                else
                {
                    File.Move(temp, latest);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySendPipe(byte[] bytes)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(ConnectTimeoutMs);
                    var length = BitConverter.GetBytes(bytes.Length);
                    client.Write(length, 0, 4);
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }
    }
}
