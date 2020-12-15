using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SlidingCache.Internal;

namespace SlidingCache
{
    public static class SlidingCache
    {
        public delegate ValueTask<Tval> Get<Tkey, Tval>(Tkey key, object state, CancellationToken cancellation);

        internal struct Options<Tk, Tv>
        {
            public readonly ICache<Tk, Tv> ExternalCache;
            public readonly TimeSpan ExpireTtl;
            public readonly TimeSpan OutdateTtl;

            public Options(ICache<Tk, Tv> external_cache, TimeSpan expire_ttl, TimeSpan outdate_ttl)
            {
                ExternalCache = external_cache;
                ExpireTtl = expire_ttl;
                OutdateTtl = outdate_ttl;
            }
        }

        internal static Options<Tk, Tv> CreateOptions<Tk, Tv>(TimeSpan expire_ttl, TimeSpan outdate_ttl, ICache<Tk, Tv> external_cache = null)
            => new Options<Tk, Tv>(external_cache, expire_ttl, outdate_ttl);

        internal static SlidingCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Get<Tk, Tv> getter)
            => new SlidingCache<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, options.ExternalCache);
    }

    public class SlidingCache<Tkey, Tval>
    {
        private readonly ICache<Tkey, Tval> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly TimeSpan _expireTtl;
        private readonly SlidingCache.Get<Tkey, Tval> _get;

        public SlidingCache(SlidingCache.Get<Tkey, Tval> get, TimeSpan expire_ttl, ICache<Tkey, Tval> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, external_cache)
        { }

        public SlidingCache(SlidingCache.Get<Tkey, Tval> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ICache<Tkey, Tval> external_cache = null)
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

            var entry = (SlidingCacheEntry<Tval>)res;
            if (_outdateTtl.Ticks > 0 && entry.Outdated())
                return UpdateAsync(key, entry, state, cancellation);

            return entry.GetValue();
        }

        private ValueTask<Tval> Add(Tkey key, object state, CancellationToken cancellation)
        {
            TaskCompletionSource<(bool, Tval)> completion;
            SlidingCacheEntry<Tval> entry;
            var lockObject = SlidingCache<Tval>.GetLock(key.GetHashCode());
            lock (lockObject)
            {
                if (_cache.TryGet(key, out var res))
                    return ((SlidingCacheEntry<Tval>)res).GetValue();

                completion = new TaskCompletionSource<(bool, Tval)>();
                entry = new SlidingCacheEntry<Tval>(completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);
            }

            return AddAsync(key,completion, state, cancellation);
        }

        private async ValueTask<Tval> UpdateAsync(Tkey key, SlidingCacheEntry<Tval> entry, object state, CancellationToken cancellation)
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
                try { _cache.Remove(key); } catch { }
                completion.SetException(ex);
                throw;
            }
        }
    }
}
