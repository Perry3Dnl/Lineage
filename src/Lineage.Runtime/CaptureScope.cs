using System;
using Lineage.Internal;

namespace Lineage
{
    public sealed class CaptureScope : IDisposable
    {
        [ThreadStatic]
        private static CaptureScope _current;

        public const int DefaultCapacity = 65536;

        private readonly CaptureScope _parent;
        private bool _disposed;

        internal EventBuffer Buffer { get; }
        private string[] _types;
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
            _types = null;
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
            if (valueId <= 0 || string.IsNullOrEmpty(typeName))
            {
                return;
            }

            EnsureTypes(valueId);
            _types[valueId] = typeName;
        }

        internal string GetTypeName(int valueId)
        {
            if (_types == null || valueId <= 0 || valueId >= _types.Length)
            {
                return null;
            }

            return _types[valueId];
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

        private void EnsureTypes(int valueId)
        {
            var needed = valueId + 1;
            if (_types == null)
            {
                _types = new string[Math.Max(64, needed)];
                return;
            }

            if (needed > _types.Length)
            {
                Array.Resize(ref _types, Math.Max(needed, _types.Length * 2));
            }
        }
    }
}
