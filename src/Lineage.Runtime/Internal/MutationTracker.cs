using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lineage.Internal
{
    internal static class MutationTracker
    {
        private static readonly ConditionalWeakTable<object, FieldMap> Table = new ConditionalWeakTable<object, FieldMap>();
        private static readonly Dictionary<long, FieldState> StaticFields = new Dictionary<long, FieldState>();
        private static long _nextRootId;

        /// <summary>
        /// Records the latest provenance value for a field and returns a stable negative
        /// root id for that object-field slot. Instance objects are never strongly held by
        /// Lineage: each FieldMap keeps only a long weak reference to its application object.
        /// </summary>
        public static long Write(object target, int fieldToken, int valueId, CaptureScope scope)
        {
            if (valueId == 0 || scope == null || scope.SessionId == 0)
            {
                return 0;
            }

            if (target == null)
            {
                var key = StaticFieldKey(scope.SessionId, fieldToken);
                lock (StaticFields)
                {
                    FieldState state;
                    if (!StaticFields.TryGetValue(key, out state))
                    {
                        state = new FieldState(AllocateRootId(), valueId);
                    }
                    else
                    {
                        state.ValueId = valueId;
                    }

                    StaticFields[key] = state;
                    return state.RootId;
                }
            }

            var map = Table.GetValue(target, key => new FieldMap(key));
            lock (map)
            {
                SessionFields session;
                if (!map.Sessions.TryGetValue(scope.SessionId, out session))
                {
                    session = new SessionFields();
                    map.Sessions[scope.SessionId] = session;
                    scope.RegisterObjectFieldMap(map);
                }

                FieldState state;
                if (!session.Fields.TryGetValue(fieldToken, out state))
                {
                    state = new FieldState(AllocateRootId(), valueId);
                }
                else
                {
                    state.ValueId = valueId;
                }

                session.Fields[fieldToken] = state;
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
                var key = StaticFieldKey(sessionId, fieldToken);
                lock (StaticFields)
                {
                    FieldState state;
                    return StaticFields.TryGetValue(key, out state) ? state.ValueId : 0;
                }
            }

            FieldMap map;
            if (!Table.TryGetValue(target, out map))
            {
                return 0;
            }

            lock (map)
            {
                SessionFields session;
                if (!map.Sessions.TryGetValue(sessionId, out session))
                {
                    return 0;
                }

                FieldState state;
                return session.Fields.TryGetValue(fieldToken, out state) ? state.ValueId : 0;
            }
        }

        /// <summary>
        /// Called only from a CaptureScope safe point. If the application object is gone,
        /// remove that session's field state and release every root owned by the object.
        /// No work is performed on the CLR finalizer thread.
        /// </summary>
        internal static bool RetireDeadObject(FieldMap map, CaptureScope scope)
        {
            if (map == null || scope == null || map.Target.IsAlive)
            {
                return false;
            }

            lock (map)
            {
                SessionFields session;
                if (!map.Sessions.TryGetValue(scope.SessionId, out session))
                {
                    return true;
                }

                foreach (var pair in session.Fields)
                {
                    scope.ReleaseRoot(pair.Value.RootId);
                }

                map.Sessions.Remove(scope.SessionId);
            }

            return true;
        }

        /// <summary>
        /// Removes session-local mutation bookkeeping when a capture scope ends. This
        /// prevents long-lived application objects and static fields from accumulating
        /// stale Lineage session state.
        /// </summary>
        internal static void ReleaseScope(CaptureScope scope, IList<FieldMap> maps)
        {
            if (scope == null)
            {
                return;
            }

            if (maps != null)
            {
                for (var i = 0; i < maps.Count; i++)
                {
                    var map = maps[i];
                    if (map == null)
                    {
                        continue;
                    }

                    lock (map)
                    {
                        map.Sessions.Remove(scope.SessionId);
                    }
                }
            }

            lock (StaticFields)
            {
                if (StaticFields.Count == 0)
                {
                    return;
                }

                var remove = new List<long>();
                foreach (var pair in StaticFields)
                {
                    if (SessionFromStaticKey(pair.Key) == scope.SessionId)
                    {
                        remove.Add(pair.Key);
                    }
                }

                for (var i = 0; i < remove.Count; i++)
                {
                    StaticFields.Remove(remove[i]);
                }
            }
        }

        private static long AllocateRootId()
        {
            return -Interlocked.Increment(ref _nextRootId);
        }

        private static long StaticFieldKey(int sessionId, int fieldToken)
        {
            return ((long)sessionId << 32) | (uint)fieldToken;
        }

        private static int SessionFromStaticKey(long key)
        {
            return unchecked((int)(key >> 32));
        }

        internal sealed class FieldMap
        {
            // Track resurrection so an object that deliberately resurrects in a finalizer
            // does not lose its provenance root before it is truly unreachable.
            public readonly WeakReference Target;
            public readonly Dictionary<int, SessionFields> Sessions = new Dictionary<int, SessionFields>();

            public FieldMap(object target)
            {
                Target = new WeakReference(target, true);
            }
        }

        internal sealed class SessionFields
        {
            public readonly Dictionary<int, FieldState> Fields = new Dictionary<int, FieldState>();
        }

        internal struct FieldState
        {
            public readonly long RootId;
            public int ValueId;

            public FieldState(long rootId, int valueId)
            {
                RootId = rootId;
                ValueId = valueId;
            }
        }
    }
}
