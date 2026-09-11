using System.Threading;

namespace Lineage
{
    public static class LineageMetrics
    {
        private static long _eventCount;
        private static long _produceTicks;
        private static int _dropped;

        public static long EventCount => Interlocked.Read(ref _eventCount);
        public static long ProduceTicks => Interlocked.Read(ref _produceTicks);
        public static bool EventsDropped => Volatile.Read(ref _dropped) != 0;

        internal static void AddProduce(long ticks)
        {
            Interlocked.Increment(ref _eventCount);
            Interlocked.Add(ref _produceTicks, ticks);
        }

        internal static void MarkDropped()
        {
            Volatile.Write(ref _dropped, 1);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _eventCount, 0);
            Interlocked.Exchange(ref _produceTicks, 0);
            Volatile.Write(ref _dropped, 0);
        }
    }
}
