using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lineage.Internal
{
    internal static class MutationTracker
    {
        private static readonly ConditionalWeakTable<object, FieldMap> Table = new ConditionalWeakTable<object, FieldMap>();
        private static readonly Dictionary<int, FieldState> StaticFields = new Dictionary<int, FieldState>();
        private static long _nextRootId;

        /// <summary>
        /// Records the latest provenance value for a field and returns a stable negative
        /// root id for that object-field slot. Instance keys remain weak; the root itself
        /// contains no reference to the application object.
        /// </summary>
        public static long Write(object target, int fieldToken, int valueId, int sessionId)
        {
            if (valueId == 0 || sessionId == 0)
            {
                return 0;
            }

            if (target == null)
            {
                lock (StaticFields)
                {
                    FieldState state;
                    if (!StaticFields.TryGetValue(fieldToken, out state) || state.SessionId != sessionId)
                    {
                        state = new FieldState(AllocateRootId(), valueId, sessionId);
                    }
                    else
                    {
                        state.ValueId = valueId;
                    }

                    StaticFields[fieldToken] = state;
                    return state.RootId;
                }
            }

            var map = Table.GetOrCreateValue(target);
            lock (map)
            {
                FieldState state;
                if (!map.Fields.TryGetValue(fieldToken, out state) || state.SessionId != sessionId)
                {
                    state = new FieldState(AllocateRootId(), valueId, sessionId);
                }
                else
                {
                    state.ValueId = valueId;
                }

                map.Fields[fieldToken] = state;
                return state.RootId;
            }
        }

        public static int Read(object target, int fieldToken, int sessionId)
        {
            if (sessionId == 0)
            {
                return 0;
            }

            if (target == null)
            {
                lock (StaticFields)
                {
                    FieldState state;
                    return StaticFields.TryGetValue(fieldToken, out state) && state.SessionId == sessionId ? state.ValueId : 0;
                }
            }

            FieldMap map;
            if (!Table.TryGetValue(target, out map))
            {
                return 0;
            }

            lock (map)
            {
                FieldState state;
                return map.Fields.TryGetValue(fieldToken, out state) && state.SessionId == sessionId ? state.ValueId : 0;
            }
        }

        private static long AllocateRootId()
        {
            return -Interlocked.Increment(ref _nextRootId);
        }

        private sealed class FieldMap
        {
            public readonly Dictionary<int, FieldState> Fields = new Dictionary<int, FieldState>();
        }

        private struct FieldState
        {
            public readonly long RootId;
            public readonly int SessionId;
            public int ValueId;

            public FieldState(long rootId, int valueId, int sessionId)
            {
                RootId = rootId;
                ValueId = valueId;
                SessionId = sessionId;
            }
        }
    }
}
