using System;
using System.Collections.Generic;
using System.Text;

namespace ProactiveCache
{
    public class ProCacheQueueLimitExceededException : Exception
    {
        public ProCacheQueueLimitExceededException(string message) : base(message)
        {

        }
    }
}
