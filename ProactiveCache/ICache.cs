using System;
using System.Collections.Generic;

namespace ProactiveCache
{
    public delegate ICache<Tk, Tv> ExternalCacheFactory<Tk, Tv>(CacheExpiredHook<Tk, Tv> expired_delegate);

    public delegate void CacheExpiredHook<Tk, Tv>(IReadOnlyList<KeyValuePair<Tk, ICacheEntry<Tv>>> expired_items);

    public interface ICache<Tkey, Tval>
    {
        void Set(Tkey key, ICacheEntry<Tval> value, TimeSpan expiration_time);
        bool TryGet(Tkey key, out ICacheEntry<Tval> value);
        void Remove(Tkey key);
    }
}
