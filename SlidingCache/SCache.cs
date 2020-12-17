using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using SlidingCache.Internal;

namespace SlidingCache
{
    public class SCache<Tkey, Tval>
    {
        private readonly ICache<Tkey, Tval> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly TimeSpan _expireTtl;
        private readonly Func<Tkey, object, CancellationToken, ValueTask<Tval>> _get;

        public SCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, ICache<Tkey, Tval> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, external_cache)
        { }

        public SCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ICache<Tkey, Tval> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            _cache = external_cache ?? new MemoryCache<Tkey, Tval>();
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;
        }

        public ValueTask<Tval> Get(Tkey key, object state, CancellationToken cancellation = default)
        {
            if (!_cache.TryGet(key, out var res))
                return Add(key, state, cancellation);

            var entry = (SCacheEntry<Tval>)res;
            if (_outdateTtl.Ticks > 0 && entry.Outdated())
                return UpdateAsync(key, entry, state, cancellation);

            return entry.GetValue();
        }

        private ValueTask<Tval> Add(Tkey key, object state, CancellationToken cancellation)
        {
            TaskCompletionSource<(bool, Tval)> completion;
            lock (SCache<Tkey>.GetLock(key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                    return ((SCacheEntry<Tval>)res).GetValue();

                completion = new TaskCompletionSource<(bool, Tval)>();
                var entry = new SCacheEntry<Tval>(completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);
            }

            return AddAsync(key, completion, state, cancellation);
        }

        private async ValueTask<Tval> UpdateAsync(Tkey key, SCacheEntry<Tval> entry, object state, CancellationToken cancellation)
        {
            try
            {
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

        private async ValueTask<Tval> AddAsync(Tkey key, TaskCompletionSource<(bool, Tval)> completion, object state, CancellationToken cancellation)
        {
            try
            {
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
    }
}
