using System.Diagnostics;
using Lineage.Internal;

namespace Lineage.Runtime.Tests
{
    public sealed class ColdJournalTests : IDisposable
    {
        private readonly string _tempRoot;

        public ColdJournalTests()
        {
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.ColdStorageEnabledByDefault = true;
            LineageSettings.Reset();
            LineageSettings.PublishToIde = false;
            CaptureScope.Current?.Dispose();
            ColdJournal.WriterDelayMillisecondsForTests = 0;
            _tempRoot = Path.Combine(Path.GetTempPath(), "lineage-tests-" + Guid.NewGuid().ToString("N"));
            LineageSettings.ColdStorageDirectory = _tempRoot;
        }

        public void Dispose()
        {
            CaptureScope.Current?.Dispose();
            ColdJournal.WriterDelayMillisecondsForTests = 0;
            LineageSettings.ColdStorageEnabledByDefault = true;
            LineageSettings.Reset();
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void ColdJournal_IsLazyUntilHotWatermarkIsCrossed()
        {
            LineageSettings.ColdStorageHighWatermarkPercent = 80;
            LineageSettings.ColdStorageTargetPercent = 50;
            LineageSettings.ColdStoragePageSteps = 10;
            LineageSettings.ColdStorageQueuePages = 8;

            using (var scope = CaptureScope.Enter(100))
            {
                for (var i = 0; i < 80; i++)
                {
                    Recorder.Produce(1000 + i, 0, 0, (int)EventKind.Constant);
                }

                Assert.False(scope.Buffer.ColdStorageActive);
                Assert.Equal(80, scope.Buffer.Count);

                Recorder.Produce(1081, 0, 0, (int)EventKind.Constant);

                Assert.True(scope.Buffer.ColdStorageActive);
                Assert.True(scope.Buffer.Count < 80);

                scope.Buffer.CreateSnapshot();
                Assert.True(scope.Buffer.ColdStorageBytes > 0);
                Assert.False(scope.Buffer.Dropped);
            }
        }

        [Fact]
        public void CausalSlice_RehydratesChainAcrossColdAndHotTiers()
        {
            LineageSettings.ColdStorageHighWatermarkPercent = 50;
            LineageSettings.ColdStorageTargetPercent = 25;
            LineageSettings.ColdStoragePageSteps = 8;
            LineageSettings.ColdStorageQueuePages = 64;
            LineageSettings.ColdStorageMaxBytes = 8L * 1024L * 1024L;

            using (var scope = CaptureScope.Enter(64))
            {
                var current = Recorder.Produce(2001, 0, 0, (int)EventKind.Constant);
                Recorder.Remember("origin", current);

                for (var i = 1; i < 200; i++)
                {
                    current = Recorder.Produce(2002, current, 0, (int)EventKind.Call);
                }

                var slice = CausalSlice.Collect(scope.Buffer, current);

                Assert.False(scope.Buffer.Dropped);
                Assert.True(scope.Buffer.ColdStorageActive);
                Assert.True(scope.Buffer.ColdStorageBytes > 0);
                Assert.Equal(200, slice.Count);
                Assert.Equal(1, slice[0].ValueId);
                Assert.Equal("\"origin\"", slice[0].Value);
                Assert.Equal(current, slice[slice.Count - 1].ValueId);
                Assert.Equal(current - 1, slice[slice.Count - 1].Parent0);
            }
        }

        [Fact]
        public void ColdJournal_RollsOldestSegmentsAtHardCapAndReportsTruncation()
        {
            LineageSettings.ColdStorageHighWatermarkPercent = 50;
            LineageSettings.ColdStorageTargetPercent = 25;
            LineageSettings.ColdStoragePageSteps = 8;
            LineageSettings.ColdStorageQueuePages = 128;
            LineageSettings.ColdStorageMaxBytes = 2048;

            using (var scope = CaptureScope.Enter(64))
            {
                var current = Recorder.Produce(3001, 0, 0, (int)EventKind.Constant);
                for (var i = 1; i < 300; i++)
                {
                    current = Recorder.Produce(3002, current, 0, (int)EventKind.Call);
                }

                var slice = CausalSlice.Collect(scope.Buffer, current);
                Assert.True(scope.Buffer.Dropped);
                Assert.True(scope.Buffer.ColdStorageBytes <= LineageSettings.ColdStorageMaxBytes);
                Assert.True(scope.Buffer.ColdStorageSegments > 0);
                Assert.True(slice.Count > 0);
                Assert.True(slice.Count < 300);

                var report = ReportFormatter.Format(slice, scope.Buffer.Dropped, 300, 0, scope);
                Assert.True(report.EventsDropped);
                Assert.Contains("provenance history incomplete", report.ToString());
            }
        }

        [Fact]
        public void SlowWriter_DropsOldHistoryInsteadOfBlockingRecorder()
        {
            LineageSettings.ColdStorageHighWatermarkPercent = 50;
            LineageSettings.ColdStorageTargetPercent = 25;
            LineageSettings.ColdStoragePageSteps = 4;
            LineageSettings.ColdStorageQueuePages = 1;
            LineageSettings.ColdStorageMaxBytes = 16L * 1024L * 1024L;
            ColdJournal.WriterDelayMillisecondsForTests = 100;

            using (var scope = CaptureScope.Enter(64))
            {
                var stopwatch = Stopwatch.StartNew();
                var current = Recorder.Produce(4001, 0, 0, (int)EventKind.Constant);
                for (var i = 1; i < 2000; i++)
                {
                    current = Recorder.Produce(4002, current, 0, (int)EventKind.Call);
                }
                stopwatch.Stop();

                Assert.Equal(2000, current);
                Assert.True(scope.Buffer.Dropped);
                Assert.True(scope.Buffer.Count <= scope.Buffer.Capacity);
                Assert.True(scope.Buffer.ColdStorageQueuedPages <= 2);

                // A synchronous 100 ms write per 4-Step page would take tens of seconds.
                // This deliberately generous bound verifies the recording thread did not
                // wait for the throttled writer while remaining robust on shared CI hosts.
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                    "Recording thread was delayed by cold storage: " + stopwatch.Elapsed);
            }
        }

        [Fact]
        public void DefaultColdJournalBudget_IsOneHundredMiB()
        {
            Assert.True(LineageSettings.ColdStorageEnabled);
            Assert.Equal(100L * 1024L * 1024L, LineageSettings.ColdStorageMaxBytes);
        }
    }
}
