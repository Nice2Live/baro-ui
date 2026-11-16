using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>The live fonts of UiFonts, bounded. A ScalableFont costs about 8 MiB (a 4 MiB GPU atlas and a 4 MiB buffer), and a zoom through the
    /// scale property asks for a new size every frame, so fonts made for a scale must be given back. The class knows nothing of the game: the
    /// frame number is passed in, so the policy is deterministic and tested in tools/ui_test.
    /// Rules, in order. (1) A font asked for in the current or the previous frame is never evicted: a sprite batch of this frame may still hold its
    /// texture, and disposing it mid-frame would crash. (2) A font idle for more than IdleFrames is evicted. (3) If more than MaxLive unpinned
    /// fonts are left, the least recently used go first, still skipping rule 1 and rule 5, so LiveCount can exceed MaxLive only by fonts used in the
    /// last two frames and by the one anchor kept per font file. (4) A pinned font (one a style Slot may hold, the base sizes) is never evicted. (5) The last live font of a group (a font file, see
    /// groupOf) is never evicted: the game shares one native Face per file only while a font of that file lives, and a new font after the last one
    /// is gone would open the file again and leak the old Face (ScalableFont.cs ctor 116-128, Dispose 844-852).
    /// Not thread safe by itself: UiFonts takes its lock.</summary>
    internal sealed class FontCache<TKey, TFont> where TFont : class
    {
        private sealed class Entry { public TFont Font; public int Last; public bool Pinned; public object Group; }

        private readonly Dictionary<TKey, Entry> entries = new();
        private readonly Dictionary<object, int> groupCount = new();
        private readonly Func<TKey, object> groupOf;
        private readonly List<TFont> evicted = new();
        private readonly List<KeyValuePair<TKey, Entry>> order = new();
        private static readonly TFont[] None = Array.Empty<TFont>();

        /// <summary>groupOf maps a key to its font file (null: no anchor rule).</summary>
        public FontCache(Func<TKey, object> groupOf = null) { this.groupOf = groupOf; }

        /// <summary>Unpinned fonts that are limited by MaxLive.</summary>
        public int MaxLive { get; set; } = 12;
        /// <summary>Frames a font may stay unused before it goes (120 is two seconds at 60 fps).</summary>
        public int IdleFrames { get; set; } = 120;
        /// <summary>Unpinned live fonts (the ones this cache may still evict, anchors included).</summary>
        public int LiveCount { get; private set; }
        public int PinnedCount { get; private set; }
        public int EvictedCount { get; private set; }

        public bool Contains(TKey key) => entries.ContainsKey(key);

        /// <summary>The font of key, made by make when absent. If make throws nothing is stored.</summary>
        public TFont Get(TKey key, int frame, Func<TKey, TFont> make)
        {
            if (entries.TryGetValue(key, out var e)) { e.Last = frame; return e.Font; }
            var font = make(key);
            var group = groupOf?.Invoke(key);
            entries[key] = new Entry { Font = font, Last = frame, Group = group };
            LiveCount++;
            if (group != null) { groupCount[group] = groupCount.TryGetValue(group, out int n) ? n + 1 : 1; }
            return font;
        }

        /// <summary>Never evict key's font from now on (a base font a Slot holds). No effect when the key is absent.</summary>
        public void Pin(TKey key)
        {
            if (entries.TryGetValue(key, out var e) && !e.Pinned) { e.Pinned = true; LiveCount--; PinnedCount++; }
        }

        /// <summary>Call once per frame, before anything draws. Returns the fonts to dispose (the caller disposes each once); the list is reused by
        /// the next call.</summary>
        public IReadOnlyList<TFont> Sweep(int frame)
        {
            evicted.Clear();
            order.Clear();
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (!e.Pinned && e.Last < frame - 1) { order.Add(kv); }
            }
            if (order.Count == 0) { return None; }
            order.Sort((a, b) => a.Value.Last.CompareTo(b.Value.Last)); // oldest first: the survivor of a group is its most recent
            int unpinned = LiveCount;
            foreach (var kv in order)
            {
                var e = kv.Value;
                bool idle = frame - e.Last > IdleFrames;
                if (!idle && unpinned <= MaxLive) { continue; }
                if (e.Group != null && groupCount[e.Group] <= 1) { continue; } // anchor of its file
                entries.Remove(kv.Key);
                if (e.Group != null) { groupCount[e.Group]--; }
                unpinned--;
                LiveCount--;
                EvictedCount++;
                evicted.Add(e.Font);
            }
            return evicted.Count == 0 ? None : evicted;
        }

        /// <summary>Forgets everything and returns every font (pinned too) for the caller to dispose once.</summary>
        public List<TFont> Clear()
        {
            var all = new List<TFont>(entries.Count);
            foreach (var e in entries.Values) { all.Add(e.Font); }
            entries.Clear();
            groupCount.Clear();
            LiveCount = 0;
            PinnedCount = 0;
            return all;
        }
    }

    /// <summary>A cache of two generations for values that are cheap to remake but hot: new entries go to the current generation, a hit in the
    /// previous one is promoted, and when the current one holds Limit entries it becomes the previous one and the old previous one is dropped.
    /// Never holds more than 2 x Limit entries, and a key asked for at least once per Limit insertions survives every rotation (no cliff like a
    /// clear-all).</summary>
    internal sealed class TwoGenCache<TKey, TValue>
    {
        private Dictionary<TKey, TValue> cur, prev;
        /// <summary>Entries per generation. It may grow (StyleEngine sizes it from the page): a working set larger than Limit would rotate out entries still
        /// in use, and every restyle would then make new style objects.</summary>
        public int Limit { get; set; }
        public int Rotations { get; private set; }

        public TwoGenCache(int limit, IEqualityComparer<TKey> comparer = null)
        {
            Limit = limit;
            cur = new Dictionary<TKey, TValue>(comparer);
            prev = new Dictionary<TKey, TValue>(comparer);
        }

        public int Count => cur.Count + prev.Count;

        public bool TryGet(TKey key, out TValue value)
        {
            if (cur.TryGetValue(key, out value)) { return true; }
            if (prev.TryGetValue(key, out value)) { Add(key, value); return true; }
            return false;
        }

        public void Add(TKey key, TValue value)
        {
            if (cur.Count >= Limit && !cur.ContainsKey(key))
            {
                var old = prev;
                prev = cur;
                cur = old;
                cur.Clear();
                Rotations++;
            }
            cur[key] = value;
        }

        public void Clear() { cur.Clear(); prev.Clear(); }
    }
}
