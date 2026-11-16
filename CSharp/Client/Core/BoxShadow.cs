using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>Splits a CSS value at top-level separators (commas or spaces) without cutting inside parentheses,
    /// so "0 4px 12px rgba(0,0,0,.4), 0 0 0 1px red" and "red 10%" both come apart the way a browser reads them.</summary>
    internal static class CssLists
    {
        /// <summary>Space splitting drops empty parts; comma splitting keeps them (an empty part is a syntax error the caller reports).</summary>
        public static List<string> Split(string s, char sep)
        {
            var list = new List<string>();
            if (s == null) { return list; }
            int depth = 0, start = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                bool isSep = i == s.Length;
                if (!isSep)
                {
                    char ch = s[i];
                    if (ch == '(') { depth++; }
                    else if (ch == ')') { depth = Math.Max(0, depth - 1); }
                    else if (depth == 0 && (sep == ' ' ? char.IsWhiteSpace(ch) : ch == sep)) { isSep = true; }
                }
                if (!isSep) { continue; }
                string part = s.Substring(start, i - start).Trim();
                if (part.Length > 0 || sep != ' ') { list.Add(part); }
                start = i + 1;
            }
            return list;
        }
    }

    /// <summary>A shadow in layout pixels: units (u, em) already resolved against the window.</summary>
    internal readonly struct ShadowPx
    {
        public readonly float X, Y, Blur, Spread;
        public ShadowPx(float x, float y, float blur, float spread) { X = x; Y = y; Blur = blur; Spread = spread; }
    }

    /// <summary>A parsed outer `box-shadow` (first shadow only). Kept in the author's units so it is parsed once per style and resolved per window.
    /// Colour is separate from the texture on purpose: one baked tile serves every colour (the painter tints it).</summary>
    internal readonly struct BoxShadow
    {
        public readonly Length X, Y, Blur, Spread;
        public readonly Rgba Color;
        /// <summary>False when the author gave no colour: CSS then means currentcolor, which only the painter knows.</summary>
        public readonly bool HasColor;
        /// <summary>`none`: nothing to draw.</summary>
        public readonly bool IsNone;

        public BoxShadow(Length x, Length y, Length blur, Length spread, Rgba color, bool hasColor, bool isNone)
        {
            X = x; Y = y; Blur = blur; Spread = spread; Color = color; HasColor = hasColor; IsNone = isNone;
        }

        public static readonly BoxShadow None = new(Length.Zero, Length.Zero, Length.Zero, Length.Zero, Rgba.Transparent, false, true);

        private static bool IsAbsolute(Length l) => l.Unit == Unit.Px || l.Unit == Unit.U || l.Unit == Unit.Em;

        /// <summary>True when there is something to use (the result may be `none`); false puts the reason in <paramref name="note"/>.
        /// With true, a non-null note is a warning (a list was cut to its first shadow).
        /// `inset` is not supported yet and is refused, so the cascade can print one clear line.</summary>
        public static bool TryParse(string css, out BoxShadow shadow, out string note)
        {
            shadow = None; note = null;
            if (string.IsNullOrWhiteSpace(css)) { note = "box-shadow: empty value"; return false; }
            var layers = CssLists.Split(css, ',');
            string first = layers[0];
            if (first.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (layers.Count > 1) { note = "box-shadow: none cannot be in a list"; return false; }
                return true;
            }
            var lens = new List<Length>(4);
            Rgba color = default; bool hasColor = false;
            foreach (string tok in CssLists.Split(first, ' '))
            {
                if (tok.Equals("inset", StringComparison.OrdinalIgnoreCase)) { note = "box-shadow: inset is not supported yet"; return false; }
                if (Length.TryParse(tok, out var l))
                {
                    if (!IsAbsolute(l) && !(l.Unit == Unit.None && l.Value == 0)) { note = "box-shadow: '" + tok + "' is not a length (use px, u or em)"; return false; }
                    if (lens.Count == 4) { note = "box-shadow: too many lengths"; return false; }
                    lens.Add(l);
                    continue;
                }
                if (tok.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) { if (hasColor) { note = "box-shadow: two colours"; return false; } hasColor = false; color = default; continue; }
                if (Rgba.TryParse(tok, out var c))
                {
                    if (hasColor) { note = "box-shadow: two colours"; return false; }
                    color = c; hasColor = true;
                    continue;
                }
                note = "box-shadow: cannot read '" + tok + "'";
                return false;
            }
            if (lens.Count < 2) { note = "box-shadow: needs at least offset-x and offset-y"; return false; }
            if (lens.Count > 2 && lens[2].Value < 0) { note = "box-shadow: blur cannot be negative"; return false; }
            if (!hasColor) { color = new Rgba(0, 0, 0); }
            shadow = new BoxShadow(lens[0], lens[1], lens.Count > 2 ? lens[2] : Length.Zero, lens.Count > 3 ? lens[3] : Length.Zero, color, hasColor, false);
            if (layers.Count > 1) { note = "box-shadow: only the first of " + layers.Count + " shadows is drawn"; }
            return true;
        }

        private static float Px(Length l, float unitPx, float emPx) => l.Unit switch { Unit.U => l.Value * unitPx, Unit.Em => l.Value * emPx, _ => l.Value };

        /// <summary>Lengths to layout pixels: u is the window unit, em the element's font size.</summary>
        public ShadowPx Resolve(float unitPx, float emPx) => new(Px(X, unitPx, emPx), Px(Y, unitPx, emPx), Math.Max(0, Px(Blur, unitPx, emPx)), Px(Spread, unitPx, emPx));
    }

    /// <summary>Texture-cache key of a baked shadow tile. Whole texture pixels under the current scale, so equal tiles are shared:
    /// the spread is folded into the radius (r + spread) and the tile does not depend on colour or offset.</summary>
    internal readonly struct ShadowKey : IEquatable<ShadowKey>
    {
        public readonly int REff, Blur;
        public ShadowKey(int rEff, int blur) { REff = rEff; Blur = blur; }
        public bool Equals(ShadowKey o) => REff == o.REff && Blur == o.Blur;
        public override bool Equals(object o) => o is ShadowKey k && Equals(k);
        public override int GetHashCode() => REff * 4099 + Blur;
        public override string ToString() => "shadow r" + REff + " b" + Blur;
    }

    /// <summary>The geometry of one 9-slice shadow tile in texture pixels. Everything is derived from (REff, Blur), so the painter only
    /// needs these fields to slice and place the tile.
    /// Layout: the blurred shape sits Margin px inside the tile on every side; Corner px from each tile edge are the fixed corner slices;
    /// the single row and column between them is the stretchable middle.</summary>
    internal readonly struct ShadowPlan
    {
        public readonly ShadowKey Key;
        public readonly float Scale;
        /// <summary>Distance from the tile edge to the shape edge: the blur reaches 3 sigma, beyond that alpha rounds to 0.</summary>
        public readonly int Margin;
        /// <summary>Slice inset on every side: Margin + corner arc + the blur reach of the arc = REff + 2 * Margin.</summary>
        public readonly int Corner;
        /// <summary>Tile width and height: 2 * Corner + 1 (a one-pixel stretchable middle).</summary>
        public readonly int TileSize;
        /// <summary>Smallest shape side (box plus spread, texture px) at which stretching equals a direct render; below it bake directly.</summary>
        public readonly int MinShape;

        public ShadowPlan(int rEff, int blur, float scale)
        {
            Key = new ShadowKey(rEff, blur);
            Scale = scale;
            Margin = ShadowRaster.MarginFor(blur);
            Corner = rEff + 2 * Margin;
            TileSize = 2 * Corner + 1;
            MinShape = TileSize - 2 * Margin;
        }

        /// <summary>Margin and slice inset converted back to layout pixels (the painter's destination rectangles).</summary>
        public float MarginLayout => Margin / Scale;
        public float CornerLayout => Corner / Scale;
        /// <summary>True when a shape of this size (layout px, after spread) can use the tile; otherwise bake a one-off with <see cref="ShadowRaster.Render"/>.</summary>
        public bool CanSlice(float shapeWLayout, float shapeHLayout) => Math.Min(shapeWLayout, shapeHLayout) * Scale >= MinShape - 0.5f;
    }

    /// <summary>Pure pixel code for outer box shadows: no game types, so the tests read the pixels.
    /// Output is white RGBA with straight alpha: colour is the same everywhere (so transparent pixels carry the neighbour's colour and
    /// bilinear filtering cannot darken the edge) and the painter tints it with the shadow colour. The game blends NonPremultiplied.</summary>
    internal static class ShadowRaster
    {
        /// <summary>Caps that bound the one-time bake (a 481 px tile at most) and the texture memory.</summary>
        public const int MaxBlur = 48, MaxRadius = 96;

        /// <summary>Texture pixels of margin: 3 sigma (sigma = blur / 2, the CSS definition), at least 1 so the edge can antialias.</summary>
        public static int MarginFor(int blur) => blur <= 0 ? 1 : (int)MathF.Ceiling(1.5f * blur);

        private static int Q(float v, float scale) => (int)MathF.Round(v * scale, MidpointRounding.AwayFromZero);

        /// <summary>Quantizes radius, blur and spread (layout px) to whole texture pixels under the current scale and derives the tile.
        /// A square box keeps square corners whatever the spread (CSS); otherwise the shadow radius is radius + spread, not below 0.</summary>
        public static ShadowPlan Plan(float radius, float blur, float spread, float scale)
        {
            if (!(scale > 0)) { scale = 1; }
            int r = Q(radius, scale), b = Math.Max(0, Q(blur, scale)), s = Q(spread, scale);
            int rEff = r <= 0 ? 0 : Math.Max(0, r + s);
            return new ShadowPlan(Math.Min(rEff, MaxRadius), Math.Min(b, MaxBlur), scale);
        }

        /// <summary>The baked 9-slice tile (TileSize squared): the blurred rounded square of the smallest sliceable size.</summary>
        public static byte[] Tile(ShadowPlan p) => Render(p.MinShape, p.MinShape, p.Key.REff, p.Key.Blur, out _, out _);

        /// <summary>Blurs a rounded rectangle of the given size directly. The result is (shapeW + 2m) x (shapeH + 2m) with the shape
        /// m = MarginFor(blur) px inside. Coverage is the signed distance to the rounded box (1 px antialiased edge), then an exact
        /// separable Gaussian (sigma = blur / 2, cut at 3 sigma). Exact rather than 3 box passes: it is baked once per key, the cost
        /// is a few milliseconds, and the 9-slice equality test can then be tight.</summary>
        public static byte[] Render(int shapeW, int shapeH, int rEff, int blur, out int width, out int height)
        {
            int m = MarginFor(blur);
            width = shapeW + 2 * m; height = shapeH + 2 * m;
            rEff = Math.Max(0, Math.Min(rEff, Math.Min(shapeW, shapeH) / 2));
            var a = new float[width * height];
            float cx = m + shapeW * 0.5f, cy = m + shapeH * 0.5f;
            float bx = shapeW * 0.5f - rEff, by = shapeH * 0.5f - rEff;
            for (int y = 0; y < height; y++)
            {
                float py = MathF.Abs(y + 0.5f - cy) - by;
                for (int x = 0; x < width; x++)
                {
                    float qx = MathF.Abs(x + 0.5f - cx) - bx;
                    float ox = MathF.Max(qx, 0), oy = MathF.Max(py, 0);
                    float d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, py), 0) - rEff;
                    a[y * width + x] = Math.Clamp(0.5f - d, 0f, 1f);
                }
            }
            if (blur > 0) { Blur(a, width, height, m, blur * 0.5f); }
            var px = new byte[width * height * 4];
            for (int i = 0; i < a.Length; i++)
            {
                px[i * 4] = 255; px[i * 4 + 1] = 255; px[i * 4 + 2] = 255;
                px[i * 4 + 3] = (byte)Math.Clamp((int)MathF.Round(a[i] * 255f), 0, 255);
            }
            return px;
        }

        private static void Blur(float[] a, int w, int h, int radius, float sigma)
        {
            var k = new float[radius * 2 + 1];
            float sum = 0;
            for (int i = -radius; i <= radius; i++) { sum += k[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma)); }
            for (int i = 0; i < k.Length; i++) { k[i] /= sum; }
            var t = new float[a.Length];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    int lo = Math.Max(-radius, -x), hi = Math.Min(radius, w - 1 - x);
                    for (int i = lo; i <= hi; i++) { acc += k[i + radius] * a[row + x + i]; }
                    t[row + x] = acc;
                }
            }
            for (int y = 0; y < h; y++)
            {
                int lo = Math.Max(-radius, -y), hi = Math.Min(radius, h - 1 - y);
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int i = lo; i <= hi; i++) { acc += k[i + radius] * t[(y + i) * w + x]; }
                    a[y * w + x] = acc;
                }
            }
        }

        /// <summary>CPU model of the 9-slice draw: corners copied, the middle row and column repeated. The painter does the same with
        /// nine sprites; the tests use this to prove the tile reproduces a direct render of any larger size.</summary>
        public static byte[] Stretch(byte[] tile, ShadowPlan p, int shapeW, int shapeH, out int width, out int height)
        {
            int m = p.Margin, c = p.Corner, t = p.TileSize;
            width = shapeW + 2 * m; height = shapeH + 2 * m;
            var px = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int sy = y < c ? y : y >= height - c ? t - (height - y) : c;
                for (int x = 0; x < width; x++)
                {
                    int sx = x < c ? x : x >= width - c ? t - (width - x) : c;
                    Buffer.BlockCopy(tile, (sy * t + sx) * 4, px, (y * width + x) * 4, 4);
                }
            }
            return px;
        }
    }

    /// <summary>The ladder of decoration scales. A baked decoration (shadow tile, gradient corner, one-off shadow) is keyed by the NEAREST step of the
    /// scale it is drawn at, not by the exact scale, and the drawn size stretches that step's texture: while a block zooms smoothly the keys do not change
    /// from frame to frame. Steps are 1/8 octave apart (ratio 2^(1/8) = 1.0905), so the nearest step is at most 2^(1/16) = 1.0443 away: a stretched
    /// texture is at most 4.4 % too large or too small (<see cref="MaxStretch"/>). Step 0 is exactly scale 1, so an unscaled window bakes what it did before.</summary>
    internal static class DecorScale
    {
        public const int PerOctave = 8, MinStep = -48, MaxStep = 48;
        /// <summary>The largest ratio between a drawn scale and the step it uses (and its inverse): 2^(1/16).</summary>
        public const float MaxStretch = 1.04427f;

        public static int Step(float scale)
        {
            if (!(scale > 0) || scale == 1f) { return 0; }
            return Math.Clamp((int)MathF.Round(MathF.Log2(scale) * PerOctave), MinStep, MaxStep);
        }

        public static float Scale(int step) => step == 0 ? 1f : MathF.Pow(2f, step / (float)PerOctave);

        /// <summary>The scale of the nearest step.</summary>
        public static float Snap(float scale) => Scale(Step(scale));

        /// <summary>The i-th try around a step: 0, +1, -1, +2, -2, ... (what the painter looks for when a bake has to wait).</summary>
        public static int Around(int step, int i) => i == 0 ? step : (i & 1) == 1 ? step + (i + 1) / 2 : step - i / 2;

        /// <summary>The length of a gradient box for its corner tiles, on a coarser ladder: exact up to 16, then the nearest 1/4 octave step (at most 9.1 % off, 10 % with the rounding to a pixel).
        /// A corner tile only holds the r texels of the ramp at the end of the line, so the colour at a texel of it is off by at most 9 % of r / length of the
        /// gradient range (for a 10 px corner on a 100 px box under 1 % of the colours), and the number of tiles per scale step halves.</summary>
        public static int CornerLength(int lenTex)
        {
            if (lenTex <= 16) { return Math.Max(1, lenTex); }
            return (int)MathF.Round(MathF.Pow(2f, MathF.Round(MathF.Log2(lenTex) * 4) / 4f));
        }

        /// <summary>A length in texture pixels on the same kind of ladder: exact up to 16, then the nearest 1/8 octave step (at most 4.4 % off).
        /// Corner tiles of a gradient whose stops are percentages are keyed by it, so thirty box heights do not bake thirty tiles per scale.</summary>
        public static int Length(int lenTex)
        {
            if (lenTex <= 16) { return Math.Max(1, lenTex); }
            return (int)MathF.Round(MathF.Pow(2f, MathF.Round(MathF.Log2(lenTex) * PerOctave) / PerOctave));
        }
    }

    /// <summary>How many decoration textures may be baked in one frame. A heavy bake (a shadow tile or a one-off shadow: an exact Gaussian, up to tens of
    /// milliseconds) is limited to <see cref="HeavyPerFrame"/>, a light one (a ramp of 2 KB, a corner tile) to <see cref="LightPerFrame"/>; what does not get
    /// its turn waits for a later frame while the painter draws the nearest step it has (shadow) or leaves the piece out for the frame (ramp, corner).</summary>
    internal sealed class DecorBudget
    {
        public int HeavyPerFrame = 2, LightPerFrame = 64;
        private int heavy, light;
        /// <summary>Bakes taken in the frame now running.</summary>
        public int HeavyNow => heavy;
        public int LightNow => light;

        public void BeginFrame() { heavy = 0; light = 0; }
        public bool TakeHeavy() { if (heavy >= HeavyPerFrame) { return false; } heavy++; return true; }
        public bool TakeLight() { if (light >= LightPerFrame) { return false; } light++; return true; }
    }

    /// <summary>One sprite of a decoration: a whole-pixel destination rectangle (layout px) and the source rectangle in the texture (texture px).</summary>
    internal readonly struct DecorSlice
    {
        public readonly int DX, DY, DW, DH, SX, SY, SW, SH;
        public DecorSlice(int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh) { DX = dx; DY = dy; DW = dw; DH = dh; SX = sx; SY = sy; SW = sw; SH = sh; }
    }

    /// <summary>The nine sprites that draw a baked shadow tile around a shape (box plus spread, already moved by the offset), in layout px.
    /// The cut lines are rounded as edges, so neighbours share them and the slices tile the outer rectangle with no gap or overlap.</summary>
    internal static class ShadowSlices
    {
        /// <summary>Writes the slices into a span of at least 9 and returns how many (empty ones are left out). Use only when
        /// <see cref="ShadowPlan.CanSlice"/> says the tile fits the shape.</summary>
        public static int Fill(Span<DecorSlice> into, ShadowPlan p, float shapeX, float shapeY, float shapeW, float shapeH)
        {
            float m = p.MarginLayout, c = p.CornerLayout;
            float l = shapeX - m, t = shapeY - m, r = shapeX + shapeW + m, b = shapeY + shapeH + m;
            Span<int> dx = stackalloc int[] { Edge(l), Edge(l + c), Edge(r - c), Edge(r) };
            Span<int> dy = stackalloc int[] { Edge(t), Edge(t + c), Edge(b - c), Edge(b) };
            Span<int> s = stackalloc int[] { 0, p.Corner, p.Corner + 1, p.TileSize };
            int n = 0;
            for (int j = 0; j < 3; j++)
            {
                int h = dy[j + 1] - dy[j];
                if (h <= 0) { continue; }
                for (int i = 0; i < 3; i++)
                {
                    int w = dx[i + 1] - dx[i];
                    if (w <= 0) { continue; }
                    into[n++] = new DecorSlice(dx[i], dy[j], w, h, s[i], s[j], s[i + 1] - s[i], s[j + 1] - s[j]);
                }
            }
            return n;
        }

        private static int Edge(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);
    }
}
