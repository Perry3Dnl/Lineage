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
        private ColdJournal _journal;

        public EventBuffer(int capacity)
        {
            _steps = new LineageStep[capacity];
            _relations = new LineageRelation[capacity > (int.MaxValue / 2) ? int.MaxValue : capacity * 2];
        }

        public int Count => _stepCount;
        public int RelationCount => _relationCount;
        public int Capacity => _steps.Length;
        public bool Dropped => _dropped || (_journal != null && _journal.Truncated);
        public LineageStep[] Steps => _steps;
        public LineageRelation[] Relations => _relations;

        internal bool ColdStorageActive => _journal != null && _journal.Active;
        internal long ColdStorageBytes => _journal != null ? _journal.Bytes : 0;
        internal int ColdStorageSegments => _journal != null ? _journal.SegmentCount : 0;
        internal int ColdStorageQueuedPages => _journal != null ? _journal.QueuedPageCount : 0;

        public LineageEvent[] Events
        {
            get
            {
                var snapshot = CreateSnapshot();
                var events = new LineageEvent[snapshot.Steps.Length];
                var indexes = new Dictionary<int, int>(snapshot.Steps.Length);

                for (var i = 0; i < snapshot.Steps.Length; i++)
                {
                    var step = snapshot.Steps[i];
                    indexes[step.Id] = i;
                    events[i] = new LineageEvent
                    {
                        LocationId = step.LocationId,
                        ValueId = step.Id,
                        Kind = step.Kind,
                        Value = step.FormatValue(),
                        TypeName = step.TypeName,
                        ValueKind = step.ValueKind
                    };
                }

                for (var i = 0; i < snapshot.Relations.Length; i++)
                {
                    var relation = snapshot.Relations[i];
                    int index;
                    if (!indexes.TryGetValue(relation.ChildStepId, out index))
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
            if (ev.ValueId <= 0)
            {
                return true;
            }

            MaybeSpill(false);
            if (_stepCount >= _steps.Length)
            {
                MaybeSpill(true);
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
            step.ValueKind = LineageValueKind.LegacyText;
            step.ValueData0 = 0;
            step.ValueData1 = 0;
            _steps[index] = step;
        }

        internal void SetCapturedValue(int valueId, LineageValueCodec.Payload value)
        {
            var index = FindStepIndex(valueId);
            if (index < 0)
            {
                return;
            }

            var step = _steps[index];
            step.ValueKind = value.Kind;
            step.ValueData0 = value.Data0;
            step.ValueData1 = value.Data1;
            step.Value = value.Text;
            _steps[index] = step;
        }

        internal void CopyValue(int fromValueId, int toValueId)
        {
            var fromIndex = FindStepIndex(fromValueId);
            var toIndex = FindStepIndex(toValueId);
            if (fromIndex < 0 || toIndex < 0)
            {
                return;
            }

            var source = _steps[fromIndex];
            var target = _steps[toIndex];
            target.ValueKind = source.ValueKind;
            target.ValueData0 = source.ValueData0;
            target.ValueData1 = source.ValueData1;
            target.Value = source.Value;
            _steps[toIndex] = target;
        }

        public string GetValue(int valueId)
        {
            var index = FindStepIndex(valueId);
            return index >= 0 ? _steps[index].FormatValue() : null;
        }

        internal LineageValueKind GetValueKind(int valueId)
        {
            var index = FindStepIndex(valueId);
            return index >= 0 ? _steps[index].ValueKind : LineageValueKind.None;
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
            if (valueId <= 0 || parentId <= 0 || FindStepIndex(valueId) < 0)
            {
                return;
            }

            TryAddRelation(valueId, parentId);
        }

        public ProvenanceCollectionResult Collect(IReadOnlyCollection<int> roots)
        {
            var beforeSteps = _stepCount;
            var beforeRelations = _relationCount;
            if (_stepCount == 0)
            {
                return new ProvenanceCollectionResult(0, 0);
            }

            var existing = new HashSet<int>();
            for (var i = 0; i < _stepCount; i++)
            {
                if (_steps[i].Id > 0)
                {
                    existing.Add(_steps[i].Id);
                }
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
                if (stepId <= 0 || live.Contains(stepId) || !existing.Contains(stepId))
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
                    if (existing.Contains(parents[i]))
                    {
                        stack.Push(parents[i]);
                    }
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
                if (!live.Contains(relation.ChildStepId))
                {
                    continue;
                }

                if (existing.Contains(relation.ParentStepId) && !live.Contains(relation.ParentStepId))
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

            MaybeSpill(false);
            return new ProvenanceCollectionResult(beforeSteps - _stepCount, beforeRelations - _relationCount);
        }

        internal ProvenanceSnapshot CreateSnapshot()
        {
            var cold = _journal != null ? _journal.ReadSnapshot() : new ProvenanceSnapshot(null, null);
            var steps = new LineageStep[cold.Steps.Length + _stepCount];
            var relations = new LineageRelation[cold.Relations.Length + _relationCount];

            if (cold.Steps.Length > 0)
            {
                Array.Copy(cold.Steps, 0, steps, 0, cold.Steps.Length);
            }

            if (_stepCount > 0)
            {
                Array.Copy(_steps, 0, steps, cold.Steps.Length, _stepCount);
            }

            if (cold.Relations.Length > 0)
            {
                Array.Copy(cold.Relations, 0, relations, 0, cold.Relations.Length);
            }

            if (_relationCount > 0)
            {
                Array.Copy(_relations, 0, relations, cold.Relations.Length, _relationCount);
            }

            return new ProvenanceSnapshot(steps, relations);
        }

        public void Clear()
        {
            Array.Clear(_steps, 0, _stepCount);
            Array.Clear(_relations, 0, _relationCount);
            _stepCount = 0;
            _relationCount = 0;
            _dropped = false;

            var journal = _journal;
            _journal = null;
            if (journal != null)
            {
                journal.Dispose();
            }
        }

        private int FindStepIndex(int valueId)
        {
            if (valueId <= 0 || _stepCount == 0)
            {
                return -1;
            }

            var direct = valueId - 1;
            if (direct >= 0 && direct < _stepCount && _steps[direct].Id == valueId)
            {
                return direct;
            }

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
            MaybeSpill(false);
            if (_relationCount >= _relations.Length)
            {
                MaybeSpill(true);
            }

            if (_relationCount >= _relations.Length)
            {
                MarkDropped();
                return false;
            }

            _relations[_relationCount++] = new LineageRelation(childStepId, parentStepId);
            return true;
        }

        private void MaybeSpill(bool emergency)
        {
            if (!LineageSettings.ColdStorageEnabled || LineageSettings.ColdStorageMaxBytes <= 0 || _stepCount == 0)
            {
                return;
            }

            var configuredPageSteps = Math.Max(1, LineageSettings.ColdStoragePageSteps);
            if (_steps.Length <= configuredPageSteps)
            {
                return;
            }

            var highPercent = Clamp(LineageSettings.ColdStorageHighWatermarkPercent, 1, 99);
            var targetPercent = Clamp(LineageSettings.ColdStorageTargetPercent, 0, highPercent - 1);
            var highSteps = Math.Max(1, (_steps.Length * highPercent) / 100);
            var highRelations = Math.Max(1, (_relations.Length * highPercent) / 100);

            if (!emergency && _stepCount < highSteps && _relationCount < highRelations)
            {
                return;
            }

            var maxSpillable = _stepCount - 1;
            if (maxSpillable <= 0)
            {
                return;
            }

            EnsureJournal();
            var pageSteps = Math.Min(configuredPageSteps, maxSpillable);

            if (emergency)
            {
                var emergencyCount = Math.Min(pageSteps, Math.Max(1, _stepCount / 4));
                emergencyCount = Math.Min(emergencyCount, maxSpillable);
                if (_journal.CanAccept)
                {
                    var page = ExtractOldestPage(emergencyCount);
                    if (!_journal.TryEnqueue(page))
                    {
                        _journal.MarkTruncated();
                        MarkDropped();
                    }
                }
                else
                {
                    DiscardOldest(emergencyCount);
                    _journal.MarkTruncated();
                    MarkDropped();
                }

                return;
            }

            var targetSteps = (_steps.Length * targetPercent) / 100;
            var targetRelations = (_relations.Length * targetPercent) / 100;
            while ((_stepCount > targetSteps || _relationCount > targetRelations) && _stepCount > 1)
            {
                if (!_journal.CanAccept)
                {
                    break;
                }

                var stepExcess = Math.Max(0, _stepCount - targetSteps);
                var relationExcess = Math.Max(0, _relationCount - targetRelations);
                var relationDrivenSteps = relationExcess > 0 ? Math.Max(1, (relationExcess + 1) / 2) : 0;
                var desired = Math.Max(stepExcess, relationDrivenSteps);
                if (desired <= 0)
                {
                    break;
                }

                maxSpillable = _stepCount - 1;
                var count = Math.Min(pageSteps, Math.Min(maxSpillable, desired));
                if (count <= 0)
                {
                    break;
                }

                var page = ExtractOldestPage(count);
                if (!_journal.TryEnqueue(page))
                {
                    _journal.MarkTruncated();
                    MarkDropped();
                    break;
                }
            }
        }

        private void EnsureJournal()
        {
            if (_journal == null)
            {
                _journal = new ColdJournal(
                    LineageSettings.ColdStorageMaxBytes,
                    LineageSettings.ColdStorageQueuePages,
                    LineageSettings.ColdStorageDirectory);
            }
        }

        private ColdJournalPage ExtractOldestPage(int count)
        {
            count = Math.Max(1, Math.Min(count, _stepCount));
            var firstStepId = _steps[0].Id;
            var lastStepId = _steps[count - 1].Id;
            var steps = new LineageStep[count];
            Array.Copy(_steps, 0, steps, 0, count);

            var relationCount = 0;
            for (var i = 0; i < _relationCount; i++)
            {
                var child = _relations[i].ChildStepId;
                if (child >= firstStepId && child <= lastStepId)
                {
                    relationCount++;
                }
            }

            var relations = new LineageRelation[relationCount];
            var relationCopy = 0;
            var relationWrite = 0;
            for (var i = 0; i < _relationCount; i++)
            {
                var relation = _relations[i];
                if (relation.ChildStepId >= firstStepId && relation.ChildStepId <= lastStepId)
                {
                    relations[relationCopy++] = relation;
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

            var remaining = _stepCount - count;
            if (remaining > 0)
            {
                Array.Copy(_steps, count, _steps, 0, remaining);
            }

            Array.Clear(_steps, remaining, count);
            _stepCount = remaining;
            return new ColdJournalPage(steps, relations, firstStepId, lastStepId);
        }

        private void DiscardOldest(int count)
        {
            if (_stepCount == 0 || count <= 0)
            {
                return;
            }

            count = Math.Min(count, Math.Max(1, _stepCount - 1));
            var firstStepId = _steps[0].Id;
            var lastStepId = _steps[count - 1].Id;

            var relationWrite = 0;
            for (var i = 0; i < _relationCount; i++)
            {
                var relation = _relations[i];
                if (relation.ChildStepId >= firstStepId && relation.ChildStepId <= lastStepId)
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

            var remaining = _stepCount - count;
            if (remaining > 0)
            {
                Array.Copy(_steps, count, _steps, 0, remaining);
            }

            Array.Clear(_steps, remaining, count);
            _stepCount = remaining;
        }

        private void MarkDropped()
        {
            _dropped = true;
            LineageMetrics.MarkDropped();
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
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
