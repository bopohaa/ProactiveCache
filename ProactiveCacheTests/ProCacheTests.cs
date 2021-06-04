using NUnit.Framework;

using SlidingCacheTests.Internal;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SlidingCacheTests
{
    public class ProCacheTests
    {
        private readonly static TimeSpan InfinityTtl = TimeSpan.MaxValue;
        private const int SET_COUNT = 100;

        [Test]
        public void Example()
        {
            ValueTask<float> getter(int key, CancellationToken cancellation) => new ValueTask<float>(key);

            var cache = ProCacheFactory
                .CreateOptions<int, float>(expire_ttl: TimeSpan.FromSeconds(120), outdate_ttl: TimeSpan.FromSeconds(60))
                .CreateCache(getter);

            var result = cache.Get(1).Result;

            Assert.AreEqual(result, (float)1);
        }

        [Test]
        public void GetTest()
        {
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            var result = new Task<Wrapper>[SET_COUNT];
            for (var i = 0; i < SET_COUNT; i++)
                result[i] = cache.Get(i, counter).AsTask();

            Task.WaitAll(result);

            for (var i = 0; i < SET_COUNT; i++)
                Assert.AreEqual(i, result[i].Result.Value);

            Assert.AreEqual(SET_COUNT, counter.Count);
        }

        [Test]
        public void GetAgainTest()
        {
            var setCount = SET_COUNT / 2;
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            var result = new Task<Wrapper>[setCount * 2];

            for (var i = 0; i < setCount; i++)
                result[i] = cache.Get(i, counter).AsTask();
            for (var i = 0; i < setCount; i++)
                result[i + setCount] = cache.Get(i, counter).AsTask();

            Task.WaitAll(result);

            for (var i = 0; i < setCount; i++)
                Assert.AreEqual(i, result[i].Result.Value);

            for (var i = 0; i < setCount; i++)
                Assert.AreEqual(i, result[i + setCount].Result.Value);

            Assert.AreEqual(setCount, counter.Count);
        }

        [Test]
        public void GetAndOutdateTest()
        {
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, TimeSpan.FromSeconds(1))
                .CreateCache(Getter);

            var res1 = cache.Get(1, counter).Result;
            Task.Delay(1000).Wait();

            var res2Task = cache.Get(1, counter).AsTask();
            var res3Task = cache.Get(1, counter).AsTask();

            Task.WaitAll(res2Task, res3Task);
            var res2 = res2Task.Result;
            var res3 = res3Task.Result;

            Task.Delay(1000).Wait();

            var res4Task = cache.Get(1, counter).AsTask();
            var res5Task = cache.Get(1, counter).AsTask();

            Task.WaitAll(res4Task, res5Task);
            var res4 = res4Task.Result;
            var res5 = res5Task.Result;

            Assert.AreNotSame(res1, res2);
            Assert.AreSame(res1, res3);
            Assert.AreNotSame(res2, res4);
            Assert.AreSame(res2, res5);

            Assert.AreEqual(3, counter.Count);
        }

        [Test]
        public void GetAndExpireTest()
        {
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(TimeSpan.FromSeconds(2), TimeSpan.Zero)
                .CreateCache(Getter);

            var res1 = cache.Get(1, counter).Result;
            var res2 = cache.Get(1, counter).Result;
            Task.Delay(2100).Wait();
            var res3 = cache.Get(1, counter).Result;
            var res4 = cache.Get(1, counter).Result;

            Assert.AreSame(res1, res2);
            Assert.AreNotSame(res1, res3);
            Assert.AreSame(res3, res4);
            Assert.AreEqual(2, counter.Count);
        }

        [Test]
        public void GetWithException()
        {
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            counter.WithThrow = true;
            var key = 1;

            var t1 = Task.Run(() => cache.Get(key, counter).AsTask());
            var t2 = Task.Run(() => cache.Get(key, counter).AsTask());

            var ex1 = Assert.CatchAsync(() => t1);
            var ex2 = Assert.CatchAsync(() => t2);

            counter.WithThrow = false;

            var t3 = Task.Run(() => cache.Get(key, counter).AsTask());
            var t4 = Task.Run(() => cache.Get(key, counter).AsTask());

            Task.WaitAll(t3, t4);

            var t5 = Task.Run(() => cache.Get(key, counter).AsTask());
            var t6 = Task.Run(() => cache.Get(key, counter).AsTask());

            Task.WaitAll(t5, t6);

            Assert.AreEqual(2, counter.Count);
        }

        [Test]
        public void GetOutdatedWithException()
        {
            var counter = new Counter();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, TimeSpan.FromSeconds(1))
                .CreateCache(Getter);

            var keys = 1;

            var r1 = cache.Get(keys, counter).Result;

            Task.Delay(1500).Wait();

            counter.WithThrow = true;

            var t2 = cache.Get(keys, counter).AsTask();
            var t3 = cache.Get(keys, counter).AsTask();

            var ex2 = Assert.CatchAsync(() => t2);
            var r3 = t3.Result;

            Assert.AreSame(r1, r3);

            counter.WithThrow = false;

            var t4 = cache.Get(keys, counter).AsTask();
            var t5 = cache.Get(keys, counter).AsTask();

            Task.WaitAll(t4, t5);

            var r4 = t4.Result;
            var r5 = t5.Result;

            Assert.AreNotSame(r1, r4);
            Assert.AreSame(r1, r5);
        }

        [Test]
        public void HookTest()
        {
            var miss = new List<int>();
            var outdated = new List<int>();
            var expired = new List<int>();
            ProactiveCache.ProCacheHook<int, float> hook = (k, v, r) =>
            {
                switch (r)
                {
                    case ProactiveCache.ProCacheHookReason.Miss:
                        lock(miss)
                        miss.Add(k);
                        break;
                    case ProactiveCache.ProCacheHookReason.Outdated:
                        lock(outdated)
                        outdated.Add(k);
                        break;
                    case ProactiveCache.ProCacheHookReason.Expired:
                        lock(expired)
                        expired.Add(k);
                        break;
                }
            };
            var cache = ProCacheFactory
                .CreateOptions<int, float>(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 1)
                .CreateCache(SimpleGetter, hook);

            cache.Get(1);
            cache.Get(2);
            cache.Get(3);
            Task.Delay(1300).Wait();
            cache.Get(2);
            cache.Get(3);
            cache.Get(4);
            Task.Delay(3100).Wait();
            cache.Get(5);
            Task.Delay(100).Wait();

            Assert.That(new[] { 1, 2, 3, 4, 5 }, Is.EquivalentTo(miss.OrderBy(e => e)));
            Assert.That(new[] { 2, 3 }, Is.EquivalentTo(outdated.OrderBy(e => e)));
            Assert.That(new[] { 1, 2, 3, 4 }, Is.EquivalentTo(expired.OrderBy(e => e)));
        }

        private static async ValueTask<Wrapper> Getter(int k, object state, CancellationToken c)
        {
            var counter = (Counter)state;
            counter.Inc();

            await Task.Delay(10);

            counter.TryDoThrow();

            return new Wrapper(k);
        }

        private static async ValueTask<float> SimpleGetter(int k, object state, CancellationToken c)
        {
            await Task.Delay(10);
            return k;
        }

    }
}
