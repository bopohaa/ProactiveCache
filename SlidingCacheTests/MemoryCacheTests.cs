using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using NUnit.Framework;

namespace SlidingCacheTests
{
    public class MemoryCacheTests
    {
        private readonly static TimeSpan InfinityTtl = TimeSpan.MaxValue;
        private const int SET_COUNT = 100;

        private class TestCacheEntry : SlidingCache.ICacheEntry<float>
        {
            private readonly Task<float> _value;
            public bool IsCompleted => _value.IsCompleted;

            public TestCacheEntry(Task<float> value) => _value = value;

            public ValueTask<float> GetValue() => new ValueTask<float>(_value);
        }

        [Test]
        public void SetTest()
        {
            var cache = new SlidingCache.MemoryCache<int, float>(0);
            for (var i = 0; i < SET_COUNT; i++)
                cache.Set(i, new TestCacheEntry(Task.FromResult((float)i)), InfinityTtl);

            Assert.AreEqual(SET_COUNT, cache.Count);
        }

        [Test]
        public void TryGetTest()
        {
            var cache = new SlidingCache.MemoryCache<int, float>(0);
            var values = AddToCache(cache, InfinityTtl, 0, SET_COUNT);

            for (var i = 0; i < SET_COUNT; i++)
            {
                Assert.IsTrue(cache.TryGet(i, out var val));
                Assert.AreEqual(values[i], val);
                Assert.AreEqual((float)i, val.GetValue().Result);
            }
        }

        [Test]
        public void RemoveTest()
        {
            var cache = new SlidingCache.MemoryCache<int, float>();
            var values = AddToCache(cache, InfinityTtl, 0, SET_COUNT);
            var removeCnt = SET_COUNT / 2;

            for (var i = 0; i < removeCnt; i++)
                cache.Remove(i);

            for (var i = 0; i < removeCnt; i++)
                Assert.IsFalse(cache.TryGet(i, out var _));

            for (var i = removeCnt; i < SET_COUNT; i++)
            {
                Assert.IsTrue(cache.TryGet(i, out var val));
                Assert.AreEqual(values[i], val);
                Assert.AreEqual((float)i, val.GetValue().Result);
            }
        }

        [Test]
        public void ExpirationTtlTest()
        {
            var expiredCnt = SET_COUNT / 2;
            var cache = new SlidingCache.MemoryCache<int, float>(2);
            var values = AddToCache(cache, TimeSpan.FromSeconds(1), 0, expiredCnt);
            values = AddToCache(cache, InfinityTtl, expiredCnt, SET_COUNT, values);

            Task.Delay(1500).Wait();

            Assert.AreEqual(SET_COUNT, cache.Count);

            for (var i = 0; i < expiredCnt; i++)
                Assert.IsFalse(cache.TryGet(i, out var _));

            for (var i = expiredCnt; i < SET_COUNT; i++)
            {
                Assert.IsTrue(cache.TryGet(i, out var val));
                Assert.AreEqual(values[i], val);
                Assert.AreEqual((float)i, val.GetValue().Result);
            }

            Task.Delay(1000).Wait();

            cache.Set(expiredCnt, values[expiredCnt], InfinityTtl);

            Task.Delay(500).Wait();

            Assert.AreEqual(SET_COUNT - expiredCnt, cache.Count);
        }

        private static TestCacheEntry[] AddToCache(SlidingCache.MemoryCache<int, float> cache, TimeSpan expiration_ttl, int from_key, int to_key, TestCacheEntry[] tmp = null)
        {
            if (!(tmp is null) && tmp.Length < to_key)
                Array.Resize(ref tmp, to_key);

            var values = tmp ?? new TestCacheEntry[to_key];
            for (var i = from_key; i < to_key; i++)
            {
                values[i] = new TestCacheEntry(Task.FromResult((float)i));
                cache.Set(i, values[i], expiration_ttl);
            }

            return values;
        }
    }
}
