using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>The geometry of a &lt;gauge&gt; and of the eased motion of its needle, free of the game (docs/ui/DESIGN_CHARTS.md 2.4). Angles are in degrees
    /// clockwise from the top (12 o'clock), as on a dial: a gauge runs from start (-135, lower left) through sweep (270) to the end.</summary>
    internal static class GaugeMath
    {
        public const double DefaultStart = -135, DefaultSweep = 270;

        /// <summary>The value as a share of the range: 0 at min, 1 at max, clamped; a flat range (max = min) gives 0.</summary>
        public static double Fraction(double v, double min, double max)
        {
            if (!(max - min > 1e-12)) { return 0; }
            return Math.Clamp((v - min) / (max - min), 0, 1);
        }

        /// <summary>The angle of the needle for the value: start + sweep times the fraction.</summary>
        public static double Angle(double v, double min, double max, double start, double sweep) => start + sweep * Fraction(v, min, max);

        /// <summary>A point on the circle of radius r about (cx, cy) at the angle in degrees (clockwise from the top, screen y goes down).</summary>
        public static DPt Point(double cx, double cy, double r, double degrees)
        {
            double t = degrees * Math.PI / 180;
            return new DPt(cx + r * Math.Sin(t), cy - r * Math.Cos(t));
        }

        /// <summary>The thresholds of the colour zones: an ascending list of values, as "500" or "300, 500" (any separators: spaces, commas, semicolons). A
        /// value below the first threshold is zone 0, from the first on zone 1, and so on. Empty text: no thresholds. False when a piece is not a number or
        /// they do not ascend.</summary>
        public static bool TryParseThresholds(string text, List<double> output)
        {
            output.Clear();
            if (string.IsNullOrWhiteSpace(text)) { return true; }
            foreach (var piece in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                {
                    output.Clear();
                    return false;
                }
                if (output.Count > 0 && !(v > output[output.Count - 1])) { output.Clear(); return false; }
                output.Add(v);
            }
            return true;
        }

        /// <summary>The zone of the value: the number of thresholds at or below it (0 below the first). A value at a threshold is in the zone above it.</summary>
        public static int ZoneOf(double v, IReadOnlyList<double> thresholds)
        {
            int z = 0;
            for (int i = 0; i < thresholds.Count; i++) { if (v >= thresholds[i]) { z = i + 1; } }
            return z;
        }

        /// <summary>Moves current toward target: the gap halves every halfLife seconds (frame-rate independent: dt is the time of this frame in seconds).
        /// halfLife 0 or less jumps straight to target. Never overshoots.</summary>
        public static double Approach(double current, double target, double dt, double halfLife)
        {
            if (!(halfLife > 0) || dt <= 0) { return halfLife > 0 ? current : target; }
            double k = Math.Pow(0.5, dt / halfLife);
            return target + (current - target) * k;
        }
    }
}
