using System;

namespace BaroUI.Core
{
    /// <summary>Overflow clip with a border-radius (game-free): does a block need its children clipped to the rounded shape, and which pixels of
    /// its padding box lie outside that shape. The painter renders the children into a target of the padding box and applies the mask
    /// (Render/Painter.cs, TryRoundedChildren); everything here is geometry in the box's own px, no textures.
    /// Corner geometry: corner (TL, TR, BR, BL) is the quarter ellipse of radii (x, y) centred at (x, y) from the box corner, so the corner square
    /// [0, rx] x [0, ry] holds the area outside the arc.</summary>
    internal static class RoundedClip
    {
        /// <summary>Key of a mask: the size and the four corners (x in the high 32 bits, y in the low ones). Equal keys, equal pixels.</summary>
        public readonly record struct MaskKey(int W, int H, long TL, long TR, long BR, long BL);

        /// <summary>The padding box's radii: the outer radii less the border width on each side (a corner keeps its arc, shrunk by the border).</summary>
        public static CornerPx Inset(in CornerPx outer, float left, float top, float right, float bottom)
        {
            int l = (int)Math.Round(left), t = (int)Math.Round(top), r = (int)Math.Round(right), b = (int)Math.Round(bottom);
            return new CornerPx(
                Math.Max(0, outer.TLx - l), Math.Max(0, outer.TLy - t),
                Math.Max(0, outer.TRx - r), Math.Max(0, outer.TRy - t),
                Math.Max(0, outer.BRx - r), Math.Max(0, outer.BRy - b),
                Math.Max(0, outer.BLx - l), Math.Max(0, outer.BLy - b));
        }

        /// <summary>The radii of a box drawn at a scale (the device px of a scaled block), fitted to its device size.</summary>
        public static CornerPx ToDevice(in CornerPx c, float scale, int w, int h) =>
            CornerPx.From(new Corners(c.TLx * scale, c.TLy * scale, c.TRx * scale, c.TRy * scale, c.BRx * scale, c.BRy * scale, c.BLx * scale, c.BLy * scale), w, h);

        /// <summary>True when a box's rectangle (x, y, rw, rh in the padding box's px, may lie partly outside it) has a point of its area in a corner
        /// square that lies outside the arc. Exact: the overlap of the rectangle with a corner square is a rectangle, and the point of it farthest
        /// from the arc's centre (the centre is a corner of the square) is one of its corners.</summary>
        public static bool Reaches(int w, int h, in CornerPx c, int x, int y, int rw, int rh)
        {
            if (rw <= 0 || rh <= 0) { return false; }
            int x1 = x + rw, y1 = y + rh;
            if (c.TLx > 0 && c.TLy > 0 && Outside(Math.Max(x, 0), Math.Max(y, 0), Math.Min(x1, c.TLx), Math.Min(y1, c.TLy), c.TLx, c.TLy, c.TLx, c.TLy)) { return true; }
            if (c.TRx > 0 && c.TRy > 0 && Outside(Math.Max(x, w - c.TRx), Math.Max(y, 0), Math.Min(x1, w), Math.Min(y1, c.TRy), w - c.TRx, c.TRy, c.TRx, c.TRy)) { return true; }
            if (c.BRx > 0 && c.BRy > 0 && Outside(Math.Max(x, w - c.BRx), Math.Max(y, h - c.BRy), Math.Min(x1, w), Math.Min(y1, h), w - c.BRx, h - c.BRy, c.BRx, c.BRy)) { return true; }
            if (c.BLx > 0 && c.BLy > 0 && Outside(Math.Max(x, 0), Math.Max(y, h - c.BLy), Math.Min(x1, c.BLx), Math.Min(y1, h), c.BLx, h - c.BLy, c.BLx, c.BLy)) { return true; }
            return false;
        }

        /// <summary>The overlap (x0..x1, y0..y1) with a corner square, the arc's centre (cx, cy) and radii (rx, ry): true when its farthest point (per axis,
        /// the end farther from the centre) lies outside the ellipse.</summary>
        private static bool Outside(int x0, int y0, int x1, int y1, int cx, int cy, int rx, int ry)
        {
            if (x1 <= x0 || y1 <= y0) { return false; }
            int fx = Math.Abs(x0 - cx) >= Math.Abs(x1 - cx) ? x0 : x1;
            int fy = Math.Abs(y0 - cy) >= Math.Abs(y1 - cy) ? y0 : y1;
            double dx = (cx - fx) / (double)rx, dy = (cy - fy) / (double)ry;
            return dx * dx + dy * dy > 1.0;
        }

        /// <summary>True when the block needs the rounded path: it clips both axes, has a radius, and some box of its subtree (the rectangles, padding
        /// box px) reaches a corner outside the arc. The rectangles are (x, y, w, h).</summary>
        public static bool Needs(bool clipBoth, int w, int h, in CornerPx c, ReadOnlySpan<(int X, int Y, int W, int H)> rects)
        {
            if (!clipBoth || !c.Any) { return false; }
            foreach (var r in rects)
            {
                if (Reaches(w, h, c, r.X, r.Y, r.W, r.H)) { return true; }
            }
            return false;
        }

        public static MaskKey Key(int w, int h, in CornerPx c) =>
            new(w, h, Pack(c.TLx, c.TLy), Pack(c.TRx, c.TRy), Pack(c.BRx, c.BRy), Pack(c.BLx, c.BLy));

        private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

        /// <summary>The outside of the shape as alpha, w x h bytes (row by row): 255 outside (the mask darkens it away), 0 inside, the corner
        /// texels in between by their analytic coverage (Core/Coverage.cs). Only the four corner squares are computed; the rest is 0 by construction.</summary>
        public static byte[] Alpha(int w, int h, in CornerPx c)
        {
            var a = new byte[Math.Max(0, w) * Math.Max(0, h)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!InCornerSquare(c, w, h, x, y)) { continue; }
                    a[y * w + x] = Coverage.ToByte(1f - InsideCoverage(c, w, h, x + 0.5f, y + 0.5f));
                }
            }
            return a;
        }

        /// <summary>The coverage (0..1) of the inside of the shape at a point of the box: the arc of its corner, judged from the corner's centre.</summary>
        public static float InsideCoverage(in CornerPx c, int w, int h, float px, float py)
        {
            if (px < c.TLx && py < c.TLy) { return Coverage.Quadrant(c.TLx - px, c.TLy - py, c.TLx, c.TLy); }
            if (px > w - c.TRx && py < c.TRy) { return Coverage.Quadrant(px - (w - c.TRx), c.TRy - py, c.TRx, c.TRy); }
            if (px > w - c.BRx && py > h - c.BRy) { return Coverage.Quadrant(px - (w - c.BRx), py - (h - c.BRy), c.BRx, c.BRy); }
            if (px < c.BLx && py > h - c.BLy) { return Coverage.Quadrant(c.BLx - px, py - (h - c.BLy), c.BLx, c.BLy); }
            return 1f;
        }

        private static bool InCornerSquare(in CornerPx c, int w, int h, int x, int y) =>
            (x < c.TLx && y < c.TLy) || (x >= w - c.TRx && y < c.TRy) || (x >= w - c.BRx && y >= h - c.BRy) || (x < c.BLx && y >= h - c.BLy);

        /// <summary>A point (px, py) of the box is inside the shape: outside every corner square, or inside the arc of its corner.</summary>
        public static bool Inside(in CornerPx c, int w, int h, float px, float py)
        {
            if (px < c.TLx && py < c.TLy) { return Ellipse(c.TLx - px, c.TLy - py, c.TLx, c.TLy); }
            if (px > w - c.TRx && py < c.TRy) { return Ellipse(px - (w - c.TRx), c.TRy - py, c.TRx, c.TRy); }
            if (px > w - c.BRx && py > h - c.BRy) { return Ellipse(px - (w - c.BRx), py - (h - c.BRy), c.BRx, c.BRy); }
            if (px < c.BLx && py > h - c.BLy) { return Ellipse(c.BLx - px, py - (h - c.BLy), c.BLx, c.BLy); }
            return true;
        }

        private static bool Ellipse(float dx, float dy, int rx, int ry) => dx * dx / ((float)rx * rx) + dy * dy / ((float)ry * ry) <= 1f;
    }
}
