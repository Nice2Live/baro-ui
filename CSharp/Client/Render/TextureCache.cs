using System;
using System.Collections.Generic;
using BaroUI.Core;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>The textures of one kind, keyed, in the window's one texture budget (Core TextureLedger): a hit touches the entry, a new texture is registered and
    /// the budget trimmed. When the budget takes an idle entry back, the cache drops it and the texture goes through the graveyard (UiImages.Bury), so a texture
    /// the batch of this or the last frame may hold is never freed early. Painter masks and corner tiles, and the decoration textures, use one of these each.</summary>
    internal sealed class TextureCache<TKey> : ITextureOwner where TKey : notnull
    {
        private readonly TextureLedger ledger;
        private readonly Dictionary<TKey, (Texture2D Tex, int Token)> map = new();
        private readonly Dictionary<int, TKey> keys = new();

        public TextureCache(TextureLedger budget) { ledger = budget; }

        public int Count => map.Count;

        /// <summary>The texture kept under the key; it counts as used in this frame.</summary>
        public bool TryGet(TKey key, out Texture2D tex)
        {
            if (map.TryGetValue(key, out var e))
            {
                ledger.Touch(e.Token);
                tex = e.Tex;
                return true;
            }
            tex = null;
            return false;
        }

        /// <summary>Keeps a new texture of this many bytes under the key (a texture already there is let go); then the budget is trimmed.</summary>
        public void Put(TKey key, Texture2D tex, long bytes)
        {
            Drop(key);
            int token = ledger.Add(this, bytes);
            map[key] = (tex, token);
            keys[token] = key;
            ledger.Trim();
        }

        /// <summary>Lets the texture under the key go (through the graveyard).</summary>
        public void Drop(TKey key)
        {
            if (!map.Remove(key, out var e)) { return; }
            ledger.Remove(e.Token);
            keys.Remove(e.Token);
            UiImages.Bury(e.Tex);
        }

        /// <summary>The budget takes the entry back (it is already out of the ledger): the cache forgets it and the texture is let go.</summary>
        void ITextureOwner.Release(int token)
        {
            if (!keys.Remove(token, out var key)) { return; }
            if (map.Remove(key, out var e)) { UiImages.Bury(e.Tex); }
        }

        /// <summary>Every texture of the cache goes through the graveyard and the cache is empty.</summary>
        public void Clear()
        {
            foreach (var e in map.Values)
            {
                ledger.Remove(e.Token);
                UiImages.Bury(e.Tex);
            }
            map.Clear();
            keys.Clear();
        }
    }
}
