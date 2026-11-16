using System;
using System.Globalization;
using System.Text;

namespace BBE.Curves
{
    /// <summary>How a curve runs between its points.</summary>
    public enum CurveMode
    {
        /// <summary>Straight lines between points (default).</summary>
        Linear,
        /// <summary>Monotone cubic (Fritsch-Carlson): smooth, and no overshoot between two points.</summary>
        Smooth,
        /// <summary>The value of the left point holds until the next point.</summary>
        Step,
    }

    /// <summary>
    /// A curve through points x:y, the value at x read between them. Pure code, no game or UI types, so gameplay (the copy
    /// in own/BBE_LuaGates/CSharp/Shared, byte-identical, checked by tools/ui_test) and the window engine run the same math
    /// (docs/ui/DESIGN_CHARTS.md, sections 2.3 and 3).
    /// Text form: "7.5:1, 25:1.24, 42.5:1.64": x:y pairs separated by commas, invariant culture, spaces free, x strictly
    /// increasing, at least one point, all numbers finite. The mode is not in the text; it is passed beside it.
    /// Eval clamps outside the ends (left of the first point: its y; right of the last: its y) and does not allocate.
    /// </summary>
    public sealed class PointCurve
    {
        private readonly double[] xs;
        private readonly double[] ys;
        private readonly double[] tangents; // smooth mode: slope at each point, empty otherwise

        public CurveMode Mode { get; }
        public int Count => xs.Length;
        public double XAt(int i) => xs[i];
        public double YAt(int i) => ys[i];

        private PointCurve(double[] xs, double[] ys, CurveMode mode)
        {
            this.xs = xs;
            this.ys = ys;
            Mode = mode;
            tangents = mode == CurveMode.Smooth ? MonotoneTangents(xs, ys) : Array.Empty<double>();
        }

        /// <summary>Checks the points and builds the curve. On failure returns false and a message naming the point.</summary>
        public static bool TryCreate(double[] xs, double[] ys, CurveMode mode, out PointCurve curve, out string error)
        {
            curve = null;
            if (xs == null || ys == null) { error = "points are missing"; return false; }
            if (xs.Length != ys.Length) { error = $"{xs.Length} x values for {ys.Length} y values"; return false; }
            if (xs.Length == 0) { error = "no points: at least one x:y pair is needed"; return false; }
            for (int i = 0; i < xs.Length; i++)
            {
                if (!double.IsFinite(xs[i])) { error = $"point {i + 1}: x is not a finite number"; return false; }
                if (!double.IsFinite(ys[i])) { error = $"point {i + 1}: y is not a finite number"; return false; }
                if (i > 0 && !(xs[i] > xs[i - 1]))
                {
                    error = $"point {i + 1}: x {Format(xs[i])} is not greater than the previous x {Format(xs[i - 1])}";
                    return false;
                }
            }
            if (!Enum.IsDefined(typeof(CurveMode), mode)) { error = $"unknown mode {(int)mode}"; return false; }
            curve = new PointCurve((double[])xs.Clone(), (double[])ys.Clone(), mode);
            error = null;
            return true;
        }

        /// <summary>Parses "x:y, x:y, ..." (see the class comment). On failure returns false and a message naming the point.</summary>
        public static bool TryParse(string text, CurveMode mode, out PointCurve curve, out string error)
        {
            curve = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty curve: at least one x:y pair is needed"; return false; }
            string[] pieces = text.Split(',');
            var xs = new double[pieces.Length];
            var ys = new double[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                int n = i + 1;
                string piece = pieces[i].Trim();
                if (piece.Length == 0) { error = $"point {n} is empty (a stray comma?)"; return false; }
                string[] parts = piece.Split(':');
                if (parts.Length != 2) { error = $"point {n} '{piece}' is not x:y"; return false; }
                string xText = parts[0].Trim(), yText = parts[1].Trim();
                if (!double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out xs[i]))
                { error = $"point {n}: x '{xText}' is not a number"; return false; }
                if (!double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out ys[i]))
                { error = $"point {n}: y '{yText}' is not a number"; return false; }
            }
            return TryCreate(xs, ys, mode, out curve, out error);
        }

        /// <summary>Like TryParse, throws ArgumentException with the message.</summary>
        public static PointCurve Parse(string text, CurveMode mode = CurveMode.Linear)
        {
            if (!TryParse(text, mode, out var curve, out string error)) { throw new ArgumentException(error, nameof(text)); }
            return curve;
        }

        /// <summary>Mode by name: "linear", "smooth" or "step" (any case, spaces around allowed).</summary>
        public static bool TryParseMode(string text, out CurveMode mode)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "linear": mode = CurveMode.Linear; return true;
                case "smooth": mode = CurveMode.Smooth; return true;
                case "step": mode = CurveMode.Step; return true;
                default: mode = CurveMode.Linear; return false;
            }
        }

        /// <summary>The text form of the points, "x:y, x:y" (round trip: Parse(Format()) gives the same points).</summary>
        public string Format()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < xs.Length; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(Format(xs[i])).Append(':').Append(Format(ys[i]));
            }
            return sb.ToString();
        }

        public override string ToString() => Format();

        /// <summary>The value at x. Outside the ends: the first or the last y. NaN gives the last y (as the old MonsterVitality.Curve).</summary>
        public double Eval(double x)
        {
            int n = xs.Length;
            if (x <= xs[0]) { return ys[0]; }
            if (!(x < xs[n - 1])) { return ys[n - 1]; }
            int k = 0;
            while (x >= xs[k + 1]) { k++; } // xs[k] <= x < xs[k + 1] from here on

            switch (Mode)
            {
                case CurveMode.Step:
                    return ys[k];
                case CurveMode.Smooth:
                {
                    double h = xs[k + 1] - xs[k];
                    double t = (x - xs[k]) / h;
                    double t2 = t * t, t3 = t2 * t;
                    double v = (2 * t3 - 3 * t2 + 1) * ys[k] + (t3 - 2 * t2 + t) * h * tangents[k]
                             + (3 * t2 - 2 * t3) * ys[k + 1] + (t3 - t2) * h * tangents[k + 1];
                    // rounding can step a hair past an end of the segment; the clamp keeps the no-overshoot promise exact
                    return Math.Min(Math.Max(v, Math.Min(ys[k], ys[k + 1])), Math.Max(ys[k], ys[k + 1]));
                }
                default:
                {
                    double t = (x - xs[k]) / (xs[k + 1] - xs[k]);
                    return ys[k] + (ys[k + 1] - ys[k]) * t;
                }
            }
        }

        private static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Fritsch-Carlson tangents: slopes at the points, zero at every local extremum, so each segment is monotone.</summary>
        private static double[] MonotoneTangents(double[] xs, double[] ys)
        {
            int n = xs.Length;
            var m = new double[n];
            if (n < 2) { return m; }
            var d = new double[n - 1];
            for (int k = 0; k < n - 1; k++) { d[k] = (ys[k + 1] - ys[k]) / (xs[k + 1] - xs[k]); }
            m[0] = d[0];
            m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) { m[i] = d[i - 1] * d[i] > 0 ? (d[i - 1] + d[i]) / 2 : 0; }
            for (int k = 0; k < n - 1; k++)
            {
                if (d[k] == 0) { m[k] = 0; m[k + 1] = 0; continue; }
                double a = m[k] / d[k], b = m[k + 1] / d[k];
                double s = a * a + b * b;
                if (s > 9)
                {
                    double tau = 3 / Math.Sqrt(s);
                    m[k] = tau * a * d[k];
                    m[k + 1] = tau * b * d[k];
                }
            }
            return m;
        }
    }
}
