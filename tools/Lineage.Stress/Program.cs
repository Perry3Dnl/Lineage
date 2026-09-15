using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lineage;
using Lineage.Internal;

namespace Lineage.Stress;

internal static class Program
{
    private const long RollingRoot = 1;

    public static int Main(string[] args)
    {
        var options = StressOptions.Parse(args);
        ConfigureRuntime();
        WarmUp(options);

        Console.WriteLine("Lineage runtime feasibility harness");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Events: {options.Events:N0}, value events: {options.ValueEvents:N0}, objects: {options.Objects:N0}");
        Console.WriteLine($"Capacity: {options.Capacity:N0} steps, collection batch: {options.Batch:N0} steps");
        Console.WriteLine();

        var report = new StressReport
        {
            Version = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            Options = options,
            RawReclaimable = RunRolling("raw-reclaimable", options.Events, options, captureValues: false),
            ValueCapture = RunRolling("value-capture", options.ValueEvents, options, captureValues: true),
            ObjectChurn = RunObjectChurn(options),
            LiveChain = RunLiveChain(options)
        };

        report.Passed = report.RawReclaimable.Passed
            && report.ValueCapture.Passed
            && report.ObjectChurn.Passed
            && report.LiveChain.Passed;

        Print(report.RawReclaimable);
        Print(report.ValueCapture);
        Print(report.ObjectChurn);
        Print(report.LiveChain);

        Console.WriteLine();
        if (report.LiveChain.ColdStorageRequired)
        {
            Console.WriteLine("Finding: a continuously growing live causal chain exhausts bounded RAM as expected.");
            Console.WriteLine("Full retrospective history therefore requires cold storage, paging, or an explicit checkpoint policy.");
        }

        Console.WriteLine(report.Passed ? "VERDICT: PASS" : "VERDICT: FAIL");

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            var fullPath = Path.GetFullPath(options.ReportPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            Console.WriteLine($"Report: {fullPath}");
        }

        return report.Passed ? 0 : 1;
    }

    private static void ConfigureRuntime()
    {
        CaptureScope.Current?.Dispose();
        LineageSettings.Reset();
        LineageSettings.PublishToIde = false;
        LineageSettings.WriteReportToConsole = false;
        LineageMetrics.Reset();
    }

    private static void WarmUp(StressOptions options)
    {
        var capacity = Math.Max(64, Math.Min(options.Capacity, 4096));
        var batch = Math.Max(16, Math.Min(options.Batch, capacity - 4));

        using (var scope = CaptureScope.Enter(capacity))
        {
            var produced = 0;
            while (produced < Math.Min(20_000, capacity * 2))
            {
                var origin = Recorder.Produce(9001, 0, 0, (int)EventKind.Constant);
                produced++;
                var current = origin;
                if (produced < Math.Min(20_000, capacity * 2))
                {
                    current = Recorder.Produce(9002, origin, 0, (int)EventKind.Call);
                    produced++;
                }

                scope.SetRoot(RollingRoot, current);
                if ((produced % batch) == 0)
                {
                    scope.CollectGarbage();
                }
            }

            scope.CollectGarbage();
        }

        LineageMetrics.Reset();
        ForceFullGc();
    }

    private static ScenarioResult RunRolling(string name, int eventCount, StressOptions options, bool captureValues)
    {
        ConfigureRuntime();
        using var scope = CaptureScope.Enter(options.Capacity);
        ForceFullGc();

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        var produced = 0;
        var sinceCollection = 0;
        var collections = 0;
        var collectionTicks = 0L;
        var maxSteps = 0;
        var maxRelations = 0;
        var stopwatch = Stopwatch.StartNew();

        while (produced < eventCount && !LineageMetrics.EventsDropped)
        {
            var origin = Recorder.Produce(10001, 0, 0, (int)EventKind.Constant);
            produced++;
            sinceCollection++;
            if (captureValues)
            {
                Recorder.Remember(produced, origin);
            }

            var current = origin;
            if (produced < eventCount && !LineageMetrics.EventsDropped)
            {
                current = Recorder.Produce(10002, origin, 0, (int)EventKind.Call);
                produced++;
                sinceCollection++;
                if (captureValues)
                {
                    Recorder.Remember(produced, current);
                }
            }

            scope.SetRoot(RollingRoot, current);
            maxSteps = Math.Max(maxSteps, scope.Buffer.Count);
            maxRelations = Math.Max(maxRelations, scope.Buffer.RelationCount);

            if (sinceCollection >= options.Batch)
            {
                collectionTicks += Collect(scope);
                collections++;
                sinceCollection = 0;
            }
        }

        collectionTicks += Collect(scope);
        collections++;
        stopwatch.Stop();

        maxSteps = Math.Max(maxSteps, scope.Buffer.Count);
        maxRelations = Math.Max(maxRelations, scope.Buffer.RelationCount);

        ForceFullGc();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        var retainedGrowth = heapAfter - heapBefore;
        var retainedLimit = options.MaxRetainedGrowthMb * 1024L * 1024L;

        var finalSteps = scope.Buffer.Count;
        var finalRelations = scope.Buffer.RelationCount;
        var structuralPass = !scope.Buffer.Dropped
            && produced == eventCount
            && finalSteps <= 2
            && finalRelations <= 1
            && maxSteps <= options.Capacity;
        var memoryPass = retainedGrowth <= retainedLimit;

        return BuildResult(
            name,
            produced,
            stopwatch.ElapsedTicks,
            collectionTicks,
            collections,
            heapBefore,
            heapAfter,
            workingSetBefore,
            workingSetAfter,
            allocatedAfter - allocatedBefore,
            gen0Before,
            gen1Before,
            gen2Before,
            maxSteps,
            maxRelations,
            finalSteps,
            finalRelations,
            scope.RootCount,
            scope.TrackedObjectCount,
            scope.Buffer.Dropped,
            structuralPass && memoryPass,
            structuralPass,
            memoryPass,
            coldStorageRequired: false,
            notes: captureValues
                ? "Exercises current Remember/value-preview path as well as Step/Relation capture."
                : "Exercises compact Step/Relation capture with repeated reclamation and a single rolling root.");
    }

    private static ScenarioResult RunObjectChurn(StressOptions options)
    {
        ConfigureRuntime();
        using var scope = CaptureScope.Enter(options.Capacity);
        ForceFullGc();

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        var batchObjects = Math.Max(1, Math.Min(options.ObjectBatch, Math.Max(1, (options.Capacity - 4) / 2)));
        var created = 0;
        var collections = 0;
        var collectionTicks = 0L;
        var maxSteps = 0;
        var maxRelations = 0;
        var maxWeakAliveAfterGc = 0;
        var stopwatch = Stopwatch.StartNew();

        while (created < options.Objects && !LineageMetrics.EventsDropped)
        {
            var count = Math.Min(batchObjects, options.Objects - created);
            var weak = new WeakReference[count];
            for (var i = 0; i < count; i++)
            {
                weak[i] = CreateTrackedObject(created + i);
            }

            created += count;
            maxSteps = Math.Max(maxSteps, scope.Buffer.Count);
            maxRelations = Math.Max(maxRelations, scope.Buffer.RelationCount);

            var alive = ForceDead(weak);
            maxWeakAliveAfterGc = Math.Max(maxWeakAliveAfterGc, alive);
            collectionTicks += Collect(scope);
            collections++;
        }

        collectionTicks += Collect(scope);
        collections++;
        stopwatch.Stop();

        ForceFullGc();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        var retainedGrowth = heapAfter - heapBefore;
        var retainedLimit = options.MaxRetainedGrowthMb * 1024L * 1024L;

        var finalSteps = scope.Buffer.Count;
        var finalRelations = scope.Buffer.RelationCount;
        var structuralPass = !scope.Buffer.Dropped
            && created == options.Objects
            && finalSteps == 0
            && finalRelations == 0
            && scope.RootCount == 0
            && scope.TrackedObjectCount == 0
            && maxWeakAliveAfterGc == 0
            && maxSteps <= options.Capacity;
        var memoryPass = retainedGrowth <= retainedLimit;

        var result = BuildResult(
            "object-churn",
            created * 2L,
            stopwatch.ElapsedTicks,
            collectionTicks,
            collections,
            heapBefore,
            heapAfter,
            workingSetBefore,
            workingSetAfter,
            allocatedAfter - allocatedBefore,
            gen0Before,
            gen1Before,
            gen2Before,
            maxSteps,
            maxRelations,
            finalSteps,
            finalRelations,
            scope.RootCount,
            scope.TrackedObjectCount,
            scope.Buffer.Dropped,
            structuralPass && memoryPass,
            structuralPass,
            memoryPass,
            coldStorageRequired: false,
            notes: $"Creates {created:N0} short-lived tracked objects in batches of {batchObjects:N0}; max weak survivors after forced GC: {maxWeakAliveAfterGc}.");
        result.ObjectsProcessed = created;
        result.MaxWeakAliveAfterGc = maxWeakAliveAfterGc;
        return result;
    }

    private static ScenarioResult RunLiveChain(StressOptions options)
    {
        ConfigureRuntime();
        using var scope = CaptureScope.Enter(options.Capacity);
        ForceFullGc();

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        var target = options.Capacity + 1;
        var produced = 0;
        var collections = 0;
        var collectionTicks = 0L;
        var maxSteps = 0;
        var maxRelations = 0;
        var stopwatch = Stopwatch.StartNew();

        var current = Recorder.Produce(20001, 0, 0, (int)EventKind.Constant);
        produced++;
        scope.SetRoot(RollingRoot, current);

        while (produced < target && !LineageMetrics.EventsDropped)
        {
            var next = Recorder.Produce(20002, current, 0, (int)EventKind.Call);
            produced++;
            if (LineageMetrics.EventsDropped)
            {
                break;
            }

            current = next;
            scope.SetRoot(RollingRoot, current);
            maxSteps = Math.Max(maxSteps, scope.Buffer.Count);
            maxRelations = Math.Max(maxRelations, scope.Buffer.RelationCount);

            if ((produced % options.Batch) == 0)
            {
                collectionTicks += Collect(scope);
                collections++;
            }
        }

        stopwatch.Stop();
        maxSteps = Math.Max(maxSteps, scope.Buffer.Count);
        maxRelations = Math.Max(maxRelations, scope.Buffer.RelationCount);

        ForceFullGc();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        var droppedAtBoundary = scope.Buffer.Dropped
            && scope.Buffer.Count == options.Capacity
            && produced == target;

        return BuildResult(
            "live-growing-chain",
            produced,
            stopwatch.ElapsedTicks,
            collectionTicks,
            collections,
            heapBefore,
            heapAfter,
            workingSetBefore,
            workingSetAfter,
            allocatedAfter - allocatedBefore,
            gen0Before,
            gen1Before,
            gen2Before,
            maxSteps,
            maxRelations,
            scope.Buffer.Count,
            scope.Buffer.RelationCount,
            scope.RootCount,
            scope.TrackedObjectCount,
            scope.Buffer.Dropped,
            passed: droppedAtBoundary,
            structuralPass: droppedAtBoundary,
            memoryPass: true,
            coldStorageRequired: droppedAtBoundary,
            notes: "Every new live value depends on the previous value, so all ancestry remains reachable. Filling the bounded buffer here is expected and proves GC alone cannot provide unlimited retrospective history.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateTrackedObject(int seed)
    {
        var target = new object();
        var source = Recorder.Produce(30001, 0, 0, (int)EventKind.Constant);
        Recorder.FieldWrite(target, 30002, 77, source);
        return new WeakReference(target);
    }

    private static int ForceDead(WeakReference[] weak)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ForceFullGc();
            var alive = CountAlive(weak);
            if (alive == 0)
            {
                return 0;
            }
        }

        return CountAlive(weak);
    }

    private static int CountAlive(WeakReference[] weak)
    {
        var alive = 0;
        for (var i = 0; i < weak.Length; i++)
        {
            if (weak[i].IsAlive)
            {
                alive++;
            }
        }

        return alive;
    }

    private static long Collect(CaptureScope scope)
    {
        var start = Stopwatch.GetTimestamp();
        scope.CollectGarbage();
        return Stopwatch.GetTimestamp() - start;
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static ScenarioResult BuildResult(
        string name,
        long logicalSteps,
        long elapsedTicks,
        long collectionTicks,
        int collections,
        long heapBefore,
        long heapAfter,
        long workingSetBefore,
        long workingSetAfter,
        long allocatedBytes,
        int gen0Before,
        int gen1Before,
        int gen2Before,
        int maxSteps,
        int maxRelations,
        int finalSteps,
        int finalRelations,
        int finalRoots,
        int finalTrackedObjects,
        bool dropped,
        bool passed,
        bool structuralPass,
        bool memoryPass,
        bool coldStorageRequired,
        string notes)
    {
        var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        var collectionSeconds = collectionTicks / (double)Stopwatch.Frequency;
        return new ScenarioResult
        {
            Name = name,
            LogicalSteps = logicalSteps,
            ElapsedSeconds = elapsedSeconds,
            StepsPerSecond = elapsedSeconds > 0 ? logicalSteps / elapsedSeconds : 0,
            CollectionSeconds = collectionSeconds,
            CollectionSharePercent = elapsedSeconds > 0 ? (collectionSeconds / elapsedSeconds) * 100.0 : 0,
            Collections = collections,
            ManagedBytesBefore = heapBefore,
            ManagedBytesAfter = heapAfter,
            ManagedGrowthBytes = heapAfter - heapBefore,
            WorkingSetBytesBefore = workingSetBefore,
            WorkingSetBytesAfter = workingSetAfter,
            AllocatedBytes = allocatedBytes,
            AllocatedBytesPerStep = logicalSteps > 0 ? allocatedBytes / (double)logicalSteps : 0,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            MaxPhysicalSteps = maxSteps,
            MaxPhysicalRelations = maxRelations,
            FinalPhysicalSteps = finalSteps,
            FinalPhysicalRelations = finalRelations,
            FinalRoots = finalRoots,
            FinalTrackedObjects = finalTrackedObjects,
            DroppedHistory = dropped,
            Passed = passed,
            StructuralPass = structuralPass,
            MemoryPass = memoryPass,
            ColdStorageRequired = coldStorageRequired,
            Notes = notes
        };
    }

    private static void Print(ScenarioResult result)
    {
        Console.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.Name}");
        Console.WriteLine($"  logical steps: {result.LogicalSteps:N0}");
        Console.WriteLine($"  throughput: {result.StepsPerSecond:N0} steps/s");
        Console.WriteLine($"  collection time: {result.CollectionSeconds:N3}s ({result.CollectionSharePercent:N1}% of scenario)");
        Console.WriteLine($"  managed retained growth: {FormatBytes(result.ManagedGrowthBytes)}");
        Console.WriteLine($"  allocated: {FormatBytes(result.AllocatedBytes)} ({result.AllocatedBytesPerStep:N1} B/step)");
        Console.WriteLine($"  physical peak/final: {result.MaxPhysicalSteps:N0}/{result.FinalPhysicalSteps:N0} steps, {result.MaxPhysicalRelations:N0}/{result.FinalPhysicalRelations:N0} relations");
        Console.WriteLine($"  dropped history: {result.DroppedHistory}");
        Console.WriteLine($"  {result.Notes}");
        Console.WriteLine();
    }

    private static string FormatBytes(long value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        var absolute = Math.Abs((double)value);
        if (absolute >= 1024 * 1024 * 1024)
        {
            return $"{sign}{absolute / (1024 * 1024 * 1024):N2} GiB";
        }

        if (absolute >= 1024 * 1024)
        {
            return $"{sign}{absolute / (1024 * 1024):N2} MiB";
        }

        if (absolute >= 1024)
        {
            return $"{sign}{absolute / 1024:N2} KiB";
        }

        return $"{value:N0} B";
    }
}

public sealed class StressOptions
{
    public int Events { get; set; } = 10_000_000;
    public int ValueEvents { get; set; } = 1_000_000;
    public int Objects { get; set; } = 10_000;
    public int Capacity { get; set; } = 65_536;
    public int Batch { get; set; } = 16_384;
    public int ObjectBatch { get; set; } = 1_000;
    public int MaxRetainedGrowthMb { get; set; } = 64;
    public string ReportPath { get; set; } = "artifacts/runtime-feasibility.json";

    public static StressOptions Parse(string[] args)
    {
        var options = new StressOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--events":
                    options.Events = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--value-events":
                    options.ValueEvents = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--objects":
                    options.Objects = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--capacity":
                    options.Capacity = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--batch":
                    options.Batch = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--object-batch":
                    options.ObjectBatch = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--max-retained-growth-mb":
                    options.MaxRetainedGrowthMb = ReadPositiveInt(args[i], value);
                    i++;
                    break;
                case "--report":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("--report requires a path.");
                    }

                    options.ReportPath = value;
                    i++;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (options.Capacity < 16)
        {
            throw new ArgumentException("--capacity must be at least 16.");
        }

        options.Batch = Math.Min(options.Batch, options.Capacity - 4);
        return options;
    }

    private static int ReadPositiveInt(string name, string? value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{name} requires a positive integer.");
        }

        return parsed;
    }
}

public sealed class StressReport
{
    public int Version { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Runtime { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public StressOptions Options { get; set; } = new();
    public ScenarioResult RawReclaimable { get; set; } = new();
    public ScenarioResult ValueCapture { get; set; } = new();
    public ScenarioResult ObjectChurn { get; set; } = new();
    public ScenarioResult LiveChain { get; set; } = new();
    public bool Passed { get; set; }
}

public sealed class ScenarioResult
{
    public string Name { get; set; } = string.Empty;
    public long LogicalSteps { get; set; }
    public int ObjectsProcessed { get; set; }
    public double ElapsedSeconds { get; set; }
    public double StepsPerSecond { get; set; }
    public double CollectionSeconds { get; set; }
    public double CollectionSharePercent { get; set; }
    public int Collections { get; set; }
    public long ManagedBytesBefore { get; set; }
    public long ManagedBytesAfter { get; set; }
    public long ManagedGrowthBytes { get; set; }
    public long WorkingSetBytesBefore { get; set; }
    public long WorkingSetBytesAfter { get; set; }
    public long AllocatedBytes { get; set; }
    public double AllocatedBytesPerStep { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int MaxPhysicalSteps { get; set; }
    public int MaxPhysicalRelations { get; set; }
    public int FinalPhysicalSteps { get; set; }
    public int FinalPhysicalRelations { get; set; }
    public int FinalRoots { get; set; }
    public int FinalTrackedObjects { get; set; }
    public int MaxWeakAliveAfterGc { get; set; }
    public bool DroppedHistory { get; set; }
    public bool StructuralPass { get; set; }
    public bool MemoryPass { get; set; }
    public bool ColdStorageRequired { get; set; }
    public bool Passed { get; set; }
    public string Notes { get; set; } = string.Empty;
}
