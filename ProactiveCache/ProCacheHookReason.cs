using System;
using System.Collections.Generic;
using System.Text;

namespace ProactiveCache
{
    public enum ProCacheHookReason
    {
        Miss,
        Outdated,
        Expired,
    }

    public delegate void ProCacheHook<Tk, Tv>(Tk key, ICacheEntry<Tv> value, ProCacheHookReason reason);

    public delegate void ProCacheBatchHook<Tk, Tv>(IReadOnlyList<KeyValuePair<Tk, ICacheEntry<Tv>>> items, ProCacheHookReason reason);
}
