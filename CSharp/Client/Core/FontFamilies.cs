using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>One font file of a family: weight (CSS numbers; here 400..700), italic or upright, and where the file is. Game is the marker
    /// of a file of the installed game, File then being relative to the game folder ("Content/Fonts/..."); otherwise File is relative to the package.</summary>
    internal sealed class FontFace
    {
        public FontFamily Family { get; internal set; }
        public int Weight { get; }
        public bool Italic { get; }
        public string File { get; }
        public bool Game { get; }
        public FontFace(int weight, bool italic, string file, bool game = false) { Weight = weight; Italic = italic; File = file; Game = game; }
        public override string ToString() => $"{Family?.Name} {Weight}{(Italic ? " italic" : "")} ({File})";
    }

    /// <summary>A font family: its files, the names CSS may call it by, and its hhea ascent and descent in em (Chrome rounds them to whole pixels).</summary>
    internal sealed class FontFamily
    {
        public string Name { get; }
        public double Ascent { get; }
        public double Descent { get; }
        public IReadOnlyList<FontFace> Faces { get; }
        /// <summary>Lower case names (family and generic) that select this family.</summary>
        public IReadOnlyList<string> Names { get; }
        internal FontFamily(string name, double ascent, double descent, string[] names, params FontFace[] faces)
        {
            Name = name; Ascent = ascent; Descent = descent; Names = names; Faces = faces;
            foreach (var f in faces) { f.Family = this; }
        }
    }

    /// <summary>The table of font families and the pure CSS logic over it: font-family list, font-weight, weight matching, italic, fallback.
    /// Code and not a data file: the tests read the very table the renderer uses and no loader or parser is on the path of a text measure.
    /// Metrics (hhea, em): read from the font files with a stdlib script (struct-unpack the 'hhea' ascender and descender, divide by head.unitsPerEm).
    /// Cyrillic: every file here has all of U+0410..U+044F, U+0401 and U+0451 in its cmap (checked the same way).</summary>
    internal static class FontFamilies
    {
        /// <summary>JetBrains Mono: the default, and the generic monospace.</summary>
        public static readonly FontFamily Mono = new("JetBrains Mono", 1.02, 0.3,
            new[] { "jetbrains mono", "jetbrainsmono", "monospace", "mono", "ui-monospace", "consolas", "courier new", "courier", "menlo", "monaco" },
            new FontFace(400, false, "Fonts/JetBrainsMono-Regular.ttf"), new FontFace(700, false, "Fonts/JetBrainsMono-Bold.ttf"));

        /// <summary>Caveat: hand writing, the generic cursive.</summary>
        public static readonly FontFamily Hand = new("Caveat", 0.96, 0.3,
            new[] { "caveat", "cursive", "handwriting", "hand" },
            new FontFace(400, false, "Fonts/Caveat-Regular.ttf"), new FontFace(700, false, "Fonts/Caveat-Bold.ttf"));

        /// <summary>PT Serif: a newspaper, the generic serif.</summary>
        public static readonly FontFamily Serif = new("PT Serif", 1.039, 0.286,
            new[] { "pt serif", "ptserif", "serif", "ui-serif", "georgia", "times new roman", "times", "newspaper" },
            new FontFace(400, false, "Fonts/PTSerif-Regular.ttf"), new FontFace(700, false, "Fonts/PTSerif-Bold.ttf"), new FontFace(400, true, "Fonts/PTSerif-Italic.ttf"));

        /// <summary>Noto Sans from the installed game (Content/Fonts/NotoSans, OFL, the face the game itself uses for CJK): the proportional family with
        /// Cyrillic, the generic sans-serif. No italic file: the upright face is sheared.</summary>
        public static readonly FontFamily Sans = new("Noto Sans", 1.069, 0.293,
            new[] { "noto sans", "notosans", "sans-serif", "ui-sans-serif", "system-ui", "sans", "proportional", "arial", "helvetica", "segoe ui" },
            new FontFace(400, false, "Content/Fonts/NotoSans/NotoSans-Regular.ttf", game: true),
            new FontFace(500, false, "Content/Fonts/NotoSans/NotoSans-Medium.ttf", game: true),
            new FontFace(600, false, "Content/Fonts/NotoSans/NotoSans-SemiBold.ttf", game: true),
            new FontFace(700, false, "Content/Fonts/NotoSans/NotoSans-Bold.ttf", game: true));

        /// <summary>Lucide (Fonts/Lucide.ttf, ISC): the icon font of [data-icon] (Core/Icons.cs). One face; ascent 1, descent 0 from its hhea table
        /// (unitsPerEm 1000, ascender 1000, descender 0): an icon stands on the baseline and is 1em high.</summary>
        public static readonly FontFamily Icons = new("Lucide", 1.0, 0.0,
            new[] { "lucide", "icons", "icon" },
            new FontFace(400, false, "Fonts/Lucide.ttf"));

        public static readonly IReadOnlyList<FontFamily> All = new[] { Mono, Hand, Serif, Sans, Icons };

        /// <summary>What no name of a font-family list matches (and the stand-in for a game font that is not there).</summary>
        public static FontFamily Default => Mono;

        private static readonly Dictionary<string, FontFamily> ByName = BuildNames();

        private static Dictionary<string, FontFamily> BuildNames()
        {
            var d = new Dictionary<string, FontFamily>(StringComparer.Ordinal);
            foreach (var f in All) { foreach (var n in f.Names) { d[n] = f; } }
            return d;
        }

        /// <summary>The names of a font-family value: split on commas outside quotes, trimmed, quotes removed, lower case, inner blanks collapsed.</summary>
        public static List<string> SplitList(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) { return result; }
            var cur = new System.Text.StringBuilder();
            char quote = '\0';
            foreach (char c in value)
            {
                if (quote != '\0') { if (c == quote) { quote = '\0'; } else { cur.Append(c); } }
                else if (c == '\'' || c == '"') { quote = c; }
                else if (c == ',') { Flush(cur, result); }
                else { cur.Append(c); }
            }
            Flush(cur, result);
            return result;
        }

        private static void Flush(System.Text.StringBuilder cur, List<string> result)
        {
            string n = string.Join(" ", cur.ToString().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            if (n.Length > 0) { result.Add(n); }
            cur.Clear();
        }

        /// <summary>The family of a font-family value: the first name the table knows wins ("Foo, serif" is the serif); none known is Default.</summary>
        public static FontFamily Find(string value) => TryFind(value, out var f) ? f : Default;

        public static bool TryFind(string value, out FontFamily family)
        {
            foreach (var n in SplitList(value))
            {
                if (ByName.TryGetValue(n, out family)) { return true; }
            }
            family = null;
            return false;
        }

        /// <summary>A font-weight value as a number 1..1000. normal 400, bold 700; bolder and lighter by the CSS Fonts 4 table relative to parent; a number as
        /// written (rounded, clamped); anything else 400.</summary>
        public static int ParseWeight(string value, int parent = 400)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "": case "normal": return 400;
                case "bold": return 700;
                case "bolder": return parent < 350 ? 400 : parent < 550 ? 700 : 900;
                case "lighter": return parent < 550 ? 100 : parent < 750 ? 400 : 700;
            }
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && !double.IsNaN(n) && !double.IsInfinity(n))
            {
                return (int)Math.Clamp(Math.Round(n), 1, 1000);
            }
            return 400;
        }

        /// <summary>CSS font matching on weight (CSS Fonts 4, 5.2): the available weight closest to the wanted one. Exact first. Wanted 400..500: heavier up to
        /// 500 ascending, then lighter descending, then heavier above 500 ascending. Below 400: lighter descending, then heavier ascending. Above 500: heavier
        /// ascending, then lighter descending. Returns -1 for an empty list.</summary>
        public static int MatchWeight(int wanted, IEnumerable<int> available)
        {
            var w = new List<int>(available);
            if (w.Count == 0) { return -1; }
            w.Sort();
            if (w.Contains(wanted)) { return wanted; }
            int r;
            if (wanted >= 400 && wanted <= 500)
            {
                if ((r = Heavier(w, wanted, 500)) >= 0) { return r; }
                if ((r = Lighter(w, wanted)) >= 0) { return r; }
                return Heavier(w, 500, int.MaxValue);
            }
            if (wanted < 400)
            {
                if ((r = Lighter(w, wanted)) >= 0) { return r; }
                return Heavier(w, wanted, int.MaxValue);
            }
            if ((r = Heavier(w, wanted, int.MaxValue)) >= 0) { return r; }
            return Lighter(w, wanted);
        }

        private static int Heavier(List<int> sorted, int from, int to)
        {
            foreach (int x in sorted) { if (x > from && x <= to) { return x; } }
            return -1;
        }

        private static int Lighter(List<int> sorted, int from)
        {
            for (int i = sorted.Count - 1; i >= 0; i--) { if (sorted[i] < from) { return sorted[i]; } }
            return -1;
        }

        /// <summary>The face for a weight and style. The weight is matched among the upright faces; an italic is the family's italic file of that very weight
        /// when there is one (PT Serif Italic for 400), else the upright face is used and the renderer shears it (slant true). So a bold italic of a family
        /// with only a regular italic is the bold face sheared.</summary>
        public static FontFace Pick(FontFamily family, int weight, bool italic, out bool slant)
        {
            var weights = new List<int>();
            foreach (var f in family.Faces) { if (!f.Italic) { weights.Add(f.Weight); } }
            int w = MatchWeight(weight, weights);
            FontFace chosen = null;
            foreach (var f in family.Faces) { if (!f.Italic && f.Weight == w) { chosen = f; break; } }
            chosen ??= family.Faces[0];
            if (italic)
            {
                foreach (var f in family.Faces) { if (f.Italic && f.Weight == chosen.Weight) { slant = false; return f; } }
            }
            slant = italic;
            return chosen;
        }

        /// <summary>The whole CSS side: font-family list, font-weight and font-style values to a face. parentWeight only matters for bolder and lighter.</summary>
        public static FontFace Resolve(string fontFamily, string fontWeight, string fontStyle, out bool slant, int parentWeight = 400)
        {
            string st = (fontStyle ?? "").Trim();
            bool italic = st.Equals("italic", StringComparison.OrdinalIgnoreCase) || st.StartsWith("oblique", StringComparison.OrdinalIgnoreCase);
            return Pick(Find(fontFamily), ParseWeight(fontWeight, parentWeight), italic, out slant);
        }

        /// <summary>The face of the Default family that stands in for a face whose file is missing: its match for the same weight and style.</summary>
        public static FontFace Fallback(FontFace missing, out bool slant) => Pick(Default, missing.Weight, missing.Italic, out slant);

        /// <summary>The file's full path: package files under packageDir, game files under gameDir (null: relative, the game's working directory).</summary>
        public static string FullPath(FontFace face, string packageDir, string gameDir = null)
        {
            if (face.Game) { return gameDir == null ? face.File : System.IO.Path.Combine(gameDir, face.File); }
            return System.IO.Path.Combine(packageDir ?? "", face.File);
        }
    }
}
