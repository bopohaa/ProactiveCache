using System.Linq;

namespace SlidingCache.Internal
{
    internal static class BatchSlidingCache<Tk>
    {
        static object[] _locks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();

        public static object GetLock(int hash) => _locks[hash % 256];
    }
}
