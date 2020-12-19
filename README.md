# General description
A simple implementation of the "cache-aside" pattern with forward-looking updates of values on demand before the expiration of the value in the cache.

# Features
* Can be used as its own internal cache (if the `external_cache` parameter is not specified when creating the cache, then by default a separate instance of the` ProactiveCache.MemoryCache` class will be created),
and any third-party (for example, for `System.Runtime.Caching.MemoryCache` it is enough to write a wrapper class that implements the ProactiveCache.ICache interface).
* Allows updating the value in the cache before the expiration of this value by specifying the time before the update - `outdate_ttl`.
After this time expires, the first read from the cache initiates the update of the value, and all subsequent reads will receive the old value from the cache until the update is completed.
* There is a special cache implementation for updating and retrieving several values from the cache at once by passing an enumeration of keys (`ProactiveCache.ProCacheBatch`).
In this case, the value request function will receive a list of keys whose values need to be updated or added to the cache.
If as a result of the operation of such a function, the values of not all the requested keys are returned, then the missing values will be marked as "empty" and will not be included in the result,
but at the same time I will be requested again after the cache time has expired.

# Performance issues
* The time of relevance (time before the value of `outdate_ttl` expires) of the value in the cache must be long enough so that there are no duplicate update requests.
The best practice is to set this value to half the lifetime of the value in the cache (`outdate_ttl` =` expire_ttl` / 2).
If the time at which the value was received rises to the expiration time, a second request to update this value will be initiated.
* Despite the strong typing of the cache, each value will be stored on the heap even if it has a value type (forced `boxing` and` unboxing`).

# Examples of using
Various usage scenarios can be found in the tests for this library `ProactiveCacheTests`

### An example of getting a value by key
Getting a single value from the cache by its key, in the absence of a value, call the local `getter` method.
```C#
ValueTask<float> getter(int key, CancellationToken cancellation) => new ValueTask<float>(key);

var cache = ProCacheFactory
    .CreateOptions<int, float>(expire_ttl: TimeSpan.FromSeconds(120), outdate_ttl: TimeSpan.FromSeconds(60))
    .CreateCache(getter);

var result = cache.Get(1).Result;
```

### An example of getting multiple values from a list of keys
Retrieving several values from the cache by the list of keys, in the absence of a value, call the local `getter` method with a list of keys not found in the cache.
In this example, the `getter` method will be called twice, the first time with a list of keys` [1,2,3] `, and the second time with a list of keys` [4,5,6] `
```C#
ValueTask<IEnumerable<KeyValuePair<int, float>>> getter(int[] keys, CancellationToken cancellation)
    => new ValueTask<IEnumerable<KeyValuePair<int, float>>>(keys.Select(k => new KeyValuePair<int, float>(k, k)));

var cache = ProCacheFactory
    .CreateOptions<int, float>(expire_ttl: TimeSpan.FromSeconds(120), outdate_ttl: TimeSpan.FromSeconds(60))
    .CreateCache(getter);

var from1to3 = cache.Get(Enumerable.Range(1, 3)).Result;
var from1to6 = cache.Get(Enumerable.Range(1, 6)).Result;
```