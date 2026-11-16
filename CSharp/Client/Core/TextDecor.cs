using System;
using System.Runtime.CompilerServices;

namespace BaroUI.Core
{
    /// <summary>letter-spacing and word-spacing of a style in px (the style engine has already turned em and u into px, and an inherited em into the
    /// parent's px, as browsers do). Read once per style (styles are shared). Spacing is added by the layout and the painter OUTSIDE the width cache, so the
    /// cache stays per (font, text). letter-spacing follows every character, the last one of a run too (as Chrome does; CSS Text 3 leaves the end of a line to
    /// the browser); word-spacing follows every space (U+0020 and U+00A0), so it also lies inside a glued pair of the Russian glue.</summary>
    internal sealed class TextSpacing
    {
        public static readonly TextSpacing None = new();

        public float Letter, Word;
        public bool Any;

        private static readonly ConditionalWeakTable<ComputedStyle, TextSpacing> cache = new();

        public static TextSpacing Of(ComputedStyle s)
        {
            if (s == null) { return None; }
            if (cache.TryGetValue(s, out var t)) { return t; }
            float l = PxOf(s, "letter-spacing"), w = PxOf(s, "word-spacing");
            t = l == 0 && w == 0 ? None : new TextSpacing { Letter = l, Word = w, Any = true };
            lock (cache) { cache.AddOrUpdate(s, t); }
            return t;
        }

        private static float PxOf(ComputedStyle s, string prop)
        {
            var l = s.GetLength(prop);
            return l.Unit == Unit.Px ? l.Value : 0;
        }

        /// <summary>What the spacing adds to a text: Letter per character (a surrogate pair is one), Word per space.</summary>
        public float Extra(string text)
        {
            if (!Any || text == null) { return 0; }
            int chars = 0, spaces = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsLowSurrogate(c)) { chars++; }
                if (c == ' ' || c == ' ') { spaces++; }
            }
            return Letter * chars + Word * spaces;
        }
    }

    /// <summary>The first line of a block starts at an indent (text-indent): a marker at the head of the block's inline items, the value is read when the
    /// line is laid out (a percentage is of the containing block's width, which is only known then).</summary>
    internal sealed class IndentItem : InlineItem
    {
        /// <summary>The indent in px for a content width, 0 when the style has none; negative is a hanging first line.</summary>
        public float Resolve(float avail) => Of(Style, avail);

        public static float Of(ComputedStyle s, float avail)
        {
            var l = s.GetLength("text-indent");
            return l.Unit == Unit.Px ? l.Value : l.Unit == Unit.Percent ? l.Value / 100f * avail : 0;
        }

        /// <summary>Puts the marker at the head of the items of a block's first inline run when its style has a text-indent.</summary>
        public static void Add(System.Collections.Generic.List<InlineItem> items, ComputedStyle s)
        {
            var l = s.GetLength("text-indent");
            if (l.Value != 0 && l.Unit is Unit.Px or Unit.Percent) { items.Insert(0, new IndentItem { Style = s }); }
        }
    }

    /// <summary>text-decoration of a style: the lines (underline, line-through, overline), their colour and thickness. Only solid lines are drawn (the
    /// other styles become solid, with one diagnostic from the style engine). Not inherited as a value; the painter draws the decoration of an element
    /// under/through every text fragment of its inline content (see Painter.PaintDecor).</summary>
    internal sealed class TextDecor
    {
        public static readonly TextDecor None = new();

        /// <summary>True once any style asked for a line: the painter skips the walk up the tree while nothing ever did.</summary>
        public static volatile bool Seen;

        public bool Underline, LineThrough, Overline, Any;
        /// <summary>The colour (currentcolor is the element's own colour) and the thickness in px, 0 for auto.</summary>
        public Rgba Color;
        public float Thickness;

        private static readonly ConditionalWeakTable<ComputedStyle, TextDecor> cache = new();

        public static TextDecor Of(ComputedStyle s)
        {
            if (s == null) { return None; }
            if (cache.TryGetValue(s, out var t)) { return t; }
            t = Build(s);
            lock (cache) { cache.AddOrUpdate(s, t); }
            return t;
        }

        private static TextDecor Build(ComputedStyle s)
        {
            ParseLine(s.Get("text-decoration-line"), out bool u, out bool o, out bool l, out _);
            if (!u && !o && !l) { return None; }
            string c = s.Get("text-decoration-color").Trim();
            var color = c.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) || !Rgba.TryParse(c, out var parsed) ? s.GetColor("color") : parsed;
            float th = 0;
            var len = s.GetLength("text-decoration-thickness");
            if (len.Unit == Unit.Px) { th = len.Value; }
            else if (len.Unit == Unit.Percent) { th = len.Value / 100f * s.FontSizePx; }
            if (th < 0) { th = 0; }
            Seen = true;
            return new TextDecor { Underline = u, Overline = o, LineThrough = l, Any = true, Color = color, Thickness = th };
        }

        /// <summary>The decoration of an element does not reach into an atomic inline (inline-block...) or a positioned-out box.</summary>
        public static bool Stops(ComputedStyle s)
        {
            string d = s.Get("display");
            return d.StartsWith("inline-", StringComparison.Ordinal) || s.Get("position") is "absolute" or "fixed";
        }

        private sealed class ScopeEntry { public ComputedStyle Parent; public bool Value; }
        private static readonly ConditionalWeakTable<ComputedStyle, ScopeEntry> scopes = new();

        /// <summary>True when the element or an ancestor it still inherits a decoration from (up to an atomic inline or a positioned-out box) has a line. Kept per
        /// style and its parent's style, so once any line exists anywhere the painter pays one lookup per text fragment, not a walk up the tree.</summary>
        public static bool InScope(Element e)
        {
            var st = e?.Style;
            if (st == null) { return false; }
            var parentStyle = e.Parent?.Style;
            if (scopes.TryGetValue(st, out var en) && ReferenceEquals(en.Parent, parentStyle)) { return en.Value; }
            bool v = Of(st).Any || !Stops(st) && e.Parent != null && InScope(e.Parent);
            en = new ScopeEntry { Parent = parentStyle, Value = v };
            lock (scopes) { scopes.AddOrUpdate(st, en); }
            return v;
        }

        // ---------------------------------------------------------------- parsing

        /// <summary>"none" or any of underline / overline / line-through (blink is accepted and ignored). problem: what is wrong with the value.</summary>
        public static bool ParseLine(string value, out bool underline, out bool overline, out bool lineThrough, out string problem)
        {
            underline = overline = lineThrough = false;
            problem = null;
            bool none = false, any = false;
            foreach (var t in StyleEngine.SplitSpaces(value ?? ""))
            {
                switch (t.ToLowerInvariant())
                {
                    case "none": none = true; break;
                    case "underline": underline = any = true; break;
                    case "overline": overline = any = true; break;
                    case "line-through": lineThrough = any = true; break;
                    case "blink": any = true; break;
                    default: problem = $"'{t}' is not a line"; underline = overline = lineThrough = false; return false;
                }
            }
            if (none && any) { problem = "none cannot go with other lines"; underline = overline = lineThrough = false; return false; }
            if (!none && !any) { problem = "needs a line"; return false; }
            return true;
        }

        public static bool IsStyleWord(string low) => low is "solid" or "double" or "dotted" or "dashed" or "wavy";

        public static bool IsThickness(string t) =>
            t.Equals("auto", StringComparison.OrdinalIgnoreCase) || t.Equals("from-font", StringComparison.OrdinalIgnoreCase)
            || Length.TryParse(t, out var l) && l.Unit is Unit.Px or Unit.U or Unit.Em or Unit.Percent && l.Value >= 0;

        /// <summary>The shorthand split into the four longhands (what is left out is reset: none, currentcolor, solid, auto). problem: the first thing not
        /// understood (then the shorthand changes nothing); style: the style word as written, so the engine can say it is drawn solid.</summary>
        public static bool Expand(string value, out string line, out string color, out string style, out string thickness, out string problem)
        {
            line = ""; color = "currentcolor"; style = "solid"; thickness = "auto"; problem = null;
            string lines = null;
            foreach (var t in StyleEngine.SplitSpaces(value ?? ""))
            {
                string low = t.ToLowerInvariant();
                if (low is "none" or "underline" or "overline" or "line-through" or "blink") { lines = lines == null ? low : lines + " " + low; }
                else if (IsStyleWord(low)) { style = low; }
                else if (IsThickness(t)) { thickness = t; }
                else if (low == "currentcolor" || Rgba.TryParse(t, out _)) { color = t; }
                else { problem = $"'{t}' is not understood"; return false; }
            }
            line = lines ?? "none";
            if (!ParseLine(line, out _, out _, out _, out problem)) { return false; }
            return true;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>The thickness of a line in whole layout px: the one asked for, else about 1/14 of the font size; at least 1.</summary>
        public static int ThicknessPx(float asked, float fontSize) =>
            Math.Max(1, (int)Math.Round(asked > 0 ? asked : fontSize / 14f));

        /// <summary>Where a line lies relative to the (rounded) baseline, in whole layout px, y down: the top row and the height. Underline: below the
        /// baseline by 0.4 of the descent (at least 1 px, and inside the descent when the font has room); line-through: its middle 0.3 of the ascent above
        /// the baseline; overline: its top on the ascent line.</summary>
        public static (int Top, int Height) Line(char kind, float ascent, float descent, float fontSize, float asked)
        {
            int th = ThicknessPx(asked, fontSize);
            switch (kind)
            {
                case 'u':
                {
                    int off = Math.Max(1, (int)Math.Round(descent * 0.4f));
                    int room = (int)Math.Round(descent) - th;
                    if (room >= 1 && off > room) { off = room; }
                    return (off, th);
                }
                case 'l': return (-(int)Math.Round(ascent * 0.3f + th * 0.5f), th);
                default: return (-(int)Math.Round(ascent), th);
            }
        }

        // ---------------------------------------------------------------- one character as a string

        private static readonly string[] low = new string[256];
        private static readonly System.Collections.Generic.Dictionary<char, string> high = new();

        /// <summary>A one-character string without allocating after the first use of the character (the painter draws spaced text one character at a time).</summary>
        public static string CharString(char c)
        {
            if (c < 256) { return low[c] ??= c.ToString(); }
            lock (high)
            {
                if (!high.TryGetValue(c, out var s)) { high[c] = s = c.ToString(); }
                return s;
            }
        }
    }
}
