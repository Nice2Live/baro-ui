using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>Resolved style of one element: declared values after the cascade, inheritance and var(); typed
    /// getters on top. Shared between elements with the same parent style and the same matched declarations.</summary>
    public sealed class ComputedStyle
    {
        internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Vars = new(StringComparer.Ordinal);
        private readonly float unitPx;
        public float FontSizePx { get; internal set; }

        internal ComputedStyle(float unitPx) { this.unitPx = unitPx; }

        /// <summary>The value as written after var() substitution, or the property's initial value.</summary>
        public string Get(string property) =>
            Values.TryGetValue(property, out var v) ? v : StyleEngine.Initial.TryGetValue(property, out var i) ? i : "";

        public string Var(string name) => Vars.TryGetValue(name, out var v) ? v : null;

        // layout asks for the same lengths many times per element; styles are shared, so parse once per style
        private readonly Dictionary<string, Length> lengthCache = new(StringComparer.Ordinal);

        /// <summary>em and u become px here; %, fr, auto and unitless numbers stay for the layout.</summary>
        public Length GetLength(string property)
        {
            if (lengthCache.TryGetValue(property, out var l)) { return l; }
            l = ToPx(Get(property));
            lengthCache[property] = l;
            return l;
        }

        public Length ToPx(string value)
        {
            if (!Length.TryParse(value, out var l)) { return Length.Auto; }
            return l.Unit switch
            {
                Unit.Em => new Length(l.Value * FontSizePx, Unit.Px),
                Unit.U => new Length(l.Value * unitPx, Unit.Px),
                _ => l,
            };
        }

        // the renderer asks for colours every frame; parsing allocates, so parse once per style
        private readonly Dictionary<string, Rgba> colorCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Length> firstLengthCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (bool, Rgba)> firstColorCache = new(StringComparer.Ordinal);

        public Rgba GetColor(string property)
        {
            if (colorCache.TryGetValue(property, out var c)) { return c; }
            if (!Rgba.TryParse(Get(property), out c)) { c = Rgba.Transparent; }
            colorCache[property] = c;
            return c;
        }

        /// <summary>First value of a list property ("4px 8px" -> 4px), em/u in px.</summary>
        public Length GetFirstLength(string property)
        {
            if (firstLengthCache.TryGetValue(property, out var l)) { return l; }
            var parts = StyleEngine.SplitSpaces(Get(property));
            l = parts.Count > 0 ? ToPx(parts[0]) : Length.Auto;
            firstLengthCache[property] = l;
            return l;
        }

        /// <summary>First value of a list property as a colour; false when it is not one ("auto").</summary>
        public bool TryGetFirstColor(string property, out Rgba color)
        {
            if (!firstColorCache.TryGetValue(property, out var r))
            {
                var parts = StyleEngine.SplitSpaces(Get(property));
                r.Item1 = parts.Count > 0 && Rgba.TryParse(parts[0], out r.Item2);
                firstColorCache[property] = r;
            }
            color = r.Item2;
            return r.Item1;
        }

        // flex factors, order and opacity are read per box per layout or frame: parse once per style
        private readonly Dictionary<string, float> numberCache = new(StringComparer.Ordinal);

        public float GetNumber(string property, float fallback = 0)
        {
            if (!numberCache.TryGetValue(property, out float f))
            {
                f = float.TryParse(Get(property), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
                numberCache[property] = f;
            }
            return float.IsNaN(f) ? fallback : f;
        }

        // animated values (Animator): written over the typed caches, so layout and paint see them without any string work
        private Dictionary<string, (float, float)> pairCache;

        /// <summary>A pair of px lengths ("10u 4u" -> (10u, 4u) in px; one value is x only; none and unparsable are zero).</summary>
        public (float X, float Y) GetPair(string property)
        {
            if (pairCache != null && pairCache.TryGetValue(property, out var p)) { return p; }
            var parts = StyleEngine.SplitSpaces(Get(property));
            float x = 0, y = 0;
            if (parts.Count > 0 && !parts[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var lx = ToPx(parts[0]);
                var ly = parts.Count > 1 ? ToPx(parts[1]) : Length.Zero;
                if (lx.Unit == Unit.Px) { x = lx.Value; }
                if (ly.Unit == Unit.Px) { y = ly.Value; }
            }
            p = (x, y);
            (pairCache ??= new Dictionary<string, (float, float)>(StringComparer.Ordinal))[property] = p;
            return p;
        }

        internal void SetNumber(string property, float value) => numberCache[property] = value;
        internal void SetColor(string property, Rgba value) => colorCache[property] = value;
        internal void SetLength(string property, Length value) => lengthCache[property] = value;
        internal void SetPair(string property, float x, float y) => (pairCache ??= new Dictionary<string, (float, float)>(StringComparer.Ordinal))[property] = (x, y);

        /// <summary>Back to the declared value (the typed caches fill again from Values).</summary>
        internal void ClearOverride(string property)
        {
            numberCache.Remove(property);
            colorCache.Remove(property);
            lengthCache.Remove(property);
            pairCache?.Remove(property);
        }

        /// <summary>A private copy for one animating element: the same declared values, empty caches, no font slot.</summary>
        internal ComputedStyle CloneBare()
        {
            var c = new ComputedStyle(unitPx) { FontSizePx = FontSizePx };
            foreach (var kv in Values) { c.Values[kv.Key] = kv.Value; }
            foreach (var kv in Vars) { c.Vars[kv.Key] = kv.Value; }
            return c;
        }

        /// <summary>The transition and animation lists of this style (Animator.SpecOf), parsed once.</summary>
        internal AnimSpec Spec;
        internal bool SpecParsed;

        /// <summary>line-height in px for this style and measurer (cached: one measurer per window).</summary>
        internal float LineHeightPx = float.NaN;
        internal object LineHeightOf;
        /// <summary>The renderer's font for this style (UiFonts): lives and dies with the style, no global table.</summary>
        internal object FontSlot;

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var kv in Values) { sb.Append(kv.Key).Append(':').Append(kv.Value).Append("; "); }
            return sb.ToString();
        }
    }

    /// <summary>The cascade (docs/ui/ENGINE_PLAN.md, stage 1): UA sheet, author sheets in order, inline style="";
    /// !important; specificity; inheritance; custom properties with var(name, fallback); inherit/initial/unset.</summary>
    public sealed class StyleEngine
    {
        public const string UserAgentCss = @"
:root { display: block; color: #ffffff; font-size: 16px; font-family: mono; line-height: 1.3; }
html, body, div, p, h1, h2, h3, h4, section, header, footer, nav, main, aside, ul, ol, li, table, dialog, form { display: block; }
tr { display: flex; } td, th { display: block; flex: 1 1 0; }
span, b, i, em, strong, a, label { display: inline; }
img, video, item, progress, button, input, select, canvas, slot, textarea { display: inline-block; }
curve, chart, gauge { display: block; }
b, strong, h1, h2, h3, h4, th { font-weight: bold; }
i, em { font-style: italic; }
h1 { font-size: 2em; } h2 { font-size: 1.5em; } h3 { font-size: 1.17em; }
head, title, meta, link, style, script, option, template { display: none; }
input, select { width: 12em; height: 1.8em; padding: 0 0.4em; border: 1px solid #4a6a6c; border-radius: 2px;
  background-color: #0b1418; color: inherit; white-space: pre; overflow: hidden; cursor: text; }
select { cursor: pointer; padding-right: 1.4em; }
input:focus, select:focus { border-color: #40c8be; }
input[type=checkbox] { width: 1em; height: 1em; padding: 0; cursor: pointer; }
input[type=checkbox]:checked { background-color: #1d3a3f; }
input[type=radio] { width: 1em; height: 1em; padding: 0; border-radius: 50%; cursor: pointer; }
input[type=radio]:checked { background-color: #1d3a3f; }
[data-icon] { font-family: lucide; font-weight: normal; font-style: normal; letter-spacing: normal; text-decoration: none; }
input[type=range] { width: 12em; height: 1.4em; padding: 0; border-width: 0; background-color: transparent; cursor: pointer; }
input:disabled, select:disabled { opacity: 0.5; cursor: auto; }
textarea { width: 30em; height: 12em; padding: 0.2em 0.3em; border: 1px solid #4a6a6c; border-radius: 2px; background-color: #0b1418; color: inherit;
  white-space: pre; overflow: hidden; cursor: text; }
textarea:focus { border-color: #40c8be; }
textarea:disabled { opacity: 0.5; cursor: auto; }
progress { width: 10em; height: 0.5em; background-color: #1d3a3f; border-radius: 2px; overflow: hidden; }
item { width: 40u; height: 40u; }
canvas { width: 300u; height: 150u; }
dialog { display: none; position: absolute; z-index: 1000; }
dialog[open] { display: block; }
";

        internal static readonly Dictionary<string, string> Initial = new(StringComparer.Ordinal)
        {
            ["display"] = "inline", ["position"] = "static", ["top"] = "auto", ["right"] = "auto", ["bottom"] = "auto", ["left"] = "auto",
            ["width"] = "auto", ["height"] = "auto", ["min-width"] = "auto", ["min-height"] = "auto", ["max-width"] = "none", ["max-height"] = "none",
            ["margin-top"] = "0", ["margin-right"] = "0", ["margin-bottom"] = "0", ["margin-left"] = "0",
            ["padding-top"] = "0", ["padding-right"] = "0", ["padding-bottom"] = "0", ["padding-left"] = "0",
            ["border-top-width"] = "0", ["border-right-width"] = "0", ["border-bottom-width"] = "0", ["border-left-width"] = "0",
            ["border-top-color"] = "currentcolor", ["border-right-color"] = "currentcolor", ["border-bottom-color"] = "currentcolor", ["border-left-color"] = "currentcolor",
            ["border-top-left-radius"] = "0", ["border-top-right-radius"] = "0", ["border-bottom-right-radius"] = "0", ["border-bottom-left-radius"] = "0",
            ["border-style"] = "solid", ["box-sizing"] = "border-box",
            ["flex-direction"] = "row", ["flex-wrap"] = "nowrap", ["flex-grow"] = "0", ["flex-shrink"] = "1", ["flex-basis"] = "auto",
            ["row-gap"] = "0", ["column-gap"] = "0", ["align-items"] = "stretch", ["align-self"] = "auto", ["align-content"] = "normal", ["justify-content"] = "flex-start",
            ["grid-template-columns"] = "none", ["grid-template-rows"] = "none", ["grid-column"] = "auto", ["order"] = "0",
            ["overflow-x"] = "visible", ["overflow-y"] = "visible", ["background-color"] = "transparent", ["background-image"] = "none",
            // pictures (Core/ImageLayout.cs ImageCss, Core/NineSlice.cs): background-size/position/repeat, object-fit for <img>, border-image longhands; none inherited
            ["background-size"] = "auto", ["background-position"] = "0% 0%", ["background-repeat"] = "repeat", ["object-fit"] = "fill",
            ["border-image-source"] = "none", ["border-image-slice"] = "100%", ["border-image-width"] = "1", ["border-image-repeat"] = "stretch",
            ["color"] = "#000000", ["opacity"] = "1", ["font-family"] = "mono", ["font-size"] = "16px", ["font-weight"] = "normal",
            ["font-style"] = "normal", ["text-align"] = "left", ["line-height"] = "normal", ["white-space"] = "normal",
            ["text-overflow"] = "clip", ["text-transform"] = "none", ["letter-spacing"] = "normal", ["visibility"] = "visible",
            ["z-index"] = "auto", ["cursor"] = "auto", ["vertical-align"] = "baseline",
            ["pointer-events"] = "auto", ["scrollbar-color"] = "auto", ["scrollbar-width"] = "auto",
            ["accent-color"] = "#40c8be", ["caret-color"] = "auto",
            ["text-shadow"] = "none", ["box-shadow"] = "none", ["outline-width"] = "medium", ["outline-style"] = "none", ["outline-color"] = "currentcolor", ["outline-offset"] = "0",
            ["transition"] = "none", ["animation"] = "none", ["translate"] = "0", ["typewriter"] = "none",
            // scale: about the top-left of the border box (see UiTransform); transform-origin is known only so a page written for a browser raises no warning, it has no effect
            ["scale"] = "1", ["transform-origin"] = "0 0",
            ["transition-property"] = "all", ["transition-duration"] = "0s", ["transition-timing-function"] = "ease", ["transition-delay"] = "0s",
            ["animation-name"] = "none", ["animation-duration"] = "0s", ["animation-timing-function"] = "ease", ["animation-delay"] = "0s",
            ["animation-iteration-count"] = "1", ["animation-direction"] = "normal", ["animation-fill-mode"] = "none",
            ["layout-fps"] = "auto",
            // line-clamp: n | none, not inherited; overflow-wrap: normal | break-word | anywhere; -bbe-ru-glue: auto | on | off (Core/RuText.cs);
            // -webkit-box-orient is known so a page copied from the web raises no warning, it has no effect
            ["line-clamp"] = "none", ["overflow-wrap"] = "normal", ["-bbe-ru-glue"] = "auto", ["-webkit-box-orient"] = "horizontal",
            // word-spacing and text-indent inherit; text-decoration-* do not (Core/TextDecor.cs; text-decoration is a shorthand)
            ["word-spacing"] = "normal", ["text-indent"] = "0",
            ["text-decoration-line"] = "none", ["text-decoration-color"] = "currentcolor", ["text-decoration-style"] = "solid", ["text-decoration-thickness"] = "auto",
            // content: only on ::before / ::after (a string, attr(name), icon(name), concatenated; none or normal: no box); not inherited
            ["content"] = "none",
            // the frame of a game inventory cell (<slot>): game (the game's slot picture) | none (the page's own background and border show) | url(file); not inherited
            ["-bbe-slot-frame"] = "game",
        };

        private static readonly HashSet<string> Inherited = new(StringComparer.Ordinal)
        {
            "color", "font-family", "font-size", "font-weight", "font-style", "text-align", "line-height", "white-space",
            "visibility", "text-transform", "letter-spacing", "cursor", "pointer-events", "scrollbar-color", "accent-color", "caret-color",
            "typewriter", "text-shadow", "overflow-wrap", "-bbe-ru-glue", "word-spacing", "text-indent",
        };

        private static readonly string[] SpacingProps = { "letter-spacing", "word-spacing", "text-indent" };

        internal static readonly HashSet<string> Shorthands = new(StringComparer.Ordinal)
        {
            "margin", "padding", "border", "border-width", "border-color", "border-style", "border-top", "border-right",
            "border-bottom", "border-left", "border-radius", "flex", "gap", "overflow", "background", "border-image", "flex-flow", "outline", "word-wrap", "-webkit-line-clamp", "text-decoration",
        };

        public static bool IsKnownProperty(string p) => Initial.ContainsKey(p) || Shorthands.Contains(p);

        /// <summary>Every property the engine reads (longhands, then shorthands): docs/ui/AUTHORING.md lists them all.</summary>
        public static IEnumerable<string> KnownProperties
        {
            get
            {
                foreach (var k in Initial.Keys) { yield return k; }
                foreach (var k in Shorthands) { yield return k; }
            }
        }

        public readonly List<Diagnostic> Diagnostics = new();
        /// <summary>The @keyframes of every sheet, by name (a later one replaces an earlier one).</summary>
        internal readonly Dictionary<string, KeyframesDef> KeyframeDefs = new(StringComparer.Ordinal);
        /// <summary>Transitions and animations (a window sets it): null styles elements as plain CSS without motion.</summary>
        internal Animator Animator;
        /// <summary>Pixels per 1u (min(width / 1600, height / 900) × HUD scale, UiWindow). Changing it needs a new Compute.</summary>
        public float UnitPx
        {
            get => unitPx;
            set { if (value != unitPx) { unitPx = value; Renew(); } }
        }
        private float unitPx = 1f;

        private readonly List<(Selector Sel, Rule Rule, int Order)> selectors = new();

        /// <summary>The selector index: every selector is filed once, by the key its last compound (the subject) must have on the element: its id, else its first class,
        /// else its tag, else "universal". The lists hold positions in selectors (= Order) in ascending order, so Resolve only tests the few selectors that can match.</summary>
        private readonly Dictionary<string, List<int>> byId = new(StringComparer.Ordinal), byClass = new(StringComparer.Ordinal), byTag = new(StringComparer.Ordinal);
        private readonly List<int> universal = new();
        /// <summary>Candidates of the element being resolved. Reused: Resolve does not call itself (Substitute, Expand and the cache never reach it) and an engine serves one thread.</summary>
        private readonly List<int> candidates = new();
        /// <summary>false: test every selector in order, as before the index. Only the tests flip it (they compare the two); the result must be the same.</summary>
        internal bool UseIndex = true;

        private void Index(Selector sel, int order)
        {
            var subject = sel.Parts[sel.Parts.Count - 1];
            List<int> list;
            if (subject.Id != null) { list = Bucket(byId, subject.Id); }
            else if (subject.Classes.Count > 0) { list = Bucket(byClass, subject.Classes[0]); }
            else if (subject.Tag != null) { list = Bucket(byTag, subject.Tag); }
            else { list = universal; }
            list.Add(order);
        }

        private static List<int> Bucket(Dictionary<string, List<int>> map, string key)
        {
            if (!map.TryGetValue(key, out var list)) { map[key] = list = new List<int>(); }
            return list;
        }

        /// <summary>The orders of the selectors that can match e, ascending (each selector sits in one bucket, so none twice).</summary>
        private List<int> CandidatesOf(Element e)
        {
            var c = candidates;
            c.Clear();
            c.AddRange(universal);
            if (byTag.TryGetValue(e.Tag, out var t)) { c.AddRange(t); }
            string id = e.Id;
            if (id != null && byId.TryGetValue(id, out var i)) { c.AddRange(i); }
            if (byClass.Count > 0) { foreach (var cls in (HashSet<string>)e.Classes) { if (byClass.TryGetValue(cls, out var l)) { c.AddRange(l); } } }
            c.Sort();
            return c;
        }

        /// <summary>One compound of one selector that reads an interaction state (hover, active, focus, focus-within, disabled; also inside :not()).
        /// Subject: the last compound, which styles the element itself. Otherwise what the combinators after it reach from e: the descendants of e (Descendants: the
        /// first combinator is a descendant or child one), or the following siblings of e (+, ~) and, when a descendant or child combinator follows, their subtrees.</summary>
        private readonly struct StateDep
        {
            public readonly Selector Sel;
            public readonly int Part;
            public readonly NodeState Bits;
            public readonly bool Subject;
            /// <summary>The first combinator after the part that is not + or ~ comes before any + or ~: the state reaches e's whole subtree (a later + or ~ stays inside it).</summary>
            public readonly bool Descendants;
            /// <summary>The state reaches the following siblings of e (not Subject, not Descendants).</summary>
            public readonly bool Siblings;
            /// <summary>Siblings: a descendant or child combinator follows the sibling ones, so the sibling subtrees are reached, else only the siblings themselves.</summary>
            public readonly bool SiblingSubtrees;
            /// <summary>Siblings: only + follows, this many of them: the siblings further than that cannot be reached. 0: any number (a ~ is there).</summary>
            public readonly int SiblingLimit;
            public StateDep(Selector sel, int part, NodeState bits)
            {
                Sel = sel; Part = part; Bits = bits; Subject = part == sel.Parts.Count - 1;
                Descendants = Siblings = SiblingSubtrees = false;
                SiblingLimit = 0;
                if (Subject) { return; }
                bool sibling = false, allAdjacent = true;
                for (int j = part; j < sel.Combinators.Count; j++)
                {
                    var c = sel.Combinators[j];
                    if (c == Combinator.Adjacent || c == Combinator.Sibling) { sibling = true; allAdjacent &= c == Combinator.Adjacent; continue; }
                    allAdjacent = false;
                    if (!sibling) { Descendants = true; return; }
                    SiblingSubtrees = true;
                    break;
                }
                Siblings = true;
                if (!SiblingSubtrees && allAdjacent) { SiblingLimit = sel.Combinators.Count - part; }
            }
        }

        /// <summary>The state index (ComputeState), filled by Add. A state of e reaches e (subject), e's descendants (a descendant or child combinator after it) and,
        /// with + and ~ (docs/ui/DESIGN_EVENTS.md 7b), the following siblings of e and, if the selector goes on with a descendant or child combinator, their subtrees.</summary>
        private readonly List<StateDep> stateDeps = new();

        /// <summary>The compounds that read the sibling position (:first-child, :last-child, :nth-child, also inside :not()), filled by Add. A change of the children
        /// of a container moves the position only of its own children, so only a child such a compound can match (ignoring the position) needs a restyle.</summary>
        private readonly List<Compound> positional = new();

        /// <summary>The compounds that come right after a + or ~ (the earlier sibling a rule asks about), filled by Add. An insert, a remove or a move among the
        /// children of a container can change whether such a compound has its earlier sibling, so the children it can match (ignoring state and position) are
        /// restyled with their subtrees.</summary>
        private readonly List<Compound> siblinged = new();

        /// <summary>Some selector reads the sibling position of an element (:first-child, :last-child, :nth-child) or its earlier siblings (+, ~).</summary>
        internal bool ReadsSiblingPosition => positional.Count > 0 || siblinged.Count > 0;

        /// <summary>Some selector uses + or ~: a class, attribute or style change on an element can then change its following siblings (FollowingReached).</summary>
        internal bool ReadsSiblingCombinators => siblinged.Count > 0;

        /// <summary>The element children after e that a + or ~ rule can reach from a change of e (class, attribute, style), for a caller that restyles by Compute(subtree):
        /// every following sibling a sibling-combinator compound can match (ignoring state and position). Their subtrees need a restyle too.</summary>
        internal void FollowingReached(Element e, List<Element> into)
        {
            if (siblinged.Count == 0 || e.Parent == null) { return; }
            var kids = e.Parent.Children;
            int at = kids.IndexOf(e);
            for (int k = at + 1; k < kids.Count; k++) { if (kids[k] is Element s && Reaches(siblinged, s)) { into.Add(s); } }
        }

        /// <summary>Like FollowingReached(e, into), for a caller that restyles a batch of changed elements after all the changes were made: a sibling already in
        /// <paramref name="covered"/> was restyled by an earlier call of the batch, so it is left out; the ones added are put in. Without this, a row of n changed siblings
        /// restyles the n-th one n times (n squared Compute calls). A sibling that is in covered needs no restyle of its own either, nor its followers: they were reached
        /// from the same earlier element (what a compound reaches depends on the follower, not on who changed).</summary>
        internal void FollowingReached(Element e, List<Element> into, HashSet<Element> covered)
        {
            if (siblinged.Count == 0 || e.Parent == null) { return; }
            var kids = e.Parent.Children;
            int at = kids.IndexOf(e);
            for (int k = at + 1; k < kids.Count; k++) { if (kids[k] is Element s && Reaches(siblinged, s) && covered.Add(s)) { into.Add(s); } }
        }

        private static bool ReadsPosition(Compound c)
        {
            foreach (var p in c.Pseudos) { if (p == "first-child" || p == "last-child" || p.StartsWith("nth-child(", StringComparison.Ordinal)) { return true; } }
            foreach (var n in c.Nots) { if (ReadsPosition(n)) { return true; } }
            return false;
        }

        /// <summary>Whether a compound that reads the sibling position can match e once its position is set aside (tag, id, classes and attributes only): its style
        /// may differ after a sibling came or went. A superset of what really changes, never less.</summary>
        internal bool SiblingPositionReaches(Element e) => Reaches(positional, e) || Reaches(siblinged, e);

        private static bool Reaches(List<Compound> compounds, Element e)
        {
            foreach (var c in compounds)
            {
                if (c.Tag != null && c.Tag != e.Tag) { continue; }
                if (c.Id != null && e.Id != c.Id) { continue; }
                bool ok = true;
                foreach (var cls in c.Classes) { if (!e.HasClass(cls)) { ok = false; break; } }
                if (ok) { foreach (var (name, value) in c.Attributes) { string v = e.GetAttribute(name); if (v == null || (value != null && v != value)) { ok = false; break; } } }
                if (ok) { return true; }
            }
            return false;
        }

        /// <summary>One compound that has a :has(): Part of Sel. Its match depends on elements other than the one it styles, so a change anywhere in the anchor's scope
        /// (a class, an attribute, a child, a state in Bits) restyles the anchor: see AnchorsOf. Subject: the compound is the last one (only the anchor itself changes,
        /// Refresh); else the combinators after it reach the anchor's subtree (ComputeTree), and with + or ~ its following siblings too.</summary>
        private readonly struct HasDep
        {
            public readonly Selector Sel;
            public readonly int Part;
            public readonly NodeState Bits;
            public readonly bool Subject;
            public readonly bool Siblings;
            public HasDep(Selector sel, int part, NodeState bits)
            {
                Sel = sel; Part = part; Bits = bits;
                Subject = part == sel.Parts.Count - 1;
                Siblings = false;
                for (int j = part; j < sel.Combinators.Count; j++) { if (sel.Combinators[j] is Combinator.Adjacent or Combinator.Sibling) { Siblings = true; } }
            }
        }

        private readonly List<HasDep> hasDeps = new();
        /// <summary>Some rule has a :has() (the anchors restyled after a change: ComputeHas). A page without one pays nothing for it.</summary>
        public bool ReadsHasSelectors => hasDeps.Count > 0;
        /// <summary>Some rule styles ::before or ::after (a pseudo-element box is made on an element once a rule gives it a content: PseudoBefore, PseudoAfter).</summary>
        private bool hasPseudo;

        /// <summary>The elements whose :has() can change when e changes (its class, attribute, structure or state): every strict ancestor of e, and the preceding
        /// siblings of e and of each of its ancestors (a + or ~ inside :has reaches back to them). Only those a :has() compound can match (ignoring state) are kept.
        /// Empty when no rule has :has(). The result is a superset: an anchor may be restyled although its style does not change.</summary>
        public List<Element> AnchorsOf(Element e, List<Element> into = null)
        {
            into ??= new List<Element>();
            if (hasDeps.Count == 0) { return into; }
            for (var x = e; x != null; x = x.Parent)
            {
                if (x != e) { AddAnchor(x, into); }
                if (x.Parent == null) { continue; }
                var kids = x.Parent.Children;
                for (int k = kids.IndexOf(x) - 1; k >= 0; k--) { if (kids[k] is Element p) { AddAnchor(p, into); } }
            }
            return into;
        }

        private void AddAnchor(Element a, List<Element> into)
        {
            if (into.Contains(a)) { return; }
            foreach (var d in hasDeps) { if (d.Sel.Parts[d.Part].MatchesIgnoringState(a)) { into.Add(a); return; } }
        }

        /// <summary>Restyles the anchors of e after a class, attribute or structure change of e (not a state change: ComputeState does that). restyled (when not null)
        /// gets the elements re-resolved with their old styles, for the layout check. Returns how many elements it re-resolved.</summary>
        public int ComputeHas(Element e, List<(Element E, ComputedStyle Old)> restyled)
        {
            if (hasDeps.Count == 0) { return 0; }
            var saved = trace;
            trace = restyled;
            try { return HasCore(e, NodeState.None); }
            finally { trace = saved; }
        }

        /// <summary>The anchors of e restyled for the state bits changed on e (ComputeStateCore calls it for every element whose bits changed).</summary>
        private int HasCore(Element e, NodeState changed)
        {
            int n = 0;
            anchorBuf.Clear();
            foreach (var a in AnchorsOf(e, anchorBuf))
            {
                bool whole = false, own = false, siblings = false;
                foreach (var d in hasDeps)
                {
                    if (changed != NodeState.None && (d.Bits & changed) == NodeState.None) { continue; }
                    if (!d.Sel.Parts[d.Part].MatchesIgnoringState(a)) { continue; }
                    if (d.Subject) { own = true; }
                    else { whole = true; siblings |= d.Siblings; }
                }
                if (whole) { n += ComputeTree(a); }
                else if (own) { n += Refresh(a); }
                if (siblings && a.Parent != null)
                {
                    var kids = a.Parent.Children;
                    for (int k = kids.IndexOf(a) + 1; k < kids.Count; k++) { if (kids[k] is Element s) { n += ComputeTree(s); } }
                }
            }
            anchorBuf.Clear();
            return n;
        }

        private readonly List<Element> anchorBuf = new();

        /// <summary>The style cache and the inherit keys of one rule set. Resolve fills them together on a miss, so they are kept and shared together.</summary>
        private sealed class StyleStore
        {
            public readonly TwoGenCache<(ComputedStyle, string), ComputedStyle> Cache = new(StyleCacheLimit);
            /// <summary>Properties that some declaration took from the parent with the keyword inherit (also non-inherited ones): the children read them from the parent's style.</summary>
            public readonly HashSet<string> InheritKeys = new(StringComparer.Ordinal);
        }

        private StyleStore store = new();
        /// <summary>The stores of the rule sets seen lately, by fingerprint (see Adopt). A page opened again, or reloaded unchanged, takes its warm cache.</summary>
        private static readonly Dictionary<string, StyleStore> SharedStores = new(StringComparer.Ordinal);
        private const int SharedStoreLimit = 4;
        /// <summary>The fingerprint of the rules and UnitPx this engine has now; null when they changed since Adopt.</summary>
        private string fingerprint;

        /// <summary>Drops the shared style caches: the next engine of a page cascades cold (for a measurement, or a page that must not keep its styles).</summary>
        public static void ForgetShared() => SharedStores.Clear();

        /// <summary>Takes the store of an engine with the same rules and UnitPx, or offers its own. The key is the whole rule set in order (selector text, declarations
        /// with their file and line), UnitPx and the UA count: a cached style is a pure function of those and of the parent style, so equal keys give equal styles.
        /// A style whose cascade reported a diagnostic is never cached (Resolve), so every open reports what a cold cascade reports.</summary>
        private void Adopt()
        {
            if (fingerprint != null) { return; }
            fingerprint = Fingerprint();
            if (!SharedStores.TryGetValue(fingerprint, out var shared))
            {
                if (SharedStores.Count >= SharedStoreLimit)
                {
                    string oldest = null;
                    foreach (var k in SharedStores.Keys) { oldest = k; break; }
                    SharedStores.Remove(oldest);
                }
                SharedStores[fingerprint] = store;
                return;
            }
            store = shared;
        }

        private string Fingerprint()
        {
            var sb = new StringBuilder();
            sb.Append(unitPx.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(uaCount).Append('\n');
            foreach (var (sel, rule, _) in selectors)
            {
                sb.Append(sel.Text).Append('{');
                foreach (var d in rule.Declarations)
                {
                    sb.Append(d.Important ? "!" : "").Append(d.Property).Append(':').Append(d.Value).Append('@').Append(d.File).Append(':').Append(d.Line).Append(';');
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        /// <summary>Starts a new store: the rules or UnitPx changed, so the old cache (possibly shared) no longer belongs to this engine.</summary>
        private void Renew()
        {
            store = new StyleStore();
            fingerprint = null;
        }

        /// <summary>Per generation limits of the two caches. Inline: a page that binds style="translate: {{x}}px" makes one string per frame, and the
        /// strings in use at once are few (hundreds), so 2048 holds a real page whole. Styles: one entry per (parent style, matched rules) and a new
        /// parent makes new entries for all its descendants, so a big page (500 cards, a few styles each) needs thousands; 8192 holds it with room.
        /// Two generations means at most twice that is alive, and what is in use keeps being promoted, so only what stopped being asked for goes.</summary>
        internal const int InlineCacheLimit = 2048, StyleCacheLimit = 8192;

        /// <summary>Entries held by the inline-style cache (both generations).</summary>
        internal int InlineCacheCount => inlineCache.Count;
        /// <summary>Entries held by the style cache (both generations).</summary>
        internal int StyleCacheCount => store.Cache.Count;

        private readonly TwoGenCache<string, List<Declaration>> inlineCache = new(InlineCacheLimit, StringComparer.Ordinal);   // Core/FontCache.cs

        /// <summary>Selectors below this order come from the UA sheet: every author rule beats them.</summary>
        private readonly int uaCount;

        public StyleEngine(params StyleSheet[] sheets)
        {
            Add(CssParser.Parse(UserAgentCss, "ua"));
            uaCount = selectors.Count;
            foreach (var s in sheets) { Add(s); }
        }

        public void Add(StyleSheet sheet)
        {
            Diagnostics.AddRange(sheet.Diagnostics);
            foreach (var k in sheet.Keyframes) { AddKeyframes(k, sheet.File); }
            foreach (var rule in sheet.Rules)
            {
                foreach (var sel in rule.Selectors)
                {
                    Index(sel, selectors.Count);
                    selectors.Add((sel, rule, selectors.Count));
                    if (sel.PseudoElement != null) { hasPseudo = true; }
                    for (int i = 0; i < sel.Parts.Count; i++)
                    {
                        var bits = sel.Parts[i].StateBits();
                        if (bits != NodeState.None) { stateDeps.Add(new StateDep(sel, i, bits)); }
                        if (ReadsPosition(sel.Parts[i])) { positional.Add(sel.Parts[i]); }
                        if (i > 0 && sel.Combinators[i - 1] is Combinator.Adjacent or Combinator.Sibling) { siblinged.Add(sel.Parts[i]); }
                        if (sel.Parts[i].ReadsHas()) { hasDeps.Add(new HasDep(sel, i, sel.Parts[i].HasBits())); }
                    }
                }
            }
            Renew();
        }

        /// <summary>Styles the whole document, &lt;style&gt; blocks included.</summary>
        public void Compute(Document doc)
        {
            foreach (var (css, line) in doc.Styles) { Add(CssParser.Parse(css, doc.File, line)); }
            doc.Styles.Clear();
            Adopt();
            // the style cache must hold a whole page in one generation: a page with more elements than the limit would rotate out entries still in use,
            // and every restyle would then miss and make new style objects (the map with its cards as blocks has over 20000 elements)
            int n = 0;
            foreach (var _ in doc.Root.Descendants()) { n++; }
            int want = n + n / 4;
            if (want > store.Cache.Limit) { store.Cache.Limit = want; }
            Compute(doc.Root);
        }

        /// <summary>Restyles one subtree (after a state, class or attribute change), reusing the parent's style.</summary>
        public void Compute(Element e) => ComputeTree(e);

        /// <summary>Compute(e) that counts the elements it restyled.</summary>
        private int ComputeTree(Element e)
        {
            int n = 1;
            ResolveOne(e);
            foreach (var c in e.ElementChildren()) { n += ComputeTree(c); }
            return n;
        }

        /// <summary>Resolves e alone against its parent's style and sets Style, BaseStyle; returns the old BaseStyle (null: never styled).</summary>
        private ComputedStyle ResolveOne(Element e)
        {
            var parent = e.Parent;
            var fresh = Resolve(e, parent?.BaseStyle ?? parent?.Style);
            var oldBase = e.BaseStyle;
            trace?.Add((e, e.Style));
            e.BaseStyle = fresh;
            e.Style = Animator == null ? fresh : Animator.Resolved(e, oldBase, fresh);
            if (hasPseudo)
            {
                ResolvePseudo(e, 1);
                ResolvePseudo(e, 2);
            }
            return oldBase;
        }

        /// <summary>Resolves the ::before (which 1) or ::after (2) box of host against host's style: the carrier element (PseudoBefore / PseudoAfter) gets the style and
        /// one text child, or no style when no rule gives a content. The carrier is not in host.Children: no query, template or handler sees it. Its style is a new
        /// object when the text changed (AffectsLayout compares the values, the text is one of them), else the same one (no relayout).</summary>
        private void ResolvePseudo(Element host, int which)
        {
            var src = Resolve(host, host.BaseStyle, which);
            string text = src == null ? null : PseudoContent(src, host);
            var box = which == 1 ? host.PseudoBefore : host.PseudoAfter;
            // the box is made only for an element that has the pseudo-element: a sheet with one ::before rule must not give every element of a big page two carriers
            if (box == null)
            {
                if (text == null) { return; }
                box = NewPseudoBox(host, which == 1 ? "::before" : "::after");
                if (which == 1) { host.PseudoBefore = box; } else { host.PseudoAfter = box; }
                PseudoBoxMade?.Invoke(host);
            }
            trace?.Add((box, box.Style));
            if (text == null)
            {
                box.BaseStyle = null;
                box.Style = null;
                box.PseudoSource = null;
                box.PseudoText = null;
                box.Children.Clear();
                return;
            }
            if (ReferenceEquals(src, box.PseudoSource) && text == box.PseudoText) { return; }
            var oldBase = box.BaseStyle;
            var st = src.CloneBare();
            st.Values[PseudoTextKey] = text;
            box.BaseStyle = st;
            box.Style = Animator == null ? st : Animator.Resolved(box, oldBase, st);
            box.PseudoSource = src;
            box.PseudoText = text;
            box.Children.Clear();
            if (text.Length > 0) { box.Append(new TextNode(text)); }
        }

        /// <summary>The value key of the evaluated content text in a pseudo-element's values (not a CSS property): a change of the text is a change of the style.</summary>
        internal const string PseudoTextKey = "-bbe-content-text";

        /// <summary>Told when an element gets its first ::before or ::after box (the window lays its element out again: the new box was in no style list compared by a restyle).</summary>
        public Action<Element> PseudoBoxMade;

        private static Element NewPseudoBox(Element host, string tag) => new(tag) { Parent = host, PseudoHost = host };

        private readonly StringBuilder contentBuf = new();
        private readonly HashSet<string> contentWarned = new(StringComparer.Ordinal);

        /// <summary>The text of a content value: quoted strings (CSS escapes \201C, \\, \" ...), attr(name) and icon(name) (a Lucide glyph, Core/Icons.cs),
        /// concatenated; null for none or normal. Anything else gives null and one diagnostic.</summary>
        private string PseudoContent(ComputedStyle st, Element host)
        {
            string raw = st.Get("content").Trim();
            if (raw.Length == 0 || raw == "none" || raw == "normal") { return null; }
            contentBuf.Clear();
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < raw.Length && raw[i] != c)
                    {
                        if (raw[i] != '\\') { contentBuf.Append(raw[i++]); continue; }
                        i++;
                        if (i >= raw.Length) { break; }
                        if (!HexDigit(raw[i])) { contentBuf.Append(raw[i++]); continue; }
                        int code = 0, n = 0;
                        while (i < raw.Length && n < 6 && HexDigit(raw[i])) { code = code * 16 + HexValue(raw[i]); i++; n++; }
                        if (i < raw.Length && char.IsWhiteSpace(raw[i])) { i++; }   // one space ends a hex escape
                        contentBuf.Append(code > 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
                    }
                    if (i >= raw.Length) { return ContentInvalid(raw, host); }
                    i++;
                    continue;
                }
                int open = raw.IndexOf('(', i);
                int close = open < 0 ? -1 : raw.IndexOf(')', open);
                if (open < 0 || close < 0) { return ContentInvalid(raw, host); }
                string fn = raw.Substring(i, open - i).Trim().ToLowerInvariant();
                string arg = raw.Substring(open + 1, close - open - 1).Trim().Trim('"', '\'').Trim();
                i = close + 1;
                if (fn == "attr") { contentBuf.Append(host.GetAttribute(arg.ToLowerInvariant()) ?? ""); }
                else if (fn == "icon") { if (Icons.TryGlyph(arg, out var glyph)) { contentBuf.Append(glyph); } }
                else { return ContentInvalid(raw, host); }
            }
            return contentBuf.ToString();
        }

        private string ContentInvalid(string raw, Element host)
        {
            if (contentWarned.Add(raw)) { Diagnostics.Add(new Diagnostic("css", host.Line, $"content: {raw} is not supported (a \"string\", attr(name), icon(name) or none)")); }
            return null;
        }

        private static bool HexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

        /// <summary>The state bits of e just changed (the caller has already set or cleared them in e.State): restyles what a rule can see of it and returns how
        /// many elements it re-resolved (0: no rule reads these bits of an element like e). Call it for EVERY element whose bits changed, not only the topmost:
        /// a descendant is restyled only when e's own rules reach it. Same computed values as Compute(Document) after the same change.</summary>
        public int ComputeState(Element e, NodeState changed) => ComputeState(e, changed, null, out _);

        /// <summary>As above; wholeSubtree is true when the whole subtree of e was restyled (a rule with e in ancestor position), so a changed descendant needs no call of its own.</summary>
        public int ComputeState(Element e, NodeState changed, out bool wholeSubtree) => ComputeState(e, changed, null, out wholeSubtree);

        /// <summary>As above; restyled (when not null) gets every element that was re-resolved with the Style it had before, for the layout check (UiWindow.AffectsLayout).</summary>
        public int ComputeState(Element e, NodeState changed, List<(Element E, ComputedStyle Old)> restyled, out bool wholeSubtree)
        {
            trace = restyled;
            try { return ComputeStateCore(e, changed, out wholeSubtree); }
            finally { trace = null; }
        }

        private List<(Element E, ComputedStyle Old)> trace;

        private int ComputeStateCore(Element e, NodeState changed, out bool wholeSubtree)
        {
            wholeSubtree = false;
            if (e.BaseStyle == null) { wholeSubtree = true; return ComputeTree(e); }   // never styled: nothing to patch
            bool subject = false;
            int n = 0;
            foreach (var d in stateDeps)
            {
                if ((d.Bits & changed) == NodeState.None || !d.Sel.Parts[d.Part].MatchesIgnoringState(e)) { continue; }
                if (d.Descendants) { wholeSubtree = true; }   // e is an ancestor part: any descendant may match the rest of the selector
                else if (d.Siblings) { n += RestyleFollowing(e, d); }   // e is before a + or ~: the siblings after it (they have their own parent style, e's restyle is separate)
                else { subject = true; }
            }
            if (hasDeps.Count > 0) { n += HasCore(e, changed); }   // :has(:hover) and the like: the ancestors and siblings e can change the match of
            if (wholeSubtree) { return n + ComputeTree(e); }
            return n + (subject ? Refresh(e) : 0);
        }

        /// <summary>The following element siblings of e that a + or ~ after e's compound can reach: the next SiblingLimit of them (only +) or all; each with its subtree
        /// when the selector goes on with a descendant or child combinator, else only those the subject compound can match. Returns how many it re-resolved.</summary>
        private int RestyleFollowing(Element e, StateDep d)
        {
            if (e.Parent == null) { return 0; }
            var kids = e.Parent.Children;
            var subjectPart = d.Sel.Parts[d.Sel.Parts.Count - 1];
            int n = 0, left = d.SiblingLimit;
            for (int k = kids.IndexOf(e) + 1; k < kids.Count; k++)
            {
                if (kids[k] is not Element s) { continue; }
                if (d.SiblingSubtrees) { n += ComputeTree(s); }
                else if (subjectPart.MatchesIgnoringState(s)) { n += Refresh(s); }
                if (d.SiblingLimit > 0 && --left == 0) { break; }
            }
            return n;
        }

        /// <summary>Re-resolves e, and its children only when what they take from e's style changed (they read nothing else of e): see InheritedSame.</summary>
        private int Refresh(Element e)
        {
            var oldBase = ResolveOne(e);
            int n = 1;
            if (oldBase != null && InheritedSame(oldBase, e.BaseStyle)) { return n; }
            foreach (var c in e.ElementChildren()) { n += Refresh(c); }
            return n;
        }

        /// <summary>Resolve(child, parent) reads of the parent: Vars, the Inherited properties' values, FontSizePx (em, %, inherited size), and the value of any property
        /// some declaration sets to inherit. The cache key is the parent style's identity only for speed: equal reads give equal child styles.</summary>
        private bool InheritedSame(ComputedStyle a, ComputedStyle b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            if (a.FontSizePx != b.FontSizePx || a.Vars.Count != b.Vars.Count) { return false; }
            foreach (var kv in a.Vars) { if (!b.Vars.TryGetValue(kv.Key, out var v) || v != kv.Value) { return false; } }
            foreach (var p in Inherited) { if (!SameValue(a, b, p)) { return false; } }
            foreach (var p in store.InheritKeys) { if (!SameValue(a, b, p)) { return false; } }
            return true;
        }

        private static bool SameValue(ComputedStyle a, ComputedStyle b, string p)
        {
            bool ha = a.Values.TryGetValue(p, out var va), hb = b.Values.TryGetValue(p, out var vb);
            return ha == hb && va == vb;
        }

        /// <summary>@keyframes: each stop's declarations expanded to longhands. var() is not substituted in them.</summary>
        private void AddKeyframes(KeyframesRule k, string file)
        {
            var def = new KeyframesDef { Name = k.Name };
            foreach (var stop in k.Stops)
            {
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var d in stop.Declarations)
                {
                    if (Shorthands.Contains(d.Property)) { Expand(d.Property, d.Value, props, d.Line, file); }
                    else { props[d.Property] = d.Value.Trim(); }
                }
                def.Stops.Add((stop.Offset, props));
            }
            KeyframeDefs[k.Name] = def;
        }

        // Resolve is never re-entered (nothing it calls reaches it, and Compute recurses only after it returns), so these buffers are shared
        private readonly List<int> matchedOrders = new();
        private readonly StringBuilder keyBuf = new();

        /// <summary>Which rules a style is resolved from: 0 the element (its rules have no pseudo-element), 1 its ::before, 2 its ::after (only those rules).</summary>
        private static bool PseudoFits(Selector sel, int pseudo) =>
            pseudo == 0 ? sel.PseudoElement == null : sel.PseudoElement == (pseudo == 1 ? "before" : "after");

        /// <summary>The style of e (pseudo 0) or of its ::before / ::after (1 / 2) from the parent style; null for a pseudo-element no rule matches.</summary>
        private ComputedStyle Resolve(Element e, ComputedStyle parent, int pseudo = 0)
        {
            // first the key (which rules match, and the inline style) and the cache: on a page almost every element is a hit, and the declarations of the
            // matched rules are only gathered on a miss
            matchedOrders.Clear();
            keyBuf.Clear();
            if (pseudo != 0) { keyBuf.Append(pseudo == 1 ? "B|" : "A|"); }
            if (UseIndex)
            {
                foreach (int order in CandidatesOf(e))
                {
                    var sel = selectors[order].Sel;
                    if (!PseudoFits(sel, pseudo) || !sel.Matches(e)) { continue; }
                    matchedOrders.Add(order);
                    keyBuf.Append(order).Append(',');
                }
            }
            else
            {
                foreach (var (sel, _, order) in selectors)
                {
                    if (!PseudoFits(sel, pseudo) || !sel.Matches(e)) { continue; }
                    matchedOrders.Add(order);
                    keyBuf.Append(order).Append(',');
                }
            }
            if (pseudo != 0 && matchedOrders.Count == 0) { return null; }   // no rule for this pseudo-element: no box
            // the inline style belongs to the element itself, not to its pseudo-elements
            string inline = pseudo == 0 ? e.GetAttribute("style") : null;
            List<Declaration> inlineDecls = null;
            if (!string.IsNullOrWhiteSpace(inline))
            {
                keyBuf.Append('|').Append(inline);
                if (!inlineCache.TryGet(inline, out inlineDecls))
                {
                    inlineDecls = new List<Declaration>();
                    int line = e.Line;
                    CssParser.ParseDeclarations(inline, inlineDecls, (l, m) => Diagnostics.Add(new Diagnostic("style", l, m)), line, "style");
                    inlineCache.Add(inline, inlineDecls);
                }
            }
            string key = keyBuf.ToString();
            if (store.Cache.TryGet((parent, key), out var hit)) { return hit; }

            int reported = Diagnostics.Count;
            var matched = new List<(Declaration D, Specificity S, int Order)>();
            foreach (int order in matchedOrders)
            {
                var (sel, rule, _) = selectors[order];
                foreach (var d in rule.Declarations) { matched.Add((d, sel.Specificity, order)); }
            }
            // inline style beats every selector: highest specificity, latest order
            if (inlineDecls != null) { foreach (var d in inlineDecls) { matched.Add((d, new Specificity(int.MaxValue, 0, 0), int.MaxValue)); } }

            // stable sort: important last, then origin (UA before author), then specificity, then source order
            matched.Sort((a, b) =>
            {
                if (a.D.Important != b.D.Important) { return a.D.Important ? 1 : -1; }
                bool ua = a.Order < uaCount, ub = b.Order < uaCount;
                if (ua != ub) { return ua ? -1 : 1; }
                int s = a.S.CompareTo(b.S);
                return s != 0 ? s : a.Order.CompareTo(b.Order);
            });

            var style = new ComputedStyle(UnitPx);
            if (parent != null)
            {
                foreach (var kv in parent.Vars) { style.Vars[kv.Key] = kv.Value; }
            }
            foreach (var (d, _, _) in matched)
            {
                if (d.Property.StartsWith("--", StringComparison.Ordinal)) { style.Vars[d.Property] = d.Value; }
            }
            // custom properties may use each other: resolve them once, in place
            foreach (var name in new List<string>(style.Vars.Keys))
            {
                style.Vars[name] = Substitute(style.Vars[name], style.Vars, 0, out _) ?? "";
            }

            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (d, _, _) in matched)
            {
                if (d.Property.StartsWith("--", StringComparison.Ordinal)) { continue; }
                string value = Substitute(d.Value, style.Vars, 0, out string missing);
                if (value == null)
                {
                    Diagnostics.Add(new Diagnostic(d.File ?? "css", d.Line, $"{d.Property}: var({missing}) is not defined and has no fallback"));
                    value = "unset";
                }
                DecorCheck(d.Property, value, d.Line, d.File);
                if (Shorthands.Contains(d.Property)) { Expand(d.Property, value, declared, d.Line, d.File); }
                else { declared[d.Property] = value; }
            }

            if (parent != null)
            {
                foreach (var p in Inherited) { if (parent.Values.TryGetValue(p, out var v)) { style.Values[p] = v; } }
            }
            // content: icon(name) is a glyph of the Lucide font unless the rule names a font itself
            if (pseudo != 0 && declared.TryGetValue("content", out var cv) && cv.TrimStart().StartsWith("icon(", StringComparison.Ordinal) && !declared.ContainsKey("font-family"))
            {
                declared["font-family"] = "lucide";
            }
            foreach (var kv in declared)
            {
                string v = kv.Value.Trim();
                switch (v.ToLowerInvariant())
                {
                    case "inherit":
                        store.InheritKeys.Add(kv.Key);
                        if (parent != null && parent.Values.TryGetValue(kv.Key, out var pv)) { style.Values[kv.Key] = pv; }
                        else { style.Values.Remove(kv.Key); }
                        continue;
                    case "initial":
                        style.Values.Remove(kv.Key);
                        continue;
                    case "unset":
                        if (!Inherited.Contains(kv.Key)) { style.Values.Remove(kv.Key); }
                        continue;
                }
                style.Values[kv.Key] = v;
            }

            float parentFont = parent?.FontSizePx ?? 16f;
            style.FontSizePx = parentFont;
            if (style.Values.TryGetValue("font-size", out var fs))
            {
                if (Length.TryParse(fs, out var l))
                {
                    style.FontSizePx = l.Unit switch
                    {
                        Unit.Px => l.Value,
                        Unit.Em => l.Value * parentFont,
                        Unit.Percent => l.Value / 100f * parentFont,
                        Unit.U => l.Value * UnitPx,
                        _ => parentFont,
                    };
                }
                style.Values["font-size"] = style.FontSizePx.ToString("0.###", CultureInfo.InvariantCulture) + "px";
            }
            // like browsers, a %/em/u line-height inherits as the computed px; a plain number inherits as the number
            if (style.Values.TryGetValue("line-height", out var lh) && Length.TryParse(lh, out var lhl)
                && lhl.Unit is Unit.Percent or Unit.Em or Unit.U)
            {
                float px = lhl.Unit == Unit.Percent ? lhl.Value / 100f * style.FontSizePx
                    : lhl.Unit == Unit.Em ? lhl.Value * style.FontSizePx : lhl.Value * UnitPx;
                style.Values["line-height"] = px.ToString("0.###", CultureInfo.InvariantCulture) + "px";
            }
            // letter-spacing, word-spacing and text-indent inherit as the computed px too (an em is of the font size of the element that wrote it)
            foreach (var prop in SpacingProps)
            {
                if (style.Values.TryGetValue(prop, out var sp) && Length.TryParse(sp, out var spl) && spl.Unit is Unit.Em or Unit.U)
                {
                    float px = spl.Unit == Unit.Em ? spl.Value * style.FontSizePx : spl.Value * UnitPx;
                    style.Values[prop] = px.ToString("0.###", CultureInfo.InvariantCulture) + "px";
                }
            }
            // text-shadow inherits as px too: an em offset is of the font size of the element that wrote it
            if (style.Values.TryGetValue("text-shadow", out var tsh) && tsh.Length > 4) { style.Values["text-shadow"] = TextShadow.ResolveUnits(tsh, style.FontSizePx, UnitPx); }
            if (style.Values.TryGetValue("text-decoration-line", out var tdl) && tdl != "none") { TextDecor.Seen = true; }
            foreach (var side in new[] { "top", "right", "bottom", "left" })
            {
                if (style.Get($"border-{side}-color").Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
                {
                    style.Values[$"border-{side}-color"] = style.Get("color");
                }
            }
            // a style that reported something is not cached: the next open of the page (another engine, the same store) reports it again, as a cold cascade does
            if (Diagnostics.Count == reported) { store.Cache.Add((parent, key), style); }
            return style;
        }

        /// <summary>Replaces var(--x[, fallback]) recursively; null when a var is missing without a fallback.</summary>
        internal static string Substitute(string value, Dictionary<string, string> vars, int depth, out string missing)
        {
            missing = null;
            if (value.IndexOf("var(", StringComparison.Ordinal) < 0) { return value; }
            if (depth > 16) { missing = "(cycle)"; return null; }
            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                int at = value.IndexOf("var(", i, StringComparison.Ordinal);
                if (at < 0) { sb.Append(value, i, value.Length - i); break; }
                sb.Append(value, i, at - i);
                int depthP = 0, end = -1;
                for (int k = at + 3; k < value.Length; k++)
                {
                    if (value[k] == '(') { depthP++; }
                    else if (value[k] == ')' && --depthP == 0) { end = k; break; }
                }
                if (end < 0) { missing = "(unclosed var)"; return null; }
                string inner = value.Substring(at + 4, end - at - 4);
                int comma = CssParser.SplitTop(inner, ',').Count > 1 ? inner.IndexOf(',') : -1;
                string name = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
                string fallback = comma < 0 ? null : inner.Substring(comma + 1).Trim();
                string replacement = vars.TryGetValue(name, out var v) && v.Length > 0 ? v : fallback;
                if (replacement == null) { missing = name; return null; }
                replacement = Substitute(replacement, vars, depth + 1, out missing);
                if (replacement == null) { return null; }
                sb.Append(replacement);
                i = end + 1;
            }
            return sb.ToString();
        }

        private void Expand(string prop, string value, Dictionary<string, string> into, int line, string file = null)
        {
            var parts = SplitSpaces(value);
            string[] sides = { "top", "right", "bottom", "left" };
            string[] Box()
            {
                return parts.Count switch
                {
                    1 => new[] { parts[0], parts[0], parts[0], parts[0] },
                    2 => new[] { parts[0], parts[1], parts[0], parts[1] },
                    3 => new[] { parts[0], parts[1], parts[2], parts[1] },
                    _ => new[] { parts[0], parts[1], parts[2], parts[3] },
                };
            }
            bool single = parts.Count == 1 && IsWideKeyword(parts[0]);
            switch (prop)
            {
                case "margin":
                case "padding":
                    for (int i = 0; i < 4; i++) { into[$"{prop}-{sides[i]}"] = single ? parts[0] : Box()[i]; }
                    return;
                case "border-width":
                case "border-color":
                    for (int i = 0; i < 4; i++) { into[$"border-{sides[i]}-{prop.Substring(7)}"] = single ? parts[0] : Box()[i]; }
                    return;
                case "border-radius":
                {
                    // 1-4 values, optionally "/" and 1-4 vertical ones (Core/Corners.cs); a bad value changes nothing
                    var four = new string[4];
                    if (!CornerSpec.Expand(value, four)) { Diagnostics.Add(new Diagnostic(file ?? "css", line, $"border-radius: '{value}' is not valid, ignored")); return; }
                    for (int i = 0; i < 4; i++) { into[CornerSpec.Longhands[i]] = four[i]; }
                    return;
                }
                case "border-style":
                    // one style for the four sides: dashed and dotted are drawn, anything else (double, groove, none...) as solid
                    into["border-style"] = parts.Count > 0 && parts[0].ToLowerInvariant() is "dashed" or "dotted" ? parts[0].ToLowerInvariant() : "solid";
                    return;
                case "border":
                case "border-top":
                case "border-right":
                case "border-bottom":
                case "border-left":
                {
                    string width = "0", color = "currentcolor", style = null;
                    if (single) { width = color = parts[0]; }
                    foreach (var p in single ? new List<string>() : parts)
                    {
                        string low = p.ToLowerInvariant();
                        if (low == "none" || low == "hidden") { width = "0"; }
                        else if (low is "solid" or "dashed" or "dotted" or "double") { style = low is "dashed" or "dotted" ? low : "solid"; }
                        else if (Length.TryParse(p, out _)) { width = p; }
                        else { color = p; }
                    }
                    var targets = prop == "border" ? sides : new[] { prop.Substring(7) };
                    foreach (var s in targets)
                    {
                        into[$"border-{s}-width"] = width;
                        into[$"border-{s}-color"] = color;
                    }
                    if (style != null) { into["border-style"] = style; }   // one style for the box: the last border declaration with a style wins
                    return;
                }
                case "outline":
                {
                    // outline: width, style and colour in any order; what is left out is reset (a lone width has no style, so no line, as in CSS)
                    string width = "medium", color = "currentcolor", style = "none";
                    if (single) { width = color = style = parts[0]; }
                    foreach (var p in single ? new List<string>() : parts)
                    {
                        string low = p.ToLowerInvariant();
                        if (OutlineSpec.IsStyleWord(low)) { style = low; }
                        else if (OutlineSpec.TryKeywordWidth(low, out _) || Length.TryParse(p, out _)) { width = p; }
                        else { color = p; }
                    }
                    into["outline-width"] = width;
                    into["outline-style"] = style;
                    into["outline-color"] = color;
                    return;
                }
                case "flex":
                    if (single) { into["flex-grow"] = into["flex-shrink"] = into["flex-basis"] = parts[0]; return; }
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "none": into["flex-grow"] = "0"; into["flex-shrink"] = "0"; into["flex-basis"] = "auto"; return;
                        case "auto": into["flex-grow"] = "1"; into["flex-shrink"] = "1"; into["flex-basis"] = "auto"; return;
                    }
                    // flex: <grow> [<shrink>] [<basis>]; a plain number in second place is the shrink factor
                    bool secondIsShrink = parts.Count > 2 || parts.Count == 2 && IsNumber(parts[1]);
                    into["flex-grow"] = parts[0];
                    into["flex-shrink"] = secondIsShrink ? parts[1] : "1";
                    into["flex-basis"] = parts.Count > 2 ? parts[2] : parts.Count == 2 && !secondIsShrink ? parts[1] : "0";
                    return;
                case "flex-flow":
                    foreach (var p in parts)
                    {
                        if (p.Contains("wrap")) { into["flex-wrap"] = p; } else { into["flex-direction"] = p; }
                    }
                    return;
                case "gap":
                    into["row-gap"] = parts[0];
                    into["column-gap"] = parts.Count > 1 ? parts[1] : parts[0];
                    return;
                case "word-wrap": into["overflow-wrap"] = value; return;
                case "text-decoration":
                    if (single) { into["text-decoration-line"] = into["text-decoration-color"] = into["text-decoration-style"] = into["text-decoration-thickness"] = parts[0]; return; }
                    // a value that is not understood changes nothing (DecorCheck has said so); what is left out is reset
                    if (TextDecor.Expand(value, out string tl, out string tc, out string ts, out string tt, out _))
                    {
                        into["text-decoration-line"] = tl; into["text-decoration-color"] = tc; into["text-decoration-style"] = ts; into["text-decoration-thickness"] = tt;
                    }
                    return;
                case "-webkit-line-clamp": into["line-clamp"] = value; return;
                case "overflow":
                    into["overflow-x"] = parts[0];
                    into["overflow-y"] = parts.Count > 1 ? parts[1] : parts[0];
                    return;
                case "background":
                {
                    if (single)
                    {
                        into["background-color"] = into["background-image"] = into["background-size"] = into["background-position"] = into["background-repeat"] = parts[0];
                        return;
                    }
                    // one layer (the first of a comma list): <image> || <repeat> || <colour> || <position> [/ <size>]
                    var layer = ImageCss.FirstLayer(value, out bool moreLayers);
                    if (moreLayers) { DecorWarn(file, line, "background: only the first layer of a list is drawn"); }
                    var posParts = new List<string>(); var sizeParts = new List<string>(); string repeat = null;
                    bool afterSlash = false;
                    foreach (var p in NineSlice.Words(layer))
                    {
                        if (p == "/") { afterSlash = true; continue; }
                        if (afterSlash && sizeParts.Count < 2 && !ImageCss.IsRepeatWord(p) && !p.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && !Rgba.TryParse(p, out _)) { sizeParts.Add(p); continue; }
                        string low = p.ToLowerInvariant();
                        if (p.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || p.StartsWith("sprite(", StringComparison.OrdinalIgnoreCase) || p.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase) >= 0 || low == "none") { into["background-image"] = p; }
                        else if (ImageCss.IsRepeatWord(p)) { repeat = repeat == null ? p : repeat + " " + p; }
                        else if (Rgba.TryParse(p, out _)) { into["background-color"] = p; }
                        else if (low is "left" or "right" or "top" or "bottom" or "center" || p.Length > 0 && (char.IsDigit(p[0]) || p[0] is '.' or '-' or '+')) { posParts.Add(p); }
                        else { Diagnostics.Add(new Diagnostic(file ?? "css", line, $"background: '{p}' is not supported")); }
                    }
                    string posText = posParts.Count > 0 ? string.Join(" ", posParts) : "0% 0%", sizeText = sizeParts.Count > 0 ? string.Join(" ", sizeParts) : "auto";
                    if (!ImageCss.TryParsePosition(posText, out _)) { Diagnostics.Add(new Diagnostic(file ?? "css", line, $"background: position '{posText}' is not valid, 0% 0% is used")); posText = "0% 0%"; }
                    if (!ImageCss.TryParseSize(sizeText, out _)) { Diagnostics.Add(new Diagnostic(file ?? "css", line, $"background: size '{sizeText}' is not valid, auto is used")); sizeText = "auto"; }
                    if (repeat != null && !ImageCss.TryParseRepeat(repeat, out _, out _)) { Diagnostics.Add(new Diagnostic(file ?? "css", line, $"background: repeat '{repeat}' is not valid, repeat is used")); repeat = null; }
                    into["background-position"] = posText; into["background-size"] = sizeText; into["background-repeat"] = repeat ?? "repeat";
                    return;
                }
                case "border-image":
                {
                    if (single) { into["border-image-source"] = into["border-image-slice"] = into["border-image-width"] = into["border-image-repeat"] = parts[0]; return; }
                    if (!NineSlice.Expand(value, out string src, out string slice, out string width, out string rep, out string note))
                    {
                        Diagnostics.Add(new Diagnostic(file ?? "css", line, $"border-image: '{value}' is not understood (url(...) [slice] [/ width] [stretch|repeat|round|space]), ignored"));
                        return;
                    }
                    if (note != null) { DecorWarn(file, line, note); }
                    into["border-image-source"] = src; into["border-image-slice"] = slice; into["border-image-width"] = width; into["border-image-repeat"] = rep;
                    return;
                }
            }
        }

        private readonly HashSet<string> decorWarned = new(StringComparer.Ordinal);

        private void DecorWarn(string file, int line, string message)
        {
            if (decorWarned.Add($"{file}:{line}:{message}")) { Diagnostics.Add(new Diagnostic(file ?? "css", line, message)); }
        }

        /// <summary>Says once per rule what text-shadow, outline and cursor will not do: blur, a list, a line style other than solid, a cursor the game lacks.</summary>
        private void DecorCheck(string prop, string value, int line, string file)
        {
            string low = value.Trim().ToLowerInvariant();
            if (low is "inherit" or "initial" or "unset") { return; }
            switch (prop)
            {
                case "text-shadow":
                {
                    var raw = TextShadow.Parse(value);
                    if (raw.Problem != null) { DecorWarn(file, line, $"text-shadow: {raw.Problem}, no shadow"); return; }
                    if (raw.Blur) { DecorWarn(file, line, "text-shadow: blur is not supported, the shadow is drawn sharp"); }
                    if (raw.ExtraShadows) { DecorWarn(file, line, "text-shadow: only the first shadow of a list is drawn"); }
                    return;
                }
                case "outline":
                case "outline-style":
                    foreach (var p in SplitSpaces(value))
                    {
                        string w = p.ToLowerInvariant();
                        if (OutlineSpec.IsStyleWord(w)) { if (w is not ("none" or "hidden" or "solid")) { DecorWarn(file, line, $"{prop}: '{w}' is drawn as solid"); } }
                        else if (prop == "outline-style") { DecorWarn(file, line, $"outline-style: '{p}' is not a line style"); }
                        else if (!OutlineSpec.TryKeywordWidth(w, out _) && !Length.TryParse(p, out _) && !Rgba.TryParse(p, out _)) { DecorWarn(file, line, $"outline: '{p}' is not understood"); }
                    }
                    return;
                case "outline-width":
                    if (!OutlineSpec.TryKeywordWidth(low, out _) && !(Length.TryParse(value, out var wl) && wl.Unit is Unit.Px or Unit.U or Unit.Em && wl.Value >= 0)) { DecorWarn(file, line, $"outline-width: '{value}' is not a width, medium is used"); }
                    return;
                case "outline-offset":
                    if (!(Length.TryParse(value, out var ol) && ol.Unit is Unit.Px or Unit.U or Unit.Em)) { DecorWarn(file, line, $"outline-offset: '{value}' is not a length, 0 is used"); }
                    return;
                case "outline-color":
                    if (low != "currentcolor" && !Rgba.TryParse(value, out _)) { DecorWarn(file, line, $"outline-color: '{value}' is not a colour"); }
                    return;
                case "text-decoration":
                    if (!TextDecor.Expand(value, out _, out _, out string tdStyle, out _, out string tdProblem)) { DecorWarn(file, line, $"text-decoration: {tdProblem}, ignored"); return; }
                    if (tdStyle != "solid") { DecorWarn(file, line, $"text-decoration: '{tdStyle}' is drawn as solid"); }
                    return;
                case "text-decoration-line":
                    if (!TextDecor.ParseLine(value, out _, out _, out _, out string tlProblem)) { DecorWarn(file, line, $"text-decoration-line: {tlProblem}, no line"); }
                    return;
                case "text-decoration-style":
                    if (!TextDecor.IsStyleWord(low)) { DecorWarn(file, line, $"text-decoration-style: '{value}' is not a line style, solid is used"); }
                    else if (low != "solid") { DecorWarn(file, line, $"text-decoration-style: '{low}' is drawn as solid"); }
                    return;
                case "text-decoration-color":
                    if (low != "currentcolor" && !Rgba.TryParse(value, out _)) { DecorWarn(file, line, $"text-decoration-color: '{value}' is not a colour"); }
                    return;
                case "text-decoration-thickness":
                    if (!TextDecor.IsThickness(value.Trim())) { DecorWarn(file, line, $"text-decoration-thickness: '{value}' is not a thickness, auto is used"); }
                    return;
                case "letter-spacing":
                case "word-spacing":
                    if (low != "normal" && !(Length.TryParse(value, out var spl) && spl.Unit is Unit.Px or Unit.U or Unit.Em)) { DecorWarn(file, line, $"{prop}: '{value}' is not a length, normal is used"); }
                    return;
                case "text-indent":
                    if (!(Length.TryParse(value, out var til) && til.Unit is Unit.Px or Unit.U or Unit.Em or Unit.Percent)) { DecorWarn(file, line, $"text-indent: '{value}' is not a length or a percentage, 0 is used"); }
                    return;
                case "background-image":
                case "border-image-source":
                    ImageCss.FirstLayer(value, out bool extraLayers);
                    // a list of gradient layers is drawn in full (Gradient.TryParseLayers); a second picture in the list is not
                    bool gradients = prop == "background-image" && value.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase) >= 0
                        && value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0 && value.IndexOf("sprite(", StringComparison.OrdinalIgnoreCase) < 0;
                    if (extraLayers && !gradients) { DecorWarn(file, line, $"{prop}: only the first layer of a list is drawn"); }
                    return;
                case "background-size":
                    if (!ImageCss.TryParseSize(value, out _)) { DecorWarn(file, line, $"background-size: '{value}' is not understood (auto, cover, contain, or one or two lengths), auto is used"); }
                    return;
                case "background-position":
                    if (!ImageCss.TryParsePosition(value, out _)) { DecorWarn(file, line, $"background-position: '{value}' is not understood, 0% 0% is used"); }
                    return;
                case "background-repeat":
                    if (!ImageCss.TryParseRepeat(value, out _, out _)) { DecorWarn(file, line, $"background-repeat: '{value}' is not understood, repeat is used"); }
                    return;
                case "object-fit":
                    if (!ImageCss.TryParseFit(value, out _)) { DecorWarn(file, line, $"object-fit: '{value}' is not fill, contain, cover, none or scale-down, fill is used"); }
                    return;
                case "border-image-slice":
                    if (!NineSlice.TryParseSlice(value, out _)) { DecorWarn(file, line, $"border-image-slice: '{value}' is not understood (1-4 numbers or percentages, and fill), 100% is used"); }
                    return;
                case "border-image-width":
                    if (!NineSlice.TryParseWidth(value, out _)) { DecorWarn(file, line, $"border-image-width: '{value}' is not understood (1-4 numbers, lengths, percentages or auto), 1 is used"); }
                    return;
                case "border-image-repeat":
                    if (!NineSlice.TryParseRepeat(value, out _, out _)) { DecorWarn(file, line, $"border-image-repeat: '{value}' is not stretch, repeat, round or space, stretch is used"); }
                    return;
                case "cursor":
                    CssCursor.Parse(value, out string unknown);
                    if (unknown != null) { DecorWarn(file, line, $"cursor: '{unknown}' is not a cursor, the default one is used"); }
                    return;
            }
        }

        private static bool IsWideKeyword(string s) => s.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || s.Equals("initial", StringComparison.OrdinalIgnoreCase) || s.Equals("unset", StringComparison.OrdinalIgnoreCase);

        private static bool IsNumber(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        /// <summary>Split on spaces outside parentheses: "1px solid rgb(1, 2, 3)" → 3 parts.</summary>
        internal static List<string> SplitSpaces(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = -1;
            for (int i = 0; i <= s.Length; i++)
            {
                char c = i < s.Length ? s[i] : ' ';
                if (c == '(') { depth++; }
                else if (c == ')') { depth = Math.Max(0, depth - 1); }
                bool space = char.IsWhiteSpace(c) && depth == 0;
                if (!space && start < 0) { start = i; }
                else if (space && start >= 0) { parts.Add(s.Substring(start, i - start)); start = -1; }
            }
            return parts;
        }
    }
}
