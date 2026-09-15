using System;
using System.Collections.Generic;

namespace Lineage
{
    internal static class CausalSlice
    {
        public static List<LineageEvent> Collect(EventBuffer buffer, int focusValueId)
        {
            var result = new List<LineageEvent>();
            if (buffer == null || focusValueId <= 0 || buffer.Count == 0)
            {
                return result;
            }

            var steps = buffer.Steps;
            var stepCount = buffer.Count;
            var relations = buffer.Relations;
            var relationCount = buffer.RelationCount;
            var maxId = steps[stepCount - 1].Id;
            if (focusValueId > maxId)
            {
                return result;
            }

            // Build a temporary child -> relation index only when a report is requested.
            // The hot capture path stays append-only and does not maintain a live graph.
            var heads = new int[maxId + 1];
            for (var i = 0; i < heads.Length; i++)
            {
                heads[i] = -1;
            }

            var next = new int[relationCount];
            for (var i = 0; i < relationCount; i++)
            {
                var child = relations[i].ChildStepId;
                if (child <= 0 || child > maxId)
                {
                    next[i] = -1;
                    continue;
                }

                next[i] = heads[child];
                heads[child] = i;
            }

            var visited = new bool[maxId + 1];
            var stack = new Stack<int>();
            stack.Push(focusValueId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id <= 0 || id > maxId || visited[id])
                {
                    continue;
                }

                visited[id] = true;
                for (var relationIndex = heads[id]; relationIndex >= 0; relationIndex = next[relationIndex])
                {
                    var parentId = relations[relationIndex].ParentStepId;
                    if (parentId > 0 && parentId <= maxId && !visited[parentId])
                    {
                        stack.Push(parentId);
                    }
                }
            }

            for (var i = 0; i < stepCount; i++)
            {
                var step = steps[i];
                if (step.Id <= 0 || step.Id > maxId || !visited[step.Id])
                {
                    continue;
                }

                var parent0 = 0;
                var parent1 = 0;
                for (var relationIndex = heads[step.Id]; relationIndex >= 0; relationIndex = next[relationIndex])
                {
                    var parentId = relations[relationIndex].ParentStepId;
                    if (parentId <= 0)
                    {
                        continue;
                    }

                    if (parent0 == 0)
                    {
                        parent0 = parentId;
                    }
                    else if (parent1 == 0 && parent0 != parentId)
                    {
                        parent1 = parentId;
                    }
                }

                result.Add(new LineageEvent
                {
                    LocationId = step.LocationId,
                    ValueId = step.Id,
                    Parent0 = parent0,
                    Parent1 = parent1,
                    Kind = step.Kind
                });
            }

            return result;
        }
    }
}
