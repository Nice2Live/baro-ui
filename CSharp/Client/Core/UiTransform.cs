using System;

namespace BaroUI.Core
{
    /// <summary>A uniform scale followed by a shift: p' = S * p + T. The whole of what the <c>scale</c> property needs (no rotation, no skew), and the set
    /// is closed under composition and inversion, so nested scaled blocks are one struct and a screen point can be taken back into a block's own
    /// layout space. Pure arithmetic, no game types: the Painter turns it into the sprite batch's matrix, the tests run it as it is.
    /// Units are pixels; rectangles are mapped by their edges, so neighbours that shared an edge still share it.</summary>
    public readonly struct UiTransform : IEquatable<UiTransform>
    {
        /// <summary>The scale a block may ask for: a runaway value (or 0) must not turn the page into a point or a screen-sized pixel.</summary>
        public const float MinScale = 0.001f, MaxScale = 1000f;

        /// <summary>Rectangle edges stay inside this, so a width (right minus left) still fits an int.</summary>
        private const int Edge = int.MaxValue / 2;

        public readonly float S, Tx, Ty;

        public UiTransform(float s, float tx, float ty) { S = s; Tx = tx; Ty = ty; }

        public static UiTransform Identity => new(1, 0, 0);

        public bool IsIdentity => S == 1 && Tx == 0 && Ty == 0;

        /// <summary>Scale s about the point (ox, oy): that point stays where it is.</summary>
        public static UiTransform Scale(float ox, float oy, float s) => new(s, ox - s * ox, oy - s * oy);

        /// <summary>This transform applied after <paramref name="inner"/> (inner first): the way a block's own scale sits inside the transform of its ancestors.</summary>
        public UiTransform After(UiTransform inner) => new(S * inner.S, S * inner.Tx + Tx, S * inner.Ty + Ty);

        public float X(float x) => S * x + Tx;
        public float Y(float y) => S * y + Ty;

        public UiTransform Inverse() => new(1f / S, -Tx / S, -Ty / S);

        /// <summary>The scale property as the painter uses it: anything that is not a positive finite number is 1 (no scale), the rest is kept in
        /// [MinScale, MaxScale].</summary>
        public static float CleanScale(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0) { return 1; }
            return Math.Clamp(v, MinScale, MaxScale);
        }

        public static float ScaleOf(ComputedStyle s) => CleanScale(s.GetNumber("scale", 1));

        /// <summary>The element's translate in pixels: its numeric override if set, else the style's. e is null for an anonymous box (style only).</summary>
        public static (float X, float Y) TranslateOf(Element e, ComputedStyle s) => e != null && e.HasTranslate ? e.TranslateOverride : s.GetPair("translate");

        /// <summary>The element's scale: its numeric override if set, else the style's (see <see cref="TranslateOf"/>).</summary>
        public static float ScaleOf(Element e, ComputedStyle s) => e?.ScaleOverride ?? ScaleOf(s);

        /// <summary>The font size (whole pixels) to render for a CSS size under the scale: exact up to 24, then steps of 2 up to 64 and of 4 above, never
        /// below 4 or above max, so a smooth zoom makes a handful of fonts, not one per frame. The factor left over (size / result) is applied when drawing.</summary>
        public static uint FontPx(uint size, float scale, uint max)
        {
            double px = size * (double)scale;
            double q = px <= 24 ? Math.Round(px) : px <= 64 ? 2 * Math.Round(px / 2) : 4 * Math.Round(px / 4);
            return (uint)Math.Clamp(q, 4, max);
        }

        /// <summary>The rectangle (x, y, w, h) mapped by its edges. outward: edges rounded away from the middle (the smallest whole-pixel rectangle
        /// that holds the image: culling, areas of a hit test); otherwise to the nearest pixel (what is drawn, scissors).</summary>
        public (int X, int Y, int W, int H) MapRect(int x, int y, int w, int h, bool outward)
        {
            int x0 = Edge32(X(x), outward, false), x1 = Edge32(X((float)x + w), outward, true);
            int y0 = Edge32(Y(y), outward, false), y1 = Edge32(Y((float)y + h), outward, true);
            return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        private static int Edge32(float v, bool outward, bool far)
        {
            double r = outward ? (far ? Math.Ceiling(v) : Math.Floor(v)) : Math.Round(v);
            return (int)Math.Clamp(r, -Edge, Edge - 1);
        }

        /// <summary>The same rectangle in floats, exactly (no rounding).</summary>
        public (float X, float Y, float W, float H) MapRect(float x, float y, float w, float h) => (X(x), Y(y), w * S, h * S);

        public bool Equals(UiTransform o) => S == o.S && Tx == o.Tx && Ty == o.Ty;
        public override bool Equals(object obj) => obj is UiTransform o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(S, Tx, Ty);
        public override string ToString() => $"x{S} +({Tx}, {Ty})";
    }
}
