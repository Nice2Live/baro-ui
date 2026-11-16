using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BaroUI.Core
{
    /// <summary>The eight radii of a box as written (CSS Backgrounds 3 5.1-5.2): per corner a horizontal and a vertical length, px or %.
    /// Read from the four longhands (border-top-left-radius ... ), which the border-radius shorthand fills; parsed once per computed style.
    /// A length in em or u is px by now; a negative or unknown one is 0.</summary>
    internal sealed class CornerSpec
    {
        /// <summary>Order TL, TR, BR, BL; X then Y.</summary>
        private readonly Length[] lens = new Length[8];
        private string t0, t1, t2, t3;

        public static readonly string[] Longhands =
            { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" };

        private static readonly ConditionalWeakTable<ComputedStyle, CornerSpec> cache = new();

        public static CornerSpec Of(ComputedStyle s)
        {
            string a = s.Get(Longhands[0]), b = s.Get(Longhands[1]), c = s.Get(Longhands[2]), d = s.Get(Longhands[3]);
            if (cache.TryGetValue(s, out var spec) && ReferenceEquals(spec.t0, a) && ReferenceEquals(spec.t1, b) && ReferenceEquals(spec.t2, c) && ReferenceEquals(spec.t3, d)) { return spec; }
            spec = new CornerSpec { t0 = a, t1 = b, t2 = c, t3 = d };
            for (int i = 0; i < 4; i++)
            {
                ParseLonghand(i == 0 ? a : i == 1 ? b : i == 2 ? c : d, s, out spec.lens[2 * i], out spec.lens[2 * i + 1]);
            }
            lock (cache) { cache.Remove(s); cache.Add(s, spec); }
            return spec;
        }

        /// <summary>One longhand: "10px" (a circle) or "10px 5px" (horizontal, vertical). Bad input is 0.</summary>
        public static void ParseLonghand(string value, ComputedStyle s, out Length x, out Length y)
        {
            x = y = Length.Zero;
            if (string.IsNullOrWhiteSpace(value)) { return; }
            var parts = StyleEngine.SplitSpaces(value);
            if (parts.Count == 0 || parts.Count > 2) { return; }
            x = Clean(s.ToPx(parts[0]));
            y = parts.Count > 1 ? Clean(s.ToPx(parts[1])) : x;
        }

        private static Length Clean(Length l) => (l.Unit == Unit.Px || l.Unit == Unit.Percent) && l.Value > 0 ? l : Length.Zero;

        /// <summary>True when any corner has a radius above 0.</summary>
        public bool Any { get { for (int i = 0; i < 8; i++) { if (lens[i].Value > 0) { return true; } } return false; } }

        /// <summary>The radii in px for a box of w x h, shrunk by the CSS rule when they do not fit.</summary>
        public Corners Resolve(float w, float h)
        {
            float R(int i, float basis) => lens[i].Unit == Unit.Percent ? lens[i].Value / 100f * basis : lens[i].Unit == Unit.Px ? lens[i].Value : 0;
            return Corners.Fit(w, h, R(0, w), R(1, h), R(2, w), R(3, h), R(4, w), R(5, h), R(6, w), R(7, h));
        }

        /// <summary>border-radius: 1-4 horizontal values, then optionally "/" and 1-4 vertical ones (TL TR BR BL, the padding order) -> the four
        /// longhand values ("h" or "h v"). False: not a valid value (nothing is written).</summary>
        public static bool Expand(string value, string[] into4)
        {
            if (value == null) { return false; }
            var parts = StyleEngine.SplitSpaces(value.Replace("/", " / "));
            if (parts.Count == 1 && (parts[0].Equals("inherit", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("initial", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("unset", StringComparison.OrdinalIgnoreCase)))
            {
                for (int i = 0; i < 4; i++) { into4[i] = parts[0]; }
                return true;
            }
            var h = new List<string>(4); var v = new List<string>(4);
            bool slash = false;
            foreach (var p in parts)
            {
                if (p == "/") { if (slash) { return false; } slash = true; continue; }
                (slash ? v : h).Add(p);
            }
            if (h.Count is 0 or > 4 || slash && v.Count is 0 or > 4) { return false; }
            for (int i = 0; i < 4; i++)
            {
                string hx = Pick(h, i);
                string vy = slash ? Pick(v, i) : hx;
                into4[i] = hx == vy ? hx : hx + " " + vy;
            }
            return true;
        }

        private static string Pick(List<string> l, int corner) => l.Count switch
        {
            1 => l[0],
            2 => l[corner is 0 or 2 ? 0 : 1],
            3 => l[corner == 0 ? 0 : corner == 2 ? 2 : 1],
            _ => l[corner],
        };
    }

    /// <summary>The eight radii of a box in px (TL, TR, BR, BL; x horizontal, y vertical).</summary>
    internal readonly struct Corners
    {
        public readonly float TLx, TLy, TRx, TRy, BRx, BRy, BLx, BLy;

        public Corners(float tlx, float tly, float trx, float try_, float brx, float bry, float blx, float bly)
        { TLx = tlx; TLy = tly; TRx = trx; TRy = try_; BRx = brx; BRy = bry; BLx = blx; BLy = bly; }

        /// <summary>The CSS rule (Backgrounds 3 5.5): f = min(1, w / (TLx + TRx), w / (BLx + BRx), h / (TLy + BLy), h / (TRy + BRy)), every radius times f.</summary>
        public static Corners Fit(float w, float h, float tlx, float tly, float trx, float try_, float brx, float bry, float blx, float bly)
        {
            float f = 1;
            void Side(float len, float a, float b) { if (a + b > len && a + b > 0) { f = Math.Min(f, Math.Max(0, len) / (a + b)); } }
            Side(w, tlx, trx); Side(w, blx, brx); Side(h, tly, bly); Side(h, try_, bry);
            return new Corners(tlx * f, tly * f, trx * f, try_ * f, brx * f, bry * f, blx * f, bly * f);
        }
    }

    /// <summary>The radii in whole px (texture px at paint): a corner with a zero axis is square. Rounded so that two corners on one side never add up
    /// to more than the side.</summary>
    internal readonly struct CornerPx
    {
        public readonly int TLx, TLy, TRx, TRy, BRx, BRy, BLx, BLy;

        public CornerPx(int tlx, int tly, int trx, int try_, int brx, int bry, int blx, int bly)
        { TLx = tlx; TLy = tly; TRx = trx; TRy = try_; BRx = brx; BRy = bry; BLx = blx; BLy = bly; }

        public static CornerPx From(in Corners c, int w, int h)
        {
            int tlx = (int)Math.Round(c.TLx), tly = (int)Math.Round(c.TLy), trx = (int)Math.Round(c.TRx), try_ = (int)Math.Round(c.TRy);
            int brx = (int)Math.Round(c.BRx), bry = (int)Math.Round(c.BRy), blx = (int)Math.Round(c.BLx), bly = (int)Math.Round(c.BLy);
            if (tlx + trx > w) { trx = Math.Max(0, w - tlx); }
            if (blx + brx > w) { brx = Math.Max(0, w - blx); }
            if (tly + bly > h) { bly = Math.Max(0, h - tly); }
            if (try_ + bry > h) { bry = Math.Max(0, h - try_); }
            if (tlx <= 0 || tly <= 0) { tlx = tly = 0; }
            if (trx <= 0 || try_ <= 0) { trx = try_ = 0; }
            if (brx <= 0 || bry <= 0) { brx = bry = 0; }
            if (blx <= 0 || bly <= 0) { blx = bly = 0; }
            return new CornerPx(tlx, tly, trx, try_, brx, bry, blx, bly);
        }

        public static CornerPx Of(ComputedStyle s, int w, int h) => From(CornerSpec.Of(s).Resolve(w, h), w, h);

        public bool Any => TLx > 0 || TRx > 0 || BRx > 0 || BLx > 0;

        /// <summary>All four corners the same circle: the old 9-slice mask draws it, exactly as before per-corner radii.</summary>
        public bool Circular => TLx == TLy && TRx == TRy && BRx == BRy && BLx == BLy && TLx == TRx && TLx == BRx && TLx == BLx;

        /// <summary>One number for what takes a single radius (box-shadow, gradient corners, outline, progress bars): the largest corner's smaller
        /// semi-axis. For equal circles it is the radius itself.</summary>
        public int Single => Math.Max(Math.Max(Math.Min(TLx, TLy), Math.Min(TRx, TRy)), Math.Max(Math.Min(BRx, BRy), Math.Min(BLx, BLy)));
    }

    /// <summary>The coverage of one corner tile, game-free. The tile is oriented as a top-left corner (the other three are mirrors of it) and is
    /// max(rx, bwx) x max(ry, bwy) texels: the outer edge is the quarter ellipse of radii (rx, ry) centred at (rx, ry), the inner edge (the padding
    /// edge of a ring) the quarter ellipse of radii (rx - bwx, ry - bwy) at the same centre, square when either is not positive. bwx is the width of
    /// the vertical side (left or right border), bwy of the horizontal one (top or bottom). Each texel is its analytic coverage (Coverage.cs), 0..255 in a byte.</summary>
    internal static class CornerRaster
    {
        /// <summary>part 0: the whole shape; 1: the half that belongs to the horizontal side (top or bottom border); 2: the half of the vertical side.
        /// Halves split along the line from the outer corner (0, 0) to the inner corner (bwx, bwy), as browsers join two border colours;
        /// 1 + 2 is exactly 0. No border widths (0, 0): the filled quarter.</summary>
        public static byte[] Hits(int rx, int ry, int bwx, int bwy, int part, out int tw, out int th)
        {
            tw = Math.Max(rx, bwx); th = Math.Max(ry, bwy);
            var hits = new byte[Math.Max(0, tw) * Math.Max(0, th)];
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    hits[y * tw + x] = Coverage.ToByte(Cover(rx, ry, bwx, bwy, part, x + 0.5f, y + 0.5f));
                }
            }
            return hits;
        }

        /// <summary>The analytic coverage (0..1) of one texel centre (x, y) in the tile (Core/Coverage.cs): the outer quarter ellipse less the inner
        /// one (or the inner square when the inner radius is not positive), split between the two sides by the diagonal of the border. Part 1 and 2
        /// add up to part 0 (their hard split is at the diagonal, the outer edge is soft).</summary>
        public static float Cover(int rx, int ry, int bwx, int bwy, int part, float x, float y)
        {
            float outer = rx <= 0 || ry <= 0 ? 1f : Coverage.Quadrant(rx - x, ry - y, rx, ry);
            float ring = outer;
            if (bwx > 0 || bwy > 0)
            {
                int irx = rx - bwx, iry = ry - bwy;
                float inner;
                if (irx > 0 && iry > 0) { inner = Coverage.Quadrant(rx - x, ry - y, irx, iry); }
                else { inner = Math.Clamp(x - bwx + 0.5f, 0f, 1f) * Math.Clamp(y - bwy + 0.5f, 0f, 1f); }
                ring = Math.Clamp(outer - inner, 0f, 1f);
            }
            if (bwx <= 0 && bwy <= 0 || part == 0) { return ring; }
            bool horizontal = y * bwx < x * bwy;
            return part == 1 ? (horizontal ? ring : 0f) : (horizontal ? 0f : ring);
        }

        public static bool Sample(int rx, int ry, int bwx, int bwy, int part, float x, float y)
        {
            if (!InOuter(rx, ry, x, y)) { return false; }
            if (bwx <= 0 && bwy <= 0) { return true; }
            if (InInner(rx, ry, bwx, bwy, x, y)) { return false; }
            if (part == 0) { return true; }
            bool horizontal = y * bwx < x * bwy;
            return part == 1 ? horizontal : !horizontal;
        }

        public static bool InOuter(int rx, int ry, float x, float y)
        {
            float dx = rx - x, dy = ry - y;
            if (dx <= 0 || dy <= 0) { return true; }
            return dx * dx / ((float)rx * rx) + dy * dy / ((float)ry * ry) <= 1f;
        }

        private static bool InInner(int rx, int ry, int bwx, int bwy, float x, float y)
        {
            if (x < bwx || y < bwy) { return false; }
            int irx = rx - bwx, iry = ry - bwy;
            if (irx <= 0 || iry <= 0) { return true; }
            float dx = rx - x, dy = ry - y;
            if (dx <= 0 || dy <= 0) { return true; }
            return dx * dx / ((float)irx * irx) + dy * dy / ((float)iry * iry) <= 1f;
        }
    }
}
