using System;

namespace BaroUI.Core
{
    /// <summary>The outline of a style, ready to draw: width, offset, colour. Only the solid line is drawn; any other style is drawn solid (the style
    /// engine says so once). It takes no room: layout never reads it.</summary>
    internal readonly struct OutlineSpec
    {
        public readonly bool Visible;
        public readonly float Width, Offset;
        public readonly Rgba Color;

        public OutlineSpec(bool visible, float width, float offset, Rgba color) { Visible = visible; Width = width; Offset = offset; Color = color; }

        /// <summary>CSS keyword widths.</summary>
        public static bool TryKeywordWidth(string word, out float px)
        {
            px = 3;
            switch (word.ToLowerInvariant())
            {
                case "thin": px = 1; return true;
                case "medium": px = 3; return true;
                case "thick": px = 5; return true;
            }
            return false;
        }

        /// <summary>No line: an empty, none or hidden outline-style in any letter case (a value written `None` by hand is the same as `none`).</summary>
        public static bool NoLine(string style) => style.Length == 0 || style.Equals("none", StringComparison.OrdinalIgnoreCase) || style.Equals("hidden", StringComparison.OrdinalIgnoreCase);

        public static bool IsStyleWord(string w) => w is "none" or "hidden" or "solid" or "dashed" or "dotted" or "double" or "groove" or "ridge" or "inset" or "outset";

        /// <summary>Read from the style's caches only (no allocation after the first read of a style): painting asks this for every box.</summary>
        public static OutlineSpec Of(ComputedStyle s)
        {
            string style = s.Get("outline-style");
            if (NoLine(style)) { return default; }
            var wl = s.GetLength("outline-width");
            float w = wl.Unit == Unit.Px ? Math.Max(0, wl.Value) : TryKeywordWidth(s.Get("outline-width"), out float kw) ? kw : 3;
            if (w <= 0) { return default; }
            var ol = s.GetLength("outline-offset");
            float off = ol.Unit == Unit.Px ? ol.Value : 0;
            var color = s.Get("outline-color").Equals("currentcolor", StringComparison.OrdinalIgnoreCase) ? s.GetColor("color") : s.GetColor("outline-color");
            return color.A == 0 ? default : new OutlineSpec(true, w, off, color);
        }
    }

    /// <summary>Where the outline ring goes, in whole pixels of the space the box is painted in (under scale the matrix scales it with the block).</summary>
    internal static class OutlineGeometry
    {
        public readonly struct Ring
        {
            public readonly int X, Y, W, H;
            /// <summary>Ring width in px.</summary>
            public readonly int Width;
            /// <summary>Radius of the outer edge: the box's radius grown by offset and width (0 for a square box, like browsers).</summary>
            public readonly int Radius;
            /// <summary>The ring is as wide as the whole outer rectangle is deep (a negative offset that eats the box): fill it.</summary>
            public readonly bool Solid;
            public bool Empty => W <= 0 || H <= 0 || Width <= 0;
            public Ring(int x, int y, int w, int h, int width, int radius, bool solid) { X = x; Y = y; W = w; H = h; Width = width; Radius = radius; Solid = solid; }
        }

        /// <summary>Like a border: whole pixels, at least 1 for anything above 0.</summary>
        public static int WidthPx(float w) => w <= 0 ? 0 : Math.Max(1, (int)Math.Floor(w + 0.001f));

        /// <summary>The ring around the border box (x, y, w, h) with the box's corner radius: the box grown by offset + width on every side.</summary>
        public static Ring Of(int x, int y, int w, int h, int radius, float width, float offset)
        {
            int bw = WidthPx(width), off = (int)Math.Round(offset);
            int grow = off + bw;
            int ox = x - grow, oy = y - grow, ow = w + 2 * grow, oh = h + 2 * grow;
            if (ow <= 0 || oh <= 0 || bw <= 0) { return new Ring(ox, oy, 0, 0, 0, 0, false); }
            int r = radius > 0 ? Math.Max(0, radius + grow) : 0;
            return new Ring(ox, oy, ow, oh, bw, r, 2 * bw >= Math.Min(ow, oh));
        }
    }
}
