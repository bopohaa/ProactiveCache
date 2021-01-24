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
            private enum ResultState { Add, Outdate }

            private readonly ResultState _state;
            private readonly object _completion;

            public Result(TaskCompletionSource<(bool, Tval)> add_completion)
            {
                _state = ResultState.Add;
                _completion = add_completion;
            }

            public Result(ProCacheEntry<Tval> cache_entry)
            {
                _state = ResultState.Outdate;
                _completion = cache_entry;
            }

            public bool Empty(TimeSpan outdated_ttl, out ProCacheEntry<Tval> entry)
            {
                switch (_state)
                {
                    case ResultState.Add:
                        ((TaskCompletionSource<(bool, Tval)>)_completion).SetResult((false, default(Tval)));
                        entry = null;
                        return false;
                    case ResultState.Outdate:
                        entry = (ProCacheEntry<Tval>)_completion;
                        entry.Reset(outdated_ttl);
                        return true;
                }

                throw new NotImplementedException();
            }

            public bool Success(Tval val, TimeSpan outdated_ttl, out ProCacheEntry<Tval> entry)
            {
                switch (_state)
                {
                    case ResultState.Add:
                        ((TaskCompletionSource<(bool, Tval)>)_completion).SetResult((true, val));
                        entry = null;
                        return false;
                    case ResultState.Outdate:
                        entry = (ProCacheEntry<Tval>)_completion;
                        entry.Reset(val, outdated_ttl);
                        return true;
                }

                throw new NotImplementedException();
            }

            public bool Error(Exception ex)
            {
                switch (_state)
                {
                    case ResultState.Add:
                        ((TaskCompletionSource<(bool, Tval)>)_completion).SetException(ex);
                        return true;
                    case ResultState.Outdate:
                        ((ProCacheEntry<Tval>)_completion).Reset();
                        return false;
                }

                throw new NotImplementedException();
            }
        }

        private struct WaitList
        {
            private List<(Tkey, Task<(bool, Tval)>)> _vals;

            public bool IsEmpty => _vals is null;
            public List<(Tkey, Task<(bool, Tval)>)> Values => _vals;

            public void Add(Tkey key, Task<(bool, Tval)> result)
            {
                if (_vals is null)
                    _vals = new List<(Tkey, Task<(bool, Tval)>)>();

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

        private readonly ICache<Tkey, Tval> _cache;
        private readonly TimeSpan _outdateTtl;
        private readonly bool _withSlidingUpdate;
        private readonly TimeSpan _expireTtl;
        private readonly Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> _get;
        private readonly ProCacheBatchHook<Tkey, Tval> _hook;

        public ProCacheBatch(Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> get, TimeSpan expire_ttl, ProCacheBatchHook<Tkey, Tval> hook, ExternalCacheFactory<Tkey, Tval> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, hook, external_cache)
        { }

        public ProCacheBatch(Func<Tkey[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ProCacheBatchHook<Tkey, Tval> hook, ExternalCacheFactory<Tkey, Tval> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            CacheExpiredHook<Tkey, Tval> cacheExpired = null;
            if (hook != null)
            {
                cacheExpired = e => hook(e, ProCacheHookReason.Expired);
                _hook = (i, r) => Task.Factory.StartNew(DoCallback, (hook, i, r));
            }
            else
                _hook = null;

            _cache = external_cache == null ? new MemoryCache<Tkey, Tval>(DEFAULT_CACHE_EXPIRATION_SEC, cacheExpired) : external_cache(cacheExpired);
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;

            _withSlidingUpdate = _outdateTtl.Ticks > 0;
        }

        public ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> Get(IEnumerable<Tkey> keys, CancellationToken cancellation = default(CancellationToken))
            => Get(keys, null, cancellation);

        public ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> Get(IEnumerable<Tkey> keys, object state, CancellationToken cancellation = default(CancellationToken))
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
                        else
                            syncRes.TryAddValue(key, entry.GetCompletedValue());
                    }
                    else
                        waitRes.Add(key, entry.GetValueWithState().AsTask());
                }
                else
                {
                    if (GetOrAdd(key, out var result, ref miss))
                    {
                        var entry = (ProCacheEntry<Tval>)result;
                        if (entry.IsCompleted)
                            syncRes.TryAddValue(key, entry.GetCompletedValue());
                        else
                            waitRes.Add(key, entry.GetValueWithState().AsTask());
                    }
                    else
                        asyncRes.Add(key, new Result((TaskCompletionSource<(bool, Tval)>)result));
                }
            }

            if (_hook != null)
            {
                if (miss != null)
                    _hook(miss, ProCacheHookReason.Miss);
                if (outdated != null)
                    _hook(outdated, ProCacheHookReason.Outdated);
            }

            return asyncRes.IsEmpty && waitRes.IsEmpty ?
                new ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>(syncRes) :
                GetAsync(asyncRes, waitRes, syncRes, state, cancellation);
        }

        private bool GetOrAdd(Tkey key, out object result, ref List<KeyValuePair<Tkey, ICacheEntry<Tval>>> miss)
        {
            lock (ProCache<Tkey>.GetLock((uint)key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                {
                    result = res;
                    return true;
                }

                var addCompletion = new TaskCompletionSource<(bool, Tval)>();
                var entry = new ProCacheEntry<Tval>(addCompletion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);

                if (_hook != null)
                    (miss = miss ?? new List<KeyValuePair<Tkey, ICacheEntry<Tval>>>()).Add(new KeyValuePair<Tkey, ICacheEntry<Tval>>(key, entry));

                result = addCompletion;
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
                    var res = val.Item2.IsCompleted ? val.Item2.Result : await val.Item2;
                    if (res.Item1)
                        results.Add(new KeyValuePair<Tkey, Tval>(val.Item1, res.Item2));
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
