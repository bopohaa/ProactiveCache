using System;
using System.Collections.Generic;
using System.Text;

namespace ProactiveCache
{
    public enum ProCacheCallbackReason
    {
        Miss,
        Outdated,
        Updated,
        Expired,
        Error,
    }

    public delegate void ProCacheCallback<Tk, Tv>(Tk key, ICacheEntry<Tv> value, ProCacheCallbackReason reason);
}
