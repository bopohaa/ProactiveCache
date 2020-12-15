using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SlidingCache.Internal;

namespace SlidingCache
{
    public static class BatchSlidingCache
    {
        public delegate ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> Get<Tkey, Tval>(IEnumerable<Tkey> keys, object state, CancellationToken cancellation);

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

        internal static BatchSlidingCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Get<Tk, Tv> getter)
            => new BatchSlidingCache<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

        internal static void TryAddValue<Tkey, Tval>(this List<KeyValuePair<Tkey, Tval>> dst, Tkey key, (bool, Tval) value)
        {
            if (value.Item1)
                dst.Add(new KeyValuePair<Tkey, Tval>(key, value.Item2));
        }
    }

    public class BatchSlidingCache<Tkey, Tval>
    {
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

            public Result(SlidingCacheEntry<Tval> cache_entry)
            {
                _state = ResultState.Outdate;
                _completion = cache_entry;
            }

            public bool Empty(TimeSpan outdated_ttl, out SlidingCacheEntry<Tval> entry)
            {
                switch (_state)
                {
                    case ResultState.Add:
                        ((TaskCompletionSource<(bool, Tval)>)_completion).SetResult((false, default));
                        entry = default;
                        return false;
                    case ResultState.Outdate:
                        entry = (SlidingCacheEntry<Tval>)_completion;
                        entry.Reset(outdated_ttl);
                        return true;
                }

                throw new NotImplementedException();
            }

            public bool Success(Tval val, TimeSpan outdated_ttl, out SlidingCacheEntry<Tval> entry)
            {
                switch (_state)
                {
                    case ResultState.Add:
                        ((TaskCompletionSource<(bool, Tval)>)_completion).SetResult((true, val));
                        entry = default;
                        return false;
                    case ResultState.Outdate:
                        entry = (SlidingCacheEntry<Tval>)_completion;
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
                        ((SlidingCacheEntry<Tval>)_completion).Reset();
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
        private readonly BatchSlidingCache.Get<Tkey, Tval> _get;

        public BatchSlidingCache(BatchSlidingCache.Get<Tkey, Tval> get, TimeSpan expire_ttl, ICache<Tkey, Tval> external_cache = null) :
            this(get, expire_ttl, TimeSpan.Zero, external_cache)
        { }

        public BatchSlidingCache(BatchSlidingCache.Get<Tkey, Tval> get, TimeSpan expire_ttl, TimeSpan outdate_ttl, ICache<Tkey, Tval> external_cache = null)
        {
            if (outdate_ttl > expire_ttl)
                throw new ArgumentException("Must be less expire ttl", nameof(outdate_ttl));

            _cache = external_cache ?? new MemoryCache<Tkey, Tval>();
            _outdateTtl = outdate_ttl;
            _expireTtl = expire_ttl;
            _get = get;

            _withSlidingUpdate = _outdateTtl.Ticks > 0;
        }

        public ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> Get(IEnumerable<Tkey> keys, object state, CancellationToken cancellation = default)
        {
            var syncRes = new List<KeyValuePair<Tkey, Tval>>();
            var asyncRes = new AsyncList();
            var waitRes = new WaitList();
            foreach (var key in keys)
            {
                if (_cache.TryGet(key, out var res))
                {
                    var entry = (SlidingCacheEntry<Tval>)res;
                    if (entry.IsCompleted)
                    {
                        if (_withSlidingUpdate && entry.Outdated())
                            asyncRes.Add(key, new Result(entry));
                        else
                            syncRes.TryAddValue(key, entry.GetCompletedValue());
                    }
                    else
                        waitRes.Add(key, entry.GetValueWithState().AsTask());
                }
                else
                {
                    var add = Add(key, out var add_completion);
                    if (add.Item1)
                        asyncRes.Add(key, new Result(add_completion));
                    else if (add.Item2.IsCompleted)
                        syncRes.TryAddValue(key, add.Item2.GetCompletedValue());
                    else
                        waitRes.Add(key, add.Item2.GetValueWithState().AsTask());
                }
            }

            return asyncRes.IsEmpty && waitRes.IsEmpty ?
                new ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>>(syncRes) :
                GetAsync(asyncRes, waitRes, syncRes, state, cancellation);
        }

        private (bool, SlidingCacheEntry<Tval>) Add(Tkey key, out TaskCompletionSource<(bool, Tval)> add_completion)
        {
            lock (SlidingCache<Tval>.GetLock(key.GetHashCode()))
            {
                if (_cache.TryGet(key, out var res))
                {
                    add_completion = null;
                    return (false, (SlidingCacheEntry<Tval>)res);
                }

                add_completion = new TaskCompletionSource<(bool, Tval)>();
                var entry = new SlidingCacheEntry<Tval>(add_completion.Task, _outdateTtl);
                _cache.Set(key, entry, _expireTtl);

                return (true, entry);
            }
        }

        private async ValueTask<IEnumerable<KeyValuePair<Tkey, Tval>>> GetAsync(AsyncList async_res, WaitList wait_res, List<KeyValuePair<Tkey, Tval>> results, object state, CancellationToken cancellation)
        {
            if (!async_res.IsEmpty)
            {
                var vals = async_res.Values;
                try
                {
                    foreach (var val in await _get(vals.Keys, state, cancellation).ConfigureAwait(false))
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
    }
}
