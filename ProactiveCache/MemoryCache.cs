﻿using ProactiveCache.Internal;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache
{
    public class MemoryCache<Tk, Tv> : ICache<Tk, Tv>
    {
        private readonly int _expirationScanFrequencySec;
        private readonly ConcurrentDictionary<Tk, CacheEntry> _entries;
        private long _nextExpirationScan;
        private Task _expirationScan;
        private readonly CacheExpiredHook<Tk,Tv> _expired;

        private struct CacheEntry
        {
            private readonly long _expireAt;
            public readonly Tv Value;

            public CacheEntry(Tv value, TimeSpan expire_ttl, long now_sec)
            {
                _expireAt = now_sec + expire_ttl.Ticks / TimeSpan.TicksPerSecond;
                Value = value;
            }

            public bool IsExpired(long now_sec) => now_sec >= _expireAt;
        }

        public int Count => _entries.Count;

        public MemoryCache(int expiration_scan_frequency_sec = 600, CacheExpiredHook<Tk, Tv> expired = null)
        {
            _expirationScanFrequencySec = expiration_scan_frequency_sec;
            _nextExpirationScan = ProCacheTimer.NowSec + _expirationScanFrequencySec;
            _entries = new ConcurrentDictionary<Tk, CacheEntry>();
            _expirationScan = Task.CompletedTask;
            _expired = expired;
        }

        public void Set(Tk key, Tv value, TimeSpan expiration_time)
        {
            var nowSec = ProCacheTimer.NowSec;
            var entry = new CacheEntry(value, expiration_time, nowSec);
            _entries.AddOrUpdate(key, entry, (k, v) => entry);

            StartScanForExpiredItemsIfNeeded(nowSec);
        }

        public bool TryGet(Tk key, out Tv value)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.IsExpired(ProCacheTimer.NowSec))
            {
                value = default(Tv);
                return false;
            }

            value = entry.Value;
            return true;
        }

        public void Remove(Tk key) => _entries.TryRemove(key, out var _);

        private void StartScanForExpiredItemsIfNeeded(long now_sec)
        {
            var nextExpirationScan = Volatile.Read(ref _nextExpirationScan);
            if (now_sec >= nextExpirationScan && _expirationScan.IsCompleted && Interlocked.CompareExchange(ref _nextExpirationScan, now_sec + _expirationScanFrequencySec, nextExpirationScan) == nextExpirationScan)
            {
                _expirationScan = Task.Factory.StartNew(ScanForExpiredItems, this,
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
        }

        private static void ScanForExpiredItems(object state)
        {
            var cache = (MemoryCache<Tk, Tv>)state;
            var nowSec = ProCacheTimer.NowSec;
            var expired = new List<KeyValuePair<Tk, Tv>>();
            foreach (var entry in cache._entries.ToArray())
            {
                if (entry.Value.IsExpired(nowSec))
                    if (cache._entries.TryRemove(entry.Key, out var val))
                        expired.Add(new KeyValuePair<Tk, Tv>(entry.Key, val.Value));
            }
            if (expired.Count > 0 && cache._expired != null)
                Task.Factory.StartNew(c => cache._expired(expired), null, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }
}
