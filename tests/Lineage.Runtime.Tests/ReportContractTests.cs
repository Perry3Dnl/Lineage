using System;
using System.IO;
using Lineage.Internal;
using static Lineage.Lineage;

namespace Lineage.Runtime.Tests
{
    public sealed class ReportContractTests : IDisposable
    {
        public ReportContractTests()
        {
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.Reset();
            LineageSettings.PublishToIde = false;
            CaptureScope.Current?.Dispose();
        }

        public void Dispose()
        {
            MetadataRegistry.Clear();
            LineageSettings.Reset();
            LineageSettings.PublishToIde = false;
            CaptureScope.Current?.Dispose();
        }

        [Fact]
        public void Report_HasStructuredContract()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "message", File = "SampleApp.cs", Line = 33, MethodName = "Run" });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember("Bob (Active): 6", id);
                Recorder.SetFocusTarget(id);
                var report = Lineage.Focus("Bob (Active): 6");
                Assert.False(string.IsNullOrEmpty(report.ReportId));
                Assert.Equal(LineageTriggerKind.ManualTrace, report.Trigger.Kind);
                Assert.NotNull(report.Focus);
                Assert.Contains("message", report.ToString());
                Assert.NotNull(report.Focus);
                Assert.NotEmpty(report.Nodes);
                Assert.NotEmpty(report.Edges);
                Assert.NotNull(report.Coverage);
                Assert.Contains(report.Nodes, n => n.File.Contains("SampleApp.cs"));
            }
        }

        [Fact]
        public void Json_RoundTripsReport()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "requestedName" });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember("Bob", id);
                Recorder.SetFocusTarget(id);
                var report = Lineage.Focus("Bob");
                var json = ReportJson.Serialize(report);
                var copy = ReportJson.Deserialize(json);
                Assert.Equal(report.Trigger.Kind, copy.Trigger.Kind);
                Assert.Contains("requestedName", copy.ToString());
                Assert.Equal(report.Nodes.Count, copy.Nodes.Count);
                Assert.Equal(ValueAvailability.Available, copy.Nodes[0].ValueAvailability);
            }
        }

        [Fact]
        public void Transport_WritesLatestReportWithoutListener()
        {
            var dir = Path.Combine(Path.GetTempPath(), "LineageTests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LINEAGE_REPORT_DIR", dir);
            try
            {
                using (CaptureScope.Enter())
                {
                    var id = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                    Recorder.SetFocusTarget(id);
                    LineageSettings.PublishToIde = true;
                    var sent = ReportTransport.TryPublish(Lineage.Focus(1));
                    Assert.True(sent);
                    Assert.True(File.Exists(Path.Combine(dir, ReportTransport.LatestFileName)));
                    Assert.True(ReportTransport.TryReadLatest(out var loaded));
                    Assert.NotNull(loaded);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("LINEAGE_REPORT_DIR", null);
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void MissingValue_IsNotCapturedNotNull()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "secret" });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.SetFocusTarget(id);
                var report = Recorder.CompleteFocus();
                LineageNode node = null;
                for (var i = 0; i < report.Nodes.Count; i++)
                {
                    if (report.Nodes[i].DisplayName == "secret")
                    {
                        node = report.Nodes[i];
                        break;
                    }
                }

                Assert.NotNull(node);
                Assert.Equal(ValueAvailability.NotCaptured, node.ValueAvailability);
                Assert.NotEqual("null", node.Value);
            }
        }
    }
}
