using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Lineage
{
    /// <summary>
    /// Disk-backed overflow tier for provenance pages. The application thread only
    /// enqueues immutable pages; all file I/O happens on a dedicated background writer.
    /// Persisted pages are immutable segments so the oldest segments can be deleted when
    /// the configured disk budget is reached without rewriting the remaining journal.
    /// </summary>
    internal sealed class ColdJournal : IDisposable
    {
        private const int SegmentMagic = 0x4C4E4A31; // LNJ1
        private const int SegmentVersion = 1;

        private readonly object _gate = new object();
        private readonly Queue<ColdJournalPage> _queue = new Queue<ColdJournalPage>();
        private readonly List<ColdSegment> _segments = new List<ColdSegment>();
        private readonly string _directory;
        private readonly long _maxBytes;
        private readonly int _queueCapacity;
        private Thread _writer;
        private bool _writing;
        private bool _stopping;
        private bool _disposed;
        private bool _truncated;
        private bool _ioFailed;
        private long _bytes;
        private int _sequence;

        // Deterministic stress hook. Never configured by normal runtime code.
        internal static int WriterDelayMillisecondsForTests;

        public ColdJournal(long maxBytes, int queueCapacity, string rootDirectory)
        {
            _maxBytes = Math.Max(1, maxBytes);
            _queueCapacity = Math.Max(1, queueCapacity);
            var root = string.IsNullOrEmpty(rootDirectory) ? Path.GetTempPath() : rootDirectory;
            _directory = Path.Combine(root, "Lineage", "session-" + Guid.NewGuid().ToString("N"));
        }

        public bool Truncated
        {
            get
            {
                lock (_gate)
                {
                    return _truncated || _ioFailed;
                }
            }
        }

        public bool Active
        {
            get
            {
                lock (_gate)
                {
                    return _writer != null || _segments.Count > 0 || _queue.Count > 0;
                }
            }
        }

        public long Bytes
        {
            get
            {
                lock (_gate)
                {
                    return _bytes;
                }
            }
        }

        public int SegmentCount
        {
            get
            {
                lock (_gate)
                {
                    return _segments.Count;
                }
            }
        }

        public int QueuedPageCount
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count + (_writing ? 1 : 0);
                }
            }
        }

        public bool CanAccept
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed && !_stopping && _queue.Count < _queueCapacity;
                }
            }
        }

        public bool TryEnqueue(ColdJournalPage page)
        {
            if (page == null || page.StepCount == 0)
            {
                return true;
            }

            lock (_gate)
            {
                if (_disposed || _stopping || _queue.Count >= _queueCapacity)
                {
                    return false;
                }

                EnsureWriterLocked();
                _queue.Enqueue(page);
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        public ProvenanceSnapshot ReadSnapshot()
        {
            Flush();

            ColdSegment[] segments;
            lock (_gate)
            {
                segments = _segments.ToArray();
            }

            var steps = new List<LineageStep>();
            var relations = new List<LineageRelation>();
            for (var i = 0; i < segments.Length; i++)
            {
                ColdJournalPage page;
                if (!TryReadSegment(segments[i], out page))
                {
                    lock (_gate)
                    {
                        _truncated = true;
                        _ioFailed = true;
                    }
                    continue;
                }

                for (var s = 0; s < page.StepCount; s++)
                {
                    steps.Add(page.Steps[s]);
                }

                for (var r = 0; r < page.RelationCount; r++)
                {
                    relations.Add(page.Relations[r]);
                }
            }

            return new ProvenanceSnapshot(steps.ToArray(), relations.ToArray());
        }

        public void MarkTruncated()
        {
            lock (_gate)
            {
                _truncated = true;
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                while (!_disposed && (_queue.Count > 0 || _writing))
                {
                    Monitor.Wait(_gate, 100);
                }
            }
        }

        /// <summary>
        /// Disposal never waits for storage. Pending pages are abandoned, the background
        /// writer is asked to stop after its current write, and that writer removes the
        /// temporary session directory when it exits.
        /// </summary>
        public void Dispose()
        {
            var cleanupHere = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopping = true;
                if (_queue.Count > 0)
                {
                    _truncated = true;
                    _queue.Clear();
                }

                cleanupHere = _writer == null;
                Monitor.PulseAll(_gate);
            }

            if (cleanupHere)
            {
                CleanupDirectory();
            }
        }

        private void EnsureWriterLocked()
        {
            if (_writer != null)
            {
                return;
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Lineage cold journal"
            };
            _writer.Start();
        }

        private void WriterLoop()
        {
            try
            {
                while (true)
                {
                    ColdJournalPage page;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_stopping)
                        {
                            Monitor.Wait(_gate);
                        }

                        if (_queue.Count == 0 && _stopping)
                        {
                            _writing = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }

                        page = _queue.Dequeue();
                        _writing = true;
                    }

                    try
                    {
                        var delay = Volatile.Read(ref WriterDelayMillisecondsForTests);
                        if (delay > 0)
                        {
                            Thread.Sleep(delay);
                        }

                        WriteSegment(page);
                    }
                    catch
                    {
                        lock (_gate)
                        {
                            _ioFailed = true;
                            _truncated = true;
                        }
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _writing = false;
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _segments.Clear();
                    _bytes = 0;
                    Monitor.PulseAll(_gate);
                }

                CleanupDirectory();
            }
        }

        private void WriteSegment(ColdJournalPage page)
        {
            Directory.CreateDirectory(_directory);
            var sequence = Interlocked.Increment(ref _sequence);
            var finalPath = Path.Combine(_directory, "segment-" + sequence.ToString("D8") + ".bin");
            var tempPath = finalPath + ".tmp";

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, false))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
                {
                    writer.Write(SegmentMagic);
                    writer.Write(SegmentVersion);
                    writer.Write(page.StepCount);
                    writer.Write(page.RelationCount);
                    writer.Write(page.FirstStepId);
                    writer.Write(page.LastStepId);

                    for (var i = 0; i < page.StepCount; i++)
                    {
                        var step = page.Steps[i];
                        writer.Write(step.Id);
                        writer.Write(step.LocationId);
                        writer.Write((int)step.Kind);
                        WriteNullable(writer, step.Value);
                        WriteNullable(writer, step.TypeName);
                    }

                    for (var i = 0; i < page.RelationCount; i++)
                    {
                        writer.Write(page.Relations[i].ChildStepId);
                        writer.Write(page.Relations[i].ParentStepId);
                    }
                }

                var size = new FileInfo(tempPath).Length;
                lock (_gate)
                {
                    if (size > _maxBytes)
                    {
                        _truncated = true;
                        TryDelete(tempPath);
                        return;
                    }

                    while (_segments.Count > 0 && _bytes + size > _maxBytes)
                    {
                        var oldest = _segments[0];
                        _segments.RemoveAt(0);
                        _bytes -= oldest.Bytes;
                        _truncated = true;
                        TryDelete(oldest.Path);
                    }
                }

                File.Move(tempPath, finalPath);
                lock (_gate)
                {
                    _segments.Add(new ColdSegment(finalPath, size, page.FirstStepId, page.LastStepId));
                    _bytes += size;
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static bool TryReadSegment(ColdSegment segment, out ColdJournalPage page)
        {
            page = null;
            try
            {
                using (var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
                {
                    if (reader.ReadInt32() != SegmentMagic || reader.ReadInt32() != SegmentVersion)
                    {
                        return false;
                    }

                    var stepCount = reader.ReadInt32();
                    var relationCount = reader.ReadInt32();
                    var firstStepId = reader.ReadInt32();
                    var lastStepId = reader.ReadInt32();
                    if (stepCount < 0 || relationCount < 0)
                    {
                        return false;
                    }

                    var steps = new LineageStep[stepCount];
                    for (var i = 0; i < stepCount; i++)
                    {
                        steps[i] = new LineageStep
                        {
                            Id = reader.ReadInt32(),
                            LocationId = reader.ReadInt32(),
                            Kind = (EventKind)reader.ReadInt32(),
                            Value = ReadNullable(reader),
                            TypeName = ReadNullable(reader)
                        };
                    }

                    var relations = new LineageRelation[relationCount];
                    for (var i = 0; i < relationCount; i++)
                    {
                        relations[i] = new LineageRelation(reader.ReadInt32(), reader.ReadInt32());
                    }

                    page = new ColdJournalPage(steps, relations, firstStepId, lastStepId);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void CleanupDirectory()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch
            {
                // Temporary debug storage cleanup is best effort only.
            }
        }

        private static void WriteNullable(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        private static string ReadNullable(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private sealed class ColdSegment
        {
            public readonly string Path;
            public readonly long Bytes;
            public readonly int FirstStepId;
            public readonly int LastStepId;

            public ColdSegment(string path, long bytes, int firstStepId, int lastStepId)
            {
                Path = path;
                Bytes = bytes;
                FirstStepId = firstStepId;
                LastStepId = lastStepId;
            }
        }
    }

    internal sealed class ColdJournalPage
    {
        public readonly LineageStep[] Steps;
        public readonly LineageRelation[] Relations;
        public readonly int FirstStepId;
        public readonly int LastStepId;

        public int StepCount => Steps != null ? Steps.Length : 0;
        public int RelationCount => Relations != null ? Relations.Length : 0;

        public ColdJournalPage(LineageStep[] steps, LineageRelation[] relations, int firstStepId, int lastStepId)
        {
            Steps = steps ?? new LineageStep[0];
            Relations = relations ?? new LineageRelation[0];
            FirstStepId = firstStepId;
            LastStepId = lastStepId;
        }
    }

    internal sealed class ProvenanceSnapshot
    {
        public readonly LineageStep[] Steps;
        public readonly LineageRelation[] Relations;

        public ProvenanceSnapshot(LineageStep[] steps, LineageRelation[] relations)
        {
            Steps = steps ?? new LineageStep[0];
            Relations = relations ?? new LineageRelation[0];
        }
    }
}
