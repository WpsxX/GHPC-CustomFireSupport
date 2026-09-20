using System.Runtime.CompilerServices;

namespace CustomFireSupport
{
    /// <summary>Reference identity membership without retaining discarded runtime ammo.</summary>
    internal sealed class WeakIdentitySet<T> where T : class
    {
        private static readonly object Marker = new object();
        private readonly ConditionalWeakTable<T, object> _items = new ConditionalWeakTable<T, object>();

        internal void Add(T item)
        {
            if (item != null && !Contains(item)) _items.Add(item, Marker);
        }

        internal bool Contains(T item)
        {
            object marker;
            return item != null && _items.TryGetValue(item, out marker);
        }
    }
}
