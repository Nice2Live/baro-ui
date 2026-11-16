using System;
using System.Collections.Generic;
using System.Text;

namespace BaroUI.Core
{
    public readonly struct Specificity : IComparable<Specificity>
    {
        public readonly int Ids, Classes, Tags;
        public Specificity(int ids, int classes, int tags) { Ids = ids; Classes = classes; Tags = tags; }
        public int CompareTo(Specificity o) => Ids != o.Ids ? Ids.CompareTo(o.Ids) : Classes != o.Classes ? Classes.CompareTo(o.Classes) : Tags.CompareTo(o.Tags);
        public override string ToString() => $"{Ids},{Classes},{Tags}";
    }

    public enum Combinator { Descendant, Child, Adjacent, Sibling }   // ' ', '>', '+' (next element sibling), '~' (any earlier element sibling)

    /// <summary>tag#id.class:pseudo — one step of a selector.</summary>
    public sealed class Compound
    {
        public string Tag;                 // null = any
        public string Id;
        public readonly List<string> Classes = new();
        public readonly List<string> Pseudos = new();   // hover, active, focus, focus-within, disabled, checked, root, first-child, last-child, nth-child(…)
        /// <summary>The arguments of :not(...): each one a compound without combinators and without a :not of its own. The compound fails if any of them matches.</summary>
        public readonly List<Compound> Nots = new();
        /// <summary>The relative selectors of :has(...), one per :has (each with its Lead combinator). The compound needs every one to match some element in its scope.</summary>
        public readonly List<Selector> Has = new();
        /// <summary>[name] (Value null) or [name=value]; names lower case, values compared exactly.</summary>
        public readonly List<(string Name, string Value)> Attributes = new();

        public bool Matches(Element e)
        {
            if (Tag != null && Tag != e.Tag) { return false; }
            if (Id != null && e.Id != Id) { return false; }
            foreach (var c in Classes) { if (!e.HasClass(c)) { return false; } }
            foreach (var (name, value) in Attributes)
            {
                string v = e.GetAttribute(name);
                if (v == null || (value != null && v != value)) { return false; }
            }
            foreach (var p in Pseudos) { if (!MatchPseudo(p, e)) { return false; } }
            foreach (var n in Nots) { if (n.Matches(e)) { return false; } }
            foreach (var h in Has) { if (!Selector.AnyRelative(h, e)) { return false; } }
            return true;
        }

        /// <summary>The interaction states this compound reads (:hover, :active, :focus, :focus-within, :disabled), also inside :not(). None: it does not depend on NodeState.
        /// The states read inside :has() are not here: HasBits.</summary>
        public NodeState StateBits()
        {
            var bits = NodeState.None;
            foreach (var p in Pseudos) { bits |= StateOf(p); }
            foreach (var n in Nots) { bits |= n.StateBits(); }
            return bits;
        }

        /// <summary>The interaction states the relative selectors of :has() read (also inside :not()): a change of one of them, on a descendant or a following
        /// sibling of the element, can change the match. None when no :has() here.</summary>
        public NodeState HasBits()
        {
            var bits = NodeState.None;
            foreach (var h in Has) { foreach (var part in h.Parts) { bits |= part.StateBits() | part.HasBits(); } }
            foreach (var n in Nots) { bits |= n.HasBits(); }
            return bits;
        }

        /// <summary>Whether this compound (or a :not() in it) has a :has(): such a compound depends on elements that are not its own.</summary>
        public bool ReadsHas()
        {
            if (Has.Count > 0) { return true; }
            foreach (var n in Nots) { if (n.ReadsHas()) { return true; } }
            return false;
        }

        private static NodeState StateOf(string p) => p switch
        {
            "hover" => NodeState.Hover,
            "active" => NodeState.Active,
            "focus" => NodeState.Focus,
            "focus-within" => NodeState.FocusWithin,
            "disabled" => NodeState.Disabled,
            "drag-over" => NodeState.DragOver,
            "dragging" => NodeState.Dragging,
            _ => NodeState.None,
        };

        /// <summary>Could this compound match e in some state of e? Everything but the state pseudos is tested; a :not() whose argument reads a state is skipped
        /// (it can be true or false). An over-approximation of Matches that StyleEngine.ComputeState uses to find the rules a state change can touch.</summary>
        public bool MatchesIgnoringState(Element e)
        {
            if (Tag != null && Tag != e.Tag) { return false; }
            if (Id != null && e.Id != Id) { return false; }
            foreach (var c in Classes) { if (!e.HasClass(c)) { return false; } }
            foreach (var (name, value) in Attributes)
            {
                string v = e.GetAttribute(name);
                if (v == null || (value != null && v != value)) { return false; }
            }
            foreach (var p in Pseudos) { if (StateOf(p) == NodeState.None && !MatchPseudo(p, e)) { return false; } }
            // :has() is not tested here (it reads descendants, which change without the element): a compound with one is kept, as a :not() with one is
            foreach (var n in Nots) { if (n.StateBits() == NodeState.None && !n.ReadsHas() && n.Matches(e)) { return false; } }
            return true;
        }

        private static bool MatchPseudo(string p, Element e)
        {
            switch (p)
            {
                case "hover": return (e.State & NodeState.Hover) != 0;
                case "active": return (e.State & NodeState.Active) != 0;
                case "focus": return (e.State & NodeState.Focus) != 0;
                case "focus-within": return (e.State & NodeState.FocusWithin) != 0;
                case "disabled": return (e.State & NodeState.Disabled) != 0 || e.GetAttribute("disabled") != null;
                case "drag-over": return (e.State & NodeState.DragOver) != 0;
                case "dragging": return (e.State & NodeState.Dragging) != 0;
                case "checked": return e.GetAttribute("checked") != null;
                case "root": return e.Tag == "#root";
                case "first-child": return e.ElementIndex(out _) == 0;
                case "last-child": { int i = e.ElementIndex(out int n); return i >= 0 && i == n - 1; }
            }
            if (p.StartsWith("nth-child(", StringComparison.Ordinal))
            {
                int i = e.ElementIndex(out _) + 1;
                string arg = p.Substring(10, p.Length - 11).Trim();
                return arg switch
                {
                    "odd" => i % 2 == 1,
                    "even" => i > 0 && i % 2 == 0,
                    _ => int.TryParse(arg, out int k) && i == k,
                };
            }
            return false;
        }

        public static readonly HashSet<string> KnownPseudos = new() { "hover", "active", "focus", "focus-within", "disabled", "drag-over", "dragging", "checked", "root", "first-child", "last-child" };
    }

    /// <summary>Compounds joined by descendant/child/+/~ combinators; stored left to right, matched right to left.</summary>
    public sealed class Selector
    {
        public readonly List<Compound> Parts = new();
        public readonly List<Combinator> Combinators = new();   // Combinators[i] joins Parts[i] and Parts[i + 1]
        public Specificity Specificity;
        public string Text;
        /// <summary>"before" or "after" for a rule of ::before / ::after (the subject compound still matches the element itself); null for the element.</summary>
        public string PseudoElement;
        /// <summary>A relative selector inside :has(): the combinator before its first compound (Descendant when none is written). Unused otherwise.</summary>
        public Combinator Lead = Combinator.Descendant;

        public bool Matches(Element e) => MatchFrom(Parts.Count - 1, e);

        /// <summary>Does some element in the scope of anchor's relative selector match it, i.e. :has(rel) on anchor: the scope is the descendants (Descendant lead),
        /// the children (Child), the next sibling (Adjacent) or the following siblings (Sibling), each with its subtree when the selector has more compounds.</summary>
        public static bool AnyRelative(Selector rel, Element anchor)
        {
            switch (rel.Lead)
            {
                case Combinator.Child:
                    foreach (var n in anchor.Children)
                    {
                        if (n is not Element d) { continue; }
                        if (rel.MatchesRelative(d, anchor) || (rel.Parts.Count > 1 && SubtreeMatch(rel, anchor, d))) { return true; }
                    }
                    return false;
                case Combinator.Adjacent:
                case Combinator.Sibling:
                    if (anchor.Parent == null) { return false; }
                    var kids = anchor.Parent.Children;
                    for (int k = kids.IndexOf(anchor) + 1; k < kids.Count; k++)
                    {
                        if (kids[k] is not Element s) { continue; }
                        if (rel.MatchesRelative(s, anchor) || SubtreeMatch(rel, anchor, s)) { return true; }
                        if (rel.Lead == Combinator.Adjacent) { return false; }   // only the next element sibling
                    }
                    return false;
                default:
                    return SubtreeMatch(rel, anchor, anchor);
            }
        }

        private static bool SubtreeMatch(Selector rel, Element anchor, Element root)
        {
            foreach (var n in root.Children)
            {
                if (n is not Element d) { continue; }
                if (rel.MatchesRelative(d, anchor) || SubtreeMatch(rel, anchor, d)) { return true; }
            }
            return false;
        }

        /// <summary>The subject of this relative selector is d and its chain starts at anchor (see Lead).</summary>
        public bool MatchesRelative(Element d, Element anchor) => RelFrom(Parts.Count - 1, d, anchor);

        private bool RelFrom(int i, Element e, Element anchor)
        {
            if (!Parts[i].Matches(e)) { return false; }
            if (i == 0) { return LeadsTo(e, anchor); }
            var comb = Combinators[i - 1];
            if (comb == Combinator.Child) { return e.Parent != null && RelFrom(i - 1, e.Parent, anchor); }
            if (comb == Combinator.Descendant)
            {
                for (var a = e.Parent; a != null; a = a.Parent) { if (RelFrom(i - 1, a, anchor)) { return true; } }
                return false;
            }
            if (e.Parent == null) { return false; }
            var kids = e.Parent.Children;
            int at = kids.IndexOf(e);
            for (int k = at - 1; k >= 0; k--)
            {
                if (kids[k] is not Element prev) { continue; }
                if (RelFrom(i - 1, prev, anchor)) { return true; }
                if (comb == Combinator.Adjacent) { return false; }
            }
            return false;
        }

        /// <summary>The first compound of a relative selector is in relation Lead to the anchor.</summary>
        private bool LeadsTo(Element e, Element anchor)
        {
            switch (Lead)
            {
                case Combinator.Child: return e.Parent == anchor;
                case Combinator.Descendant:
                    for (var a = e.Parent; a != null; a = a.Parent) { if (a == anchor) { return true; } }
                    return false;
                default:
                    if (e.Parent == null || e.Parent != anchor.Parent) { return false; }
                    var kids = e.Parent.Children;
                    int at = kids.IndexOf(e), from = kids.IndexOf(anchor);
                    if (from < 0 || from >= at) { return false; }
                    if (Lead == Combinator.Sibling) { return true; }
                    for (int k = at - 1; k > from; k--) { if (kids[k] is Element) { return false; } }   // Adjacent: no element between them
                    return true;
            }
        }

        private bool MatchFrom(int i, Element e)
        {
            if (!Parts[i].Matches(e)) { return false; }
            if (i == 0) { return true; }
            var comb = Combinators[i - 1];
            if (comb == Combinator.Child) { return e.Parent != null && MatchFrom(i - 1, e.Parent); }
            if (comb == Combinator.Adjacent || comb == Combinator.Sibling)
            {
                if (e.Parent == null) { return false; }
                var kids = e.Parent.Children;
                int at = -1;
                for (int k = 0; k < kids.Count; k++) { if (ReferenceEquals(kids[k], e)) { at = k; break; } }
                for (int k = at - 1; k >= 0; k--)
                {
                    if (kids[k] is not Element prev) { continue; }
                    if (MatchFrom(i - 1, prev)) { return true; }
                    if (comb == Combinator.Adjacent) { return false; }   // only the nearest earlier element
                }
                return false;
            }
            for (var a = e.Parent; a != null; a = a.Parent) { if (MatchFrom(i - 1, a)) { return true; } }
            return false;
        }
    }

    public sealed class Declaration
    {
        public string Property;   // lower case; custom properties (--x) keep their case
        public string Value;
        public bool Important;
        public int Line;
        /// <summary>Stylesheet file name for messages ("style": a style="" attribute).</summary>
        public string File;
    }

    public sealed class Rule
    {
        public readonly List<Selector> Selectors = new();
        public readonly List<Declaration> Declarations = new();
        public int Line;
    }

    /// <summary>One stop of an @keyframes block: an offset (from = 0, to = 1, NN% = NN/100) and its declarations.</summary>
    public sealed class KeyStop
    {
        public float Offset;
        public readonly List<Declaration> Declarations = new();
    }

    public sealed class KeyframesRule
    {
        public string Name = "";
        public int Line;
        public readonly List<KeyStop> Stops = new();
    }

    /// <summary>One @font-face block: the family name, the src urls in the order written (the first readable one is used), the weight and the italic style.</summary>
    internal sealed class FontFaceRule
    {
        public string Family;
        public readonly List<string> Sources = new();
        public int Weight = 400;
        public bool Italic;
        public int Line;
    }

    public sealed class StyleSheet
    {
        public readonly List<Rule> Rules = new();
        public readonly List<KeyframesRule> Keyframes = new();
        /// <summary>The @font-face blocks, as written (the files are read by StyleEngine, which knows the page folder).</summary>
        internal readonly List<FontFaceRule> FontFaces = new();
        public readonly List<Diagnostic> Diagnostics = new();
        public string File;
    }

    /// <summary>CSS subset parser (docs/ui/ENGINE_PLAN.md, stage 1). Skips what it does not know with a diagnostic:
    /// a bad selector drops its rule, a bad declaration only itself.</summary>
    internal static class CssParser
    {
        public static StyleSheet Parse(string css, string file = "css", int firstLine = 1)
        {
            var sheet = new StyleSheet { File = file };
            css = StripComments(css ?? "");
            int pos = 0;
            int Line(int at)
            {
                int n = firstLine;
                for (int i = 0; i < at && i < css.Length; i++) { if (css[i] == '\n') { n++; } }
                return n;
            }
            void Warn(int at, string m) => sheet.Diagnostics.Add(new Diagnostic(file, Line(at), m));

            while (pos < css.Length)
            {
                int brace = css.IndexOf('{', pos);
                int semi = css.IndexOf(';', pos);
                string head = (brace < 0 ? css.Substring(pos) : css.Substring(pos, brace - pos)).Trim();
                if (head.StartsWith("@", StringComparison.Ordinal) && semi >= 0 && (brace < 0 || semi < brace))
                {
                    Warn(pos, $"{head.Split(' ')[0]} is not supported");
                    pos = semi + 1;
                    continue;
                }
                if (brace < 0)
                {
                    if (head.Length > 0) { Warn(pos, $"text without a block: '{Clip(head)}'"); }
                    break;
                }
                int close = MatchingBrace(css, brace);
                int headAt = pos;
                while (headAt < brace && char.IsWhiteSpace(css[headAt])) { headAt++; }
                string body = css.Substring(brace + 1, (close < 0 ? css.Length : close) - brace - 1);
                if (close < 0) { Warn(brace, "block is not closed"); }
                pos = close < 0 ? css.Length : close + 1;
                if (head.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
                {
                    ParseKeyframes(head.Substring(10).Trim(), body, sheet, file, Line(brace + 1));
                    continue;
                }
                if (string.Equals(head, "@font-face", StringComparison.OrdinalIgnoreCase))
                {
                    ParseFontFace(body, sheet, file, Line(headAt));
                    continue;
                }
                if (head.StartsWith("@", StringComparison.Ordinal))
                {
                    Warn(headAt, $"{head.Split(' ')[0]} is not supported, block skipped");
                    continue;
                }
                var rule = new Rule { Line = Line(headAt) };
                bool ok = true;
                foreach (var part in SplitTop(head, ','))
                {
                    var sel = ParseSelector(part.Trim(), out string error);
                    if (sel == null) { Warn(headAt, $"selector '{Clip(part.Trim())}': {error}; rule skipped"); ok = false; break; }
                    rule.Selectors.Add(sel);
                }
                if (!ok || rule.Selectors.Count == 0) { continue; }
                ParseDeclarations(body, rule.Declarations, (line, m) => sheet.Diagnostics.Add(new Diagnostic(file, line, m)), Line(brace + 1), file);
                sheet.Rules.Add(rule);
            }
            return sheet;
        }

        /// <summary>One url(...) item of a src: quoted or bare, an optional format(...) after it (ignored).</summary>
        private static readonly System.Text.RegularExpressions.Regex FontUrl = new(
            @"^url\(\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^""')]*))\s*\)(?:\s+format\([^)]*\))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>@font-face { font-family; src; font-weight; font-style }: a rule, or a diagnostic and no rule. A descriptor not in the list is reported and
        /// ignored. The src items are kept as written: whether their files exist and stay in the package is StyleEngine's check (ImageCss.ResolvePath).</summary>
        private static void ParseFontFace(string body, StyleSheet sheet, string file, int line)
        {
            string family = null, src = null, weight = null, style = null;
            foreach (var raw in SplitTop(StripComments(body), ';'))
            {
                string d = raw.Trim();
                if (d.Length == 0) { continue; }
                int colon = d.IndexOf(':');
                if (colon <= 0) { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face: '{Clip(d)}' is not a descriptor")); continue; }
                string name = d.Substring(0, colon).Trim().ToLowerInvariant(), value = d.Substring(colon + 1).Trim();
                switch (name)
                {
                    case "font-family": family = value; break;
                    case "src": src = value; break;
                    case "font-weight": weight = value; break;
                    case "font-style": style = value; break;
                    default: sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face: descriptor {name} is not supported, it is ignored")); break;
                }
            }
            if (string.IsNullOrWhiteSpace(family)) { sheet.Diagnostics.Add(new Diagnostic(file, line, "@font-face needs font-family, block skipped")); return; }
            var rule = new FontFaceRule { Family = Unquote(family), Line = line };
            if (FontFamilies.SplitList(family).Count != 1) { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face: font-family '{Clip(family)}' must be one name, block skipped")); return; }
            rule.Weight = ParseFaceWeight(weight, out string weightProblem);
            if (weightProblem != null) { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face {rule.Family}: {weightProblem}")); }
            string st = (style ?? "").Trim().ToLowerInvariant();
            if (st == "italic" || st.StartsWith("oblique", StringComparison.Ordinal)) { rule.Italic = true; }
            else if (st.Length > 0 && st != "normal") { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face {rule.Family}: font-style '{Clip(style.Trim())}' is not normal, italic or oblique, normal is used")); }
            if (string.IsNullOrWhiteSpace(src)) { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face {rule.Family} needs a src url(...), block skipped")); return; }
            foreach (var part in SplitTop(src, ','))
            {
                string p = part.Trim();
                var m = FontUrl.Match(p);
                if (m.Success && m.Groups["u"].Value.Trim().Length > 0) { rule.Sources.Add(m.Groups["u"].Value.Trim()); continue; }
                string why = p.StartsWith("local(", StringComparison.OrdinalIgnoreCase) ? "local() fonts are not supported" : "is not url(\"...\")";
                sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face {rule.Family}: src '{Clip(p)}' {why}, skipped"));
            }
            if (rule.Sources.Count == 0) { sheet.Diagnostics.Add(new Diagnostic(file, line, $"@font-face {rule.Family}: no usable src, block skipped")); return; }
            sheet.FontFaces.Add(rule);
        }

        /// <summary>The font-weight of an @font-face: normal, bold or 1..1000. A range (two values) keeps its first. Anything else is 400 and a problem text.</summary>
        private static int ParseFaceWeight(string value, out string problem)
        {
            problem = null;
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "normal") { return 400; }
            if (v == "bold") { return 700; }
            var parts = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)
                || double.IsNaN(n) || n < 1 || n > 1000)
            {
                problem = $"font-weight '{Clip(value.Trim())}' is not normal, bold or 1..1000, 400 is used";
                return 400;
            }
            if (parts.Length > 1) { problem = $"font-weight '{Clip(value.Trim())}': a range is not supported, {parts[0]} is used"; }
            return (int)Math.Round(n);
        }

        /// <summary>A family name as written: quotes removed, inner blanks collapsed (the case is kept for the messages).</summary>
        private static string Unquote(string value)
        {
            string t = (value ?? "").Trim();
            if (t.Length >= 2 && (t[0] == '"' || t[0] == '\'') && t[t.Length - 1] == t[0]) { t = t.Substring(1, t.Length - 2); }
            return string.Join(" ", t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>@keyframes name { from { ... } 40%, 60% { ... } to { ... } }: stops with their declarations, in the order written.</summary>
        private static void ParseKeyframes(string name, string body, StyleSheet sheet, string file, int firstLine)
        {
            if (name.Length == 0) { sheet.Diagnostics.Add(new Diagnostic(file, firstLine, "@keyframes needs a name")); return; }
            var rule = new KeyframesRule { Name = name, Line = firstLine };
            int pos = 0;
            while (pos < body.Length)
            {
                int brace = body.IndexOf('{', pos);
                if (brace < 0) { break; }
                string head = body.Substring(pos, brace - pos).Trim();
                int close = MatchingBrace(body, brace);
                string inner = body.Substring(brace + 1, (close < 0 ? body.Length : close) - brace - 1);
                pos = close < 0 ? body.Length : close + 1;
                var decls = new List<Declaration>();
                ParseDeclarations(inner, decls, (l, m) => sheet.Diagnostics.Add(new Diagnostic(file, l, m)), firstLine, file);
                foreach (var part in SplitTop(head, ','))
                {
                    string p = part.Trim().ToLowerInvariant();
                    float offset;
                    if (p == "from") { offset = 0; }
                    else if (p == "to") { offset = 1; }
                    else if (p.EndsWith("%", StringComparison.Ordinal)
                        && float.TryParse(p.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pc) && pc >= 0 && pc <= 100)
                    {
                        offset = pc / 100f;
                    }
                    else { sheet.Diagnostics.Add(new Diagnostic(file, firstLine, $"@keyframes {name}: '{Clip(p)}' is not a stop (from, to or NN%)")); continue; }
                    var stop = new KeyStop { Offset = offset };
                    stop.Declarations.AddRange(decls);
                    rule.Stops.Add(stop);
                }
            }
            sheet.Keyframes.Add(rule);
        }

        /// <summary>The body of a style="" attribute or a rule block.</summary>
        public static void ParseDeclarations(string body, List<Declaration> into, Action<string> warn, int line = 1)
            => ParseDeclarations(body, into, (_, m) => warn(m), line, "style");

        /// <summary>As above; warn gets the line of the declaration itself.</summary>
        public static void ParseDeclarations(string body, List<Declaration> into, Action<int, string> warn, int line, string file)
        {
            foreach (var raw in SplitTop(StripComments(body), ';'))
            {
                string d = raw.Trim();
                int declLine = line;
                bool lead = true;
                foreach (char ch in raw)
                {
                    if (ch == '\n') { line++; if (lead) { declLine++; } }
                    else if (!char.IsWhiteSpace(ch)) { lead = false; }
                }
                if (d.Length == 0) { continue; }
                void Warn(string m) => warn(declLine, m);
                int colon = d.IndexOf(':');
                if (colon <= 0) { Warn($"'{Clip(d)}' is not a declaration"); continue; }
                string prop = d.Substring(0, colon).Trim();
                string value = d.Substring(colon + 1).Trim();
                bool important = false;
                int bang = value.LastIndexOf('!');
                if (bang >= 0 && value.Substring(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    important = true;
                    value = value.Substring(0, bang).Trim();
                }
                if (!prop.StartsWith("--", StringComparison.Ordinal)) { prop = prop.ToLowerInvariant(); }
                if (value.Length == 0) { Warn($"{prop} has no value"); continue; }
                if (!prop.StartsWith("--", StringComparison.Ordinal) && !StyleEngine.IsKnownProperty(prop))
                {
                    Warn($"unknown property {prop}");
                    continue;
                }
                into.Add(new Declaration { Property = prop, Value = value, Important = important, Line = declLine, File = file });
            }
        }

        public static Selector ParseSelector(string text, out string error)
        {
            error = null;
            var sel = new Selector { Text = text };
            int i = 0;
            Combinator? pending = null;
            int ids = 0, classes = 0, tags = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { pending ??= Combinator.Descendant; i++; continue; }
                if (c == '>' || c == '+' || c == '~')
                {
                    if (pending != null && pending != Combinator.Descendant) { error = $"two combinators in a row (before '{c}') are not supported"; return null; }
                    pending = c == '>' ? Combinator.Child : c == '+' ? Combinator.Adjacent : Combinator.Sibling;
                    i++;
                    continue;
                }
                if (sel.PseudoElement != null) { error = "a pseudo-element ends the selector (nothing may follow ::before or ::after)"; return null; }
                var comp = new Compound();
                bool any = false;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '+' && text[i] != '~')
                {
                    char k = text[i];
                    if (sel.PseudoElement != null) { error = "a pseudo-element ends the selector (nothing may follow ::before or ::after in the same compound)"; return null; }
                    if (k == '*') { i++; any = true; continue; }
                    if (k == '[')
                    {
                        int end = text.IndexOf(']', i);
                        if (end < 0) { error = "'[' is not closed"; return null; }
                        string inner = text.Substring(i + 1, end - i - 1).Trim();
                        int eq = inner.IndexOf('=');
                        string an = (eq < 0 ? inner : inner.Substring(0, eq)).Trim().ToLowerInvariant();
                        string av = eq < 0 ? null : inner.Substring(eq + 1).Trim().Trim('"', '\'');
                        if (an.Length == 0 || (eq > 0 && "~|^$*".IndexOf(inner[eq - 1]) >= 0))
                        {
                            error = $"attribute selector [{inner}]: only [name] and [name=value] are supported";
                            return null;
                        }
                        comp.Attributes.Add((an, av));
                        classes++;
                        i = end + 1;
                        any = true;
                        continue;
                    }
                    if (k == '#' || k == '.' || k == ':')
                    {
                        i++;
                        bool dbl = k == ':' && i < text.Length && text[i] == ':';
                        if (dbl) { i++; }
                        string name = Ident(text, ref i);
                        if (name.Length == 0) { error = $"empty name after '{k}'"; return null; }
                        if (k == ':' && name.ToLowerInvariant() is "before" or "after")
                        {
                            // ::before / ::after (and the legacy :before / :after): the rule styles the pseudo-element of the element the compound matches
                            if (sel.PseudoElement != null) { error = "one pseudo-element per selector"; return null; }
                            sel.PseudoElement = name.ToLowerInvariant();
                            tags++;
                            any = true;
                            continue;
                        }
                        if (dbl) { error = $"pseudo-element ::{name} is not supported (::before and ::after are)"; return null; }
                        if (k == '#') { comp.Id = name; ids++; }
                        else if (k == '.') { comp.Classes.Add(name); classes++; }
                        else
                        {
                            name = name.ToLowerInvariant();
                            if (name == "has" && i < text.Length && text[i] == '(')
                            {
                                int end = CloseParen(text, i);
                                if (end < 0) { error = ":has( is not closed"; return null; }
                                var rel = ParseRelative(text.Substring(i + 1, end - i - 1).Trim(), out error);
                                if (rel == null) { return null; }
                                comp.Has.Add(rel);
                                ids += rel.Specificity.Ids;       // CSS: :has() weighs its argument
                                classes += rel.Specificity.Classes;
                                tags += rel.Specificity.Tags;
                                i = end + 1;
                                any = true;
                                continue;
                            }
                            if (name == "nth-child" && i < text.Length && text[i] == '(')
                            {
                                int end = text.IndexOf(')', i);
                                if (end < 0) { error = "nth-child( is not closed"; return null; }
                                name += text.Substring(i, end - i + 1).Replace(" ", "");
                                i = end + 1;
                            }
                            else if (name == "not" && i < text.Length && text[i] == '(')
                            {
                                int end = CloseParen(text, i);
                                if (end < 0) { error = "not( is not closed"; return null; }
                                string arg = text.Substring(i + 1, end - i - 1).Trim();
                                var inner = arg.Length == 0 || arg.IndexOf(',') >= 0 ? null : ParseSelector(arg, out error);
                                if (error != null) { return null; }   // the argument's own problem (an unknown pseudo-class, ...)
                                if (inner == null || inner.Parts.Count != 1 || inner.Parts[0].Nots.Count > 0 || inner.PseudoElement != null)
                                {
                                    error = $"pseudo-class :not({Clip(arg)}) is not supported (one compound without combinators, lists, a nested :not or a pseudo-element)";
                                    return null;
                                }
                                comp.Nots.Add(inner.Parts[0]);
                                ids += inner.Specificity.Ids;       // CSS: :not() weighs what its argument weighs
                                classes += inner.Specificity.Classes;
                                tags += inner.Specificity.Tags;
                                i = end + 1;
                                any = true;
                                continue;
                            }
                            else if (!Compound.KnownPseudos.Contains(name)) { error = $"pseudo-class :{name} is not supported"; return null; }
                            comp.Pseudos.Add(name);
                            classes++;
                        }
                        any = true;
                        continue;
                    }
                    string tag = Ident(text, ref i);
                    if (tag.Length == 0) { error = $"unexpected '{k}'"; return null; }
                    comp.Tag = tag.ToLowerInvariant();
                    tags++;
                    any = true;
                }
                if (!any) { error = "empty step"; return null; }
                if (sel.Parts.Count > 0) { sel.Combinators.Add(pending ?? Combinator.Descendant); }
                else if (pending != null && pending != Combinator.Descendant) { error = pending == Combinator.Child ? "starts with '>'" : "starts with a combinator"; return null; }
                pending = null;
                sel.Parts.Add(comp);
            }
            if (sel.Parts.Count == 0) { error = "empty selector"; return null; }
            if (pending != null && pending != Combinator.Descendant) { error = "ends with a combinator"; return null; }
            sel.Specificity = new Specificity(ids, classes, tags);
            return sel;
        }

        /// <summary>The argument of :has(): one relative selector, optionally led by > + ~ (none: descendants). Lists, nested :has() and pseudo-elements are not supported.</summary>
        private static Selector ParseRelative(string arg, out string error)
        {
            error = null;
            if (arg.Length == 0) { error = ":has() needs a selector"; return null; }
            if (arg.IndexOf(',') >= 0) { error = $":has({Clip(arg)}): a list of selectors is not supported"; return null; }
            var lead = Combinator.Descendant;
            if (arg[0] is '>' or '+' or '~')
            {
                lead = arg[0] == '>' ? Combinator.Child : arg[0] == '+' ? Combinator.Adjacent : Combinator.Sibling;
                arg = arg.Substring(1).Trim();
                if (arg.Length == 0) { error = $":has({lead} ...) needs a selector after the combinator"; return null; }
            }
            if (arg.IndexOf(":has(", StringComparison.OrdinalIgnoreCase) >= 0) { error = $":has({Clip(arg)}): a nested :has() is not supported"; return null; }
            var sel = ParseSelector(arg, out error);
            if (sel == null) { return null; }
            if (sel.PseudoElement != null) { error = $":has({Clip(arg)}): a pseudo-element is not allowed here"; return null; }
            sel.Lead = lead;
            return sel;
        }

        /// <summary>Index of the ')' that closes the '(' at open (nested parentheses and [..] are skipped), or -1.</summary>
        private static int CloseParen(string s, int open)
        {
            int depth = 0;
            for (int k = open; k < s.Length; k++)
            {
                if (s[k] == '[') { int b = s.IndexOf(']', k); if (b < 0) { return -1; } k = b; }
                else if (s[k] == '(') { depth++; }
                else if (s[k] == ')' && --depth == 0) { return k; }
            }
            return -1;
        }

        private static string Ident(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) { i++; }
            return s.Substring(start, i - start);
        }

        private static string StripComments(string css)
        {
            if (css.IndexOf("/*", StringComparison.Ordinal) < 0) { return css; }
            var sb = new StringBuilder(css.Length);
            int i = 0;
            while (i < css.Length)
            {
                int start = css.IndexOf("/*", i, StringComparison.Ordinal);
                if (start < 0) { sb.Append(css, i, css.Length - i); break; }
                sb.Append(css, i, start - i);
                int end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
                string comment = end < 0 ? css.Substring(start) : css.Substring(start, end + 2 - start);
                foreach (char ch in comment) { if (ch == '\n') { sb.Append('\n'); } }   // keep line numbers
                i = end < 0 ? css.Length : end + 2;
            }
            return sb.ToString();
        }

        private static int MatchingBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') { depth++; }
                else if (s[i] == '}' && --depth == 0) { return i; }
            }
            return -1;
        }

        /// <summary>Split on a separator outside parentheses and quotes.</summary>
        internal static List<string> SplitTop(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) { quote = '\0'; } continue; }
                if (c == '"' || c == '\'') { quote = c; }
                else if (c == '(') { depth++; }
                else if (c == ')') { depth = Math.Max(0, depth - 1); }
                else if (c == sep && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static string Clip(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }
}
