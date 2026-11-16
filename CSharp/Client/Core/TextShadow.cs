using System;
using System.Runtime.CompilerServices;

namespace BaroUI.Core
{
    /// <summary>The text-shadow of a style, ready to draw: the same text again at (X, Y) layout px in Color, before the text itself. One shadow without
    /// blur: the game's font atlas keeps glyph masks only, a blurred copy would need its own glyph bake (docs/ui/DESIGN_DRAWING.md section 2). The value is read once
    /// per style (styles are shared), so the painter asks every frame without allocating.</summary>
    internal sealed class TextShadow
    {
        public static readonly TextShadow None = new();

        public bool Active;
        public float X, Y;
        /// <summary>The colour as written; when <see cref="CurrentColor"/> it is the style's own text colour at the time of reading, and the painter takes
        /// the colour the text is drawn with in that frame instead (a colour transition moves the shadow with it).</summary>
        public Rgba Color;
        /// <summary>No colour was written (or currentcolor): the shadow has the colour of the text it belongs to.</summary>
        public bool CurrentColor;

        private static readonly ConditionalWeakTable<ComputedStyle, TextShadow> cache = new();

        /// <summary>The shadow of a style (em and u already in px; currentcolor is the style's own colour, as the inherited keyword is for browsers).</summary>
        public static TextShadow Of(ComputedStyle s)
        {
            if (s == null) { return None; }
            if (cache.TryGetValue(s, out var t)) { return t; }
            t = Build(s);
            lock (cache) { cache.AddOrUpdate(s, t); }
            return t;
        }

        private static TextShadow Build(ComputedStyle s)
        {
            var raw = Parse(s.Get("text-shadow"));
            if (!raw.Active) { return None; }
            return new TextShadow { Active = true, X = s.ToPx(raw.X.ToString()).Value, Y = s.ToPx(raw.Y.ToString()).Value, Color = raw.CurrentColor ? s.GetColor("color") : raw.Color, CurrentColor = raw.CurrentColor };
        }

        /// <summary>What the text said, before em and u become px: for the style (Build) and for the warnings of the style engine.</summary>
        public readonly struct Raw
        {
            public readonly bool Active, CurrentColor;
            public readonly Length X, Y;
            public readonly Rgba Color;
            /// <summary>More than one shadow in the list (only the first is drawn).</summary>
            public readonly bool ExtraShadows;
            /// <summary>A blur above 0 was written (ignored).</summary>
            public readonly bool Blur;
            /// <summary>Why the value is no shadow (then Active is false); null when it is a shadow or "none".</summary>
            public readonly string Problem;

            public Raw(bool active, Length x, Length y, Rgba color, bool currentColor, bool extra, bool blur, string problem)
            {
                Active = active; X = x; Y = y; Color = color; CurrentColor = currentColor; ExtraShadows = extra; Blur = blur; Problem = problem;
            }

            public static Raw Bad(string problem, bool extra = false) => new(false, Length.Zero, Length.Zero, Rgba.Transparent, false, extra, false, problem);
        }

        /// <summary>The value with its em and u lengths turned into px, with the font size and unit of the element that wrote it (the style engine does it when
        /// the style is made, as for letter-spacing): a child that inherits `1em 1em #000` gets the parent's px, not its own em.</summary>
        public static string ResolveUnits(string value, float emPx, float unitPx)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('u') < 0 && value.IndexOf("em", StringComparison.Ordinal) < 0) { return value; }
            var lists = CssParser.SplitTop(value, ',');
            var sb = new System.Text.StringBuilder();
            bool changed = false;
            for (int i = 0; i < lists.Count; i++)
            {
                if (i > 0) { sb.Append(", "); }
                bool first = true;
                foreach (var t in StyleEngine.SplitSpaces(lists[i]))
                {
                    if (!first) { sb.Append(' '); }
                    first = false;
                    if (Length.TryParse(t, out var l) && l.Unit is Unit.Em or Unit.U)
                    {
                        float px = l.Unit == Unit.Em ? l.Value * emPx : l.Value * unitPx;
                        sb.Append(px.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("px");
                        changed = true;
                    }
                    else { sb.Append(t); }
                }
            }
            return changed ? sb.ToString() : value;
        }

        private static bool IsOffset(Length l) => l.Unit is Unit.Px or Unit.U or Unit.Em;

        /// <summary>"none", or "x y [blur] [color]" with the colour anywhere; a list uses its first shadow and says so. A length needs a unit (0 does not).</summary>
        public static Raw Parse(string value)
        {
            string v = value?.Trim() ?? "";
            if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) { return new Raw(false, Length.Zero, Length.Zero, Rgba.Transparent, false, false, false, null); }
            var list = CssParser.SplitTop(v, ',');
            bool extra = list.Count > 1;
            Length x = Length.Zero, y = Length.Zero;
            bool blur = false, hasColor = false, current = true;
            var color = Rgba.Transparent;
            int n = 0;
            foreach (var t in StyleEngine.SplitSpaces(list[0]))
            {
                if (Length.TryParse(t, out var l))
                {
                    if (!IsOffset(l)) { return Raw.Bad($"'{t}' is not a length with a unit", extra); }
                    if (n == 0) { x = l; }
                    else if (n == 1) { y = l; }
                    else if (n == 2)
                    {
                        if (l.Value < 0) { return Raw.Bad("a blur cannot be negative", extra); }
                        blur = l.Value > 0;
                    }
                    else { return Raw.Bad("a spread is not part of text-shadow", extra); }
                    n++;
                }
                else if (t.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasColor) { return Raw.Bad("two colours", extra); }
                    hasColor = true;
                    current = true;
                }
                else if (Rgba.TryParse(t, out var c))
                {
                    if (hasColor) { return Raw.Bad("two colours", extra); }
                    hasColor = true;
                    current = false;
                    color = c;
                }
                else { return Raw.Bad($"'{t}' is not understood", extra); }
            }
            if (n < 2) { return Raw.Bad("needs an x and a y offset", extra); }
            return new Raw(true, x, y, color, current, extra, blur, null);
        }
    }
}
