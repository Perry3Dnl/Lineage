using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lineage;
using Lineage.Internal;

namespace Lineage.ColdStorage.Stress;

internal static class Program
{
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        var root = Path.Combine(Path.GetTempPath(), "lineage-cold-stress-" + Guid.NewGuid().ToString("N"));

        Console.WriteLine("Lineage cold-storage stress harness");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Live-chain events: {options.Events:N0}");
        Console.WriteLine();

        Result normal;
        Result throttled;
        Result capped;
        try
        {
            normal = RunNormal(options.Events, root);
            throttled = RunThrottled(options.ThrottledEvents, root);
            capped = RunCapped(options.CappedEvents, root);
        }
        finally
        {
            ColdJournal.WriterDelayMillisecondsForTests = 0;
            CaptureScope.Current?.Dispose();
        }

        Print(normal);
        Print(throttled);
        Print(capped);

        var report = new Report
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Normal = normal,
            Throttled = throttled,
            Capped = capped,
            Passed = normal.Passed && throttled.Passed && capped.Passed
        };

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            var path = Path.GetFullPath(options.ReportPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Report: {path}");
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
        }

        Console.WriteLine(report.Passed ? "VERDICT: PASS" : "VERDICT: FAIL");
        return report.Passed ? 0 : 1;
    }

    private static Result RunNormal(int events, string root)
    {
        // The normal case gets a larger but still bounded queue so this scenario measures
        // sustainable sequential disk throughput, not the overload behavior tested below.
        Configure(root, capacity: 65_536, maxBytes: 100L * 1024L * 1024L, pageSteps: 4096, queuePages: 32);
        using var scope = CaptureScope.Enter(65_536);

        var maxHot = 0;
        var stopwatch = Stopwatch.StartNew();
        var current = Recorder.Produce(51001, 0, 0, (int)EventKind.Constant);
        for (var i = 1; i < events; i++)
        {
            current = Recorder.Produce(51002, current, 0, (int)EventKind.Call);
            if ((i & 1023) == 0)
            {
                maxHot = Math.Max(maxHot, scope.Buffer.Count);
            }
        }
        stopwatch.Stop();
        maxHot = Math.Max(maxHot, scope.Buffer.Count);

        var hydrate = Stopwatch.StartNew();
        var snapshot = scope.Buffer.CreateSnapshot();
        hydrate.Stop();

        var passed = current == events
            && !scope.Buffer.Dropped
            && snapshot.Steps.Length == events
            && snapshot.Relations.Length == Math.Max(0, events - 1)
            && scope.Buffer.ColdStorageBytes <= LineageSettings.ColdStorageMaxBytes
            && maxHot <= scope.Buffer.Capacity;

        return Build(
            "normal-spill",
            events,
            stopwatch.Elapsed,
            hydrate.Elapsed,
            maxHot,
            scope.Buffer.Count,
            scope.Buffer.ColdStorageBytes,
            scope.Buffer.ColdStorageSegments,
            scope.Buffer.ColdStorageQueuedPages,
            scope.Buffer.Dropped,
            passed,
            "Live causal chain spills only after RAM pressure and is fully rehydrated from cold + hot tiers.");
    }

    private static Result RunThrottled(int events, string root)
    {
        Configure(root, capacity: 8192, maxBytes: 100L * 1024L * 1024L, pageSteps: 512, queuePages: 1);
        ColdJournal.WriterDelayMillisecondsForTests = 100;
        using var scope = CaptureScope.Enter(8192);

        var maxHot = 0;
        var stopwatch = Stopwatch.StartNew();
        var current = Recorder.Produce(52001, 0, 0, (int)EventKind.Constant);
        for (var i = 1; i < events; i++)
        {
            current = Recorder.Produce(52002, current, 0, (int)EventKind.Call);
            if ((i & 255) == 0)
            {
                maxHot = Math.Max(maxHot, scope.Buffer.Count);
            }
        }
        stopwatch.Stop();
        maxHot = Math.Max(maxHot, scope.Buffer.Count);

        // A blocking implementation would spend roughly 100 ms on every page write and
        // take many seconds here. The generous two-second ceiling is a regression guard,
        // not a throughput target.
        var passed = current == events
            && scope.Buffer.Dropped
            && scope.Buffer.Count <= scope.Buffer.Capacity
            && scope.Buffer.ColdStorageQueuedPages <= 2
            && stopwatch.Elapsed < TimeSpan.FromSeconds(2);

        var result = Build(
            "throttled-writer",
            events,
            stopwatch.Elapsed,
            TimeSpan.Zero,
            maxHot,
            scope.Buffer.Count,
            scope.Buffer.ColdStorageBytes,
            scope.Buffer.ColdStorageSegments,
            scope.Buffer.ColdStorageQueuedPages,
            scope.Buffer.Dropped,
            passed,
            "Writer is artificially delayed by 100 ms/page. The recorder must keep running and truncate old provenance instead of waiting.");

        ColdJournal.WriterDelayMillisecondsForTests = 0;
        return result;
    }

    private static Result RunCapped(int events, string root)
    {
        const long cap = 1L * 1024L * 1024L;
        Configure(root, capacity: 8192, maxBytes: cap, pageSteps: 512, queuePages: 256);
        using var scope = CaptureScope.Enter(8192);

        var maxHot = 0;
        var stopwatch = Stopwatch.StartNew();
        var current = Recorder.Produce(53001, 0, 0, (int)EventKind.Constant);
        for (var i = 1; i < events; i++)
        {
            current = Recorder.Produce(53002, current, 0, (int)EventKind.Call);
            if ((i & 255) == 0)
            {
                maxHot = Math.Max(maxHot, scope.Buffer.Count);
            }
        }
        stopwatch.Stop();
        maxHot = Math.Max(maxHot, scope.Buffer.Count);

        var hydrate = Stopwatch.StartNew();
        var snapshot = scope.Buffer.CreateSnapshot();
        hydrate.Stop();

        var latestPresent = snapshot.Steps.Length > 0 && snapshot.Steps[snapshot.Steps.Length - 1].Id == current;
        var passed = current == events
            && scope.Buffer.Dropped
            && scope.Buffer.ColdStorageBytes <= cap
            && latestPresent
            && scope.Buffer.Count <= scope.Buffer.Capacity;

        return Build(
            "rolling-disk-cap",
            events,
            stopwatch.Elapsed,
            hydrate.Elapsed,
            maxHot,
            scope.Buffer.Count,
            scope.Buffer.ColdStorageBytes,
            scope.Buffer.ColdStorageSegments,
            scope.Buffer.ColdStorageQueuedPages,
            scope.Buffer.Dropped,
            passed,
            "Uses a 1 MiB cap to force deletion of oldest immutable segments while preserving recent provenance and the hard disk budget.");
    }

    private static void Configure(string root, int capacity, long maxBytes, int pageSteps, int queuePages)
    {
        CaptureScope.Current?.Dispose();
        ColdJournal.WriterDelayMillisecondsForTests = 0;
        LineageSettings.ColdStorageEnabledByDefault = true;
        LineageSettings.Reset();
        LineageSettings.PublishToIde = false;
        LineageSettings.WriteReportToConsole = false;
        LineageSettings.ColdStorageEnabled = true;
        LineageSettings.ColdStorageDirectory = root;
        LineageSettings.ColdStorageMaxBytes = maxBytes;
        LineageSettings.ColdStorageHighWatermarkPercent = 70;
        LineageSettings.ColdStorageTargetPercent = 50;
        LineageSettings.ColdStoragePageSteps = Math.Min(pageSteps, capacity);
        LineageSettings.ColdStorageQueuePages = queuePages;
        LineageMetrics.Reset();
    }

    private static Result Build(
        string name,
        int events,
        TimeSpan recording,
        TimeSpan hydration,
        int maxHot,
        int finalHot,
        long coldBytes,
        int segments,
        int queuedPages,
        bool truncated,
        bool passed,
        string notes)
    {
        return new Result
        {
            Name = name,
            Events = events,
            RecordingMilliseconds = recording.TotalMilliseconds,
            EventsPerSecond = recording.TotalSeconds > 0 ? events / recording.TotalSeconds : 0,
            HydrationMilliseconds = hydration.TotalMilliseconds,
            MaxHotSteps = maxHot,
            FinalHotSteps = finalHot,
            ColdBytes = coldBytes,
            ColdSegments = segments,
            QueuedPages = queuedPages,
            HistoryTruncated = truncated,
            Passed = passed,
            Notes = notes
        };
    }

    private static void Print(Result result)
    {
        Console.WriteLine(result.Passed ? $"[PASS] {result.Name}" : $"[FAIL] {result.Name}");
        Console.WriteLine($"  events: {result.Events:N0}");
        Console.WriteLine($"  recording: {result.RecordingMilliseconds:N1} ms ({result.EventsPerSecond:N0} events/s)");
        if (result.HydrationMilliseconds > 0)
        {
            Console.WriteLine($"  hydration: {result.HydrationMilliseconds:N1} ms");
        }
        Console.WriteLine($"  hot peak/final: {result.MaxHotSteps:N0}/{result.FinalHotSteps:N0} steps");
        Console.WriteLine($"  cold: {FormatBytes(result.ColdBytes)}, {result.ColdSegments:N0} segments, {result.QueuedPages:N0} queued/writing pages");
        Console.WriteLine($"  truncated: {result.HistoryTruncated}");
        Console.WriteLine($"  {result.Notes}");
        Console.WriteLine();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d)).ToString("N2") + " MiB";
        }
        if (bytes >= 1024)
        {
            return (bytes / 1024d).ToString("N2") + " KiB";
        }
        return bytes + " B";
    }

    internal sealed class Options
    {
        public int Events { get; set; } = 250_000;
        public int ThrottledEvents { get; set; } = 20_000;
        public int CappedEvents { get; set; } = 100_000;
        public string? ReportPath { get; set; }

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--events" when i + 1 < args.Length:
                        result.Events = int.Parse(args[++i]);
                        break;
                    case "--throttled-events" when i + 1 < args.Length:
                        result.ThrottledEvents = int.Parse(args[++i]);
                        break;
                    case "--capped-events" when i + 1 < args.Length:
                        result.CappedEvents = int.Parse(args[++i]);
                        break;
                    case "--report" when i + 1 < args.Length:
                        result.ReportPath = args[++i];
                        break;
                }
            }
            return result;
        }
    }

    internal sealed class Result
    {
        public string Name { get; set; } = string.Empty;
        public int Events { get; set; }
        public double RecordingMilliseconds { get; set; }
        public double EventsPerSecond { get; set; }
        public double HydrationMilliseconds { get; set; }
        public int MaxHotSteps { get; set; }
        public int FinalHotSteps { get; set; }
        public long ColdBytes { get; set; }
        public int ColdSegments { get; set; }
        public int QueuedPages { get; set; }
        public bool HistoryTruncated { get; set; }
        public bool Passed { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    internal sealed class Report
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string Runtime { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public Result Normal { get; set; } = new();
        public Result Throttled { get; set; } = new();
        public Result Capped { get; set; } = new();
        public bool Passed { get; set; }
    }
}
