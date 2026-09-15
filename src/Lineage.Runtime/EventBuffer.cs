using System;
using System.Collections.Generic;

namespace Lineage
{
    internal sealed class EventBuffer
    {
        private readonly LineageStep[] _steps;
        private readonly LineageRelation[] _relations;
        private int _stepCount;
        private int _relationCount;
        private bool _dropped;

        public EventBuffer(int capacity)
        {
            _steps = new LineageStep[capacity];
            _relations = new LineageRelation[capacity > (int.MaxValue / 2) ? int.MaxValue : capacity * 2];
        }

        public int Count => _stepCount;
        public int RelationCount => _relationCount;
        public bool Dropped => _dropped;
        public LineageStep[] Steps => _steps;
        public LineageRelation[] Relations => _relations;

        // Compatibility view for existing consumers. Raw capture is stored as separate
        // step and relation arrays; events are hydrated only when explicitly requested.
        public LineageEvent[] Events
        {
            get
            {
                var events = new LineageEvent[_stepCount];
                for (var i = 0; i < _stepCount; i++)
                {
                    var step = _steps[i];
                    events[i] = new LineageEvent
                    {
                        LocationId = step.LocationId,
                        ValueId = step.Id,
                        Kind = step.Kind
                    };
                }

                for (var i = 0; i < _relationCount; i++)
                {
                    var relation = _relations[i];
                    var index = FindStepIndex(relation.ChildStepId);
                    if (index < 0)
                    {
                        continue;
                    }

                    var ev = events[index];
                    if (ev.Parent0 <= 0)
                    {
                        ev.Parent0 = relation.ParentStepId;
                    }
                    else if (ev.Parent1 <= 0 && ev.Parent0 != relation.ParentStepId)
                    {
                        ev.Parent1 = relation.ParentStepId;
                    }

                    events[index] = ev;
                }

                return events;
            }
        }

        public bool TryAdd(LineageEvent ev)
        {
            // Method-entry and other context-only events have no produced value and do
            // not belong in the raw value-step table.
            if (ev.ValueId <= 0)
            {
                return true;
            }

            if (_stepCount >= _steps.Length)
            {
                MarkDropped();
                return false;
            }

            _steps[_stepCount++] = new LineageStep(ev.ValueId, null, ev.LocationId, ev.Kind);

            if (ev.Parent0 > 0)
            {
                TryAddRelation(ev.ValueId, ev.Parent0);
            }

            if (ev.Parent1 > 0 && ev.Parent1 != ev.Parent0)
            {
                TryAddRelation(ev.ValueId, ev.Parent1);
            }

            return true;
        }

        public void SetValue(int valueId, string value)
        {
            if (value == null)
            {
                return;
            }

            var index = FindStepIndex(valueId);
            if (index < 0)
            {
                return;
            }

            var step = _steps[index];
            step.Value = value;
            _steps[index] = step;
        }

        public string GetValue(int valueId)
        {
            var index = FindStepIndex(valueId);
            return index >= 0 ? _steps[index].Value : null;
        }

        public void SetTypeName(int valueId, string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return;
            }

            var index = FindStepIndex(valueId);
            if (index < 0)
            {
                return;
            }

            var step = _steps[index];
            step.TypeName = typeName;
            _steps[index] = step;
        }

        public string GetTypeName(int valueId)
        {
            var index = FindStepIndex(valueId);
            return index >= 0 ? _steps[index].TypeName : null;
        }

        public void AttachParent(int valueId, int parentId)
        {
            if (valueId <= 0 || parentId <= 0 || FindStepIndex(valueId) < 0 || FindStepIndex(parentId) < 0)
            {
                return;
            }

            TryAddRelation(valueId, parentId);
        }

        /// <summary>
        /// Keeps only steps reachable from the supplied live roots. This deliberately
        /// runs outside the hot recording path: it builds temporary lookup structures,
        /// walks the causal graph backwards, then compacts both raw arrays in place.
        /// Step IDs stay stable even when their physical slots move.
        /// </summary>
        public ProvenanceCollectionResult Collect(IReadOnlyCollection<int> roots)
        {
            var beforeSteps = _stepCount;
            var beforeRelations = _relationCount;
            if (_stepCount == 0)
            {
                return new ProvenanceCollectionResult(0, 0);
            }

            var parentsByChild = new Dictionary<int, List<int>>();
            for (var i = 0; i < _relationCount; i++)
            {
                var relation = _relations[i];
                List<int> parents;
                if (!parentsByChild.TryGetValue(relation.ChildStepId, out parents))
                {
                    parents = new List<int>(2);
                    parentsByChild[relation.ChildStepId] = parents;
                }

                parents.Add(relation.ParentStepId);
            }

            var live = new HashSet<int>();
            var stack = new Stack<int>();
            if (roots != null)
            {
                foreach (var root in roots)
                {
                    if (root > 0)
                    {
                        stack.Push(root);
                    }
                }
            }

            while (stack.Count > 0)
            {
                var stepId = stack.Pop();
                if (stepId <= 0 || live.Contains(stepId) || FindStepIndex(stepId) < 0)
                {
                    continue;
                }

                live.Add(stepId);
                List<int> parents;
                if (!parentsByChild.TryGetValue(stepId, out parents))
                {
                    continue;
                }

                for (var i = 0; i < parents.Count; i++)
                {
                    stack.Push(parents[i]);
                }
            }

            var stepWrite = 0;
            for (var i = 0; i < _stepCount; i++)
            {
                var step = _steps[i];
                if (!live.Contains(step.Id))
                {
                    continue;
                }

                if (stepWrite != i)
                {
                    _steps[stepWrite] = step;
                }

                stepWrite++;
            }

            Array.Clear(_steps, stepWrite, _stepCount - stepWrite);
            _stepCount = stepWrite;

            var relationWrite = 0;
            for (var i = 0; i < _relationCount; i++)
            {
                var relation = _relations[i];
                if (!live.Contains(relation.ChildStepId) || !live.Contains(relation.ParentStepId))
                {
                    continue;
                }

                if (relationWrite != i)
                {
                    _relations[relationWrite] = relation;
                }

                relationWrite++;
            }

            Array.Clear(_relations, relationWrite, _relationCount - relationWrite);
            _relationCount = relationWrite;

            return new ProvenanceCollectionResult(beforeSteps - _stepCount, beforeRelations - _relationCount);
        }

        public void Clear()
        {
            // Steps can contain captured strings, so clear the used range to release
            // references immediately when the raw session is discarded.
            Array.Clear(_steps, 0, _stepCount);
            Array.Clear(_relations, 0, _relationCount);
            _stepCount = 0;
            _relationCount = 0;
            _dropped = false;
        }

        private int FindStepIndex(int valueId)
        {
            if (valueId <= 0 || _stepCount == 0)
            {
                return -1;
            }

            // Before the first compaction IDs and slots line up, which covers the
            // overwhelmingly common hot-path SetValue immediately after Produce.
            var direct = valueId - 1;
            if (direct >= 0 && direct < _stepCount && _steps[direct].Id == valueId)
            {
                return direct;
            }

            // Compaction preserves ascending Step IDs, so no permanent hash index is
            // required just to keep stable identities after reclamation.
            var low = 0;
            var high = _stepCount - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var id = _steps[middle].Id;
                if (id == valueId)
                {
                    return middle;
                }

                if (id < valueId)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
        }

        private bool TryAddRelation(int childStepId, int parentStepId)
        {
            if (_relationCount >= _relations.Length)
            {
                MarkDropped();
                return false;
            }

            _relations[_relationCount++] = new LineageRelation(childStepId, parentStepId);
            return true;
        }

        private void MarkDropped()
        {
            _dropped = true;
            LineageMetrics.MarkDropped();
        }
    }

    internal struct ProvenanceCollectionResult
    {
        public readonly int StepsReclaimed;
        public readonly int RelationsReclaimed;

        public ProvenanceCollectionResult(int stepsReclaimed, int relationsReclaimed)
        {
            StepsReclaimed = stepsReclaimed;
            RelationsReclaimed = relationsReclaimed;
        }
    }
}
