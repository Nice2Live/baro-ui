using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>The editing rules of a &lt;curve&gt; (docs/ui/DESIGN_CHARTS.md 2.3), free of the game so the tests run them. The points are x:y pairs with x
    /// strictly increasing. The rules: the first and the last point keep their x (they move up and down only); a point never overtakes its neighbours (it
    /// stays a minimum gap away from them); every y stays in [yMin, yMax]; with monotone the y of a point stays between its neighbours' (the curve only
    /// rises); snapX / snapY round the moves to a grid (0: free); at most maxPoints points, at least minPoints; the middle points can be added and removed,
    /// the ends never. The text form is "x:y, x:y" like <see cref="BBE.Curves.PointCurve"/>'s; the mode is not here.</summary>
    internal sealed class CurveEditor
    {
        private readonly List<DPt> pts = new();
        private readonly double xMin, xMax, yMin, yMax;

        public CurveEditor(double xMin, double xMax, double yMin, double yMax)
        {
            if (!(xMax > xMin)) { throw new ArgumentException("x-max must be greater than x-min"); }
            if (!(yMax > yMin)) { throw new ArgumentException("y-max must be greater than y-min"); }
            this.xMin = xMin; this.xMax = xMax; this.yMin = yMin; this.yMax = yMax;
        }

        public double XMin => xMin;
        public double XMax => xMax;
        public double YMin => yMin;
        public double YMax => yMax;
        /// <summary>The grid of x and y (0: no snap).</summary>
        public double SnapX { get; set; }
        public double SnapY { get; set; }
        public bool Monotone { get; set; }
        public int MaxPoints { get; set; } = 16;
        public int MinPoints { get; set; } = 2;
        /// <summary>The least distance between the x of two neighbours: a thousandth of the range.</summary>
        public double MinGap => (xMax - xMin) / 1000;

        public int Count => pts.Count;
        public DPt this[int i] => pts[i];
        /// <summary>Counts every change of the points (a load, a move, an add, a remove): a chart that derives a curve from the points keys it on this.</summary>
        public int Revision { get; private set; }

        /// <summary>Fills the points from the text "x:y, x:y" (invariant culture, spaces free). The x must increase; values outside the range are
        /// clamped into it. False with no change on a malformed text.</summary>
        public bool Load(string text, out string error)
        {
            error = null;
            var next = new List<DPt>();
            if (string.IsNullOrWhiteSpace(text)) { error = "empty curve"; return false; }
            foreach (var piece0 in text.Split(','))
            {
                string piece = piece0.Trim();
                string[] parts = piece.Split(':');
                if (parts.Length != 2
                    || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.IsFinite(x) || !double.IsFinite(y))
                {
                    error = $"'{piece}' is not x:y";
                    return false;
                }
                if (next.Count > 0 && !(x > next[next.Count - 1].X)) { error = $"x {x} does not increase"; return false; }
                next.Add(new DPt(Math.Clamp(x, xMin, xMax), Math.Clamp(y, yMin, yMax)));
            }
            if (next.Count < MinPoints) { error = $"at least {MinPoints} points are needed"; return false; }
            pts.Clear();
            pts.AddRange(next);
            if (Monotone) { for (int i = 1; i < pts.Count; i++) { pts[i] = new DPt(pts[i].X, Math.Max(pts[i].Y, pts[i - 1].Y)); } }
            Revision++;
            return true;
        }

        /// <summary>Fills the points from the first n entries of xs and ys (the same rules as Load: clamped into the range, x strictly increasing, at
        /// least MinPoints). No allocation once the list has its capacity. False with no change on bad points.</summary>
        public bool LoadPoints(double[] xs, double[] ys, int n, out string error)
        {
            error = null;
            if (n < MinPoints) { error = $"at least {MinPoints} points are needed"; return false; }
            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(xs[i]) || !double.IsFinite(ys[i])) { error = $"point {i} is not finite"; return false; }
                if (i > 0 && !(xs[i] > xs[i - 1])) { error = $"x {xs[i]} does not increase"; return false; }
            }
            pts.Clear();
            for (int i = 0; i < n; i++)
            {
                double y = Math.Clamp(ys[i], yMin, yMax);
                if (Monotone && i > 0) { y = Math.Max(y, pts[i - 1].Y); }
                pts.Add(new DPt(Math.Clamp(xs[i], xMin, xMax), y));
            }
            Revision++;
            return true;
        }

        /// <summary>The text form of the points, "x:y, x:y" ("R" numbers, invariant: Load reads it back).</summary>
        public string Format()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(pts[i].X.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(pts[i].Y.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Where the point i can move in x: from its left neighbour (plus the gap) to its right one (minus the gap), within the range. An end
        /// point has no range in x (it is fixed): lo = hi = its x.</summary>
        public void XBounds(int i, out double lo, out double hi)
        {
            if (i <= 0 || i >= pts.Count - 1) { lo = hi = pts[i].X; return; }
            double gap = MinGap;
            lo = Math.Max(xMin, pts[i - 1].X + gap);
            hi = Math.Min(xMax, pts[i + 1].X - gap);
        }

        /// <summary>Where the point i can move in y: the range, and with monotone the values of its neighbours.</summary>
        public void YBounds(int i, out double lo, out double hi)
        {
            lo = yMin; hi = yMax;
            if (!Monotone) { return; }
            if (i > 0) { lo = Math.Max(lo, pts[i - 1].Y); }
            if (i < pts.Count - 1) { hi = Math.Min(hi, pts[i + 1].Y); }
        }

        /// <summary>Moves the point i to (x, y) under the rules: the x is taken to the grid and kept between the neighbours, the y to the grid and between
        /// its bounds (a grid that has no point inside the bounds leaves the value unsnapped). Returns the point's new position.</summary>
        public DPt Move(int i, double x, double y)
        {
            XBounds(i, out double xLo, out double xHi);
            x = Snap(x, SnapX, xLo, xHi);
            YBounds(i, out double yLo, out double yHi);
            y = Snap(y, SnapY, yLo, yHi);
            var p = new DPt(x, y);
            pts[i] = p;
            Revision++;
            return p;
        }

        /// <summary>Moves the point i by whole snap steps (the step of the grid, or a hundredth of the range without one). Returns false when it did not move.</summary>
        public bool Nudge(int i, int dxSteps, int dySteps)
        {
            double sx = SnapX > 0 ? SnapX : (xMax - xMin) / 100;
            double sy = SnapY > 0 ? SnapY : (yMax - yMin) / 100;
            var before = pts[i];
            Move(i, before.X + dxSteps * sx, before.Y + dySteps * sy);
            var after = pts[i];
            return after.X != before.X || after.Y != before.Y;
        }

        /// <summary>Adds a point at (x, y) (the double click). Refused (returns -1) when there are MaxPoints already, the x is not strictly inside the ends
        /// or too near a point. Returns the index of the new point; the y is kept between the new neighbours under the monotone rule.</summary>
        public int Add(double x, double y)
        {
            if (pts.Count >= MaxPoints) { return -1; }
            if (!(x > xMin && x < xMax)) { return -1; }
            int at = 0;
            while (at < pts.Count && pts[at].X < x) { at++; }
            if (at == 0 || at == pts.Count) { return -1; }
            if (Math.Abs(pts[at].X - x) < MinGap || Math.Abs(pts[at - 1].X - x) < MinGap) { return -1; }
            pts.Insert(at, new DPt(x, yMin));
            Move(at, x, y);
            Revision++;
            return at;
        }

        /// <summary>Removes the point i: a middle point, when more than MinPoints remain. False for an end point or the minimum.</summary>
        public bool Remove(int i)
        {
            if (i <= 0 || i >= pts.Count - 1 || pts.Count <= MinPoints) { return false; }
            pts.RemoveAt(i);
            Revision++;
            return true;
        }

        /// <summary>The pixel of point i in a plot of plotW by plotH px (the origin is the top left; y grows down).</summary>
        public DPt ToPixel(int i, double plotW, double plotH) => ToPixel(pts[i], plotW, plotH);

        public DPt ToPixel(DPt p, double plotW, double plotH) =>
            new DPt((p.X - xMin) / (xMax - xMin) * plotW, plotH - (p.Y - yMin) / (yMax - yMin) * plotH);

        /// <summary>The value at a pixel of the plot (not clamped: Move does that, and the grid).</summary>
        public DPt FromPixel(double px, double py, double plotW, double plotH) =>
            new DPt(xMin + px / plotW * (xMax - xMin), yMin + (plotH - py) / plotH * (yMax - yMin));

        /// <summary>The point nearest the pixel (px, py) within radius px, or -1: the handle a pointer picks. Ties go to the lower index.</summary>
        public int HitTest(double px, double py, double plotW, double plotH, double radius)
        {
            int best = -1;
            double bestD = radius * radius;
            for (int i = 0; i < pts.Count; i++)
            {
                var q = ToPixel(i, plotW, plotH);
                double dx = q.X - px, dy = q.Y - py, d = dx * dx + dy * dy;
                if (d <= bestD && (best < 0 || d < bestD)) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>The value of the curve at x by the linear rule: the editor's own check of its points (the game reads the same points through
        /// BBE.Curves.PointCurve).</summary>
        public double LinearAt(double x)
        {
            if (pts.Count == 0) { return 0; }
            if (x <= pts[0].X) { return pts[0].Y; }
            for (int i = 1; i < pts.Count; i++)
            {
                if (x <= pts[i].X)
                {
                    double t = (x - pts[i - 1].X) / (pts[i].X - pts[i - 1].X);
                    return pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t;
                }
            }
            return pts[pts.Count - 1].Y;
        }

        /// <summary>The grid rule: rounds v to the step and keeps it between lo and hi; when no grid value lies between them, v is only kept between them.</summary>
        internal static double Snap(double v, double step, double lo, double hi)
        {
            // neighbours loaded closer than the minimum gap can leave lo above hi: the point then stays where its lower bound is
            if (!(lo <= hi)) { return lo; }
            if (step > 0)
            {
                double first = Math.Ceiling(lo / step - 1e-9) * step, last = Math.Floor(hi / step + 1e-9) * step;
                if (first <= last + 1e-12)
                {
                    double r = Math.Round(v / step) * step;
                    return Math.Clamp(r, first, last);
                }
            }
            return Math.Clamp(v, lo, hi);
        }
    }
}
