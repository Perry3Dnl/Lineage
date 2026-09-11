using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Lineage.Internal
{
    internal static class MutationTracker
    {
        private static readonly ConditionalWeakTable<object, FieldMap> Table = new ConditionalWeakTable<object, FieldMap>();

        public static void Write(object target, int fieldToken, int valueId)
        {
            if (target == null || valueId == 0)
            {
                return;
            }

            var map = Table.GetOrCreateValue(target);
            lock (map)
            {
                map.Fields[fieldToken] = valueId;
            }
        }

        public static int Read(object target, int fieldToken)
        {
            if (target == null)
            {
                return 0;
            }

            FieldMap map;
            if (!Table.TryGetValue(target, out map))
            {
                return 0;
            }

            lock (map)
            {
                int id;
                return map.Fields.TryGetValue(fieldToken, out id) ? id : 0;
            }
        }

        private sealed class FieldMap
        {
            public readonly Dictionary<int, int> Fields = new Dictionary<int, int>();
        }
    }
}
