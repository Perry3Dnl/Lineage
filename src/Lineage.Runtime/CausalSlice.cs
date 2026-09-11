using System.Collections.Generic;

namespace Lineage
{
    internal static class CausalSlice
    {
        public static List<LineageEvent> Collect(EventBuffer buffer, int focusValueId)
        {
            var result = new List<LineageEvent>();
            if (buffer == null || focusValueId <= 0)
            {
                return result;
            }

            var count = buffer.Count;
            var events = buffer.Events;
            var maxId = 0;
            for (var i = 0; i < count; i++)
            {
                var id = events[i].ValueId;
                if (id > maxId)
                {
                    maxId = id;
                }
            }

            var producer = new int[maxId + 1];
            for (var i = 0; i < producer.Length; i++)
            {
                producer[i] = -1;
            }

            for (var i = 0; i < count; i++)
            {
                var id = events[i].ValueId;
                if (id > 0 && producer[id] < 0)
                {
                    producer[id] = i;
                }
            }

            var keep = new bool[count];
            var stack = new Stack<int>();
            stack.Push(focusValueId);
            var visited = new bool[maxId + 1];

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id <= 0 || id > maxId || visited[id])
                {
                    continue;
                }

                visited[id] = true;
                var index = producer[id];
                if (index < 0)
                {
                    continue;
                }

                keep[index] = true;
                var ev = events[index];
                if (ev.Parent0 > 0)
                {
                    stack.Push(ev.Parent0);
                }

                if (ev.Parent1 > 0)
                {
                    stack.Push(ev.Parent1);
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (keep[i])
                {
                    result.Add(events[i]);
                }
            }

            return result;
        }
    }
}
