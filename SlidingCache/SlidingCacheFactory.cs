using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SlidingCache;
using SlidingCache.Internal;

public static class SlidingCacheFactory
{
    public class Options<Tk, Tv>
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

    public static Options<Tk, Tv> CreateOptions<Tk, Tv>(TimeSpan expire_ttl, TimeSpan outdate_ttl, ICache<Tk, Tv> external_cache)
        => new Options<Tk, Tv>(external_cache, expire_ttl, outdate_ttl);

    public static Options<Tk, Tv> CreateOptions<Tk, Tv>(TimeSpan expire_ttl, TimeSpan outdate_ttl, int cache_expiration_scan_frequency_sec = 600)
        => new Options<Tk, Tv>(new MemoryCache<Tk, Tv>(cache_expiration_scan_frequency_sec), expire_ttl, outdate_ttl);

    public static SCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, object, CancellationToken, ValueTask<Tv>> getter)
            => new SCache<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, CancellationToken, ValueTask<Tv>> getter)
        => new SCache<Tk, Tv>((k, _, c) => getter(k, c), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, object, CancellationToken, Task<Tv>> getter)
        => new SCache<Tk, Tv>((k, s, c) => getter(k, s, c).AsValueTask(), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, CancellationToken, Task<Tv>> getter)
        => new SCache<Tk, Tv>((k, _, c) => getter(k, c).AsValueTask(), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<IEnumerable<Tk>, object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tk, Tv>>>> getter)
        => new SCacheBatch<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<IEnumerable<Tk>, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tk, Tv>>>> getter)
        => new SCacheBatch<Tk, Tv>((k, _, c) => getter(k, c), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<IEnumerable<Tk>, object, CancellationToken, Task<IEnumerable<KeyValuePair<Tk, Tv>>>> getter)
        => new SCacheBatch<Tk, Tv>((k, s, c) => getter(k, s, c).AsValueTask(), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);

    public static SCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<IEnumerable<Tk>, CancellationToken, Task<IEnumerable<KeyValuePair<Tk, Tv>>>> getter)
        => new SCacheBatch<Tk, Tv>((k, _, c) => getter(k, c).AsValueTask(), options.ExpireTtl, options.OutdateTtl, options.ExternalCache);
}
