using System;

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
                    var index = relation.ChildStepId - 1;
                    if (index < 0 || index >= _stepCount)
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
            var index = valueId - 1;
            if (index < 0 || index >= _stepCount || string.IsNullOrEmpty(value))
            {
                return;
            }

            var step = _steps[index];
            if (step.Id != valueId)
            {
                return;
            }

            step.Value = value;
            _steps[index] = step;
        }

        public string GetValue(int valueId)
        {
            var index = valueId - 1;
            if (index < 0 || index >= _stepCount)
            {
                return null;
            }

            var step = _steps[index];
            return step.Id == valueId ? step.Value : null;
        }

        public void AttachParent(int valueId, int parentId)
        {
            if (valueId <= 0 || parentId <= 0 || valueId > _stepCount)
            {
                return;
            }

            TryAddRelation(valueId, parentId);
        }

        public void Clear()
        {
            // Steps can contain captured strings, so clear the used range to release
            // references immediately when the raw session is discarded.
            Array.Clear(_steps, 0, _stepCount);
            _stepCount = 0;
            _relationCount = 0;
            _dropped = false;
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
}
