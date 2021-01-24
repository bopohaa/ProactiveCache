using ProactiveCache.Internal;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache
{
    public class ProCache<Tkey, Tval>
    {
        private const int DEFAULT_CACHE_EXPIRATION_SEC = 600;

        private readonly ICache<Tkey, Tval> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly TimeSpan _expireTtl;
        private readonly Func<Tkey, object, CancellationToken, ValueTask<Tval>> _get;
        private readonly ProCacheHook<Tkey, Tval> _hook;

        public ProCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, ProCacheHook<Tkey, Tval> hook = null, ExternalCacheFactory<Tkey,Tval> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, hook, external_cache)
        { }

        public ProCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ProCacheHook<Tkey, Tval> hook = null, ExternalCacheFactory<Tkey, Tval> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            CacheExpiredHook<Tkey, Tval> cacheExpired = null;
            if (hook != null)
            {
                cacheExpired = e => { foreach (var i in e) try { hook(i.Key, i.Value, ProCacheHookReason.Expired); } catch { } };
                _hook = (k, v, r) => Task.Factory.StartNew(DoCallback, (hook, k, v, r));
            }
            else
                _hook = null;

            _cache = external_cache == null ? new MemoryCache<Tkey, Tval>(DEFAULT_CACHE_EXPIRATION_SEC, cacheExpired) : external_cache(cacheExpired);
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;
        }

        public ValueTask<Tval> Get(Tkey key, CancellationToken cancellation = default(CancellationToken))
            => Get(key, null, cancellation);

        public ValueTask<Tval> Get(Tkey key, object state, CancellationToken cancellation = default(CancellationToken))
        {
            if (!_cache.TryGet(key, out var res))
                return Add(key, state, cancellation);

            var entry = (ProCacheEntry<Tval>)res;
            if (_outdateTtl.Ticks > 0 && entry.Outdated())
                return UpdateAsync(key, entry, state, cancellation);

            return entry.GetValue();
        }

        private ValueTask<Tval> Add(Tkey key, object state, CancellationToken cancellation)
        {
            TaskCompletionSource<(bool, Tval)> completion;
            ICacheEntry<Tval> entry;
            lock (ProCache<Tkey>.GetLock((uint)key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                    return ((ProCacheEntry<Tval>)res).GetValue();

                completion = new TaskCompletionSource<(bool, Tval)>();
                entry = new ProCacheEntry<Tval>(completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);
            }

            return AddAsync(key, completion, state, entry, cancellation);
        }

        private async ValueTask<Tval> UpdateAsync(Tkey key, ProCacheEntry<Tval> entry, object state, CancellationToken cancellation)
        {
            try
            {
                _hook?.Invoke(key, entry, ProCacheHookReason.Outdated);

                var res = await _get(key, state, cancellation).ConfigureAwait(false);
                entry.Reset(res, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);

                return res;
            }
            catch
            {
                entry.Reset();
                throw;
            }
        }

        private async ValueTask<Tval> AddAsync(Tkey key, TaskCompletionSource<(bool, Tval)> completion, object state, ICacheEntry<Tval> entry, CancellationToken cancellation)
        {
            try
            {
                _hook?.Invoke(key, entry, ProCacheHookReason.Miss);

                var res = await _get(key, state, cancellation).ConfigureAwait(false);
                completion.SetResult((true, res));

                return res;
            }
            catch (Exception ex)
            {
                _cache.Remove(key);
                completion.SetException(ex);
                throw;
            }
        }

        private static void DoCallback(object state)
        {
            var (callback, key, value, reason) = ((ProCacheHook<Tkey, Tval>, Tkey, ICacheEntry<Tval>, ProCacheHookReason))state;
            callback(key, value, reason);
        }
    }
}
