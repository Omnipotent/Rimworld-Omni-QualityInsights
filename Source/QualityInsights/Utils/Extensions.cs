using System.Collections.Generic;

namespace QualityInsights.Utils
{
    public static class Extensions
    {
        public static TValue GetOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue fallback = default)
        {
            return dict != null && dict.TryGetValue(key, out var value) ? value : fallback;
        }
    }
}
