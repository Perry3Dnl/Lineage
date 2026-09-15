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

        // Compatibility view for existing consumers. Raw capture is stored as separate
        // step and relation arrays; events are hydrated only when explicitly requested.
        public LineageEvent[] Events
        {
            get
            {
                var snapshot = CreateSnapshot();
                var steps = snapshot.Steps;
                var relations = snapshot.Relations;
                var events = new LineageEvent[steps.Length];
                var indexes = new Dictionary<int, int>(steps.Length);
                for (var i = 0; i < steps.Length; i++)
                {
                    var step = steps[i];
                    indexes[step.Id] = i;
                    events[i] = new LineageEvent
                    {
                        LocationId = step.LocationId,
                        ValueId = step.Id,
                        Kind = step.Kind,
                        Value = step.Value,
                        TypeName = step.TypeName
                    };
                }

                for (var i = 0; i < relations.Length; i++)
                {
                    var relation = relations[i];
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
            // Method-entry and other context-only events have no produced value and do
            // not belong in the raw value-step table.
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
            // The parent may already be cold. Only the child must still be in the hot
            // store because the relation itself belongs to the child's page.
            if (valueId <= 0 || parentId <= 0 || FindStepIndex(valueId) < 0)
            {
                return;
            }

            TryAddRelation(valueId, parentId);
        }

        /// <summary>
        /// Keeps only hot steps reachable from the supplied live roots. Parents that have
        /// already moved to cold storage remain as cross-tier relations and are resolved
        /// later during explicit report hydration.
        /// </summary>
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

                // A missing parent can be a valid cold parent. A parent that was hot but
                // was not live is genuinely dead and the relation can be discarded.
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
            // Steps can contain captured strings, so clear the used range to release
            // references immediately when the raw session is discarded.
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

            // Before the first compaction/spill IDs and slots line up, which covers the
            // overwhelmingly common hot-path SetValue immediately after Produce.
            var direct = valueId - 1;
            if (direct >= 0 && direct < _stepCount && _steps[direct].Id == valueId)
            {
                return direct;
            }

            // Compaction and spilling preserve ascending Step IDs, so no permanent hash
            // index is required just to keep stable identities after reclamation.
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

            var highPercent = Clamp(LineageSettings.ColdStorageHighWatermarkPercent, 1, 99);
            var targetPercent = Clamp(LineageSettings.ColdStorageTargetPercent, 0, highPercent - 1);
            var highSteps = Math.Max(1, (_steps.Length * highPercent) / 100);
            var highRelations = Math.Max(1, (_relations.Length * highPercent) / 100);

            if (!emergency && _stepCount < highSteps && _relationCount < highRelations)
            {
                return;
            }

            EnsureJournal();
            var pageSteps = Math.Max(1, Math.Min(LineageSettings.ColdStoragePageSteps, _steps.Length));

            if (emergency)
            {
                // One small oldest slice is sufficient to regain headroom. Never spill
                // the entire hot store merely because a tiny test/application capacity is
                // smaller than the configured page size.
                var emergencyCount = Math.Min(pageSteps, Math.Max(1, _stepCount / 4));
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
            while ((_stepCount > targetSteps || _relationCount > targetRelations) && _stepCount > 0)
            {
                if (!_journal.CanAccept)
                {
                    // The writer is behind, but there is still hot headroom. Return to the
                    // application immediately and try again on a later pressure check.
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

                var count = Math.Min(pageSteps, Math.Min(_stepCount, desired));
                var page = ExtractOldestPage(count);
                if (!_journal.TryEnqueue(page))
                {
                    // The page has already left the hot store. Record the gap rather than
                    // synchronously retrying I/O on the application thread.
                    _journal.MarkTruncated();
                    MarkDropped();
                    break;
                }
            }
        }

        private void EnsureJournal()
        {
            if (_journal != null)
            {
                return;
            }

            _journal = new ColdJournal(
                LineageSettings.ColdStorageMaxBytes,
                LineageSettings.ColdStorageQueuePages,
                LineageSettings.ColdStorageDirectory);
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

            count = Math.Min(count, _stepCount);
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
