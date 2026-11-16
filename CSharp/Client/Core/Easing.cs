using System;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>transition-timing-function and animation-timing-function: linear, ease, ease-in, ease-out, ease-in-out,
    /// cubic-bezier(x1, y1, x2, y2) and steps(n[, start|end]). At(t) maps the time progress 0..1 to the value progress.</summary>
    internal sealed class Ease
    {
        public static readonly Ease Linear = new(0, 0, 1, 1) { linear = true };
        public static readonly Ease Default = new(0.25f, 0.1f, 0.25f, 1f);   // CSS "ease"

        private bool linear;
        private readonly float ax, bx, cx, ay, by, cy;
        private int steps;
        private bool stepStart;

        private Ease(float x1, float y1, float x2, float y2)
        {
            cx = 3 * x1; bx = 3 * (x2 - x1) - cx; ax = 1 - cx - bx;
            cy = 3 * y1; by = 3 * (y2 - y1) - cy; ay = 1 - cy - by;
        }

        public static bool TryParse(string text, out Ease ease)
        {
            ease = null;
            string s = text?.Trim().ToLowerInvariant() ?? "";
            switch (s)
            {
                case "linear": ease = Linear; return true;
                case "ease": ease = Default; return true;
                case "ease-in": ease = new Ease(0.42f, 0, 1, 1); return true;
                case "ease-out": ease = new Ease(0, 0, 0.58f, 1); return true;
                case "ease-in-out": ease = new Ease(0.42f, 0, 0.58f, 1); return true;
                case "step-start": ease = new Ease(0, 0, 1, 1) { steps = 1, stepStart = true }; return true;
                case "step-end": ease = new Ease(0, 0, 1, 1) { steps = 1 }; return true;
            }
            if (s.StartsWith("cubic-bezier(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal))
            {
                var p = s.Substring(13, s.Length - 14).Split(',');
                if (p.Length == 4 && Num(p[0], out float x1) && Num(p[1], out float y1) && Num(p[2], out float x2) && Num(p[3], out float y2)
                    && x1 >= 0 && x1 <= 1 && x2 >= 0 && x2 <= 1)
                {
                    ease = new Ease(x1, y1, x2, y2);
                    return true;
                }
                return false;
            }
            if (s.StartsWith("steps(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal))
            {
                var p = s.Substring(6, s.Length - 7).Split(',');
                if (p.Length is 1 or 2 && int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
                {
                    string jump = p.Length == 2 ? p[1].Trim() : "end";
                    if (jump is not ("start" or "end" or "jump-start" or "jump-end")) { return false; }
                    ease = new Ease(0, 0, 1, 1) { steps = n, stepStart = jump.EndsWith("start", StringComparison.Ordinal) };
                    return true;
                }
            }
            return false;
        }

        private static bool Num(string s, out float f) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        public float At(float t)
        {
            if (t <= 0) { return 0; }
            if (t >= 1) { return 1; }
            if (linear) { return t; }
            if (steps > 0)
            {
                float k = stepStart ? (float)Math.Ceiling(t * steps) : (float)Math.Floor(t * steps);
                return Math.Clamp(k / steps, 0, 1);
            }
            float s = SolveX(t);
            return ((ay * s + by) * s + cy) * s;
        }

        // x(s) = t for the bezier parameter s: Newton's steps, then bisection when the slope is flat
        private float SolveX(float x)
        {
            float s = x;
            for (int i = 0; i < 8; i++)
            {
                float err = ((ax * s + bx) * s + cx) * s - x;
                if (Math.Abs(err) < 1e-5f) { return s; }
                float slope = (3 * ax * s + 2 * bx) * s + cx;
                if (Math.Abs(slope) < 1e-6f) { break; }
                s -= err / slope;
            }
            float lo = 0, hi = 1;
            s = x;
            for (int i = 0; i < 24; i++)
            {
                float v = ((ax * s + bx) * s + cx) * s;
                if (Math.Abs(v - x) < 1e-5f) { break; }
                if (v < x) { lo = s; } else { hi = s; }
                s = (lo + hi) / 2;
            }
            return s;
        }
    }
}
