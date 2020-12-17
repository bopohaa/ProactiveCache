using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlidingCache.Internal
{
    internal static class SCache
    {
        internal static ValueTask<T> AsValueTask<T>(this Task<T> task) => new ValueTask<T>(task);

        internal static void TryAddValue<Tkey, Tval>(this List<KeyValuePair<Tkey, Tval>> dst, Tkey key, (bool, Tval) value)
        {
            if (value.Item1)
                dst.Add(new KeyValuePair<Tkey, Tval>(key, value.Item2));
        }
    }

    internal static class SCache<Tk>
    {
        static object[] _locks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();

        public static object GetLock(int hash) => _locks[hash % 256];
    }
}
