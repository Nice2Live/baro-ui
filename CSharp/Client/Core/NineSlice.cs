using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>How the edge pieces of a nine-slice fill their side (border-image-repeat).</summary>
    internal enum EdgeRepeat : byte { Stretch, Repeat, Round, Space }

    /// <summary>One number of border-image-slice: pixels of the source picture, or a percentage of its width (left, right) or height (top, bottom).</summary>
    internal readonly struct SliceValue
    {
        public readonly float V;
        public readonly bool Percent;
        public SliceValue(float v, bool percent) { V = v; Percent = percent; }
    }

    /// <summary>border-image-slice: the four cuts and the "fill" keyword (the middle piece is drawn).</summary>
    internal readonly struct SliceSpec
    {
        public readonly SliceValue T, R, B, L;
        public readonly bool Fill;
        public SliceSpec(SliceValue t, SliceValue r, SliceValue b, SliceValue l, bool fill) { T = t; R = r; B = b; L = l; Fill = fill; }
        public static readonly SliceSpec Default = new(new SliceValue(100, true), new SliceValue(100, true), new SliceValue(100, true), new SliceValue(100, true), false);
    }

    internal enum WidthKind : byte { Multiple, Length, Percent, Auto }

    /// <summary>One number of border-image-width: a multiple of the border's width, a length, a percentage of the box side, or auto (the slice's own size).</summary>
    internal readonly struct WidthValue
    {
        public readonly WidthKind Kind;
        public readonly Length L;
        public readonly float N;
        public WidthValue(WidthKind kind, Length l, float n) { Kind = kind; L = l; N = n; }
        public static readonly WidthValue One = new(WidthKind.Multiple, Length.Zero, 1);
    }

    internal readonly struct WidthSpec
    {
        public readonly WidthValue T, R, B, L;
        public WidthSpec(WidthValue t, WidthValue r, WidthValue b, WidthValue l) { T = t; R = r; B = b; L = l; }
        public static readonly WidthSpec Default = new(WidthValue.One, WidthValue.One, WidthValue.One, WidthValue.One);
    }

    internal readonly struct SliceEdges
    {
        public readonly float T, R, B, L;
        public SliceEdges(float t, float r, float b, float l) { T = t; R = r; B = b; L = l; }
    }

    /// <summary>One piece of a nine-slice: where it goes (already inside the box) and which part of the picture it shows.</summary>
    internal readonly struct NineQuad
    {
        public readonly float X, Y, W, H;
        public readonly int SrcX, SrcY, SrcW, SrcH;
        public NineQuad(float x, float y, float w, float h, int sx, int sy, int sw, int sh) { X = x; Y = y; W = w; H = h; SrcX = sx; SrcY = sy; SrcW = sw; SrcH = sh; }
    }

    /// <summary>border-image (docs/ui/AUTHORING.md): parsing of the shorthand and its longhands, and the geometry of the nine pieces, free of the game so the tests
    /// run it. The corners keep their size (the width of the border, whatever the slice was in the picture), the edges are stretched, repeated or rounded
    /// to a whole number of copies, the middle is drawn only with "fill".</summary>
    internal static class NineSlice
    {
        // ---------------------------------------------------------------- parsing

        /// <summary>Splits on spaces outside brackets and quotes; a "/" outside them is a word of its own.</summary>
        internal static List<string> Words(string s)
        {
            var list = new List<string>();
            if (s == null) { return list; }
            int depth = 0, start = -1;
            char quote = (char)0;
            for (int i = 0; i <= s.Length; i++)
            {
                char c = i < s.Length ? s[i] : ' ';
                if (quote != 0) { if (c == quote) { quote = (char)0; } }
                else if (c == '"' || c == '\'') { quote = c; if (start < 0) { start = i; } continue; }
                else if (c == '(') { depth++; }
                else if (c == ')') { depth = Math.Max(0, depth - 1); }
                bool slash = c == '/' && depth == 0 && quote == 0;
                bool space = char.IsWhiteSpace(c) && depth == 0 && quote == 0;
                if ((space || slash) && start >= 0) { list.Add(s.Substring(start, i - start)); start = -1; }
                if (slash) { list.Add("/"); }
                else if (!space && start < 0 && i < s.Length) { start = i; }
            }
            return list;
        }

        private static bool Number(string w, out float v) => float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool SliceWord(string w, out SliceValue v)
        {
            v = default;
            if (w.EndsWith("%", StringComparison.Ordinal)) { if (!Number(w.Substring(0, w.Length - 1), out float p)) { return false; } v = new SliceValue(p, true); return true; }
            if (!Number(w, out float n)) { return false; }
            v = new SliceValue(n, false);
            return true;
        }

        private static void Four<T>(IReadOnlyList<T> v, out T t, out T r, out T b, out T l)
        {
            t = v[0]; r = v.Count > 1 ? v[1] : t; b = v.Count > 2 ? v[2] : t; l = v.Count > 3 ? v[3] : r;
        }

        /// <summary>border-image-slice: one to four numbers or percentages, and "fill" anywhere among them.</summary>
        public static bool TryParseSlice(string value, out SliceSpec spec)
        {
            spec = SliceSpec.Default;
            var vals = new List<SliceValue>(4);
            bool fill = false;
            foreach (var w in Words(value))
            {
                if (w.Equals("fill", StringComparison.OrdinalIgnoreCase)) { if (fill) { return false; } fill = true; continue; }
                if (!SliceWord(w, out var v) || vals.Count == 4) { return false; }
                vals.Add(v);
            }
            if (vals.Count == 0) { return false; }
            Four(vals, out var t, out var r, out var b, out var l);
            spec = new SliceSpec(t, r, b, l, fill);
            return true;
        }

        /// <summary>border-image-width: one to four of: a number (times the border width), a length, a percentage, auto.</summary>
        public static bool TryParseWidth(string value, out WidthSpec spec)
        {
            spec = WidthSpec.Default;
            var vals = new List<WidthValue>(4);
            foreach (var w in Words(value))
            {
                if (vals.Count == 4) { return false; }
                if (w.Equals("auto", StringComparison.OrdinalIgnoreCase)) { vals.Add(new WidthValue(WidthKind.Auto, Length.Zero, 0)); continue; }
                if (!Length.TryParse(w, out var l) || l.Value < 0) { return false; }
                switch (l.Unit)
                {
                    case Unit.None: vals.Add(new WidthValue(WidthKind.Multiple, Length.Zero, l.Value)); break;
                    case Unit.Percent: vals.Add(new WidthValue(WidthKind.Percent, Length.Zero, l.Value)); break;
                    case Unit.Px: case Unit.U: case Unit.Em: vals.Add(new WidthValue(WidthKind.Length, l, 0)); break;
                    default: return false;
                }
            }
            if (vals.Count == 0) { return false; }
            Four(vals, out var t, out var r, out var b, out var lft);
            spec = new WidthSpec(t, r, b, lft);
            return true;
        }

        private static bool RepeatWord(string w, out EdgeRepeat r)
        {
            r = EdgeRepeat.Stretch;
            switch (w.ToLowerInvariant())
            {
                case "stretch": return true;
                case "repeat": r = EdgeRepeat.Repeat; return true;
                case "round": r = EdgeRepeat.Round; return true;
                case "space": r = EdgeRepeat.Space; return true;
            }
            return false;
        }

        /// <summary>border-image-repeat: one keyword (both axes) or two (horizontal, vertical).</summary>
        public static bool TryParseRepeat(string value, out EdgeRepeat x, out EdgeRepeat y)
        {
            x = y = EdgeRepeat.Stretch;
            var w = Words(value);
            if (w.Count == 0 || w.Count > 2 || !RepeatWord(w[0], out x)) { return false; }
            y = x;
            return w.Count == 1 || RepeatWord(w[1], out y);
        }

        /// <summary>Splits the shorthand "&lt;source&gt; || &lt;slice&gt; [/ &lt;width&gt; [/ &lt;outset&gt;]] || &lt;repeat&gt;" into the longhand texts (defaults: slice 100%, width 1,
        /// repeat stretch). False: it cannot be read at all; note, when not null, tells what is ignored (the outset).</summary>
        public static bool Expand(string value, out string source, out string slice, out string width, out string repeat, out string note)
        {
            source = "none"; slice = "100%"; width = "1"; repeat = "stretch"; note = null;
            var all = Words(value);
            if (all.Count == 0) { return false; }
            if (all.Count == 1 && all[0].ToLowerInvariant() is "none") { return true; }
            var sliceParts = new List<string>(); var widthParts = new List<string>(); var rep = new List<string>();
            bool haveSource = false;
            int section = 0;
            foreach (var w in all)
            {
                if (w == "/") { section++; if (section > 2) { return false; } continue; }
                if (section >= 1 && RepeatWord(w, out _)) { rep.Add(w); continue; }   // the repeat words come last, after the width or the outset
                if (section == 1) { widthParts.Add(w); continue; }
                if (section == 2) { note = "border-image: the outset is not supported, ignored"; continue; }
                string low = w.ToLowerInvariant();
                if (low.StartsWith("url(", StringComparison.Ordinal) || low.StartsWith("sprite(", StringComparison.Ordinal) || low.Contains("gradient(") || low == "none")
                {
                    if (haveSource) { return false; }
                    haveSource = true; source = w; continue;
                }
                if (RepeatWord(w, out _)) { rep.Add(w); continue; }
                sliceParts.Add(w);
            }
            if (!haveSource) { return false; }
            if (sliceParts.Count > 0) { slice = string.Join(" ", sliceParts); }
            if (widthParts.Count > 0) { width = string.Join(" ", widthParts); }
            if (rep.Count > 0) { repeat = string.Join(" ", rep); }
            return true;
        }

        // ---------------------------------------------------------------- numbers

        private static float Cut(in SliceValue v, int dim) => Math.Min(dim, v.Percent ? dim * v.V / 100f : v.V);

        /// <summary>The cuts in pixels of a picture imgW x imgH. Each is at most the picture's size; cuts that overlap (left + right wider than the picture) are
        /// scaled down together so the middle is empty.</summary>
        public static SliceEdges ResolveSlice(in SliceSpec s, int imgW, int imgH)
        {
            float t = Cut(s.T, imgH), b = Cut(s.B, imgH), l = Cut(s.L, imgW), r = Cut(s.R, imgW);
            if (t + b > imgH && t + b > 0) { float f = imgH / (t + b); t *= f; b *= f; }
            if (l + r > imgW && l + r > 0) { float f = imgW / (l + r); l *= f; r *= f; }
            return new SliceEdges((float)Math.Round(t), (float)Math.Round(r), (float)Math.Round(b), (float)Math.Round(l));
        }

        private static float One(in WidthValue v, float border, float slicePx, float side, float unitPx, float emPx) => v.Kind switch
        {
            WidthKind.Multiple => v.N * border,
            WidthKind.Percent => v.N * side / 100f,
            WidthKind.Auto => slicePx,
            _ => v.L.Unit == Unit.U ? v.L.Value * unitPx : v.L.Unit == Unit.Em ? v.L.Value * emPx : v.L.Value,
        };

        /// <summary>The widths of the four edge pieces on screen: border is the box's border width at each side, slice the cuts in picture pixels (for auto).</summary>
        public static SliceEdges ResolveWidth(in WidthSpec w, in SliceEdges slice, in SliceEdges border, float boxW, float boxH, float unitPx, float emPx) =>
            new(One(w.T, border.T, slice.T, boxH, unitPx, emPx), One(w.R, border.R, slice.R, boxW, unitPx, emPx),
                One(w.B, border.B, slice.B, boxH, unitPx, emPx), One(w.L, border.L, slice.L, boxW, unitPx, emPx));

        // ---------------------------------------------------------------- the nine pieces

        /// <summary>The pieces of a nine-slice into quads: x, y, w, h is the box, width the size of the corners and the thickness of the edges on screen, slice the
        /// cuts in picture pixels. Pieces with no area are left out; corners that do not fit are scaled down together (as CSS does). Returns the count;
        /// truncated: there were more pieces than quads holds (a very short repeating tile). Nothing is allocated.</summary>
        public static int Plan(Span<NineQuad> quads, int imgW, int imgH, in SliceEdges slice, in SliceEdges width, bool fill, EdgeRepeat rx, EdgeRepeat ry,
            float x, float y, float w, float h, out bool truncated)
        {
            truncated = false;
            if (imgW <= 0 || imgH <= 0 || w <= 0 || h <= 0) { return 0; }
            float wl = width.L, wr = width.R, wt = width.T, wb = width.B;
            float fx = wl + wr > w && wl + wr > 0 ? w / (wl + wr) : 1, fy = wt + wb > h && wt + wb > 0 ? h / (wt + wb) : 1, f = Math.Min(fx, fy);
            if (f < 1) { wl *= f; wr *= f; wt *= f; wb *= f; }
            int sl = (int)slice.L, sr = (int)slice.R, st = (int)slice.T, sb = (int)slice.B;
            int midW = imgW - sl - sr, midH = imgH - st - sb;
            float x1 = x + wl, x2 = x + w - wr, y1 = y + wt, y2 = y + h - wb;
            int n = 0;
            // corners
            if (!Add(quads, ref n, ref truncated, x, y, wl, wt, 0, 0, sl, st) || !Add(quads, ref n, ref truncated, x2, y, wr, wt, imgW - sr, 0, sr, st)
                || !Add(quads, ref n, ref truncated, x, y2, wl, wb, 0, imgH - sb, sl, sb) || !Add(quads, ref n, ref truncated, x2, y2, wr, wb, imgW - sr, imgH - sb, sr, sb)) { return n; }
            // edges: along x for top and bottom, along y for left and right
            float tileTop = st > 0 ? midW * wt / st : midW, tileBottom = sb > 0 ? midW * wb / sb : midW;
            float tileLeft = sl > 0 ? midH * wl / sl : midH, tileRight = sr > 0 ? midH * wr / sr : midH;
            if (!Run(quads, ref n, ref truncated, true, x1, x2 - x1, y, wt, sl, midW, 0, st, tileTop, rx)) { return n; }
            if (!Run(quads, ref n, ref truncated, true, x1, x2 - x1, y2, wb, sl, midW, imgH - sb, sb, tileBottom, rx)) { return n; }
            if (!Run(quads, ref n, ref truncated, false, y1, y2 - y1, x, wl, st, midH, 0, sl, tileLeft, ry)) { return n; }
            if (!Run(quads, ref n, ref truncated, false, y1, y2 - y1, x2, wr, st, midH, imgW - sr, sr, tileRight, ry)) { return n; }
            if (fill) { Add(quads, ref n, ref truncated, x1, y1, x2 - x1, y2 - y1, sl, st, midW, midH); }
            return n;
        }

        private static bool Add(Span<NineQuad> quads, ref int n, ref bool truncated, float dx, float dy, float dw, float dh, int ssx, int ssy, int ssw, int ssh)
        {
            if (dw <= 0.001f || dh <= 0.001f || ssw <= 0 || ssh <= 0) { return true; }
            if (n >= quads.Length) { truncated = true; return false; }
            quads[n++] = new NineQuad(dx, dy, dw, dh, ssx, ssy, ssw, ssh);
            return true;
        }

        /// <summary>One edge piece: along is the axis it runs on (x when horizontal), cross the other. dest*: start and length along, start and thickness across;
        /// src*: the same in the picture; tile is the length of one copy on screen.</summary>
        private static bool PutRun(Span<NineQuad> quads, ref int n, ref bool truncated, bool horizontal, float at, float len, float destCross, float thick,
            int srcAlong, int srcCross, int srcThick, int s0, int s1)
        {
            if (len <= 0.001f || s1 <= s0) { return true; }
            if (n >= quads.Length) { truncated = true; return false; }
            quads[n++] = horizontal
                ? new NineQuad(at, destCross, len, thick, srcAlong + s0, srcCross, s1 - s0, srcThick)
                : new NineQuad(destCross, at, thick, len, srcCross, srcAlong + s0, srcThick, s1 - s0);
            return true;
        }

        private static bool Run(Span<NineQuad> quads, ref int n, ref bool truncated, bool horizontal, float destAlong, float lenAlong, float destCross, float thick,
            int srcAlong, int srcLen, int srcCross, int srcThick, float tile, EdgeRepeat mode)
        {
            if (lenAlong <= 0.001f || thick <= 0.001f || srcLen <= 0 || srcThick <= 0) { return true; }
            if (mode == EdgeRepeat.Stretch || tile < 1f) { return PutRun(quads, ref n, ref truncated, horizontal, destAlong, lenAlong, destCross, thick, srcAlong, srcCross, srcThick, 0, srcLen); }
            float step = tile, first;
            int count;
            if (mode == EdgeRepeat.Round)
            {
                count = Math.Max(1, (int)Math.Round(lenAlong / tile));
                step = tile = lenAlong / count;
                first = destAlong;
            }
            else if (mode == EdgeRepeat.Space)
            {
                count = (int)Math.Floor(lenAlong / tile + 0.0001f);
                if (count < 1) { return true; }
                float gap = (lenAlong - count * tile) / (count + 1);
                step = tile + gap;
                first = destAlong + gap;
            }
            else
            {
                // repeat: copies centred along the edge, the first and last cropped
                count = (int)Math.Ceiling(lenAlong / tile - 0.0001f);
                float total = lenAlong / tile;
                int whole = (int)Math.Ceiling(total);
                float over = whole * tile - lenAlong;
                first = destAlong - over / 2;
                count = whole;
            }
            float end = destAlong + lenAlong;
            for (int i = 0; i < count; i++)
            {
                float a = first + i * step, b = a + tile;
                float a0 = Math.Max(a, destAlong), b0 = Math.Min(b, end);
                if (b0 - a0 <= 0.001f) { continue; }
                int s0 = (int)Math.Round((a0 - a) / tile * srcLen), s1 = (int)Math.Round((b0 - a) / tile * srcLen);
                s0 = Math.Min(s0, srcLen - 1);
                s1 = Math.Min(srcLen, Math.Max(s1, s0 + 1));
                if (!PutRun(quads, ref n, ref truncated, horizontal, a0, b0 - a0, destCross, thick, srcAlong, srcCross, srcThick, s0, s1)) { return false; }
            }
            return true;
        }
    }
}
