using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache.Internal
{
    internal class ProCacheEntry<Tval> : ICacheEntry<Tval>
    {
        private const long OUTDATED_FLAG = 0x4000000000000000;
        private long _outdatedSec;
        private object _value;
        private bool _hasValue;
        private bool _isEmpty;

        private readonly Task<(bool, Tval)> _valueAsTask;

        public bool IsCompleted => _hasValue ? true : _valueAsTask.IsCompleted;

        public ValueTask<Tval> GetValue() => Volatile.Read(ref _hasValue) ?
            new ValueTask<Tval>((Tval)Volatile.Read(ref _value)) :
            new ValueTask<Tval>(_valueAsTask.ContinueWith(t => t.Result.Item2, TaskContinuationOptions.OnlyOnRanToCompletion));

        internal ValueTask<(bool, Tval)> GetValueWithState() => Volatile.Read(ref _hasValue) ?
            new ValueTask<(bool, Tval)>((!Volatile.Read(ref _isEmpty), (Tval)Volatile.Read(ref _value))) :
            new ValueTask<(bool, Tval)>(_valueAsTask);

        internal (bool, Tval) GetCompletedValue()
            => Volatile.Read(ref _hasValue) ? (!Volatile.Read(ref _isEmpty), (Tval)_value) : _valueAsTask.Result;

        internal ProCacheEntry(Task<(bool, Tval)> value, TimeSpan outdated_ttl)
        {
            _valueAsTask = value;
            _outdatedSec = GetOutdatedSec(outdated_ttl);
        }

        internal ProCacheEntry(TimeSpan outdated_ttl)
        {
            _isEmpty = true;
            _hasValue = true;
            _outdatedSec = GetOutdatedSec(outdated_ttl);
        }

        internal bool Outdated()
        {
            var outdated = Volatile.Read(ref _outdatedSec);
            if (outdated > ProCacheTimer.NowSec || !_valueAsTask.IsCompleted)
                return false;

            return Interlocked.CompareExchange(ref _outdatedSec, outdated | OUTDATED_FLAG, outdated) == outdated;
        }

        internal void Reset(Tval value, TimeSpan outdated_ttl)
        {
            Volatile.Write(ref _value, value);
            Volatile.Write(ref _isEmpty, false);
            Volatile.Write(ref _hasValue, true);
            _outdatedSec = GetOutdatedSec(outdated_ttl);
        }

        internal void Reset(TimeSpan outdated_ttl)
        {
            Volatile.Write(ref _value, default(Tval));
            Volatile.Write(ref _isEmpty, true);
            Volatile.Write(ref _hasValue, true);
            _outdatedSec = GetOutdatedSec(outdated_ttl);
        }

        internal void Reset()
            => _outdatedSec &= ~OUTDATED_FLAG;


        private static long GetOutdatedSec(TimeSpan outdated_ttl)
            => ProCacheTimer.NowSec + outdated_ttl.Ticks / TimeSpan.TicksPerSecond;
    }
}
