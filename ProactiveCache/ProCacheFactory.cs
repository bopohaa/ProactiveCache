using ProactiveCache;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class ProCacheFactory
{
    public class Options<Tk, Tv>
    {
        public readonly ExternalCacheFactory<Tk, Tv> ExternalCache;
        public readonly TimeSpan ExpireTtl;
        public readonly TimeSpan OutdateTtl;

        public Options(TimeSpan expire_ttl, TimeSpan outdate_ttl, ExternalCacheFactory<Tk, Tv> external_cache = null)
        {
            ExternalCache = external_cache;
            ExpireTtl = expire_ttl;
            OutdateTtl = outdate_ttl;
        }
    }

    public static Options<Tk, Tv> CreateOptions<Tk, Tv>(ExternalCacheFactory<Tk, Tv> external_cache, TimeSpan expire_ttl, TimeSpan outdate_ttl)
        => new Options<Tk, Tv>(expire_ttl, outdate_ttl, external_cache);

    public static Options<Tk, Tv> CreateOptions<Tk, Tv>(TimeSpan expire_ttl, TimeSpan outdate_ttl, int cache_expiration_scan_frequency_sec = 600)
        => new Options<Tk, Tv>(expire_ttl, outdate_ttl, h => new MemoryCache<Tk, Tv>(cache_expiration_scan_frequency_sec, h));

    public static ProCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, object, CancellationToken, ValueTask<Tv>> getter, ProCacheHook<Tk, Tv> hook = null)
        => new ProCache<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, hook, options.ExternalCache);

    public static ProCache<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk, CancellationToken, ValueTask<Tv>> getter, ProCacheHook<Tk, Tv> hook = null)
        => new ProCache<Tk, Tv>((k, _, c) => getter(k, c), options.ExpireTtl, options.OutdateTtl, hook, options.ExternalCache);

    public static ProCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk[], object, CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tk, Tv>>>> getter, ProCacheBatchHook<Tk, Tv> hook = null)
        => new ProCacheBatch<Tk, Tv>(getter, options.ExpireTtl, options.OutdateTtl, hook, options.ExternalCache);

    public static ProCacheBatch<Tk, Tv> CreateCache<Tk, Tv>(this Options<Tk, Tv> options, Func<Tk[], CancellationToken, ValueTask<IEnumerable<KeyValuePair<Tk, Tv>>>> getter, ProCacheBatchHook<Tk, Tv> hook = null)
        => new ProCacheBatch<Tk, Tv>((k, _, c) => getter(k, c), options.ExpireTtl, options.OutdateTtl, hook, options.ExternalCache);
}
