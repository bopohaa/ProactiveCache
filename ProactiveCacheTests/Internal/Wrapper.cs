namespace SlidingCacheTests.Internal
{
    internal class Wrapper
    {
        private readonly int _value;
        public int Value => _value;

        public Wrapper(int value) => _value = value;
    }
}
