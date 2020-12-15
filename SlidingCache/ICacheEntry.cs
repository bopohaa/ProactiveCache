using System.Threading.Tasks;

namespace SlidingCache
{
    public interface ICacheEntry<Tval>
    {
        bool IsCompleted { get; }
        ValueTask<Tval> GetValue();
    }
}
