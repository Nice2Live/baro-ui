using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BaroUI.Core
{
    /// <summary>The canvases of a window by key (docs/ui/DESIGN_CHARTS.md 2.6, the lifetime of the mouse handlers). A canvas with an id is keyed by the id;
    /// a chart or a curve has none, so it gets a key of its own that stays with its element object (a weak table: the key goes with the element). The
    /// entries hold the element and the handler, so an entry of an element that left the document would keep both alive until the window closed: Prune
    /// drops those (the stale ones the caller names), and the window calls it when a new anonymous key appears. Pure: the tests run the lifetime.</summary>
    internal sealed class CanvasRegistry<TOwner, TEntry> where TOwner : class where TEntry : class
    {
        private readonly Dictionary<string, TEntry> entries = new(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<TOwner, string> anonKeys = new();
        private readonly List<string> dropKeys = new();
        private static int anonSeq;

        public int Count => entries.Count;

        /// <summary>The key of an owner: its id when it has one, else its own key (made once, then the same for that object). A steady frame makes no
        /// string: the key is looked up before one is made.</summary>
        public string KeyOf(TOwner owner, string id)
        {
            if (id != null) { return id; }
            if (anonKeys.TryGetValue(owner, out string key)) { return key; }
            key = "#" + Interlocked.Increment(ref anonSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);
            anonKeys.AddOrUpdate(owner, key);
            return key;
        }

        public bool TryGet(string key, out TEntry entry) => entries.TryGetValue(key, out entry);

        public bool Contains(string key) => key != null && entries.ContainsKey(key);

        public void Put(string key, TEntry entry) => entries[key] = entry;

        public bool Remove(string key, out TEntry entry)
        {
            if (entries.Remove(key, out entry)) { return true; }
            entry = null;
            return false;
        }

        /// <summary>All entries, into dst from index 0 (dst must have room for Count).</summary>
        public void CopyTo(TEntry[] dst) => entries.Values.CopyTo(dst, 0);

        public void Clear() => entries.Clear();

        /// <summary>Removes the entries that stale says to drop and adds them to dropped (the caller releases them). Returns how many. Allocates nothing
        /// when nothing is stale.</summary>
        public int Prune(Func<TEntry, bool> stale, List<TEntry> dropped)
        {
            dropKeys.Clear();
            foreach (var kv in entries)
            {
                if (stale(kv.Value)) { dropKeys.Add(kv.Key); }
            }
            foreach (var key in dropKeys)
            {
                dropped.Add(entries[key]);
                entries.Remove(key);
            }
            return dropKeys.Count;
        }
    }
}
