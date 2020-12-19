using System;

namespace ProactiveCache
{
    public interface ICache<Tkey, Tval>
    {
        void Set(Tkey key, ICacheEntry<Tval> value, TimeSpan expiration_time);
        bool TryGet(Tkey key, out ICacheEntry<Tval> value);
        void Remove(Tkey key);
    }
}
