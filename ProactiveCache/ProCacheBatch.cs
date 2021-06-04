using ProactiveCache.Internal;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache
{
    public class ProCacheBatch<Tkey, Tval>
    {
        private const int DEFAULT_CACHE_EXPIRATION_SEC = 600;

        private struct Result
        {
            private readonly ProCacheEntry<Tval> _entry;
            private readonly TaskCompletionSource<Tval> _completion;

            public Result(ProCacheEntry<Tval> entry, TaskCompletionSource<Tval> completion = null)
            {
                _entry = entry;
                _completion = completion;
            }

            public bool Empty(TimeSpan outdated_ttl, out ProCacheEntry<Tval> entry)
            {
                entry = _entry;

                if (_completion is null)
                {
                    entry.Reset(outdated_ttl);
                    return true;
                }

                entry.Reset(null);
                _completion.SetResult(default(Tval));

                return false;
            }

            public bool Success(Tval val, TimeSpan outdated_ttl, out ProCacheEntry<Tval> entry)
            {
                entry = _entry;

                if (_completion is null)
                {
                    entry.Reset(val, outdated_ttl);
                    return true;
                }

                _completion.SetResult(val);
                entry.Reset(val, null);

                return false;
            }

            public bool Error(Exception ex)
            {
                if (_completion is null)
                {
                    _entry.Reset();
                    return false;
                }

                _completion.SetException(ex);
                return true;
            }
        }

        private struct WaitList
        {
            private List<(Tkey, ProCacheEntry<Tval>)> _vals;

            public bool IsEmpty => _vals is null;
            public List<(Tkey, ProCacheEntry<Tval>)> Values => _vals;

            public void Add(Tkey key, ProCacheEntry<Tval> result)
            {
                if (_vals is null)
                    _vals = new List<(Tkey, ProCacheEntry<Tval>)>();

                _vals.Add((key, result));
            }
        }

        private struct AsyncList
        {
            private Dictionary<Tkey, Result> _vals;

            public bool IsEmpty => _vals is null;
            public Dictionary<Tkey, Result> Values => _vals;

            public void Add(Tkey key, Result val)
            {
                if (_vals is null)
                    _vals = new Dictionary<Tkey, Result>();

                _vals.Add(key, val);
            }

        }

        private readonly ICache<Tkey, ICacheEntry<Tval>> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly bool _withSlidingUpdate;
        private readonly TimeSpan _expireTtl;
        private readonly Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> _get;
        private readonly ProCacheBatchHook<Tkey, Tval> _hook;
        private readonly ushort _maxQueueLength;
        private readonly string _queueLimitExceededMessage;


        public ProCacheBatch(Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> get, TimeSpan expire_ttl, ushort max_queue_length = ProCache.UNLIMITED_QUEUE_SIZE, ProCacheBatchHook<Tkey, Tval> hook, ExternalCacheFactory<Tkey, ICacheEntry<Tval>> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, max_queue_length, hook, external_cache)
        { }

        public ProCacheBatch(Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ushort max_queue_length = ProCache.UNLIMITED_QUEUE_SIZE, ProCacheBatchHook<Tkey, Tval> hook, ExternalCacheFactory<Tkey, ICacheEntry<Tval>> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            CacheExpiredHook<Tkey, ICacheEntry<Tval>> cacheExpired = null;
            if (hook != null)
            {
                cacheExpired = e => hook(e, ProCacheHookReason.Expired);
                _hook = (i, r) => Task.Factory.StartNew(DoCallback, (hook, i, r));
            }
            else
                _hook = null;

            _cache = external_cache == null ? new MemoryCache<Tkey, ICacheEntry<Tval>>(DEFAULT_CACHE_EXPIRATION_SEC, cacheExpired) : external_cache(cacheExpired);
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;
            _maxQueueLength = max_queue_length;

            _withSlidingUpdate = _outdateTtl.Ticks > 0;
            _queueLimitExceededMessage = $"Wait queue limit '{_maxQueueLength}' excedeed in ProCacheBatch<{typeof(Tkey)},{typeof(Tval)}>";
        }

        public ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> Get(IEnumerable<Tkey> keys, object state = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (TryGet(keys, out var result, state, cancellation))
                return result;

            throw new ProCacheQueueLimitExceededException(_queueLimitExceededMessage);
        }

        public bool TryGet(IEnumerable<Tkey> keys, out ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> result, object state = null, CancellationToken cancellation = default(CancellationToken))
        {
            var syncRes = new List<KeyValuePair<Tkey, Tval>>();
            var asyncRes = new AsyncList();
            var waitRes = new WaitList();
            List<KeyValuePair<Tkey, ICacheEntry<Tval>>> miss = null;
            List<KeyValuePair<Tkey, ICacheEntry<Tval>>> outdated = null;
            foreach (var key in keys)
            {
                if (_cache.TryGet(key, out var res))
                {
                    var entry = (ProCacheEntry<Tval>)res;
                    if (entry.IsCompleted)
                    {
                        if (_withSlidingUpdate && entry.Outdated())
                        {
                            asyncRes.Add(key, new Result(entry));
                            if (_hook != null)
                                (outdated = outdated ?? new List<KeyValuePair<Tkey, ICacheEntry<Tval>>>()).Add(new KeyValuePair<Tkey, ICacheEntry<Tval>>(key, entry));
                        }
                        else if (!entry.IsEmpty)
                            syncRes.Add(key, entry.GetCompletedValue());
                    }
                    else
                        waitRes.Add(key, entry);
                }
                else
                {
                    var exist = GetOrAdd(key, out var entry, out var completion, ref miss);
                    if (exist)
                    {
                        if (!entry.IsCompleted)
                            waitRes.Add(key, entry);
                        else if (!entry.IsEmpty)
                            syncRes.Add(key, entry.GetCompletedValue());
                    }
                    else
                        asyncRes.Add(key, new Result(entry, completion));
                }
            }

            if (_hook != null)
            {
                if (miss != null)
                    _hook(miss, ProCacheHookReason.Miss);
                if (outdated != null)
                    _hook(outdated, ProCacheHookReason.Outdated);
            }

            if(asyncRes.IsEmpty && waitRes.IsEmpty)
            {
                result = new ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>(syncRes);
                return true;
            }

            return asyncRes.IsEmpty && waitRes.IsEmpty ?
                new ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>(syncRes) :
                GetAsync(asyncRes, waitRes, syncRes, state, cancellation);
        }

        private bool GetOrAdd(Tkey key, out ProCacheEntry<Tval> entry, out TaskCompletionSource<Tval> completion, ref List<KeyValuePair<Tkey, ICacheEntry<Tval>>> miss)
        {
            lock (ProCache<Tkey>.GetLock((uint)key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                {
                    entry = (ProCacheEntry<Tval>)res;
                    completion = null;
                    return true;
                }

                completion = new TaskCompletionSource<Tval>();
                entry = new ProCacheEntry<Tval>(completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);

                if (_hook != null)
                    (miss = miss ?? new List<KeyValuePair<Tkey, ICacheEntry<Tval>>>()).Add(new KeyValuePair<Tkey, ICacheEntry<Tval>>(key, entry));

                return false;
            }
        }

        private async ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> GetAsync(AsyncList async_res, WaitList wait_res, List<KeyValuePair<Tkey, Tval>> results, object state, CancellationToken cancellation)
        {
            if (!async_res.IsEmpty)
            {
                var vals = async_res.Values;
                try
                {
                    var keys = new Tkey[vals.Count];
                    vals.Keys.CopyTo(keys, 0);
                    foreach (var val in await _get(keys, state, cancellation).ConfigureAwait(false))
                    {
                        if (vals.TryGetValue(val.Key, out var result))
                        {
                            if (result.Success(val.Value, _outdateTtl, out var entry))
                                _cache.Set(val.Key, entry, _expireTtl);
                            vals.Remove(val.Key);
                            results.Add(val);
                        }
                    }
                    foreach (var val in vals)
                        if (val.Value.Empty(_outdateTtl, out var entry))
                            _cache.Set(val.Key, entry, _expireTtl);
                }
                catch (Exception ex)
                {
                    foreach (var entry in vals)
                        if (entry.Value.Error(ex))
                            _cache.Remove(entry.Key);
                    throw;
                }
            }

            if (!wait_res.IsEmpty)
            {
                var vals = wait_res.Values;
                var size = vals.Count;
                for (var i = 0; i < size; i++)
                {
                    var val = vals[i];
                    var res = val.Item2.IsCompleted ? val.Item2.GetCompletedValue() : await val.Item2.GetValue().ConfigureAwait(false);
                    if (!val.Item2.IsEmpty)
                        results.Add(new KeyValuePair<Tkey, Tval>(val.Item1, res));
                }
            }

            return results;
        }

        private static void DoCallback(object state)
        {
            var (callback, items, reason) = ((ProCacheBatchHook<Tkey, Tval>, IReadOnlyList<KeyValuePair<Tkey, ICacheEntry<Tval>>>, ProCacheHookReason))state;
            callback(items, reason);
        }
    }
}
