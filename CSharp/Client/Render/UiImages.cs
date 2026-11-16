using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>Pictures from files (a mod's PNG or JPG) for the draw callback of a canvas (UiWindow.Images, docs/ui/AUTHORING.md) and for url() in CSS
    /// (the painter reads the same store, so one file is one texture). Draw(sb, path, box, fit) loads the file once, keeps the texture (reloaded when the file
    /// is saved again, dropped when the window closes or the window's texture budget takes it back) and draws it into a box, whole, cropped or a sprite-sheet
    /// cell. Pasted pictures (bytes) are kept the same way under a key. Textures are made with the game's own loader, so they look as the game's sprites do;
    /// they are freed late (a texture the sprite batch still holds must not go before the batch ends).</summary>
    public sealed class UiImages : IDisposable, ITextureOwner
    {
        private sealed class Entry
        {
            public Texture2D Tex;
            public DateTime Stamp;
            public long Length;
            public double Checked;
            public bool Failed;
            /// <summary>The token of the window's texture budget (TextureLedger), -1 when there is no texture.</summary>
            public int Token = -1;
            public long Bytes => Tex == null ? 0 : (long)Tex.Width * Tex.Height * 4;
        }

        /// <summary>The textures that are no longer used wait here until the batch that may hold them has ended.</summary>
        private static readonly List<(Texture2D Tex, double At)> Graveyard = new();
        private const double GraveDelay = 0.75, RecheckEvery = 1.0;

        private readonly string window;
        private readonly TextureLedger ledger;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> keys = new();
        private readonly HashSet<string> warned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pictures of a window: its texture budget is the window's one (the same ledger as the painter's caches).</summary>
        internal UiImages(string windowName, TextureLedger budget) { window = windowName; ledger = budget; }

        public int Count => entries.Count;

        public long Bytes
        {
            get { long n = 0; foreach (var e in entries.Values) { n += e.Bytes; } return n; }
        }

        /// <summary>The texture of a picture file: loaded on the first call, the same one after. Null (and one line in the log) when the file is
        /// missing, empty or no picture. The file is looked at again once a second: saved over, it is read anew.</summary>
        public Texture2D Get(string path)
        {
            if (string.IsNullOrEmpty(path)) { return null; }
            string key;
            try { key = Path.GetFullPath(path); }
            catch (Exception) { return null; }
            return GetFull(key);
        }

        /// <summary>Get for a path that is already full (Path.GetFullPath done once by the caller): no string is made per call, so the painter may ask every frame.</summary>
        internal Texture2D GetFull(string key)
        {
            if (string.IsNullOrEmpty(key)) { return null; }
            double now = Timing.TotalTime;
            if (entries.TryGetValue(key, out var e))
            {
                Touch(e);
                if (now - e.Checked < RecheckEvery) { return e.Tex; }
                e.Checked = now;
                try
                {
                    var info = new FileInfo(key);
                    if (info.Exists && info.LastWriteTimeUtc == e.Stamp && info.Length == e.Length) { return e.Tex; }
                    if (!info.Exists && (e.Tex != null || e.Failed)) { return e.Tex; }   // deleted while shown: the picture stays; a missing one stays missing (no new exception every second)
                }
                catch (IOException) { return e.Tex; }
                Forget(key, e);
            }
            var entry = new Entry { Checked = now };
            try
            {
                var info = new FileInfo(key);
                entry.Stamp = info.LastWriteTimeUtc;
                entry.Length = info.Exists ? info.Length : 0;
                if (!info.Exists) { throw new FileNotFoundException("no such file", key); }
                if (entry.Length == 0) { throw new InvalidDataException("the file is empty"); }
                using var stream = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                entry.Tex = TextureLoader.FromStream(stream, key, compress: false, mipmap: false);
                if (entry.Tex == null) { entry.Failed = true; }
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                if (warned.Add(key)) { UiPlugin.Log($"{window}: picture '{key}': {ex.Message}"); }
            }
            entries[key] = entry;
            Track(key, entry);
            return entry.Tex;
        }

        /// <summary>The texture of a picture that is not a file (pasted: PNG, JPG or BMP bytes), kept under a key; the same key again replaces it.</summary>
        public Texture2D Add(string key, byte[] encoded)
        {
            if (string.IsNullOrEmpty(key)) { return null; }
            if (entries.TryGetValue(key, out var old)) { Forget(key, old); }
            var entry = new Entry { Checked = double.MaxValue };
            try
            {
                if (encoded == null || encoded.Length == 0) { throw new InvalidDataException("no data"); }
                using var stream = new MemoryStream(encoded, writable: false);
                entry.Tex = TextureLoader.FromStream(stream, key, compress: false, mipmap: false);
                if (entry.Tex == null) { entry.Failed = true; }
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                if (warned.Add(key)) { UiPlugin.Log($"{window}: picture '{key}': {ex.Message}"); }
            }
            entries[key] = entry;
            Track(key, entry);
            return entry.Tex;
        }

        /// <summary>The texture kept under a key by Add (or the file by Get), without loading.</summary>
        public Texture2D Peek(string keyOrPath)
        {
            if (string.IsNullOrEmpty(keyOrPath)) { return null; }
            if (entries.TryGetValue(keyOrPath, out var e)) { return e.Tex; }
            try { return entries.TryGetValue(Path.GetFullPath(keyOrPath), out e) ? e.Tex : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Forgets a picture (a file by its path, a pasted one by its key): the next Get reads the file again.</summary>
        public void Release(string keyOrPath)
        {
            if (string.IsNullOrEmpty(keyOrPath)) { return; }
            string key = keyOrPath;
            if (!entries.ContainsKey(key)) { try { key = Path.GetFullPath(keyOrPath); } catch (Exception) { return; } }
            if (entries.TryGetValue(key, out var e)) { Forget(key, e); warned.Remove(key); }
        }

        public void Clear()
        {
            foreach (var e in entries.Values)
            {
                if (e.Token >= 0) { ledger.Remove(e.Token); }
                Bury(e.Tex);
            }
            entries.Clear();
            keys.Clear();
            warned.Clear();
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>Draws a picture file into a box. False when it cannot be shown (the box is empty, the file is missing).</summary>
        public bool Draw(SpriteBatch sb, string path, Rectangle box, ImageFit fit = ImageFit.Contain, Color? tint = null, bool pixelPerfect = false) =>
            Draw(sb, Get(path), box, fit, tint, pixelPerfect);

        /// <summary>Draws a texture (a picture Get or Add returned) into a box. pixelPerfect: whole-number scales and no smoothing (pixel art);
        /// the sprite batch is restarted for it (once per call), so give a few large pictures rather than many small ones.</summary>
        public bool Draw(SpriteBatch sb, Texture2D tex, Rectangle box, ImageFit fit = ImageFit.Contain, Color? tint = null, bool pixelPerfect = false) =>
            tex != null && DrawRegion(sb, tex, new Rectangle(0, 0, tex.Width, tex.Height), box, fit, tint, pixelPerfect);

        /// <summary>Draws a part of a picture (a sprite-sheet cell, see ImageLayout.Cell) into a box.</summary>
        public bool DrawRegion(SpriteBatch sb, Texture2D tex, Rectangle source, Rectangle box, ImageFit fit = ImageFit.Contain, Color? tint = null, bool pixelPerfect = false)
        {
            if (tex == null || tex.IsDisposed) { return false; }
            source = Rectangle.Intersect(source, new Rectangle(0, 0, tex.Width, tex.Height));
            var pl = ImageLayout.Place(source.X, source.Y, source.Width, source.Height, box.X, box.Y, box.Width, box.Height, fit, pixelPerfect);
            if (pl.IsEmpty) { return false; }
            var dest = new Rectangle((int)Math.Round(pl.X), (int)Math.Round(pl.Y), Math.Max(1, (int)Math.Round(pl.W)), Math.Max(1, (int)Math.Round(pl.H)));
            var src = new Rectangle(pl.SrcX, pl.SrcY, pl.SrcW, pl.SrcH);
            var color = tint ?? Color.White;
            if (!pixelPerfect) { sb.Draw(tex, dest, src, color); return true; }
            sb.End();
            try
            {
                sb.Begin(SpriteSortMode.Deferred, samplerState: SamplerState.PointClamp, rasterizerState: GameMain.ScissorTestEnable, transformMatrix: Painter.BatchMatrix);
                sb.Draw(tex, dest, src, color);
                sb.End();
            }
            finally { sb.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable, transformMatrix: Painter.BatchMatrix); }
            return true;
        }

        // ---------------------------------------------------------------- memory

        /// <summary>A texture of this entry was used: it counts as used in this frame (the budget does not release it before the next frame).</summary>
        private void Touch(Entry e)
        {
            if (e.Token >= 0) { ledger.Touch(e.Token); }
        }

        /// <summary>A new texture is in the budget under its key; the budget is trimmed (it may release idle textures of every cache of the window).</summary>
        private void Track(string key, Entry e)
        {
            if (e.Tex == null) { return; }
            e.Token = ledger.Add(this, e.Bytes);
            keys[e.Token] = key;
            ledger.Trim();
        }

        /// <summary>The entry is let go by its owner (replaced, released, cleared): out of the budget, its texture through the graveyard.</summary>
        private void Forget(string key, Entry e)
        {
            if (e.Token >= 0) { ledger.Remove(e.Token); keys.Remove(e.Token); }
            entries.Remove(key);
            Bury(e.Tex);
        }

        /// <summary>The budget takes an idle entry back (TextureLedger.Trim): the entry goes, its texture through the graveyard.</summary>
        void ITextureOwner.Release(int token)
        {
            if (!keys.Remove(token, out var key)) { return; }
            if (!entries.Remove(key, out var e)) { return; }
            e.Token = -1;
            Bury(e.Tex);
        }

        /// <summary>Lets a texture go: freed a moment later by Sweep, when no batch can hold it.</summary>
        internal static void Bury(Texture2D tex)
        {
            if (tex != null) { Graveyard.Add((tex, Timing.TotalTime)); }
        }

        /// <summary>Frees the textures that were let go more than a moment ago (the window calls it every frame; the batches that held them have ended).</summary>
        internal static void Sweep()
        {
            if (Graveyard.Count == 0) { return; }
            double now = Timing.TotalTime;
            for (int i = Graveyard.Count - 1; i >= 0; i--)
            {
                if (now - Graveyard[i].At < GraveDelay) { continue; }
                try { Graveyard[i].Tex.Dispose(); } catch (Exception) { }
                Graveyard.RemoveAt(i);
            }
        }

        /// <summary>Every texture of every window that is still waiting (the game is closing).</summary>
        internal static void SweepAll()
        {
            foreach (var g in Graveyard) { try { g.Tex.Dispose(); } catch (Exception) { } }
            Graveyard.Clear();
        }

        /// <summary>The window closed: its pictures are let go (and freed a moment later).</summary>
        public void Dispose() => Clear();
    }
}
