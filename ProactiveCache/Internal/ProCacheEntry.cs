using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProactiveCache.Internal
{
    internal class ProCacheEntry<Tval> : ICacheEntry<Tval>
    {
        private const uint ISEMPTY_MASK = 0x80000000;
        private const uint HASVALUE_MASK = 0x40000000;
        private const uint QUEUESIZE_MASK = 0x3fffffff;
        private const long OUTDATED_FLAG = 0x4000000000000000;

        private long _outdatedSec;
        private object _value;
        private int _data;

        private object Value => Volatile.Read(ref _value);
        internal bool IsEmpty => (unchecked((uint)Volatile.Read(ref _data)) & ISEMPTY_MASK) > 0;
        private bool HasValue => (unchecked((uint)Volatile.Read(ref _data)) & HASVALUE_MASK) > 0;

        private readonly Task<Tval> _valueAsTask;
        public bool IsCompleted => HasValue ? true : _valueAsTask.IsCompleted;

        protected bool IsCompletedTask => _valueAsTask.IsCompleted;


        public ProCacheEntry(Task<Tval> value, TimeSpan outdated_ttl)
        {
            _valueAsTask = value;
            _outdatedSec = GetOutdatedSec(outdated_ttl);
        }

        public ValueTask<Tval> GetValue()
        {
            if (HasValue)
                return new ValueTask<Tval>((Tval)Value);

            return new ValueTask<Tval>(_valueAsTask);
        }

        internal Tval GetCompletedValue()
        {
            if (HasValue)
                return (Tval)Value;

            return _valueAsTask.Result;
        }

        internal bool Outdated()
        {
            var outdated = Volatile.Read(ref _outdatedSec);
            if (outdated > ProCacheTimer.NowSec || !IsCompletedTask)
                return false;

            return Interlocked.CompareExchange(ref _outdatedSec, outdated | OUTDATED_FLAG, outdated) == outdated;
        }

        internal void Reset(Tval value, TimeSpan? outdated_ttl)
        {
            Volatile.Write(ref _value, value);
            Volatile.Write(ref _data, unchecked((int)HASVALUE_MASK));

            if (outdated_ttl.HasValue)
                _outdatedSec = GetOutdatedSec(outdated_ttl.Value);
        }

        internal void Reset(TimeSpan? outdated_ttl)
        {
            Volatile.Write(ref _value, default(Tval));
            Volatile.Write(ref _data, unchecked((int)(ISEMPTY_MASK | HASVALUE_MASK)));
            if (outdated_ttl.HasValue)
                _outdatedSec = GetOutdatedSec(outdated_ttl.Value);
        }


        internal void Reset()
            => _outdatedSec &= ~OUTDATED_FLAG;


        internal bool TryEnterQueue(ushort max_queue_size)
        {
            if (HasValue || IsCompletedTask)
                return true;

            if ((unchecked((uint)_data) & QUEUESIZE_MASK) >= max_queue_size)
                return false;

            return (unchecked((uint)Interlocked.Increment(ref _data)) & QUEUESIZE_MASK) <= max_queue_size;
        }

        private static long GetOutdatedSec(TimeSpan outdated_ttl)
            => ProCacheTimer.NowSec + outdated_ttl.Ticks / TimeSpan.TicksPerSecond;
    }
}
