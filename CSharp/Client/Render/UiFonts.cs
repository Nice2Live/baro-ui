using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>The package's fonts (Fonts/, all OFL) and the game's own (Content/Fonts, loaded from the game folder) as ScalableFonts, one per face and whole
    /// pixel size, loaded with dynamicLoading so only the glyphs in use are rendered. The families, faces and the CSS choice among them are the table in
    /// Core/FontFamilies.cs: JetBrains Mono (the default), Caveat, PT Serif, Noto Sans (proportional, Cyrillic). A game font that is missing is
    /// replaced by the default family with one log line. Also the layout's
    /// text measurer: widths come from the real glyph advances; ascent and descent follow the font's hhea rounded to whole
    /// pixels, the way Chrome does it, so line boxes match the browser reference.</summary>
    public sealed class UiFonts : ITextMeasurer, IDisposable
    {
        private readonly string dir;
        /// <summary>Every live font: base ones (pinned, a Slot may hold them) and those made in Draw for a scale (bounded and given back, see
        /// Core/FontCache.cs). The same (family, face, size) is always one instance. The group is the font file: the last font of a file is kept so the
        /// game's shared native Face is not opened a second time and leaked (ScalableFont.cs 116-128; its Dispose, 844-852, does not free the Face).</summary>
        private readonly FontCache<(FontFace, uint), ScalableFont> cache = new(k => k.Item1.File);
        private readonly Dictionary<ScalableFont, int> capOffset = new();
        private readonly object sync = new();
        /// <summary>The age of fonts counts DRAWN frames, not game updates: at a low frame rate the game runs several updates per draw, and counting
        /// those would make the fonts the last draw used look stale (they would be disposed and remade, with their warm-up, every draw). While nothing
        /// draws at all, the clock still moves once per IdleStep updates so idle fonts are given back in the end.</summary>
        private int frame;
        private bool drewSinceSweep;
        private int updatesWithoutDraw;
        private const int IdleStep = 60;
        /// <summary>Text widths by font and string: layout measures the same labels on every pass, and ScalableFont
        /// takes a lock and two glyph lookups per character. Two generations of MaxWidths: a full one is kept as the older one, not cleared.</summary>
        private readonly TwoGenCache<(ScalableFont, string), float> widths = new(MaxWidths);
        private const int MaxWidths = 10000;
        public int WidthHits { get; private set; }
        public int WidthMisses { get; private set; }
        /// <summary>Fonts the cache may still evict (not pinned), and fonts given back so far (diagnostics).</summary>
        public int LiveScaledFonts => cache.LiveCount;
        public int EvictedFonts => cache.EvictedCount;

        /// <summary>Every glyph a page is likely to use, rendered in one batch when a font is made: ASCII, the Russian alphabet in both cases and the common
        /// punctuation. Without it each new letter on screen is a 4 MiB texture upload of its own (and, in a parallel draw worker, a retried frame).</summary>
        private const string Prewarm = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"
            + "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя«»—–…№•×°±";

        private sealed class Slot
        {
            public ScalableFont Font;
            public FontFace Face;
            public uint Size;
            public bool Italic;
            public double Ascent, Descent;
            public UiFonts Owner;
        }

        public UiFonts(string packageDir) { dir = packageDir; }

        public static uint SizeOf(ComputedStyle s) => (uint)Math.Clamp(Math.Round(s.FontSizePx), 1, 512);

        private Slot Resolve(ComputedStyle s)
        {
            if (s.FontSlot is Slot slot && slot.Owner == this) { return slot; }
            // the table picks the family (first known name), the weight face and a real italic when the family has one for that weight
            var face = Usable(FontFamilies.Resolve(s.Get("font-family"), s.Get("font-weight"), s.Get("font-style"), out bool slant), ref slant);
            uint size = SizeOf(s);
            ScalableFont font;
            try { font = FontAt(face, size, pin: true); }
            catch (Exception e) when (face.Game)
            {
                // the file is there but the game could not read it: the same stand-in as for a missing one
                lock (sync) { if (toldFamily.Add("load|" + face.Family?.Name)) { UiPlugin.Log($"fonts: {face.File} could not be loaded ({e.Message}), using {FontFamilies.Default.Name}"); } }
                lock (sync) { substitute[face] = FontFamilies.Fallback(face, out _); }
                face = Usable(face, ref slant);
                font = FontAt(face, size, pin: true);
            }
            var created = new Slot { Font = font, Face = face, Size = size, Italic = slant, Ascent = face.Family.Ascent, Descent = face.Family.Descent, Owner = this };
            s.FontSlot = created;
            return created;
        }

        /// <summary>Game fonts a face stands in for (a missing file, once logged); read and written under sync.</summary>
        private readonly Dictionary<FontFace, FontFace> substitute = new();
        /// <summary>Families whose loss was already told (one log line per family, not per weight and slant); under sync.</summary>
        private readonly HashSet<string> toldFamily = new(StringComparer.Ordinal);

        /// <summary>The face itself, or for a game font whose file is not there the default family's face of the same weight and style (one log line per file).</summary>
        private FontFace Usable(FontFace face, ref bool slant)
        {
            if (!face.Game) { return face; }
            lock (sync)
            {
                if (!substitute.TryGetValue(face, out var stand))
                {
                    stand = File.Exists(FontFamilies.FullPath(face, dir)) ? face : FontFamilies.Fallback(face, out _);
                    if (stand != face && toldFamily.Add("miss|" + face.Family?.Name)) { UiPlugin.Log($"fonts: {face.File} not found (the other faces of {face.Family?.Name} too, if any), using {FontFamilies.Default.Name}"); }
                    substitute[face] = stand;
                }
                if (stand == face) { return face; }
                if (face.Italic) { slant = true; }
                return stand;
            }
        }

        private string FileOf(FontFace face) => FontFamilies.FullPath(face, dir);

        /// <summary>The font at a size. pin is for a base font (a Slot keeps it): never evicted, even when it was first made for a scale.</summary>
        private ScalableFont FontAt(FontFace face, uint size, bool pin)
        {
            var key = (face, size);
            lock (sync)
            {
                var font = cache.Get(key, frame, Make);
                if (pin) { cache.Pin(key); }
                return font;
            }
        }

        /// <summary>Makes a font and renders the warm-up glyphs on the creating thread, before the first frame that draws with it. Resolve and Draw run
        /// on the main thread (GUI draw is sequential; BbeParallelDraw's workers draw world items and characters). Were it a worker, the warm-up throws
        /// BbeRetryException (ScalableFont.cs ~312): the font is disposed, nothing is stored and the frame repeats on the main thread.</summary>
        private ScalableFont Make((FontFace, uint) key)
        {
            var font = new ScalableFont(FileOf(key.Item1), key.Item2, GameMain.Instance.GraphicsDevice, dynamicLoading: true);
            try { font.MeasureString(Prewarm); }
            catch { font.Dispose(); throw; }
            return font;
        }

        /// <summary>Once per game update, before the windows draw (BaroUi.AddWindowsPrefix): gives back the scaled fonts nothing draws with any more.
        /// A font used in this drawn frame or the last is kept, since the previous draw's sprite batch may still point at its texture.</summary>
        public void Sweep()
        {
            lock (sync)
            {
                if (drewSinceSweep) { drewSinceSweep = false; updatesWithoutDraw = 0; }
                else if (++updatesWithoutDraw % IdleStep != 0) { return; }
                frame++;
                var gone = cache.Sweep(frame);
                for (int i = 0; i < gone.Count; i++)
                {
                    capOffset.Remove(gone[i]);
                    // one font that fails to go must not keep the others (already out of the cache) from being disposed
                    try { gone[i].Dispose(); }
                    catch (Exception e) { UiPlugin.Log($"fonts: disposing a font failed: {e.Message}"); }
                }
            }
        }

        public float Width(string text, ComputedStyle style)
        {
            var font = Resolve(style).Font;
            if (widths.TryGet((font, text), out float w)) { WidthHits++; return w; }
            WidthMisses++;
            w = font.MeasureString(text).X;
            widths.Add((font, text), w);
            return w;
        }
        public float Ascent(ComputedStyle style) => (float)Math.Round(Resolve(style).Ascent * SizeOf(style));
        public float Descent(ComputedStyle style) => (float)Math.Round(Resolve(style).Descent * SizeOf(style));
        public float NormalLineHeight(ComputedStyle style) => Ascent(style) + Descent(style);

        /// <summary>Draws one line of text with its baseline at y. ScalableFont puts a glyph at
        /// position.Y + capHeight * 14 / 10 - bitmapTop, so the baseline sits capHeight * 1.4 below the position.</summary>
        public void Draw(SpriteBatch sb, string text, float x, float baseline, Color color, ComputedStyle style)
        {
            var slot = Resolve(style);
            var font = slot.Font;
            var xf = Painter.CurrentXf;
            // inside a scaled block (the scale property) the batch carries a matrix of scale S. The glyphs would be the CSS size's bitmap stretched by S:
            // blurry or blocky. So take the font for size * S and draw it with the factor that brings it back to the CSS size, which the matrix then
            // multiplies by S again: the texels land 1:1 on screen. The synthetic slant (a face without an italic file) only exists in the unscaled overload.
            if (xf.S != 1 && !slot.Italic)
            {
                uint q = ScaledSize(slot.Size, xf.S);
                drewSinceSweep = true;
                var big = FontAt(slot.Face, q, pin: false);
                int off = CapOffset(big);
                float f = slot.Size / (float)q;
                // the glyph origin snapped to a screen pixel (rounding in layout pixels would put it between two when S is not a whole number)
                var back = xf.Inverse();
                float sx = (float)Math.Round(xf.X(x)), sy = (float)Math.Round(xf.Y(baseline));
                big.DrawString(sb, text, new Vector2(back.X(sx), back.Y(sy) - off * f), color, 0f, Vector2.Zero, f, SpriteEffects.None, 0f);
                return;
            }
            int offset = CapOffset(font);
            font.DrawString(sb, text, new Vector2((float)Math.Round(x), (float)Math.Round(baseline) - offset), color, italics: slot.Italic);
        }

        /// <summary>Draws text with letter-spacing and word-spacing, one character at a time with the ordinary Draw (so scale, italics and pixel snapping
        /// are the same as for a plain text): the pen moves by the layout font's advance of each character plus the spacing, the same sum the layout makes
        /// (UiFonts.Width is a sum of advances). The one-character strings are cached (TextDecor.CharString): no allocation per frame. A surrogate pair is
        /// drawn as two units with the spacing after the pair.</summary>
        public void DrawSpaced(SpriteBatch sb, string text, float x, float baseline, Color color, ComputedStyle style, float letter, float word)
        {
            var font = Resolve(style).Font;
            // the start is snapped to a screen pixel once, so a fractional letter-spacing keeps the same rounding of every character wherever the text is
            // (a start that moves by a fraction of a pixel, as in a scroll, flipped single characters by a pixel)
            var xf = Painter.CurrentXf;
            float pen = xf.S == 1 ? (float)Math.Round(x) : xf.Inverse().X((float)Math.Round(xf.X(x)));
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string one = TextDecor.CharString(c);
                if (c != ' ') { Draw(sb, one, pen, baseline, color, style); }
                pen += font.MeasureChar(c).X;
                if (!char.IsHighSurrogate(c)) { pen += letter; }
                if (c == ' ' || c == ' ') { pen += word; }
            }
        }

        private int CapOffset(ScalableFont font)
        {
            lock (sync)
            {
                if (!capOffset.TryGetValue(font, out int offset))
                {
                    font.MeasureString("T"); // loads the atlas, which sets the cap height
                    offset = (int)Math.Round(font.LineHeight / 1.8f) * 14 / 10;
                    capOffset[font] = offset;
                }
                return offset;
            }
        }

        /// <summary>Largest font a scaled text may ask for (px): a zoom-in on one card must not render a 500 px atlas. Past it the glyphs are stretched.</summary>
        private const uint MaxScaledSize = 160;

        private static uint ScaledSize(uint size, float scale) => UiTransform.FontPx(size, scale, MaxScaledSize);


        public void Dispose()
        {
            lock (sync)
            {
                // the cache holds each live font once and an evicted one is already out of it: no font is disposed twice
                foreach (var f in cache.Clear())
                {
                    try { f.Dispose(); }
                    catch (Exception e) { UiPlugin.Log($"fonts: disposing a font failed: {e.Message}"); }
                }
                capOffset.Clear();
                widths.Clear();
            }
        }
    }
}
