using ProactiveCache.Internal;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache
{
    public static class ProCache
    {
        public const ushort UNLIMITED_QUEUE_SIZE = ushort.MaxValue;

        internal static void Add<Tkey, Tval>(this List<KeyValuePair<Tkey, Tval>> dst, Tkey key, Tval value)
        {
            dst.Add(new KeyValuePair<Tkey, Tval>(key, value));
        }

    }

    public class ProCache<Tkey, Tval>
    {
        private const int DEFAULT_CACHE_EXPIRATION_SEC = 600;

        private readonly ICache<Tkey, ICacheEntry<Tval>> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly TimeSpan _expireTtl;
        private readonly Func<Tkey, object, CancellationToken, ValueTask<Tval>> _get;
        private readonly ProCacheHook<Tkey, Tval> _hook;
        private readonly ushort _maxQueueLength;
        private readonly string _queueLimitExceededMessage;

        public ProCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, ushort max_queue_length = ProCache.UNLIMITED_QUEUE_SIZE, ProCacheHook<Tkey, Tval> hook = null, ExternalCacheFactory<Tkey, ICacheEntry<Tval>> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, max_queue_length, hook, external_cache)
        { }

        public ProCache(Func<Tkey, object, CancellationToken, ValueTask<Tval>> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ushort max_queue_size = ProCache.UNLIMITED_QUEUE_SIZE, ProCacheHook<Tkey, Tval> hook = null, ExternalCacheFactory<Tkey, ICacheEntry<Tval>> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            CacheExpiredHook<Tkey, ICacheEntry<Tval>> cacheExpired = null;
            if (hook != null)
            {
                cacheExpired = e => { foreach (var i in e) try { hook(i.Key, i.Value, ProCacheHookReason.Expired); } catch { } };
                _hook = (k, v, r) => Task.Factory.StartNew(DoCallback, (hook, k, v, r));
            }
            else
                _hook = null;

            _cache = external_cache == null ? new MemoryCache<Tkey, ICacheEntry<Tval>>(DEFAULT_CACHE_EXPIRATION_SEC, cacheExpired) : external_cache(cacheExpired);
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;
            _maxQueueLength = max_queue_size;
            _queueLimitExceededMessage = $"Wait queue limit '{_maxQueueLength}' excedeed in ProCache<{typeof(Tkey)},{typeof(Tval)}>";
        }

        public ValueTask<Tval> Get(Tkey key, object state = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (TryGet(key, out var result, state, cancellation))
                return result;

            throw new ProCacheQueueLimitExceededException(_queueLimitExceededMessage);
        }

        public bool TryGet(Tkey key, out ValueTask<Tval> result, object state = null, CancellationToken cancellation = default(CancellationToken))
        {
            ProCacheEntry<Tval> entry;
            if (_cache.TryGet(key, out var res))
            {
                entry = (ProCacheEntry<Tval>)res;
                if (_outdateTtl.Ticks > 0 && entry.Outdated())
                {
                    result = UpdateAsync(key, entry, state, cancellation);
                    return true;
                }
            }
            else
                entry = Add(key, state, cancellation);

            return TryEnterWaitQueue(entry, out result);
        }

        private ProCacheEntry<Tval> Add(Tkey key, object state, CancellationToken cancellation)
        {
            TaskCompletionSource<Tval> completion;
            ProCacheEntry<Tval> entry;
            lock (ProCache<Tkey>.GetLock((uint)key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                    return (ProCacheEntry<Tval>)res;

                completion = new TaskCompletionSource<Tval>();
                entry = new ProCacheEntry<Tval>(completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);
            }

            AddAsync(key, completion, state, entry, cancellation);

            return entry;
        }

        private bool TryEnterWaitQueue(ProCacheEntry<Tval> entry, out ValueTask<Tval> result)
        {
            if (_maxQueueLength == ProCache.UNLIMITED_QUEUE_SIZE || entry.TryEnterQueue(_maxQueueLength))
            {
                result = entry.GetValue();
                return true;
            }

            result = default;
            return false;

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

        private void AddAsync(Tkey key, TaskCompletionSource<Tval> completion, object state, ProCacheEntry<Tval> entry, CancellationToken cancellation)
        {
            try
            {
                _hook?.Invoke(key, entry, ProCacheHookReason.Miss);

                var task = _get(key, state, cancellation);
                if (task.IsCompleted)
                {
                    var res = task.Result;
                    entry.Reset(res, null);
                    completion.SetResult(res);
                }
                else
                    AddAsync(task, key, completion, entry);
            }
            catch (Exception ex)
            {
                _cache.Remove(key);
                completion.TrySetException(ex);
            }
        }

        private async void AddAsync(ValueTask<Tval> task, Tkey key, TaskCompletionSource<Tval> completion, ProCacheEntry<Tval> entry)
        {
            try
            {
                var res = await task.ConfigureAwait(false);
                entry.Reset(res, null);
                completion.SetResult(res);
            }
            catch (Exception ex)
            {
                _cache.Remove(key);
                completion.SetException(ex);
            }
        }

        private static void DoCallback(object state)
        {
            var (callback, key, value, reason) = ((ProCacheHook<Tkey, Tval>, Tkey, ICacheEntry<Tval>, ProCacheHookReason))state;
            callback(key, value, reason);
        }
    }
}
