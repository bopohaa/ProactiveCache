using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SlidingCacheTests.Internal
{
    internal class Counter
    {
        public bool WithThrow { get; set; }

        private int _count;
        public int Count => _count;

        public int Inc() => Interlocked.Increment(ref _count);

        public void TryDoThrow()
        {
            if (WithThrow)
                throw new Exception();
        }
    }

    internal class CounterForBatch : Counter
    {
        private ConcurrentStack<int> _keysCount = new ConcurrentStack<int>();
        public int[] GetKeysCount() => _keysCount.ToArray();

        public void AddKeysCount(int keys_count) => _keysCount.Push(keys_count);
    }
}
