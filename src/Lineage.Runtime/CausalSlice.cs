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

            // Step IDs remain stable after provenance GC, so they are deliberately not
            // used as array indexes here. Report hydration can afford temporary maps.
            var existing = new HashSet<int>();
            for (var i = 0; i < stepCount; i++)
            {
                if (steps[i].Id > 0)
                {
                    existing.Add(steps[i].Id);
                }
            }

            if (!existing.Contains(focusValueId))
            {
                return result;
            }

            var parentsByChild = new Dictionary<int, List<int>>();
            for (var i = 0; i < relationCount; i++)
            {
                var relation = relations[i];
                if (!existing.Contains(relation.ChildStepId) || !existing.Contains(relation.ParentStepId))
                {
                    continue;
                }

                List<int> parents;
                if (!parentsByChild.TryGetValue(relation.ChildStepId, out parents))
                {
                    parents = new List<int>(2);
                    parentsByChild[relation.ChildStepId] = parents;
                }

                parents.Add(relation.ParentStepId);
            }

            var visited = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(focusValueId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!existing.Contains(id) || !visited.Add(id))
                {
                    continue;
                }

                List<int> parents;
                if (!parentsByChild.TryGetValue(id, out parents))
                {
                    continue;
                }

                for (var i = 0; i < parents.Count; i++)
                {
                    stack.Push(parents[i]);
                }
            }

            for (var i = 0; i < stepCount; i++)
            {
                var step = steps[i];
                if (!visited.Contains(step.Id))
                {
                    continue;
                }

                var parent0 = 0;
                var parent1 = 0;
                List<int> parents;
                if (parentsByChild.TryGetValue(step.Id, out parents))
                {
                    for (var p = 0; p < parents.Count; p++)
                    {
                        var parentId = parents[p];
                        if (parent0 == 0)
                        {
                            parent0 = parentId;
                        }
                        else if (parent1 == 0 && parent0 != parentId)
                        {
                            parent1 = parentId;
                        }
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
