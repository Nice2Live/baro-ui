using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>How a &lt;chart&gt; draws its series (the type attribute).</summary>
    internal enum ChartKind { Line, Area, Bar, Step }

    /// <summary>One series of a &lt;chart&gt;: the name it is shown by and the model path its values come from.</summary>
    internal readonly struct SeriesRef
    {
        public readonly string Name, Path;
        public SeriesRef(string name, string path) { Name = name; Path = path; }
    }

    /// <summary>The axes and the series of a &lt;chart&gt; without the game (docs/ui/DESIGN_CHARTS.md 2.2): round axis ticks (1, 2, 5 times a power of ten, the
    /// axis widened to whole steps), the labels by a .NET format, the series spec "income: stats.income; loss: stats.loss", the bar slots, and the value
    /// under the pointer. Pure: the tests run it, the window draws it.</summary>
    internal static class ChartScale
    {
        /// <summary>A round step for the span divided into about target steps: 1, 2, 5 or 10 times a power of ten (never a step of 3 or 4).</summary>
        public static double NiceStep(double span, int target)
        {
            if (!(span > 0) || target < 1) { return 1; }
            double raw = span / target;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            // a hair over a boundary from the division (0.2 / 0.1 is 2.0000000000000004) still counts as on it
            double nice = f <= 1 + 1e-9 ? 1 : f <= 2 + 1e-9 ? 2 : f <= 5 + 1e-9 ? 5 : 10;
            return nice * mag;
        }

        /// <summary>The ticks of an axis from min to max, about target of them: lo and hi are the whole steps around the range (the axis is widened to
        /// them), the ticks are lo, lo + step, ..., hi. A flat range (min = max) gets a span of 1. Returns false (no ticks) when the ticks would be more
        /// than 200 (a bad target).</summary>
        public static bool Ticks(double min, double max, int target, List<double> ticks, out double lo, out double hi, out double step)
        {
            ticks.Clear();
            lo = hi = step = 0;
            if (!double.IsFinite(min) || !double.IsFinite(max)) { return false; }
            if (max < min) { (min, max) = (max, min); }
            if (max - min < 1e-12) { max = min + 1; }
            step = NiceStep(max - min, target);
            double kLo = Math.Floor(min / step + 1e-9), kHi = Math.Ceiling(max / step - 1e-9);
            if (kHi - kLo > 200) { return false; }
            lo = kLo * step;
            hi = kHi * step;
            for (double k = kLo; k <= kHi + 0.5; k++) { ticks.Add(Math.Round(k * step, 12)); }
            return true;
        }

        /// <summary>The label of an axis value by a .NET format ("0", "0.0", "0 %" ...; null: the shortest exact text). Invariant culture, no -0.</summary>
        public static string Label(double v, string format)
        {
            if (v == 0) { v = 0; }
            if (string.IsNullOrEmpty(format)) { return v.ToString("R", CultureInfo.InvariantCulture); }
            try { return v.ToString(format, CultureInfo.InvariantCulture); }
            catch (FormatException) { return v.ToString("R", CultureInfo.InvariantCulture); }
        }

        /// <summary>The position of v on a scale from lo to hi, mapped to p0..p1 (linear; lo = hi gives p0).</summary>
        public static double Map(double v, double lo, double hi, double p0, double p1)
        {
            if (Math.Abs(hi - lo) < 1e-12) { return p0; }
            return p0 + (v - lo) / (hi - lo) * (p1 - p0);
        }

        /// <summary>The bar of category i of count in a plot of plotW px: its slot is plotW / count, the bar fills the given share of the slot (0..1), centred.
        /// Returns the left and the right edge.</summary>
        public static void BarSlot(int i, int count, double plotW, double fill, out double x0, out double x1)
        {
            double slot = count > 0 ? plotW / count : plotW;
            double w = slot * Math.Clamp(fill, 0, 1);
            x0 = i * slot + (slot - w) / 2;
            x1 = x0 + w;
        }

        /// <summary>The category under the pointer at px (a category i sits at its slot's middle, 0 .. count - 1, the nearest one), or -1 with no categories.
        /// The points are at p0 + i * step.</summary>
        public static int NearestIndex(double px, double p0, double step, int count)
        {
            if (count <= 0) { return -1; }
            if (!(step > 0)) { return 0; }
            return (int)Math.Clamp(Math.Round((px - p0) / step), 0, count - 1);
        }

        /// <summary>The value of category k on a numeric x axis from lo to hi: the first category sits at lo, the last at hi (count 1: lo).</summary>
        public static double CategoryValue(int k, int count, double lo, double hi) => count > 1 ? lo + k * (hi - lo) / (count - 1) : lo;

        /// <summary>The step between the category labels of an x axis so that they do not touch: every stride-th category is labelled, the smallest
        /// stride whose labels (labelW px wide, gap px apart) fit the pitch (the distance between neighbouring categories, px). At least 1.</summary>
        public static int LabelStride(int count, double pitch, double labelW, double gap)
        {
            if (count <= 1 || !(pitch > 0)) { return 1; }
            double need = Math.Max(0, labelW) + Math.Max(0, gap);
            if (need <= pitch) { return 1; }
            return (int)Math.Clamp(Math.Ceiling(need / pitch), 1, count);
        }

        /// <summary>Parses the series spec "name: path; name: path" (a path is a model path, as in data-model). Empty pieces are skipped; a piece without a
        /// name or a path, or a name used twice, fails with the message. Returns false and no series.</summary>
        public static bool TryParseSeries(string spec, List<SeriesRef> output, out string error)
        {
            output.Clear();
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) { error = "no series: data-series is empty"; return false; }
            foreach (var piece0 in spec.Split(';'))
            {
                string piece = piece0.Trim();
                if (piece.Length == 0) { continue; }
                int colon = piece.IndexOf(':');
                if (colon < 0) { output.Clear(); error = $"series '{piece}' is not name: path"; return false; }
                string name = piece.Substring(0, colon).Trim(), path = piece.Substring(colon + 1).Trim();
                if (name.Length == 0 || path.Length == 0) { output.Clear(); error = $"series '{piece}' needs a name and a path"; return false; }
                foreach (var s in output)
                {
                    if (s.Name == name) { output.Clear(); error = $"series name '{name}' is used twice"; return false; }
                }
                output.Add(new SeriesRef(name, path));
            }
            if (output.Count == 0) { error = "no series: data-series is empty"; return false; }
            return true;
        }

        /// <summary>The kind by its name ("line", "area", "bar", "step"; any case). False for another name.</summary>
        public static bool TryParseKind(string text, out ChartKind kind)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "line": kind = ChartKind.Line; return true;
                case "area": kind = ChartKind.Area; return true;
                case "bar": kind = ChartKind.Bar; return true;
                case "step": kind = ChartKind.Step; return true;
                default: kind = ChartKind.Line; return false;
            }
        }
    }
}
