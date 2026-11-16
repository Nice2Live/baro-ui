using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BaroUI.Core;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>What kind of baked picture a cache entry is, and the numbers that tell two of a kind apart (all whole texture pixels under the current scale).</summary>
    internal readonly struct DecorKey : IEquatable<DecorKey>
    {
        public const byte ShadowTile = 1, ShadowOneOff = 2, Ramp = 3, Corner = 4, Area = 5;
        public readonly byte Kind;
        public readonly ulong Hash;
        public readonly int A, B, C, D;
        public DecorKey(byte kind, ulong hash, int a, int b, int c, int d) { Kind = kind; Hash = hash; A = a; B = b; C = c; D = d; }
        public bool Equals(DecorKey o) => Kind == o.Kind && Hash == o.Hash && A == o.A && B == o.B && C == o.C && D == o.D;
        public override bool Equals(object o) => o is DecorKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Kind, Hash, A, B, C, D);
    }

    /// <summary>What a style draws beyond a plain filled box, read once per style (styles are shared and not changed after they are made, so the painter asks every
    /// frame with one lookup and allocates nothing): the box-shadow, the gradient layers, the corners, whether a url() or sprite() picture or an outline is there
    /// at all. A box with none of them has <see cref="Plain"/> set. A value that cannot be drawn is told once in the log.</summary>
    internal sealed class DecorSpec
    {
        public bool HasShadow;
        public BoxShadow Shadow;
        /// <summary>The gradient layers of background-image, the first on top (the painter draws the last one first). Null: none.</summary>
        public Gradient[] Grads;
        /// <summary>The corner radii (parsed once) and whether any is above 0.</summary>
        public CornerSpec Corners;
        public bool HasRadius;
        /// <summary>background-image or border-image-source is a url() or sprite() (the painter then asks PicSpec).</summary>
        public bool HasPicture;
        /// <summary>outline-style is not none or hidden.</summary>
        public bool HasOutline;
        /// <summary>No shadow, gradient, corners, picture or outline: the cheapest path.</summary>
        public bool Plain;

        private static readonly ConditionalWeakTable<ComputedStyle, DecorSpec> cache = new();
        private static readonly HashSet<string> told = new(StringComparer.Ordinal);

        public static DecorSpec Of(ComputedStyle s)
        {
            if (cache.TryGetValue(s, out var d)) { return d; }
            d = Build(s);
            lock (cache) { cache.AddOrUpdate(s, d); }
            return d;
        }

        private static DecorSpec Build(ComputedStyle s)
        {
            string sh = s.Get("box-shadow"), img = s.Get("background-image");
            var d = new DecorSpec { Corners = CornerSpec.Of(s) };
            d.HasRadius = d.Corners.Any;
            d.HasPicture = PicSpec.IsPicture(img) || PicSpec.IsPicture(s.Get("border-image-source"));
            d.HasOutline = !OutlineSpec.NoLine(s.Get("outline-style"));
            if (sh.Length > 0 && !sh.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = BoxShadow.TryParse(sh, out var shadow, out string note);
                if (ok) { d.Shadow = shadow; d.HasShadow = !shadow.IsNone; }
                if (note != null) { Tell(ok ? note : note + ", no shadow"); }
            }
            if (img.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool ok = Gradient.TryParseLayers(img, out var layers, out string note);
                if (ok) { d.Grads = layers.ToArray(); }
                if (note != null) { Tell(ok ? note : note + ", no gradient"); }
            }
            d.Plain = !d.HasShadow && d.Grads == null && !d.HasRadius && !d.HasPicture && !d.HasOutline;
            return d;
        }

        internal static void Tell(string text)
        {
            lock (told)
            {
                if (told.Count > 256) { told.Clear(); }   // a page that keeps making new texts cannot grow the set for ever
                if (!told.Add(text)) { return; }
            }
            UiPlugin.Log(text);
        }
    }

    /// <summary>Textures of the decorations the painter draws: shadow tiles (9-slice), shadows of boxes too small for a tile, gradient ramps, gradient corners and
    /// gradient areas (angled, radial, rounded). Baked on the main thread from the Core byte arrays (white or ramp colour, straight alpha: the game blends
    /// NonPremultiplied). Keys are whole texture pixels at a STEP of the decoration scale (Core DecorScale: 1/8 octave, so a texture is stretched at most 4.4 %),
    /// and corner lengths are quantized the same way, so a smooth zoom of a block reuses what it has instead of baking every frame. At most
    /// <see cref="DecorBudget.HeavyPerFrame"/> shadow bakes and <see cref="DecorBudget.LightPerFrame"/> ramp, corner or area bakes per frame.
    /// The textures are kept in the window's one texture budget (TextureCache over the TextureLedger): an idle one goes through the graveyard when the
    /// budget is passed, and a texture used in this frame or the one before is never freed. A bake that failed is left out for a while, then tried again.
    /// The painter owns one per window and disposes it with itself.</summary>
    internal sealed class UiDecorTextures : IDisposable
    {
        private const int Tries = 7;          // a shadow looks for the nearest step among 7 (the step itself and three each way) when its bake has to wait
        private const int RetryAfter = 600;   // frames before a bake that failed is tried again

        private readonly TextureCache<DecorKey> cache;
        private readonly Dictionary<DecorKey, long> failed = new();
        private long frame;

        /// <summary>How many bakes a frame may do (see DecorBudget).</summary>
        public readonly DecorBudget Budget = new();
        /// <summary>Textures baked since the window opened (diagnostics).</summary>
        public int Baked { get; private set; }

        public UiDecorTextures(TextureLedger budget) { cache = new TextureCache<DecorKey>(budget); }

        public int Count => cache.Count;

        /// <summary>The painter calls it once per Paint: what is drawn from now on counts as used in this frame.</summary>
        public void BeginFrame() { frame++; Budget.BeginFrame(); }

        /// <summary>The 9-slice tile of a shadow at a step of the scale (<see cref="DecorScale.Step"/>), with the plan it was made for (draw with that plan:
        /// the margins in layout px are its own). When the bake has to wait, the nearest step that has a tile and can still slice this shape is used;
        /// null (the shadow is left out for the frame) when there is none.</summary>
        public Texture2D ShadowTile(GraphicsDevice dev, float radius, float blur, float spread, int step, float shapeW, float shapeH, out ShadowPlan plan)
        {
            plan = ShadowRaster.Plan(radius, blur, spread, DecorScale.Scale(step));
            for (int i = 0; i < Tries; i++)
            {
                var p = i == 0 ? plan : ShadowRaster.Plan(radius, blur, spread, DecorScale.Scale(DecorScale.Around(step, i)));
                if (!p.CanSlice(shapeW, shapeH)) { continue; }
                var key = new DecorKey(DecorKey.ShadowTile, 0, p.Key.REff, p.Key.Blur, 0, 0);
                if (TryGet(key, out var tex)) { plan = p; return tex; }
                if (i == 0 && Budget.TakeHeavy()) { return Make(dev, key, null, 0, 1f, 1f, 16f); }
            }
            return null;
        }

        /// <summary>The shadow of a box smaller than a tile, baked for its size in texture px at the step's scale, the size on the length ladder too
        /// (boxes of nearly one size share it; (w + 2m) x (h + 2m)), and stretched to the box, with the plan it was made for. While its bake waits the nearest step that has one is drawn.</summary>
        public Texture2D ShadowOneOff(GraphicsDevice dev, float radius, float blur, float spread, int step, float shapeW, float shapeH, out ShadowPlan plan)
        {
            plan = ShadowRaster.Plan(radius, blur, spread, DecorScale.Scale(step));
            for (int i = 0; i < Tries; i++)
            {
                float qs = DecorScale.Scale(DecorScale.Around(step, i));
                var p = i == 0 ? plan : ShadowRaster.Plan(radius, blur, spread, qs);
                var key = new DecorKey(DecorKey.ShadowOneOff, 0, p.Key.REff, p.Key.Blur, DecorScale.Length((int)MathF.Round(shapeW * qs)), DecorScale.Length((int)MathF.Round(shapeH * qs)));
                if (TryGet(key, out var tex)) { plan = p; return tex; }
                if (i == 0 && Budget.TakeHeavy()) { return Make(dev, key, null, 0, 1f, 1f, 16f); }
            }
            return null;
        }

        /// <summary>The ramp of a gradient with one texel of its end colours repeated on every side (see <see cref="DecorPixels.PadEdges"/>): draw the source
        /// rectangle from (1, 1), N texels along the line. The key has the unit and font size when a stop is in u or em. Null while its bake waits.</summary>
        public Texture2D Ramp(GraphicsDevice dev, Gradient g, GradientKey k, float lengthLayout, float scale, float unitPx, float emPx)
        {
            var key = new DecorKey(DecorKey.Ramp, g.Hash, k.N, k.LengthTex, g.UnitKey(unitPx, emPx), 0);
            if (TryGet(key, out var tex)) { return tex; }
            if (!Budget.TakeLight()) { return null; }
            return Make(dev, key, g, lengthLayout, scale, unitPx, emPx);
        }

        /// <summary>A corner tile of a gradient (rx x ry texels; equal for a circle), padded by one texel like the ramp: draw the source rectangle
        /// (1, 1, rx, ry). The length of the box is on a 1/4 octave ladder (DecorScale.CornerLength) unless a stop is in px, u or em (then it is exact and the scale is
        /// already a step). Null while its bake waits.</summary>
        public Texture2D Corner(GraphicsDevice dev, Gradient g, int rxTex, int ryTex, float lengthLayout, float scale, bool end, float unitPx, float emPx)
        {
            int len = (int)MathF.Round(lengthLayout * scale);
            if (!g.UsesLengthStops) { len = DecorScale.CornerLength(len); lengthLayout = len / scale; }
            var key = new DecorKey(DecorKey.Corner, g.Hash, rxTex, ryTex, len, (end ? 1 : 0) | (g.UnitKey(unitPx, emPx) << 1));
            if (TryGet(key, out var tex)) { return tex; }
            if (!Budget.TakeLight()) { return null; }
            return Make(dev, key, g, lengthLayout, scale, unitPx, emPx);
        }

        /// <summary>A gradient drawn per pixel (an angle, a corner or a radial layer; Core GradientArea), the box of tw x th texels at a step of the scale, with
        /// the corner radii in texels (rt). It is keyed by the gradient, the texels, the step, the radii and the unit, so the same box at the same step reuses it.
        /// Null while its bake waits (or when the frame has no budget left: the layer is left out for the frame).</summary>
        public Texture2D Area(GraphicsDevice dev, Gradient g, int tw, int th, int step, in CornerPx rt, float unitPx, float emPx)
        {
            ulong h = g.Hash;
            h = Mix(h, (ulong)(uint)rt.TLx); h = Mix(h, (ulong)(uint)rt.TLy); h = Mix(h, (ulong)(uint)rt.TRx); h = Mix(h, (ulong)(uint)rt.TRy);
            h = Mix(h, (ulong)(uint)rt.BRx); h = Mix(h, (ulong)(uint)rt.BRy); h = Mix(h, (ulong)(uint)rt.BLx); h = Mix(h, (ulong)(uint)rt.BLy);
            h = Mix(h, (ulong)(uint)g.UnitKey(unitPx, emPx));
            var key = new DecorKey(DecorKey.Area, h, tw, th, step, 0);
            if (TryGet(key, out var tex)) { return tex; }
            bool heavy = tw * th > 128 * 128;
            if (heavy ? !Budget.TakeHeavy() : !Budget.TakeLight()) { return null; }
            Texture2D made = null;
            try
            {
                byte[] px = GradientArea.Bake(g, tw, th, DecorScale.Scale(step), rt, unitPx, emPx);
                made = new Texture2D(dev, tw, th);
                made.SetData(px);
                Baked++;
                cache.Put(key, made, (long)tw * th * 4);
                return made;
            }
            catch (Exception ex)
            {
                try { made?.Dispose(); } catch (Exception) { }
                failed[key] = frame + RetryAfter;
                DecorSpec.Tell($"gradient area {tw}x{th}: {ex.Message}");
                return null;
            }
        }

        private static ulong Mix(ulong h, ulong v) { h ^= v; return h * 1099511628211UL; }

        /// <summary>A key with a texture, a key whose bake failed (true with null: left out until its retry) or nothing (false).</summary>
        private bool TryGet(in DecorKey key, out Texture2D tex)
        {
            if (cache.TryGet(key, out tex))
            {
                if (!tex.IsDisposed) { return true; }
                cache.Drop(key);
                tex = null;
                return false;
            }
            tex = null;
            if (!failed.TryGetValue(key, out long retryAt)) { return false; }
            // a bake that failed: left out for a while, then tried again
            if (frame < retryAt) { return true; }
            failed.Remove(key);
            return false;
        }

        /// <summary>Bakes and uploads on a miss (no closure: the kind and the numbers are in the key, the gradient and its numbers are arguments). A failure
        /// frees the half-made texture, is logged once and is retried after <see cref="RetryAfter"/> frames, not for good.</summary>
        private Texture2D Make(GraphicsDevice dev, in DecorKey key, Gradient g, float lengthLayout, float scale, float unitPx, float emPx)
        {
            Texture2D tex = null;
            int w = 0, h = 0;
            try
            {
                byte[] px;
                switch (key.Kind)
                {
                    case DecorKey.ShadowTile:
                    {
                        var plan = new ShadowPlan(key.A, key.B, 1f);
                        px = ShadowRaster.Tile(plan); w = h = plan.TileSize;
                        break;
                    }
                    case DecorKey.ShadowOneOff:
                    {
                        int m = ShadowRaster.MarginFor(key.B);
                        px = ShadowRaster.Render(key.C, key.D, key.A, key.B, out _, out _); w = key.C + 2 * m; h = key.D + 2 * m;
                        break;
                    }
                    case DecorKey.Ramp:
                    {
                        var ramp = g.BuildRamp(lengthLayout, scale, unitPx, emPx);
                        px = DecorPixels.PadEdges(ramp.Rgba, ramp.Width, ramp.Height, 1); w = ramp.Width + 2; h = ramp.Height + 2;
                        break;
                    }
                    default:
                    {
                        bool end = (key.D & 1) != 0;
                        var tile = key.A == key.B ? g.CornerTile(key.A, lengthLayout, scale, end, unitPx, emPx)
                            : g.CornerTileE(g.Vertical ? key.B : key.A, g.Vertical ? key.A : key.B, lengthLayout, scale, end, unitPx, emPx);
                        px = DecorPixels.PadEdges(tile, key.A, key.B, 1); w = key.A + 2; h = key.B + 2;
                        break;
                    }
                }
                tex = new Texture2D(dev, w, h);
                tex.SetData(px);
                Baked++;
                cache.Put(key, tex, (long)w * h * 4);
                return tex;
            }
            catch (Exception ex)
            {
                try { tex?.Dispose(); } catch (Exception) { }
                failed[key] = frame + RetryAfter;
                DecorSpec.Tell($"decoration texture {w}x{h}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The window closed: its painter has finished. The textures go through the same graveyard as every picture (a batch of the frame just
        /// ended may still point at one).</summary>
        public void Dispose()
        {
            cache.Clear();
            failed.Clear();
        }
    }
}
