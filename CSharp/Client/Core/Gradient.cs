using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>Direction of an axis-aligned linear gradient: where the last stop is.</summary>
    internal enum GradientAxis { ToBottom, ToTop, ToRight, ToLeft }

    /// <summary>One colour stop as written: the position is optional and may be a percentage or a length.</summary>
    internal readonly struct GradientStop
    {
        public readonly Rgba Color;
        public readonly Length Pos;
        public readonly bool HasPos;
        public GradientStop(Rgba color, Length pos, bool hasPos) { Color = color; Pos = pos; HasPos = hasPos; }
    }

    /// <summary>A baked ramp: straight-alpha RGBA, 1 pixel wide and N tall (vertical) or N wide and 1 tall (horizontal), always in screen
    /// order (top to bottom, left to right) so the painter never flips it.</summary>
    internal readonly struct GradientRamp
    {
        public readonly byte[] Rgba;
        public readonly int Width, Height, N;
        public GradientRamp(byte[] rgba, int w, int h, int n) { Rgba = rgba; Width = w; Height = h; N = n; }
    }

    /// <summary>Cache key of a ramp: stops and direction (the hash), the bucketed size, and the length in texture px only when a stop is
    /// positioned in px (percent stops make the ramp independent of the box size, so an animated width does not rebake it).</summary>
    internal readonly struct GradientKey : IEquatable<GradientKey>
    {
        public readonly ulong Hash;
        public readonly int N, LengthTex;
        public GradientKey(ulong hash, int n, int lengthTex) { Hash = hash; N = n; LengthTex = lengthTex; }
        public bool Equals(GradientKey o) => Hash == o.Hash && N == o.N && LengthTex == o.LengthTex;
        public override bool Equals(object o) => o is GradientKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Hash, N, LengthTex);
    }

    /// <summary>A parsed axis-aligned `linear-gradient`. Parsed once per style; the ramp is built per (key).
    /// Colour stops are interpolated like a browser does for sRGB gradients: in premultiplied RGBA, so `red` to `transparent` fades the alpha
    /// and keeps the colour red instead of passing through grey.</summary>
    internal sealed class Gradient
    {
        public const int MaxRamp = 512, RampStep = 16;

        public readonly GradientAxis Axis;
        public readonly GradientStop[] Stops;
        public readonly ulong Hash;
        /// <summary>True when any stop uses px, u or em: the ramp then depends on the box length.</summary>
        public readonly bool UsesLengthStops;
        /// <summary>True when a stop is in u or in em: the ramp then also depends on the window unit or the element's font size.</summary>
        public readonly bool UsesU, UsesEm;
        /// <summary>The shape and the geometry as written (GradientArea.cs). A layer with <see cref="NeedsArea"/> is drawn per pixel, not as a ramp.</summary>
        public readonly GradientGeometry Geometry;

        public bool Vertical => Axis == GradientAxis.ToBottom || Axis == GradientAxis.ToTop;
        private bool Reversed => Axis == GradientAxis.ToTop || Axis == GradientAxis.ToLeft;
        public GradientShape Shape => Geometry.Shape;
        /// <summary>A radial layer, or a linear line that is not on an axis (an angle other than 0/90/180/270deg, or a corner): an area (GradientArea.Bake).</summary>
        public bool NeedsArea => Geometry.Shape == GradientShape.Radial || Geometry.Oblique;

        private Gradient(GradientAxis axis, GradientStop[] stops, GradientGeometry geo)
        {
            Axis = axis; Stops = stops; Geometry = geo;
            ulong h = 14695981039346656037UL;
            h = Mix(h, (ulong)axis);
            h = geo.Mix(h);
            bool lens = false, usesU = false, usesEm = false;
            foreach (var s in stops)
            {
                h = Mix(h, ((ulong)s.Color.R << 24) | ((ulong)s.Color.G << 16) | ((ulong)s.Color.B << 8) | s.Color.A);
                h = Mix(h, s.HasPos ? 1UL : 0UL);
                if (s.HasPos)
                {
                    h = Mix(h, (ulong)(uint)BitConverter.SingleToInt32Bits(s.Pos.Value));
                    h = Mix(h, (ulong)s.Pos.Unit);
                    if (s.Pos.Unit != Unit.Percent) { lens = true; }
                    if (s.Pos.Unit == Unit.U) { usesU = true; }
                    if (s.Pos.Unit == Unit.Em) { usesEm = true; }
                }
            }
            Hash = h; UsesLengthStops = lens; UsesU = usesU; UsesEm = usesEm;
        }

        private static ulong Mix(ulong h, ulong v) { h ^= v; return h * 1099511628211UL; }

        private static bool TryAngle(string s, out float deg)
        {
            deg = 0;
            string[] units = { "deg", "turn", "grad", "rad" };
            foreach (string u in units)
            {
                if (!s.EndsWith(u, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!float.TryParse(s.AsSpan(0, s.Length - u.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) { return false; }
                deg = u switch { "turn" => v * 360f, "grad" => v * 0.9f, "rad" => v * 180f / MathF.PI, _ => v };
                return true;
            }
            return false;
        }

        /// <summary>The first gradient layer of a value (see TryParseLayers). A value with more layers keeps the first one, with a note.</summary>
        public static bool TryParse(string css, out Gradient gradient, out string note)
        {
            gradient = null;
            if (!TryParseLayers(css, out var layers, out note)) { return false; }
            gradient = layers[0];
            if (layers.Count > 1) { note = "gradient: only the first of " + layers.Count + " layers is drawn"; }
            return true;
        }

        /// <summary>The gradient layers of a background-image value, in CSS order: the first is on top (the painter draws the last one first). url() and
        /// sprite() layers are not gradients and are left out (the painter draws them itself). False, with the reason, when a gradient layer cannot be drawn,
        /// and when there is no gradient layer at all. Linear: an axis, an angle or a corner (`to top right`), then colour stops; radial: `circle` or
        /// `ellipse`, a size (keyword, or px/u/em lengths), `at` a position, then colour stops. Repeating gradients are refused.</summary>
        public static bool TryParseLayers(string css, out List<Gradient> layers, out string note)
        {
            layers = new List<Gradient>(); note = null;
            if (string.IsNullOrWhiteSpace(css)) { note = "gradient: empty value"; return false; }
            foreach (string layer in CssLists.Split(css.Trim(), ','))
            {
                if (layer.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || layer.StartsWith("sprite(", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!TryParseOne(layer, out var g, out note)) { layers.Clear(); return false; }
                layers.Add(g);
            }
            if (layers.Count == 0) { note = "gradient: expected linear-gradient(...) or radial-gradient(...)"; return false; }
            return true;
        }

        private static bool TryParseOne(string layer, out Gradient g, out string note)
        {
            g = null; note = null;
            int open = layer.IndexOf('(');
            if (open < 0 || !layer.EndsWith(")", StringComparison.Ordinal)) { note = "gradient: expected linear-gradient(...) or radial-gradient(...)"; return false; }
            string name = layer.Substring(0, open).Trim().ToLowerInvariant();
            var args = CssLists.Split(layer.Substring(open + 1, layer.Length - open - 2), ',');
            if (name == "linear-gradient") { return TryLinear(args, out g, out note); }
            if (name == "radial-gradient") { return TryRadial(args, out g, out note); }
            note = name.EndsWith("gradient", StringComparison.Ordinal) ? name + " is not supported (only linear-gradient and radial-gradient)" : "not a gradient: " + name;
            return false;
        }

        private static bool TryLinear(List<string> args, out Gradient g, out string note)
        {
            g = null; note = null;
            var axis = GradientAxis.ToBottom;
            var geo = GradientGeometry.Axis;
            int from = 0;
            if (args.Count > 0 && IsLinearDirection(args[0]))
            {
                string a0 = args[0].Trim();
                from = 1;
                if (a0.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
                {
                    string side = a0.Substring(3).Trim().ToLowerInvariant();
                    string[] words = side.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int cx = 0, cy = 0;
                    bool bad = words.Length == 0 || words.Length > 2;
                    foreach (string w in words)
                    {
                        if (w is "left" or "right") { if (cx != 0) { bad = true; } cx = w == "left" ? -1 : 1; }
                        else if (w is "top" or "bottom") { if (cy != 0) { bad = true; } cy = w == "top" ? -1 : 1; }
                        else { bad = true; }
                    }
                    if (bad) { note = "linear-gradient: 'to " + side + "' is not supported (only to top, right, bottom, left and their corners)"; return false; }
                    if (cx != 0 && cy != 0) { geo = new GradientGeometry(GradientShape.Linear, true, 0f, cx, cy, false, RadialSize.FarthestCorner, Length.Zero, Length.Zero, Length.Zero, Length.Zero); }
                    else if (cy != 0) { axis = cy < 0 ? GradientAxis.ToTop : GradientAxis.ToBottom; }
                    else { axis = cx < 0 ? GradientAxis.ToLeft : GradientAxis.ToRight; }
                }
                else
                {
                    TryAngle(a0.ToLowerInvariant(), out float deg);
                    float n = ((deg % 360f) + 360f) % 360f;
                    if (n < 0.001f || n > 359.999f) { axis = GradientAxis.ToTop; }
                    else if (MathF.Abs(n - 90f) < 0.001f) { axis = GradientAxis.ToRight; }
                    else if (MathF.Abs(n - 180f) < 0.001f) { axis = GradientAxis.ToBottom; }
                    else if (MathF.Abs(n - 270f) < 0.001f) { axis = GradientAxis.ToLeft; }
                    else { geo = new GradientGeometry(GradientShape.Linear, true, n, 0, 0, false, RadialSize.FarthestCorner, Length.Zero, Length.Zero, Length.Zero, Length.Zero); }
                }
            }
            var stops = new List<GradientStop>();
            if (!TryStops(args, from, stops, "linear-gradient", out note)) { return false; }
            if (stops.Count < 2) { note = "linear-gradient: needs at least two colour stops"; return false; }
            g = new Gradient(axis, stops.ToArray(), geo);
            return true;
        }

        private static bool IsLinearDirection(string arg)
        {
            string t = arg.Trim();
            if (t.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) { return true; }
            return TryAngle(t.ToLowerInvariant(), out _);
        }

        private static bool TryRadial(List<string> args, out Gradient g, out string note)
        {
            g = null; note = null;
            bool ellipse = true, shapeSeen = false, sizeSeen = false;
            var size = RadialSize.FarthestCorner;
            Length sx = Length.Zero, sy = Length.Zero;
            Length px = new(50, Unit.Percent), py = new(50, Unit.Percent);
            int from = 0;
            if (args.Count > 0 && IsRadialPrelude(args[0]))
            {
                var toks = CssLists.Split(args[0].Trim(), ' ');
                var lens = new List<Length>();
                int i = 0;
                for (; i < toks.Count && !toks[i].Equals("at", StringComparison.OrdinalIgnoreCase); i++)
                {
                    string t = toks[i].ToLowerInvariant();
                    switch (t)
                    {
                        case "circle":
                        case "ellipse":
                            if (shapeSeen) { note = "radial-gradient: two shapes"; return false; }
                            shapeSeen = true; ellipse = t == "ellipse";
                            break;
                        case "closest-side":
                        case "farthest-side":
                        case "closest-corner":
                        case "farthest-corner":
                            if (sizeSeen) { note = "radial-gradient: two sizes"; return false; }
                            sizeSeen = true;
                            size = t switch { "closest-side" => RadialSize.ClosestSide, "farthest-side" => RadialSize.FarthestSide, "closest-corner" => RadialSize.ClosestCorner, _ => RadialSize.FarthestCorner };
                            break;
                        default:
                            if (!Length.TryParse(toks[i], out var l) || !(l.Unit is Unit.Px or Unit.U or Unit.Em))
                            {
                                note = "radial-gradient: cannot read '" + toks[i] + "' (a size is a keyword or a length in px, u or em)";
                                return false;
                            }
                            lens.Add(l);
                            break;
                    }
                }
                if (lens.Count > 0)
                {
                    if (sizeSeen || lens.Count > 2) { note = "radial-gradient: a size keyword and lengths cannot be mixed, and there are at most two lengths"; return false; }
                    if (shapeSeen && !ellipse && lens.Count > 1) { note = "radial-gradient: a circle takes one length"; return false; }
                    if (!shapeSeen) { ellipse = lens.Count == 2; }
                    size = RadialSize.Explicit; sizeSeen = true;
                    sx = lens[0]; sy = lens.Count == 2 ? lens[1] : lens[0];
                }
                if (i < toks.Count)
                {
                    var pos = new List<string>();
                    for (i++; i < toks.Count; i++) { pos.Add(toks[i]); }
                    if (pos.Count == 0 || pos.Count > 2 || !TryPosition(pos, out px, out py)) { note = "radial-gradient: cannot read the position after 'at'"; return false; }
                }
                from = 1;
            }
            var stops = new List<GradientStop>();
            if (!TryStops(args, from, stops, "radial-gradient", out note)) { return false; }
            if (stops.Count < 2) { note = "radial-gradient: needs at least two colour stops"; return false; }
            g = new Gradient(GradientAxis.ToBottom, stops.ToArray(), GradientGeometry.Radial(ellipse, size, sx, sy, px, py));
            return true;
        }

        private static bool IsRadialPrelude(string arg)
        {
            var toks = CssLists.Split(arg.Trim(), ' ');
            if (toks.Count == 0) { return false; }
            string f = toks[0].ToLowerInvariant();
            if (f is "circle" or "ellipse" or "at" or "closest-side" or "farthest-side" or "closest-corner" or "farthest-corner") { return true; }
            return Length.TryParse(toks[0], out _) && !Rgba.TryParse(toks[0], out _);
        }

        /// <summary>One coordinate of `at`: a keyword (left, center, right, top, bottom) as a percentage, or a length in px, u or em. kind: 1 for a horizontal
        /// keyword, 2 for a vertical one, 0 otherwise (for the order of two values).</summary>
        private static bool Coord(string t, out Length l, out int kind)
        {
            kind = 0;
            switch (t.ToLowerInvariant())
            {
                case "left": l = new Length(0, Unit.Percent); kind = 1; return true;
                case "right": l = new Length(100, Unit.Percent); kind = 1; return true;
                case "top": l = new Length(0, Unit.Percent); kind = 2; return true;
                case "bottom": l = new Length(100, Unit.Percent); kind = 2; return true;
                case "center": l = new Length(50, Unit.Percent); return true;
            }
            return Length.TryParse(t, out l) && (l.Unit is Unit.Percent or Unit.Px or Unit.U or Unit.Em);
        }

        private static bool TryPosition(List<string> toks, out Length x, out Length y)
        {
            x = y = new Length(50, Unit.Percent);
            if (toks.Count == 1)
            {
                if (!Coord(toks[0], out var a, out int ka)) { return false; }
                if (ka == 2) { y = a; } else { x = a; }
                return true;
            }
            if (!Coord(toks[0], out var p, out int kp) || !Coord(toks[1], out var q, out int kq)) { return false; }
            if (kp == 2 || kq == 1) { (p, q) = (q, p); (kp, kq) = (kq, kp); }
            if (kp == 2 || kq == 1) { return false; }
            x = p; y = q;
            return true;
        }

        /// <summary>The colour stops from argument `from` on, each `colour`, `colour position` or `colour position position` (the positions in %, px, u or em).</summary>
        private static bool TryStops(List<string> args, int from, List<GradientStop> stops, string fn, out string note)
        {
            note = null;
            for (int i = from; i < args.Count; i++)
            {
                var toks = CssLists.Split(args[i], ' ');
                if (toks.Count == 0) { note = fn + ": empty colour stop"; return false; }
                if (toks[0].Equals("currentcolor", StringComparison.OrdinalIgnoreCase) || !Rgba.TryParse(toks[0], out var color))
                {
                    note = Length.TryParse(toks[0], out _) && toks.Count == 1
                        ? fn + ": colour hints (a lone position) are not supported"
                        : fn + ": cannot read colour '" + toks[0] + "'";
                    return false;
                }
                if (toks.Count > 3) { note = fn + ": too many values in a colour stop"; return false; }
                if (toks.Count == 1) { stops.Add(new GradientStop(color, Length.Zero, false)); continue; }
                for (int k = 1; k < toks.Count; k++)
                {
                    if (!Length.TryParse(toks[k], out var pos) || !(pos.Unit == Unit.Percent || pos.Unit == Unit.Px || pos.Unit == Unit.U || pos.Unit == Unit.Em || (pos.Unit == Unit.None && pos.Value == 0)))
                    {
                        note = fn + ": bad stop position '" + toks[k] + "'";
                        return false;
                    }
                    stops.Add(new GradientStop(color, pos, true));
                }
            }
            return true;
        }

        /// <summary>Stop positions as fractions of the gradient line, by the CSS rules: the first defaults to 0, the last to 1, a position
        /// never goes below an earlier one, and stops without a position are spread evenly between their neighbours.</summary>
        public float[] ResolveStops(float lengthLayout, float unitPx = 1f, float emPx = 16f)
        {
            int n = Stops.Length;
            var p = new float[n];
            var known = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (!Stops[i].HasPos) { continue; }
                var l = Stops[i].Pos;
                float v = l.Unit switch
                {
                    Unit.Percent => l.Value / 100f,
                    Unit.U => lengthLayout > 0 ? l.Value * unitPx / lengthLayout : 0,
                    Unit.Em => lengthLayout > 0 ? l.Value * emPx / lengthLayout : 0,
                    _ => lengthLayout > 0 ? l.Value / lengthLayout : 0,
                };
                p[i] = v; known[i] = true;
            }
            if (!known[0]) { p[0] = 0; known[0] = true; }
            if (!known[n - 1]) { p[n - 1] = 1; known[n - 1] = true; }
            float top = p[0];
            for (int i = 1; i < n; i++)
            {
                if (!known[i]) { continue; }
                if (p[i] < top) { p[i] = top; }
                top = p[i];
            }
            int a = 0;
            while (a < n - 1)
            {
                int b = a + 1;
                while (!known[b]) { b++; }
                for (int i = a + 1; i < b; i++) { p[i] = p[a] + (p[b] - p[a]) * (i - a) / (b - a); }
                a = b;
            }
            return p;
        }

        private void Straight(int i, out float r, out float g, out float b, out float a)
        {
            var c = Stops[i].Color;
            r = c.R / 255f; g = c.G / 255f; b = c.B / 255f; a = c.A / 255f;
        }

        /// <summary>Colour at gradient position t (0..1 along the line), straight alpha. Premultiplied interpolation; where alpha is 0 the
        /// colour is that of the nearest stop that has any, so a fully transparent pixel never carries black into a bilinear blend.</summary>
        public void Eval(float[] p, float t, out float r, out float g, out float b, out float a)
        {
            int n = Stops.Length, k = 0;
            while (k < n && p[k] <= t) { k++; }
            if (k == 0) { Straight(0, out r, out g, out b, out a); }
            else if (k == n) { Straight(n - 1, out r, out g, out b, out a); }
            else
            {
                float u = (t - p[k - 1]) / (p[k] - p[k - 1]);
                Straight(k - 1, out float r0, out float g0, out float b0, out float a0);
                Straight(k, out float r1, out float g1, out float b1, out float a1);
                a = a0 + (a1 - a0) * u;
                float pr = r0 * a0 + (r1 * a1 - r0 * a0) * u, pg = g0 * a0 + (g1 * a1 - g0 * a0) * u, pb = b0 * a0 + (b1 * a1 - b0 * a0) * u;
                if (a > 0) { r = Math.Clamp(pr / a, 0, 1); g = Math.Clamp(pg / a, 0, 1); b = Math.Clamp(pb / a, 0, 1); }
                else { r = g = b = 0; }
            }
            if (a > 0) { return; }
            float best = float.MaxValue;
            int pick = -1;
            for (int i = 0; i < n; i++)
            {
                if (Stops[i].Color.A == 0) { continue; }
                float d = MathF.Abs(p[i] - t);
                if (d < best) { best = d; pick = i; }
            }
            if (pick < 0) { pick = 0; }
            Straight(pick, out r, out g, out b, out float _);
        }

        /// <summary>Colour at a screen position s (0 at the top or left edge, 1 at the bottom or right), whatever the axis direction.</summary>
        public void EvalScreen(float[] p, float s, out float r, out float g, out float b, out float a) => Eval(p, Reversed ? 1f - s : s, out r, out g, out b, out a);

        /// <summary>Ramp size for a gradient line of this many texture pixels: rounded up to a multiple of 16 so an animated size reuses
        /// textures, never above 512 (a longer ramp is stretched by the bilinear sampler).</summary>
        public static int RampSize(float lengthTex)
        {
            int n = (int)MathF.Ceiling(Math.Max(1f, lengthTex) / RampStep) * RampStep;
            return Math.Clamp(n, RampStep, MaxRamp);
        }

        /// <summary>Cache key for a box whose gradient line is this long (layout px) under this scale.</summary>
        public GradientKey Key(float lengthLayout, float scale)
        {
            float lt = lengthLayout * (scale > 0 ? scale : 1f);
            return new GradientKey(Hash, RampSize(lt), UsesLengthStops ? (int)MathF.Round(lt) : 0);
        }

        /// <summary>What else tells two ramps (or corner tiles) of one gradient apart: the unit and font size in px, but only when a stop is written in
        /// u or em (otherwise 0). Two equally long boxes with different font sizes and an em stop need two ramps.</summary>
        public int UnitKey(float unitPx, float emPx)
        {
            if (!UsesU && !UsesEm) { return 0; }
            return HashCode.Combine(UsesU ? BitConverter.SingleToInt32Bits(unitPx) : 0, UsesEm ? BitConverter.SingleToInt32Bits(emPx) : 0) | 1;
        }

        private static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

        /// <summary>Bakes the ramp. Texel i stands for the middle of its slice, t = (i + 0.5) / N; the painter must sample it with a clamped
        /// sampler or a source rectangle inset by half a texel (the game's default sampler wraps).</summary>
        public GradientRamp BuildRamp(float lengthLayout, float scale, float unitPx = 1f, float emPx = 16f)
        {
            int n = RampSize(lengthLayout * (scale > 0 ? scale : 1f));
            var p = ResolveStops(lengthLayout, unitPx, emPx);
            var px = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                EvalScreen(p, (i + 0.5f) / n, out float r, out float g, out float b, out float a);
                px[i * 4] = B(r); px[i * 4 + 1] = B(g); px[i * 4 + 2] = B(b); px[i * 4 + 3] = B(a);
            }
            return Vertical ? new GradientRamp(px, 1, n, n) : new GradientRamp(px, n, 1, n);
        }

        /// <summary>Corner tile (rTex x rTex, straight alpha) for a rounded box: the ramp colour of the rows the corner covers, times the quarter-circle
        /// coverage. The start corner is at the top (vertical) or left (horizontal); the end corner at the bottom or right. Pixels outside the
        /// circle keep the ramp colour and get alpha 0, so bilinear filtering at the rim does not darken. The tile is laid out like the box
        /// (vertical: rows run along the gradient; horizontal: columns do) and its cross-axis outer edge is index 0, mirrored by the painter
        /// for the other side.</summary>
        public byte[] CornerTile(int rTex, float lengthLayout, float scale, bool end, float unitPx = 1f, float emPx = 16f)
        {
            var px = new byte[Math.Max(0, rTex * rTex * 4)];
            if (rTex <= 0) { return px; }
            float lenTex = Math.Max(1f, lengthLayout * (scale > 0 ? scale : 1f));
            var p = ResolveStops(lengthLayout, unitPx, emPx);
            for (int a = 0; a < rTex; a++)
            {
                float s = ((end ? lenTex - rTex : 0f) + a + 0.5f) / lenTex;
                EvalScreen(p, s, out float r, out float g, out float b, out float al);
                float ca = (end ? rTex - 1 - a : a) + 0.5f;
                for (int c = 0; c < rTex; c++)
                {
                    float dx = ca - rTex, dy = c + 0.5f - rTex;
                    float cov = ca >= rTex || c + 0.5f >= rTex ? 1f : Math.Clamp(rTex - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
                    int o = (Vertical ? a * rTex + c : c * rTex + a) * 4;
                    px[o] = B(r); px[o + 1] = B(g); px[o + 2] = B(b); px[o + 3] = B(al * cov);
                }
            }
            return px;
        }

        /// <summary>The corner tile for an elliptical corner: rAlong texels along the gradient line and rCross across it (so a vertical gradient's tile is
        /// rCross wide and rAlong tall, a horizontal one's rAlong wide and rCross tall). With rAlong = rCross it is the circle of <see cref="CornerTile"/>
        /// (which the painter still bakes for equal circular corners). Same orientation rules: the cross-axis outer edge is index 0.</summary>
        public byte[] CornerTileE(int rAlong, int rCross, float lengthLayout, float scale, bool end, float unitPx = 1f, float emPx = 16f)
        {
            int tw = Vertical ? rCross : rAlong, th = Vertical ? rAlong : rCross;
            var px = new byte[Math.Max(0, tw * th * 4)];
            if (rAlong <= 0 || rCross <= 0) { return px; }
            float lenTex = Math.Max(1f, lengthLayout * (scale > 0 ? scale : 1f));
            var p = ResolveStops(lengthLayout, unitPx, emPx);
            for (int a = 0; a < rAlong; a++)
            {
                float s = ((end ? lenTex - rAlong : 0f) + a + 0.5f) / lenTex;
                EvalScreen(p, s, out float r, out float g, out float b, out float al);
                float da = (end ? rAlong - 1 - a : a) + 0.5f - rAlong;
                for (int c = 0; c < rCross; c++)
                {
                    float dc = c + 0.5f - rCross;
                    float nx = da / rAlong, ny = dc / rCross, f = MathF.Sqrt(nx * nx + ny * ny);
                    float cov = 1f;
                    if (f > 0)
                    {
                        // distance to the ellipse along the gradient of f, in texels; 0.5 px of soft edge as for the circle
                        float grad = MathF.Sqrt(nx * nx / ((float)rAlong * rAlong) + ny * ny / ((float)rCross * rCross)) / f;
                        cov = Math.Clamp(0.5f - (f - 1f) / grad, 0f, 1f);
                    }
                    int o = (Vertical ? a * tw + c : c * tw + a) * 4;
                    px[o] = B(r); px[o + 1] = B(g); px[o + 2] = B(b); px[o + 3] = B(al * cov);
                }
            }
            return px;
        }
    }

    /// <summary>Which baked piece a quad of a rounded gradient draws.</summary>
    internal enum GradientPiece { Ramp, CornerStart, CornerEnd }

    /// <summary>One sprite of a rounded gradient: destination in box-local layout px, the part of the ramp it shows (T0..T1 as fractions of the line)
    /// and whether the corner tile is mirrored across the cross axis (flip horizontally for a vertical gradient, vertically for a horizontal one).</summary>
    internal readonly struct GradientQuad
    {
        public readonly GradientPiece Piece;
        public readonly float X, Y, W, H, T0, T1;
        public readonly bool FlipCross;
        /// <summary>Which corner a corner piece is of when the corners differ (0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left); 0 otherwise.</summary>
        public readonly int Corner;
        public GradientQuad(GradientPiece piece, float x, float y, float w, float h, float t0, float t1, bool flip, int corner = 0)
        { Piece = piece; X = x; Y = y; W = w; H = h; T0 = t0; T1 = t1; FlipCross = flip; Corner = corner; }
    }

    /// <summary>Geometry of a gradient clipped to a rounded box without a shader or render target: seven sprites that tile the box exactly.
    /// The ramp covers the middle band and the straight parts of the two end bands; the four r x r corners come from
    /// <see cref="Gradient.CornerTile"/> (ramp colour times corner coverage). That is the "three parts" variant of DESIGN_DRAWING section 1.</summary>
    internal static class GradientRounded
    {
        /// <summary>The radius is cut to half the shorter side, like the painter's border-radius. With radius 0 the plan is one ramp quad.</summary>
        public static List<GradientQuad> Plan(float w, float h, float radius, bool vertical)
        {
            Span<GradientQuad> buf = stackalloc GradientQuad[7];
            int n = Fill(buf, w, h, radius, vertical);
            var list = new List<GradientQuad>(n);
            for (int i = 0; i < n; i++) { list.Add(buf[i]); }
            return list;
        }

        /// <summary>The same quads as <see cref="Plan"/> written into a caller's span of at least 7 (no allocation: the painter calls it every frame).
        /// Returns how many were written.</summary>
        public static int Fill(Span<GradientQuad> into, float w, float h, float radius, bool vertical)
        {
            float along = vertical ? h : w, cross = vertical ? w : h;
            float r = Math.Max(0, Math.Min(radius, Math.Min(along, cross) * 0.5f));
            int n = 0;
            if (r <= 0) { Add(into, ref n, vertical, along, GradientPiece.Ramp, 0, along, 0, cross, false); return n; }
            Add(into, ref n, vertical, along, GradientPiece.Ramp, r, along - r, 0, cross, false);
            Add(into, ref n, vertical, along, GradientPiece.Ramp, 0, r, r, cross - r, false);
            Add(into, ref n, vertical, along, GradientPiece.Ramp, along - r, along, r, cross - r, false);
            Add(into, ref n, vertical, along, GradientPiece.CornerStart, 0, r, 0, r, false);
            Add(into, ref n, vertical, along, GradientPiece.CornerStart, 0, r, cross - r, cross, true);
            Add(into, ref n, vertical, along, GradientPiece.CornerEnd, along - r, along, 0, r, false);
            Add(into, ref n, vertical, along, GradientPiece.CornerEnd, along - r, along, cross - r, cross, true);
            return n;
        }

        /// <summary>The quads of a box whose four corners differ (each its own ellipse, in px, already fitted to the box): the box is cut into a middle band,
        /// and a top and a bottom band as tall as the taller of their two corners; in a band the centre is a ramp and each side is a corner tile with a
        /// ramp strip under (over) it where the other corner of the band is taller. Writes at most 11 quads. Returns -1 when the bands would overlap
        /// (corners reaching past each other across the diagonal): the caller draws a plain ramp then.</summary>
        public static int FillCorners(Span<GradientQuad> into, float w, float h, in CornerPx c, bool vertical)
        {
            int tH = Math.Max(c.TLy, c.TRy), bH = Math.Max(c.BLy, c.BRy);
            if (tH + bH > h) { return -1; }
            int n = 0;
            Rect(into, ref n, vertical, w, h, GradientPiece.Ramp, 0, tH, w, h - bH, false, 0);
            Rect(into, ref n, vertical, w, h, GradientPiece.Ramp, c.TLx, 0, w - c.TRx, tH, false, 0);
            Rect(into, ref n, vertical, w, h, GradientPiece.Ramp, c.BLx, h - bH, w - c.BRx, h, false, 0);
            Side(into, ref n, vertical, w, h, c.TLx, c.TLy, 0, 0, tH, true, false, 0);
            Side(into, ref n, vertical, w, h, c.TRx, c.TRy, w - c.TRx, 0, tH, true, true, 1);
            Side(into, ref n, vertical, w, h, c.BRx, c.BRy, w - c.BRx, h - c.BRy, h - bH, false, true, 2);
            Side(into, ref n, vertical, w, h, c.BLx, c.BLy, 0, h - c.BLy, h - bH, false, false, 3);
            return n;
        }

        // the ramp strip in the band beside a corner (from the corner tile's far edge to the band's edge), then the corner tile itself
        private static void Side(Span<GradientQuad> into, ref int n, bool vertical, float w, float h, int rx, int ry, float x, float y, float band, bool top, bool right, int corner)
        {
            if (rx <= 0 || ry <= 0) { return; }
            if (top) { Rect(into, ref n, vertical, w, h, GradientPiece.Ramp, x, ry, x + rx, band, false, 0); }
            else { Rect(into, ref n, vertical, w, h, GradientPiece.Ramp, x, band, x + rx, y, false, 0); }
            bool end = vertical ? !top : right;
            bool flip = vertical ? right : !top;
            Rect(into, ref n, vertical, w, h, end ? GradientPiece.CornerEnd : GradientPiece.CornerStart, x, y, x + rx, y + ry, flip, corner);
        }

        // a rectangle (x0, y0)-(x1, y1) of the box: a ramp part shows the line from its start to its end along the axis
        private static void Rect(Span<GradientQuad> into, ref int n, bool vertical, float w, float h, GradientPiece piece, float x0, float y0, float x1, float y1, bool flip, int corner)
        {
            if (x1 - x0 <= 0 || y1 - y0 <= 0) { return; }
            float t0 = 0, t1 = 0;
            if (piece == GradientPiece.Ramp) { t0 = vertical ? y0 / h : x0 / w; t1 = vertical ? y1 / h : x1 / w; }
            into[n++] = new GradientQuad(piece, x0, y0, x1 - x0, y1 - y0, t0, t1, flip, corner);
        }

        private static void Add(Span<GradientQuad> into, ref int n, bool vertical, float along, GradientPiece piece, float a0, float a1, float c0, float c1, bool flip)
        {
            if (a1 - a0 <= 0 || c1 - c0 <= 0) { return; }
            float t0 = piece == GradientPiece.Ramp ? a0 / along : 0, t1 = piece == GradientPiece.Ramp ? a1 / along : 0;
            into[n++] = vertical
                ? new GradientQuad(piece, c0, a0, c1 - c0, a1 - a0, t0, t1, flip)
                : new GradientQuad(piece, a0, c0, a1 - a0, c1 - c0, t0, t1, flip);
        }

        /// <summary>The rows (or columns) of an N-texel ramp that a quad showing the line from t0 to t1 samples: whole texels, at least one, inside the ramp.</summary>
        public static void RampSpan(float t0, float t1, int n, out int start, out int count)
        {
            start = Math.Clamp((int)MathF.Round(t0 * n), 0, Math.Max(0, n - 1));
            int end = Math.Clamp((int)MathF.Round(t1 * n), start + 1, n);
            count = end - start;
        }
    }

    /// <summary>Pixel helpers shared by the decoration textures.</summary>
    internal static class DecorPixels
    {
        /// <summary>The image with its edge pixels repeated <paramref name="pad"/> times on every side, (w + 2 pad) x (h + 2 pad). The painter samples inside the
        /// padding, so bilinear filtering at the border of a sprite never reads the opposite edge (the game's sampler wraps).</summary>
        public static byte[] PadEdges(byte[] px, int w, int h, int pad)
        {
            int pw = w + 2 * pad, ph = h + 2 * pad;
            var o = new byte[pw * ph * 4];
            for (int y = 0; y < ph; y++)
            {
                int sy = Math.Clamp(y - pad, 0, h - 1);
                for (int x = 0; x < pw; x++)
                {
                    int sx = Math.Clamp(x - pad, 0, w - 1);
                    Buffer.BlockCopy(px, (sy * w + sx) * 4, o, (y * pw + x) * 4, 4);
                }
            }
            return o;
        }
    }
}
