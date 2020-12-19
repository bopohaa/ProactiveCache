using System;

namespace ProactiveCache.Internal
{
    internal static class ProCacheTimer
    {
        private static ulong _nowMs;
        public static ulong NowMs => _nowMs;
        private static uint _nowSec;
        public static uint NowSec => _nowSec;

        private readonly static System.Timers.Timer _timer;

        static ProCacheTimer()
        {
            _nowMs = (uint)Environment.TickCount;
            _nowSec = (uint)(_nowMs / 1000);
            _timer = new System.Timers.Timer(1000);
            _timer.AutoReset = true;
            _timer.Elapsed += (sender, e) =>
            {
                var now = (uint)Environment.TickCount;
                var t = _nowMs;
                var f = t % 0x0100000000;
                if (now < f)
                    t += 0x0100000000;
                _nowMs = t & 0xffffffff00000000 | now;
                _nowSec = (uint)(_nowMs / 1000);
            };
            _timer.Start();
        }
    }
}
