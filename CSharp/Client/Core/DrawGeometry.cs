using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>A point in the pixels of a canvas (double: the geometry is made in full precision, the draw rounds it).</summary>
    public readonly struct DPt
    {
        public readonly double X, Y;
        public DPt(double x, double y) { X = x; Y = y; }
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>A straight piece of a path, from A to B.</summary>
    internal readonly struct DSeg
    {
        public readonly DPt A, B;
        public DSeg(DPt a, DPt b) { A = a; B = b; }
    }

    /// <summary>A horizontal run of pixels of a filled shape: row Y (the pixel row, its middle at Y + 0.5), from X0 to X1.</summary>
    internal readonly struct DSpan
    {
        public readonly int Y;
        public readonly double X0, X1;
        public DSpan(int y, double x0, double x1) { Y = y; X0 = x0; X1 = x1; }
    }

    /// <summary>The geometry of the drawing kit (Render/UiDraw.cs draws it), free of the game so the tests run it (docs/ui/DESIGN_CHARTS.md 2.1):
    /// a path with its right-angle corners rounded into quarter rings (<see cref="Fillet"/>), the coverage of a stroke with round joins (its pieces are
    /// segments and the joins: a disc of half the width at each vertex, so no notch shows at a corner), a dash walk with a running phase, arcs and sectors,
    /// the scanline spans of a filled polygon (a sector, the area under a curve), a smooth sampling of a function, and <see cref="GeometryCache"/>, which says
    /// when the geometry of the data and the size must be made again. All lists are the caller's: the functions clear and fill them, so a steady frame
    /// allocates nothing.</summary>
    internal static class DrawGeometry
    {
        private const double Eps = 1e-9;

        /// <summary>0 when the segment a-b is horizontal, 1 when vertical, -1 otherwise (or no length).</summary>
        public static int AxisOf(DPt a, DPt b)
        {
            bool sx = Math.Abs(a.X - b.X) < 1e-6, sy = Math.Abs(a.Y - b.Y) < 1e-6;
            return sy && !sx ? 0 : sx && !sy ? 1 : -1;
        }

        /// <summary>Copies the path into output and rounds each corner between two perpendicular axis-aligned legs into a quarter ring of the given radius (a
        /// corner cut back on both legs, at most half of each leg, so two corners never meet). Other corners stay sharp (a stroke joins them round). The ring
        /// has about one sample per 2 px of arc, between 4 and 64. radius 0 or less copies the path as it is.</summary>
        public static void Fillet(IReadOnlyList<DPt> pts, double radius, List<DPt> output)
        {
            output.Clear();
            int n = pts.Count;
            if (n == 0) { return; }
            output.Add(pts[0]);
            for (int i = 1; i < n - 1; i++)
            {
                DPt p = pts[i], a = pts[i - 1], b = pts[i + 1];
                int inAxis = AxisOf(a, p), outAxis = AxisOf(p, b);
                double lenIn = Dist(a, p), lenOut = Dist(p, b);
                if (radius <= 0 || inAxis < 0 || outAxis < 0 || inAxis == outAxis || lenIn < Eps || lenOut < Eps)
                {
                    output.Add(p);
                    continue;
                }
                double q = Math.Min(radius, Math.Min(lenIn / 2, lenOut / 2));
                // unit directions of the two legs (a to p, p to b)
                double ux = (p.X - a.X) / lenIn, uy = (p.Y - a.Y) / lenIn;
                double vx = (b.X - p.X) / lenOut, vy = (b.Y - p.Y) / lenOut;
                var s = new DPt(p.X - ux * q, p.Y - uy * q);
                var e = new DPt(p.X + vx * q, p.Y + vy * q);
                // the centre of the ring: q along the outgoing leg from s
                var c = new DPt(s.X + vx * q, s.Y + vy * q);
                double a0 = Math.Atan2(s.Y - c.Y, s.X - c.X), a1 = Math.Atan2(e.Y - c.Y, e.X - c.X);
                // the short way round (a quarter turn)
                double d = a1 - a0;
                while (d > Math.PI) { d -= 2 * Math.PI; }
                while (d < -Math.PI) { d += 2 * Math.PI; }
                int steps = Math.Clamp((int)Math.Ceiling(q * Math.Abs(d) / 2), 4, 64);
                output.Add(s);
                for (int k = 1; k < steps; k++)
                {
                    double t = a0 + d * k / steps;
                    output.Add(new DPt(c.X + q * Math.Cos(t), c.Y + q * Math.Sin(t)));
                }
                output.Add(e);
            }
            output.Add(pts[n - 1]);
        }

        /// <summary>The segments of a path: one per pair of neighbouring points, zero length ones left out.</summary>
        public static void Segments(IReadOnlyList<DPt> pts, List<DSeg> output)
        {
            output.Clear();
            for (int i = 1; i < pts.Count; i++)
            {
                if (Dist(pts[i - 1], pts[i]) < Eps) { continue; }
                output.Add(new DSeg(pts[i - 1], pts[i]));
            }
        }

        /// <summary>The distance from p to the segment a-b.</summary>
        public static double DistanceToSegment(DPt p, DPt a, DPt b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double t = len2 < Eps ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            return Dist(p, new DPt(a.X + dx * t, a.Y + dy * t));
        }

        /// <summary>Whether a stroke of the width covers p: within half the width of a segment or of a vertex (the round joins). The vertices of a path are the
        /// joins of its stroke; a fillet's samples are vertices too, so a rounded corner is covered as a ring.</summary>
        public static bool StrokeCovers(IReadOnlyList<DPt> pts, double width, DPt p)
        {
            double half = width / 2;
            for (int i = 0; i < pts.Count; i++)
            {
                if (Dist(p, pts[i]) <= half + Eps) { return true; }
                if (i > 0 && DistanceToSegment(p, pts[i - 1], pts[i]) <= half + Eps) { return true; }
            }
            return false;
        }

        /// <summary>Walks the dashes of a path: on-pieces of length on, gaps of off, the pattern starting at phase (the distance already walked into it, any
        /// value: it is taken modulo on + off). The pieces go to output. Returns the phase at the end of the path, so the next path continues the pattern
        /// (the running phase of a wire, a border, a picked wire). on + off must be positive.</summary>
        public static double WalkDashes(IReadOnlyList<DPt> pts, double on, double off, double phase, List<DSeg> output)
        {
            output.Clear();
            double total = on + off;
            if (!(total > 0) || on <= 0) { return phase; }
            double ph = ((phase % total) + total) % total;
            for (int i = 1; i < pts.Count; i++)
            {
                DPt a = pts[i - 1], b = pts[i];
                double len = Dist(a, b);
                if (len < Eps) { continue; }
                double at = 0;
                while (at < len - 1e-12)
                {
                    bool isOn = ph < on;
                    double left = isOn ? on - ph : total - ph;
                    double piece = Math.Min(len - at, left);
                    if (piece <= 1e-12) { piece = Math.Min(len - at, 1e-9); }
                    if (isOn)
                    {
                        double t0 = at / len, t1 = (at + piece) / len;
                        output.Add(new DSeg(new DPt(a.X + (b.X - a.X) * t0, a.Y + (b.Y - a.Y) * t0), new DPt(a.X + (b.X - a.X) * t1, a.Y + (b.Y - a.Y) * t1)));
                    }
                    at += piece;
                    ph = (ph + piece) % total;
                }
            }
            return ph;
        }

        /// <summary>The points of an arc of the circle centred at (cx, cy): angles in radians, 0 at the top, growing clockwise (screen y goes down), steps
        /// segments (at least 1), steps + 1 points.</summary>
        public static void Arc(double cx, double cy, double r, double a0, double a1, int steps, List<DPt> output)
        {
            output.Clear();
            steps = Math.Max(1, steps);
            for (int k = 0; k <= steps; k++)
            {
                double t = a0 + (a1 - a0) * k / steps;
                output.Add(new DPt(cx + r * Math.Sin(t), cy - r * Math.Cos(t)));
            }
        }

        /// <summary>The polygon of a sector: its centre, then the arc (a closed shape; the last point joins the centre again by the spans).</summary>
        public static void Sector(double cx, double cy, double r, double a0, double a1, int steps, List<DPt> output)
        {
            Arc(cx, cy, r, a0, a1, steps, output);
            output.Insert(0, new DPt(cx, cy));
        }

        /// <summary>The number of arc samples for a circle of this radius in px: about one per 2 px of circumference, 8 to 256.</summary>
        public static int StepsFor(double radius, double sweep) =>
            Math.Clamp((int)Math.Ceiling(radius * Math.Abs(sweep) / 2), 8, 256);

        /// <summary>The scanline spans of a filled polygon (its outline in pts, closed by the last point to the first): for each pixel row, the runs inside by the
        /// even-odd rule, sampled at the middle of the row. Rows outside the polygon give nothing. Covers a sector, the area under a curve, a bar.</summary>
        public static void Spans(IReadOnlyList<DPt> pts, List<DSpan> output)
        {
            output.Clear();
            int n = pts.Count;
            if (n < 3) { return; }
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++) { minY = Math.Min(minY, pts[i].Y); maxY = Math.Max(maxY, pts[i].Y); }
            var xs = new List<double>();
            for (int row = (int)Math.Floor(minY); row < (int)Math.Ceiling(maxY); row++)
            {
                double y = row + 0.5;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    DPt p = pts[i], q = pts[(i + 1) % n];
                    if ((p.Y <= y && q.Y > y) || (q.Y <= y && p.Y > y))
                    {
                        xs.Add(p.X + (y - p.Y) / (q.Y - p.Y) * (q.X - p.X));
                    }
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    if (xs[k + 1] - xs[k] > 1e-9) { output.Add(new DSpan(row, xs[k], xs[k + 1])); }
                }
            }
        }

        /// <summary>The outline of the area under a curve: the curve's points, then down to the base line at its last x and back along the base to the first.</summary>
        public static void AreaOutline(IReadOnlyList<DPt> curve, double baseY, List<DPt> output)
        {
            output.Clear();
            if (curve.Count == 0) { return; }
            output.AddRange(curve);
            output.Add(new DPt(curve[curve.Count - 1].X, baseY));
            output.Add(new DPt(curve[0].X, baseY));
        }

        /// <summary>Samples f over x0..x1 in steps + 1 points (a smooth curve: f is a PointCurve's Eval).</summary>
        public static void Sample(Func<double, double> f, double x0, double x1, int steps, List<DPt> output)
        {
            output.Clear();
            steps = Math.Max(1, steps);
            for (int k = 0; k <= steps; k++)
            {
                double x = x0 + (x1 - x0) * k / steps;
                output.Add(new DPt(x, f(x)));
            }
        }

        public static double Dist(DPt a, DPt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>The hash of a sequence of numbers, to tell a changed data set from the one drawn before (FNV-1a over the bits). Start from
        /// <see cref="HashSeed"/>; the values are mixed in order.</summary>
        public const ulong HashSeed = 1469598103934665603UL;

        public static ulong Mix(ulong h, double v)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
            for (int i = 0; i < 8; i++)
            {
                h ^= (bits >> (8 * i)) & 0xFF;
                h *= 1099511628211UL;
            }
            return h;
        }

        public static ulong Mix(ulong h, int v) => Mix(h, (double)v);

        public static ulong Mix(ulong h, string s)
        {
            if (s == null) { return h ^ 0x9E37UL; }
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>The hash of a path's points (for <see cref="GeometryCache"/>).</summary>
        public static ulong Hash(IReadOnlyList<DPt> pts)
        {
            ulong h = HashSeed;
            for (int i = 0; i < pts.Count; i++) { h = Mix(Mix(h, pts[i].X), pts[i].Y); }
            return h;
        }
    }

    /// <summary>Remembers the data and the size the geometry of a drawing was made for. <see cref="Need"/> says whether the geometry must be made again; it
    /// records the new key when it says yes. A steady frame (the same data, the same size) asks and gets false: nothing is rebuilt and nothing is
    /// allocated. Counts the rebuilds and the hits so a test (and the perf tool) can see them.</summary>
    internal sealed class GeometryCache
    {
        private ulong key;
        private int width = -1, height = -1;
        private bool valid;

        /// <summary>How many times the geometry was asked for again and made (Need said true).</summary>
        public int Builds { get; private set; }
        /// <summary>How many times the geometry was still valid (Need said false).</summary>
        public int Hits { get; private set; }

        /// <summary>True when the geometry of this data and size must be made (and records it); false when the one made last is still right.</summary>
        public bool Need(ulong dataKey, int w, int h)
        {
            if (valid && dataKey == key && w == width && h == height)
            {
                Hits++;
                return false;
            }
            key = dataKey; width = w; height = h; valid = true;
            Builds++;
            return true;
        }

        /// <summary>Forgets the geometry: the next <see cref="Need"/> says true (the owner dropped its lists, or the style changed).</summary>
        public void Invalidate() => valid = false;
    }
}
