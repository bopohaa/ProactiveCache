using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ProactiveCache.Internal
{
    internal static class ProCache<Tk>
    {
        static object[] _locks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();

        public static object GetLock(uint hash) => _locks[hash % 256];
    }
}
