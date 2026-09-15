using System;
using System.Collections.Generic;
using Lineage.Internal;

namespace Lineage
{
    public sealed class CaptureScope : IDisposable
    {
        [ThreadStatic]
        private static CaptureScope _current;

        public const int DefaultCapacity = 65536;

        private readonly CaptureScope _parent;
        private readonly Dictionary<long, int> _roots = new Dictionary<long, int>();
        private long[] _frameRootKeys = new long[32];
        private int _frameRootKeyCount;
        private bool _disposed;

        internal EventBuffer Buffer { get; }
        internal int NextValueId = 1;
        internal int NextFrameId = 1;
        internal int CurrentFrameId;
        internal int FocusValueId;
        internal bool Frozen;

        public static CaptureScope Current => _current;

        private CaptureScope(CaptureScope parent, int capacity)
        {
            _parent = parent;
            Buffer = new EventBuffer(capacity);
        }

        public static CaptureScope Enter()
        {
            return Enter(DefaultCapacity);
        }

        public static CaptureScope Enter(int capacity)
        {
            if (LineageSettings.AutomaticTriggersEnabled)
            {
                Recorder.EnsureUnhandledHook();
            }

            var scope = new CaptureScope(_current, capacity);
            _current = scope;
            return scope;
        }

        internal static CaptureScope Ensure()
        {
            if (!LineageSettings.IsEnabled)
            {
                return null;
            }

            var current = _current;
            if (current != null && !current._disposed)
            {
                return current;
            }

            return Enter();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Buffer.Clear();
            _roots.Clear();
            _frameRootKeyCount = 0;
            NextValueId = 1;
            NextFrameId = 1;
            CurrentFrameId = 0;
            FocusValueId = 0;
            Frozen = false;
            if (_current == this)
            {
                _current = _parent;
            }
        }

        internal void SetPreview(int valueId, string preview)
        {
            Buffer.SetValue(valueId, preview);
        }

        internal void SetTypeName(int valueId, string typeName)
        {
            Buffer.SetTypeName(valueId, typeName);
        }

        internal string GetTypeName(int valueId)
        {
            return Buffer.GetTypeName(valueId);
        }

        internal void CopyPreview(int fromValueId, int toValueId)
        {
            var preview = GetPreview(fromValueId);
            if (preview != null)
            {
                SetPreview(toValueId, preview);
            }
        }

        internal string GetPreview(int valueId)
        {
            return Buffer.GetValue(valueId);
        }

        /// <summary>
        /// Associates a stable runtime slot with its current provenance step. Reassigning
        /// the same root replaces the previous value rather than growing the root set.
        /// </summary>
        internal void SetRoot(long rootId, int valueId)
        {
            if (valueId <= 0)
            {
                _roots.Remove(rootId);
                return;
            }

            _roots[rootId] = valueId;
        }

        /// <summary>
        /// Registers a root owned by the current method frame. The key is remembered only
        /// once, so repeated assignments to the same slot do not grow frame bookkeeping.
        /// </summary>
        internal void SetFrameRoot(long rootId, int valueId)
        {
            if (valueId <= 0)
            {
                return;
            }

            if (!_roots.ContainsKey(rootId))
            {
                EnsureFrameRootCapacity();
                _frameRootKeys[_frameRootKeyCount++] = rootId;
            }

            _roots[rootId] = valueId;
        }

        internal int FrameRootMark => _frameRootKeyCount;

        internal void ReleaseFrameRoots(int mark)
        {
            if (mark < 0)
            {
                mark = 0;
            }
            else if (mark > _frameRootKeyCount)
            {
                mark = _frameRootKeyCount;
            }

            for (var i = _frameRootKeyCount - 1; i >= mark; i--)
            {
                _roots.Remove(_frameRootKeys[i]);
                _frameRootKeys[i] = 0;
            }

            _frameRootKeyCount = mark;
        }

        internal void ReleaseRoot(long rootId)
        {
            _roots.Remove(rootId);
        }

        internal int RootCount => _roots.Count;

        /// <summary>
        /// Reclaims raw steps that cannot contribute to any currently registered root.
        /// Collection is invoked only at runtime safe points where no unrooted IL
        /// evaluation-stack values from an instrumented caller can be discarded.
        /// </summary>
        internal ProvenanceCollectionResult CollectGarbage()
        {
            var roots = new List<int>(_roots.Count + (FocusValueId > 0 ? 1 : 0));
            foreach (var pair in _roots)
            {
                if (pair.Value > 0)
                {
                    roots.Add(pair.Value);
                }
            }

            if (FocusValueId > 0)
            {
                roots.Add(FocusValueId);
            }

            return Buffer.Collect(roots);
        }

        private void EnsureFrameRootCapacity()
        {
            if (_frameRootKeyCount < _frameRootKeys.Length)
            {
                return;
            }

            Array.Resize(ref _frameRootKeys, _frameRootKeys.Length * 2);
        }
    }
}
