namespace Lineage
{
    internal sealed class EventBuffer
    {
        private readonly LineageEvent[] _events;
        private int _count;
        private bool _dropped;

        public EventBuffer(int capacity)
        {
            _events = new LineageEvent[capacity];
        }

        public int Count => _count;
        public bool Dropped => _dropped;
        public LineageEvent[] Events => _events;

        public bool TryAdd(LineageEvent ev)
        {
            if (_count >= _events.Length)
            {
                _dropped = true;
                LineageMetrics.MarkDropped();
                return false;
            }

            _events[_count] = ev;
            _count++;
            return true;
        }

        public void AttachParent(int valueId, int parentId)
        {
            if (valueId <= 0 || parentId <= 0)
            {
                return;
            }

            for (var i = _count - 1; i >= 0; i--)
            {
                if (_events[i].ValueId != valueId)
                {
                    continue;
                }

                if (_events[i].Parent0 <= 0)
                {
                    _events[i].Parent0 = parentId;
                    return;
                }

                if (_events[i].Parent1 <= 0 && _events[i].Parent0 != parentId)
                {
                    _events[i].Parent1 = parentId;
                }

                return;
            }
        }

        public void Clear()
        {
            _count = 0;
            _dropped = false;
        }
    }
}
