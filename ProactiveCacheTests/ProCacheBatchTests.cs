using NUnit.Framework;
using SlidingCacheTests.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SlidingCacheTests
{
    public class ProCacheBatchTests
    {

        private readonly static TimeSpan InfinityTtl = TimeSpan.MaxValue;
        private const int SET_COUNT = 100;

        [Test]
        public void GetTest()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            var keys = Enumerable.Range(0, SET_COUNT);
            var t1 = Task.Run(() => cache.Get(keys, counter).AsTask());
            var t2 = Task.Run(() => cache.Get(keys, counter).AsTask());
            var t3 = Task.Run(() => cache.Get(keys, counter).AsTask());

            Task.WaitAll(t1, t2, t3);

            var r1 = new Dictionary<int, Wrapper>(t1.Result);
            var r2 = new Dictionary<int, Wrapper>(t2.Result);
            var r3 = new Dictionary<int, Wrapper>(t3.Result);

            for (var i = 0; i < SET_COUNT; i++)
            {
                Assert.AreEqual(i, r1[i].Value);
                Assert.AreEqual(i, r2[i].Value);
                Assert.AreEqual(i, r3[i].Value);
            }

            var cnt = counter.GetKeysCount().Sum();

            Assert.AreEqual(SET_COUNT, cnt);
        }

        [Test]
        public void GetAndOutdateTest()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, TimeSpan.FromSeconds(1))
                .CreateCache(Getter);
            var keys = new[] { 1 };

            var res1 = cache.Get(keys, counter).Result;
            Task.Delay(1000).Wait();

            var res2Task = cache.Get(keys, counter).AsTask();
            var res3Task = cache.Get(keys, counter).AsTask();

            Task.WaitAll(res2Task, res3Task);
            var res2 = res2Task.Result;
            var res3 = res3Task.Result;

            Task.Delay(1000).Wait();

            var res4Task = cache.Get(keys, counter).AsTask();
            var res5Task = cache.Get(keys, counter).AsTask();

            Task.WaitAll(res4Task, res5Task);
            var res4 = res4Task.Result;
            var res5 = res5Task.Result;

            Assert.AreNotSame(res1.Single().Value, res2.Single().Value);
            Assert.AreSame(res1.Single().Value, res3.Single().Value);
            Assert.AreNotSame(res2.Single().Value, res4.Single().Value);
            Assert.AreSame(res2.Single().Value, res5.Single().Value);

            Assert.AreEqual(3, counter.Count);
        }

        [Test]
        public void GetAndExpireTest()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(TimeSpan.FromSeconds(1), TimeSpan.Zero)
                .CreateCache(Getter);

            var keys = new[] { 1 };

            var res1 = cache.Get(keys, counter).Result;
            var res2 = cache.Get(keys, counter).Result;
            Task.Delay(1000).Wait();
            var res3 = cache.Get(keys, counter).Result;
            var res4 = cache.Get(keys, counter).Result;

            Assert.AreSame(res1.Single().Value, res2.Single().Value);
            Assert.AreNotSame(res1.Single().Value, res3.Single().Value);
            Assert.AreSame(res3.Single().Value, res4.Single().Value);
            Assert.AreEqual(2, counter.Count);
        }


        [Test]
        public void GetPartialTest()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(GetterPatial);
            var keys = Enumerable.Range(0, SET_COUNT);
            var r1 = new Dictionary<int, Wrapper>(cache.Get(keys, counter).Result);
            var r2 = new Dictionary<int, Wrapper>(cache.Get(keys, counter).Result);

            for (var i = 0; i < SET_COUNT; i++)
            {
                if (r1.TryGetValue(i, out var v1))
                    Assert.AreEqual(i, v1.Value);
                else
                    Assert.AreEqual(1, i % 2);

                if (r2.TryGetValue(i, out var v2))
                    Assert.AreEqual(i, v2.Value);
                else
                    Assert.AreEqual(1, i % 2);
            }

            Assert.AreEqual(1, counter.Count);
        }

        [Test]
        public void GetIntersectTest()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            var count = SET_COUNT / 3;
            var r1 = (0, count);
            var r2 = (SET_COUNT / 4, SET_COUNT / 2);
            var r3 = (SET_COUNT - count, count);

            var k1 = Enumerable.Range(r1.Item1, r1.Item2);
            var k2 = Enumerable.Range(r2.Item1, r2.Item2);
            var k3 = Enumerable.Range(r3.Item1, r3.Item2);
            var t1 = Task.Run(() => cache.Get(k1, counter).AsTask());
            var t2 = Task.Run(() => cache.Get(k2, counter).AsTask());
            var t3 = Task.Run(() => cache.Get(k3, counter).AsTask());

            Task.WaitAll(t1, t2, t3);

            var v1 = new Dictionary<int, Wrapper>(t1.Result);
            var v2 = new Dictionary<int, Wrapper>(t2.Result);
            var v3 = new Dictionary<int, Wrapper>(t3.Result);

            for (var i = 0; i < SET_COUNT; i++)
            {
                if (i >= r1.Item1 && i < r1.Item1 + r1.Item2)
                    Assert.AreEqual(i, v1[i].Value);
                if (i >= r2.Item1 && i < r2.Item1 + r2.Item2)
                    Assert.AreEqual(i, v2[i].Value);
                if (i >= r3.Item1 && i < r3.Item1 + r3.Item2)
                    Assert.AreEqual(i, v3[i].Value);
            }

            var cnt = counter.GetKeysCount().Sum();
            Assert.AreEqual(SET_COUNT, cnt);
            Assert.AreEqual(3, counter.Count);
        }

        [Test]
        public void GetWithException()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, InfinityTtl)
                .CreateCache(Getter);
            counter.WithThrow = true;
            var keys = new[] { 1 };

            var t1 = Task.Run(() => cache.Get(keys, counter).AsTask());
            var t2 = Task.Run(() => cache.Get(keys, counter).AsTask());

            var ex1 = Assert.CatchAsync(() => t1);
            var ex2 = Assert.CatchAsync(() => t2);

            counter.WithThrow = false;

            var t3 = Task.Run(() => cache.Get(keys, counter).AsTask());
            var t4 = Task.Run(() => cache.Get(keys, counter).AsTask());

            Task.WaitAll(t3, t4);

            var t5 = Task.Run(() => cache.Get(keys, counter).AsTask());
            var t6 = Task.Run(() => cache.Get(keys, counter).AsTask());

            Task.WaitAll(t5, t6);

            Assert.AreEqual(2, counter.Count);
        }

        [Test]
        public void GetOutdatedWithException()
        {
            var counter = new CounterForBatch();
            var cache = ProCacheFactory
                .CreateOptions<int, Wrapper>(InfinityTtl, TimeSpan.FromSeconds(1))
                .CreateCache(Getter);

            var keys = new[] { 1 };

            var r1 = cache.Get(keys, counter).Result.Single();

            Task.Delay(1500).Wait();

            counter.WithThrow = true;

            var t2 = cache.Get(keys, counter).AsTask();
            var t3 = cache.Get(keys, counter).AsTask();

            var ex2 = Assert.CatchAsync(() => t2);
            var r3 = t3.Result.Single();

            Assert.AreSame(r1.Value, r3.Value);

            counter.WithThrow = false;

            var t4 = cache.Get(keys, counter).AsTask();
            var t5 = cache.Get(keys, counter).AsTask();

            Task.WaitAll(t4, t5);

            var r4 = t4.Result.Single();
            var r5 = t5.Result.Single();

            Assert.AreNotSame(r1.Value, r4.Value);
            Assert.AreSame(r1.Value, r5.Value);
        }

        private static async ValueTask<IEnumerable<KeyValuePair<int, Wrapper>>> Getter(int[] keys, object state, CancellationToken c)
        {
            var counter = (CounterForBatch)state;
            counter.Inc();
            counter.AddKeysCount(keys.Count());

            await Task.Delay(10);

            counter.TryDoThrow();

            return keys.Select(k => new KeyValuePair<int, Wrapper>(k, new Wrapper(k)));
        }


        private static async ValueTask<IEnumerable<KeyValuePair<int, Wrapper>>> GetterPatial(int[] keys, object state, CancellationToken c)
        {
            var counter = (CounterForBatch)state;
            counter.Inc();
            counter.AddKeysCount(keys.Count());

            await Task.Delay(10);

            counter.TryDoThrow();

            return keys.Where(k => k % 2 == 0).Select(k => new KeyValuePair<int, Wrapper>(k, new Wrapper(k)));
        }

    }
}
