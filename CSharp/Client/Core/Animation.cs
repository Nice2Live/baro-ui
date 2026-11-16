using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BaroUI.Core
{
    internal enum AnimKind { Number, Color, Length, Pair, Shadow }

    /// <summary>One animatable value: a number (opacity), a colour, a length in px or % (the unit must match to interpolate),
    /// or a pair of px lengths (translate). The components live in A..D.</summary>
    internal readonly struct Val : IEquatable<Val>
    {
        public readonly AnimKind Kind;
        public readonly Unit Unit;
        public readonly float A, B, C, D;

        private Val(AnimKind kind, Unit unit, float a, float b = 0, float c = 0, float d = 0) { Kind = kind; Unit = unit; A = a; B = b; C = c; D = d; }

        public static Val Num(float v) => new(AnimKind.Number, Unit.None, v);
        public static Val Len(float v, Unit unit) => new(AnimKind.Length, unit, v);
        public static Val Pair(float x, float y) => new(AnimKind.Pair, Unit.Px, x, y);
        public static Val Col(Rgba c) => new(AnimKind.Color, Unit.None, c.R, c.G, c.B, c.A);
        /// <summary>The geometry of a box-shadow in px: x, y, blur, spread (its colour is a Color value of its own, see Animator.ShadowColorProp).</summary>
        public static Val Shadow(float x, float y, float blur, float spread) => new(AnimKind.Shadow, Unit.None, x, y, blur, spread);

        public Rgba ToRgba() => new((byte)Math.Clamp((int)Math.Round(A), 0, 255), (byte)Math.Clamp((int)Math.Round(B), 0, 255),
            (byte)Math.Clamp((int)Math.Round(C), 0, 255), (byte)Math.Clamp((int)Math.Round(D), 0, 255));

        public bool SameKind(in Val o) => Kind == o.Kind && Unit == o.Unit;

        public static Val Lerp(in Val a, in Val b, float t)
        {
            if (a.Kind == AnimKind.Color)
            {
                // a transparent end takes the other end's colour, so a fade does not pass through black
                float ar = a.A, ag = a.B, ab = a.C, br = b.A, bg = b.B, bb = b.C;
                if (a.D == 0) { ar = br; ag = bg; ab = bb; }
                else if (b.D == 0) { br = ar; bg = ag; bb = ab; }
                return new Val(AnimKind.Color, Unit.None, ar + (br - ar) * t, ag + (bg - ag) * t, ab + (bb - ab) * t, a.D + (b.D - a.D) * t);
            }
            return new Val(a.Kind, a.Unit, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t, a.C + (b.C - a.C) * t, a.D + (b.D - a.D) * t);
        }

        public bool Equals(Val o) => Kind == o.Kind && Unit == o.Unit && A == o.A && B == o.B && C == o.C && D == o.D;
        public override bool Equals(object o) => o is Val v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Kind, Unit, A, B, C, D);
        public override string ToString() => $"{Kind} {A} {B} {C} {D}";
    }

    internal sealed class TransitionDef
    {
        public string Prop = "all";
        public float Dur, Delay;
        public Ease Ease = Ease.Default;
    }

    internal enum AnimDir { Normal, Reverse, Alternate, AlternateReverse }
    internal enum AnimFill { None, Forwards, Backwards, Both }

    internal sealed class AnimationDef
    {
        public string Name = "";
        public float Dur, Delay;
        public Ease Ease = Ease.Default;
        public float Iterations = 1;
        public AnimDir Dir;
        public AnimFill Fill;
    }

    /// <summary>The transition and animation lists of one computed style (parsed once per style).</summary>
    internal sealed class AnimSpec
    {
        public readonly List<TransitionDef> Transitions = new();
        public readonly List<AnimationDef> Animations = new();
        public string AnimationText = "";
        /// <summary>layout-fps: how often lengths are sampled. 0 = the window's default (auto), infinity = every frame (max).</summary>
        public float LayoutFps;
        /// <summary>transition: layout (the element glides to where a layout run put it); null when it has none.</summary>
        public TransitionDef Flip;

        /// <summary>null when the style declares neither (the usual case: no per-element work at all).</summary>
        internal static readonly string[] TransitionLonghands = { "transition-property", "transition-duration", "transition-timing-function", "transition-delay" };
        internal static readonly string[] AnimationLonghands =
        {
            "animation-name", "animation-duration", "animation-timing-function", "animation-delay",
            "animation-iteration-count", "animation-direction", "animation-fill-mode",
        };

        private static bool HasAny(ComputedStyle s, string[] names)
        {
            foreach (var n in names) { if (s.Values.ContainsKey(n)) { return true; } }
            return false;
        }

        // the comma list of a longhand (null when the style does not declare it); a list shorter than the transitions repeats
        private static string[] ListOf(ComputedStyle s, string prop)
        {
            if (!s.Values.TryGetValue(prop, out var v)) { return null; }
            var items = CssParser.SplitTop(v, ',');
            var list = new string[items.Count];
            for (int i = 0; i < list.Length; i++) { list[i] = items[i].Trim(); }
            return list.Length == 0 ? null : list;
        }

        /// <summary>The shorthand gives the base lists; a declared longhand overrides its field of each item.</summary>
        public static AnimSpec Parse(ComputedStyle s)
        {
            string t = s.Get("transition").Trim(), a = s.Get("animation").Trim();
            bool noT = t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase);
            bool noA = a.Length == 0 || a.Equals("none", StringComparison.OrdinalIgnoreCase);
            bool longT = HasAny(s, TransitionLonghands), longA = HasAny(s, AnimationLonghands);
            if (noT && noA && !longT && !longA) { return null; }
            var spec = new AnimSpec();
            if (!noT) { foreach (var item in CssParser.SplitTop(t, ',')) { ParseTransition(item, spec.Transitions); } }
            if (longT) { ApplyTransitionLonghands(s, spec.Transitions); }
            if (!noA) { foreach (var item in CssParser.SplitTop(a, ',')) { ParseAnimation(item, spec.Animations); } }
            if (longA) { ApplyAnimationLonghands(s, spec.Animations); }
            if (s.Values.TryGetValue("layout-fps", out var fpsText)) { spec.LayoutFps = ParseFps(fpsText); }
            foreach (var d in spec.Transitions) { if (d.Prop == "layout") { spec.Flip = d; } }
            if (spec.Animations.Count > 0)
            {
                // what restarts the animations when it changes: the shorthand and every longhand
                var sb = new StringBuilder(noA ? "" : a);
                foreach (var n in AnimationLonghands) { if (s.Values.TryGetValue(n, out var v)) { sb.Append('|').Append(n).Append(':').Append(v); } }
                spec.AnimationText = sb.ToString();
            }
            return spec;
        }

        private static float ParseFps(string text)
        {
            text = text.Trim();
            if (text.Equals("max", StringComparison.OrdinalIgnoreCase)) { return float.PositiveInfinity; }
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= 1 ? f : 0;
        }

        private static void ApplyTransitionLonghands(ComputedStyle s, List<TransitionDef> defs)
        {
            var props = ListOf(s, "transition-property");
            var dur = ListOf(s, "transition-duration");
            var timing = ListOf(s, "transition-timing-function");
            var delay = ListOf(s, "transition-delay");
            int n = props?.Length ?? defs.Count;
            if (n == 0 && (dur != null || timing != null || delay != null)) { n = 1; }
            var result = new List<TransitionDef>();
            for (int i = 0; i < n; i++)
            {
                var def = new TransitionDef();
                if (defs.Count > 0) { var b = defs[i % defs.Count]; def.Prop = b.Prop; def.Dur = b.Dur; def.Delay = b.Delay; def.Ease = b.Ease; }
                if (props != null)
                {
                    string p = props[i].ToLowerInvariant();
                    if (p == "none") { continue; }
                    def.Prop = p;
                }
                if (dur != null && TryTime(dur[i % dur.Length], out float d)) { def.Dur = Math.Max(0, d); }
                if (delay != null && TryTime(delay[i % delay.Length], out float dl)) { def.Delay = dl; }
                if (timing != null && Ease.TryParse(timing[i % timing.Length], out var e)) { def.Ease = e; }
                result.Add(def);
            }
            defs.Clear();
            defs.AddRange(result);
        }

        private static void ApplyAnimationLonghands(ComputedStyle s, List<AnimationDef> defs)
        {
            var names = ListOf(s, "animation-name");
            var dur = ListOf(s, "animation-duration");
            var timing = ListOf(s, "animation-timing-function");
            var delay = ListOf(s, "animation-delay");
            var count = ListOf(s, "animation-iteration-count");
            var dir = ListOf(s, "animation-direction");
            var fill = ListOf(s, "animation-fill-mode");
            int n = names?.Length ?? defs.Count;
            var result = new List<AnimationDef>();
            for (int i = 0; i < n; i++)
            {
                var def = new AnimationDef();
                if (defs.Count > 0)
                {
                    var b = defs[i % defs.Count];
                    def.Name = b.Name; def.Dur = b.Dur; def.Delay = b.Delay; def.Ease = b.Ease; def.Iterations = b.Iterations; def.Dir = b.Dir; def.Fill = b.Fill;
                }
                if (names != null)
                {
                    if (names[i].Equals("none", StringComparison.OrdinalIgnoreCase)) { continue; }
                    def.Name = names[i];
                }
                if (dur != null && TryTime(dur[i % dur.Length], out float d)) { def.Dur = Math.Max(0, d); }
                if (delay != null && TryTime(delay[i % delay.Length], out float dl)) { def.Delay = dl; }
                if (timing != null && Ease.TryParse(timing[i % timing.Length], out var e)) { def.Ease = e; }
                if (count != null)
                {
                    string c = count[i % count.Length].ToLowerInvariant();
                    if (c == "infinite") { def.Iterations = float.PositiveInfinity; }
                    else if (float.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out float k)) { def.Iterations = Math.Max(0, k); }
                }
                if (dir != null)
                {
                    def.Dir = dir[i % dir.Length].ToLowerInvariant() switch
                    {
                        "reverse" => AnimDir.Reverse, "alternate" => AnimDir.Alternate, "alternate-reverse" => AnimDir.AlternateReverse, _ => AnimDir.Normal,
                    };
                }
                if (fill != null)
                {
                    def.Fill = fill[i % fill.Length].ToLowerInvariant() switch
                    {
                        "forwards" => AnimFill.Forwards, "backwards" => AnimFill.Backwards, "both" => AnimFill.Both, _ => AnimFill.None,
                    };
                }
                if (def.Name.Length > 0) { result.Add(def); }
            }
            defs.Clear();
            defs.AddRange(result);
        }

        internal static bool TryTime(string s, out float seconds)
        {
            seconds = 0;
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("ms", StringComparison.Ordinal))
            {
                if (!float.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out float ms)) { return false; }
                seconds = ms / 1000f;
                return true;
            }
            if (s.EndsWith("s", StringComparison.Ordinal))
            {
                return float.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            }
            return false;
        }

        private static void ParseTransition(string item, List<TransitionDef> into)
        {
            var def = new TransitionDef();
            int times = 0;
            foreach (var token in StyleEngine.SplitSpaces(item))
            {
                if (TryTime(token, out float sec)) { if (times++ == 0) { def.Dur = Math.Max(0, sec); } else { def.Delay = sec; } }
                else if (Ease.TryParse(token, out var ease)) { def.Ease = ease; }
                else { def.Prop = token.ToLowerInvariant(); }
            }
            if (def.Prop == "none") { return; }
            into.Add(def);
        }

        private static void ParseAnimation(string item, List<AnimationDef> into)
        {
            var def = new AnimationDef();
            int times = 0;
            bool named = false;
            foreach (var token in StyleEngine.SplitSpaces(item))
            {
                string low = token.ToLowerInvariant();
                if (TryTime(token, out float sec)) { if (times++ == 0) { def.Dur = Math.Max(0, sec); } else { def.Delay = sec; } }
                else if (Ease.TryParse(token, out var ease)) { def.Ease = ease; }
                else if (low == "infinite") { def.Iterations = float.PositiveInfinity; }
                else if (float.TryParse(low, NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) { def.Iterations = Math.Max(0, n); }
                else if (low is "normal" or "reverse" or "alternate" or "alternate-reverse")
                {
                    def.Dir = low switch { "reverse" => AnimDir.Reverse, "alternate" => AnimDir.Alternate, "alternate-reverse" => AnimDir.AlternateReverse, _ => AnimDir.Normal };
                }
                else if (low is "forwards" or "backwards" or "both")
                {
                    def.Fill = low switch { "forwards" => AnimFill.Forwards, "backwards" => AnimFill.Backwards, _ => AnimFill.Both };
                }
                else if (low is "running" or "paused") { }
                else if (!named) { def.Name = token; named = true; }
            }
            if (named && !def.Name.Equals("none", StringComparison.OrdinalIgnoreCase)) { into.Add(def); }
        }
    }

    /// <summary>@keyframes name: stops by offset (0..1), each with its declarations expanded to longhands.</summary>
    internal sealed class KeyframesDef
    {
        public string Name = "";
        public readonly List<(float Offset, Dictionary<string, string> Props)> Stops = new();
    }

    /// <summary>One layout transition: the offsets at its start (against the new layout box) fade to zero.</summary>
    internal sealed class FlipState
    {
        public double Start;
        public float Delay, Dur;
        public Ease Ease = Ease.Default;
        public float X0, Y0, W0, H0;
    }

    internal struct FlipSnap
    {
        public Element E;
        public float X, Y, W, H;   // against the parent's border box, before the layout run
    }

    internal sealed class Tween
    {
        public string Prop;
        public Val From, To;
        public double Start;
        public float Delay, Dur;
        public Ease Ease;
    }

    internal sealed class Running
    {
        public AnimationDef Def;
        public double Start;
        public readonly Dictionary<string, List<(float Offset, Val V)>> Tracks = new(StringComparer.Ordinal);
    }

    /// <summary>The running animations of one element (null on the element when it has none).</summary>
    internal sealed class ElementAnim
    {
        public readonly List<Tween> Tweens = new();
        public readonly List<Running> Anims = new();
        public readonly Dictionary<string, Val> Over = new(StringComparer.Ordinal);
        public readonly HashSet<string> Overridden = new(StringComparer.Ordinal);
        public ComputedStyle Clone, CloneOf;
        /// <summary>The lengths of the last composed frame: the layout runs again only when they differ.</summary>
        public readonly Dictionary<string, Val> LayoutPrev = new(StringComparer.Ordinal);
    }

    /// <summary>CSS transitions and keyframe animations for the BBE UI engine (docs/ui/AUTHORING.md, "Анимации").
    ///
    /// The cascade (StyleEngine.Compute) calls Resolved for every restyled element. Elements whose style declares no transition and
    /// no animation, and have none running, cost one lookup. For the others the cascade's result (the base style, shared by many
    /// elements) stays in Element.BaseStyle, and Element.Style becomes a private copy whose animated properties are overridden with
    /// the current values: opacity, scale, colours and translate are paint-only; lengths (width, height, margins, paddings, borders,
    /// offsets, gaps) change the layout, so the element's layout is marked dirty each frame they move. Tick is called once a frame
    /// and moves every running animation to the current time. The core has no clock of its own: the window gives it one.
    ///
    /// Not animated: inherited text colour does not reach the children (put the transition on the element that owns the text),
    /// values that do not interpolate (auto, none, a px to % change) change at once.</summary>
    internal sealed class Animator
    {
        public Func<double> Clock = () => 0;
        /// <summary>An animated length moved: the layout of this element must run again.</summary>
        public Action<Element> LayoutChanged;
        /// <summary>False for an element that left the tree: its animation stops.</summary>
        public Func<Element, bool> IsAttached;
        public Func<string, KeyframesDef> Keyframes = _ => null;
        /// <summary>False switches every animation off (bbe_ui anim off): changes are at once, running ones end where they are.</summary>
        public Func<bool> Enabled = () => true;
        /// <summary>Updates a second for animated lengths (they move the layout, which is the costly part); 0: every frame. An element's layout-fps wins.</summary>
        public Func<float> LayoutFps = () => 0;

        private readonly HashSet<Element> active = new();
        private readonly List<Element> scratch = new();
        private double lastTick = double.NaN;

        /// <summary>Elements with an animation that has still to move.</summary>
        public int Running => active.Count + flipActive.Count;

        /// <summary>Some animation of the last composed frame moves where or whether an element is hit by the mouse: a FLIP move, an animated translate,
        /// scale, opacity (a nearly transparent block is not hit) or visibility. Lengths are not asked here: they relayout, and a layout already renews
        /// the hit cache. A page with only a colour pulse or a spinner's colour keeps its hit cache while the mouse rests.</summary>
        public bool MovesHit
        {
            get
            {
                if (flipActive.Count > 0) { return true; }
                foreach (var e in active)
                {
                    var over = e.Anim?.Over;
                    if (over == null || over.Count == 0) { continue; }
                    if (over.ContainsKey("translate") || over.ContainsKey("scale") || over.ContainsKey("opacity") || over.ContainsKey("visibility")) { return true; }
                }
                return false;
            }
        }

        internal static readonly Dictionary<string, AnimKind> Props = BuildProps();

        private static Dictionary<string, AnimKind> BuildProps()
        {
            var d = new Dictionary<string, AnimKind>(StringComparer.Ordinal)
            {
                ["opacity"] = AnimKind.Number, ["color"] = AnimKind.Color, ["background-color"] = AnimKind.Color, ["translate"] = AnimKind.Pair,
                ["scale"] = AnimKind.Number,
                // the three paint-only colours and shadows: transitions animate them (keyframes do not take box-shadow, see Keyframes)
                ["box-shadow"] = AnimKind.Shadow, ["outline-color"] = AnimKind.Color, ["text-decoration-color"] = AnimKind.Color,
                ["width"] = AnimKind.Length, ["height"] = AnimKind.Length, ["min-width"] = AnimKind.Length, ["min-height"] = AnimKind.Length,
                ["max-width"] = AnimKind.Length, ["max-height"] = AnimKind.Length, ["row-gap"] = AnimKind.Length, ["column-gap"] = AnimKind.Length,
            };
            foreach (var side in new[] { "top", "right", "bottom", "left" })
            {
                d[side] = AnimKind.Length;
                d[$"margin-{side}"] = AnimKind.Length;
                d[$"padding-{side}"] = AnimKind.Length;
                d[$"border-{side}-width"] = AnimKind.Length;
                d[$"border-{side}-color"] = AnimKind.Color;
            }
            return d;
        }

        // ---------------------------------------------------------------- the cascade's hook

        /// <summary>The visual style of an element the cascade just resolved. oldBase is its previous cascade result (null: a new element).</summary>
        public ComputedStyle Resolved(Element e, ComputedStyle oldBase, ComputedStyle fresh)
        {
            var a = e.Anim;
            var spec = SpecOf(fresh);
            if (spec?.Flip != null) { FlipTargets.Add(e); }
            else if (FlipTargets.Count > 0 && FlipTargets.Remove(e)) { ClearFlip(e); }
            if (a == null && spec == null) { e.AnimText = null; return fresh; }
            if (!Enabled())
            {
                e.Anim = null;
                active.Remove(e);
                return fresh;
            }
            double now = Clock();
            bool isNew = oldBase == null;
            a ??= e.Anim = new ElementAnim();
            string text = spec?.AnimationText ?? "";
            if (text.Length > 0)
            {
                if (isNew || e.AnimText != text) { StartAnimations(a, spec, fresh, now); }
            }
            else { a.Anims.Clear(); }
            e.AnimText = text;
            if (spec != null && spec.Transitions.Count > 0)
            {
                if (!isNew) { StartTransitions(a, spec, oldBase, fresh, now); }
            }
            else { a.Tweens.Clear(); }
            var visual = Compose(e, a, fresh, now, out bool layout);
            if (layout) { LayoutChanged?.Invoke(e); }
            return visual;
        }

        internal static AnimSpec SpecOf(ComputedStyle s)
        {
            if (!s.SpecParsed) { s.Spec = AnimSpec.Parse(s); s.SpecParsed = true; }
            return s.Spec;
        }

        /// <summary>An element (and its descendants) became visible: its keyframe animations start over (data-if turned on).</summary>
        public void Enter(Element root)
        {
            if (!Enabled()) { return; }
            double now = Clock();
            Walk(root);

            void Walk(Element e)
            {
                var bas = e.BaseStyle;
                var spec = bas == null ? null : SpecOf(bas);
                if (spec != null && spec.Animations.Count > 0)
                {
                    var a = e.Anim ??= new ElementAnim();
                    StartAnimations(a, spec, bas, now);
                    e.AnimText = spec.AnimationText;
                    var visual = Compose(e, a, bas, now, out bool layout);
                    e.Style = visual;
                    if (layout) { LayoutChanged?.Invoke(e); }
                }
                foreach (var c in e.ElementChildren()) { Walk(c); }
            }
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Moves every running animation to the clock's time (once per time value: the window calls it twice a frame).</summary>
        public void Tick()
        {
            if (active.Count == 0 && flipActive.Count == 0) { return; }
            if (!Enabled())
            {
                ClearFlips();
                scratch.Clear();
                scratch.AddRange(active);
                active.Clear();
                foreach (var e in scratch)
                {
                    if (e.BaseStyle != null) { e.Style = e.BaseStyle; }
                    e.Anim = null;
                    LayoutChanged?.Invoke(e);
                }
                return;
            }
            double now = Clock();
            if (now == lastTick) { return; }
            lastTick = now;
            scratch.Clear();
            scratch.AddRange(active);
            foreach (var e in scratch)
            {
                var a = e.Anim;
                if (a == null || e.BaseStyle == null || (IsAttached != null && !IsAttached(e))) { active.Remove(e); continue; }
                var visual = Compose(e, a, e.BaseStyle, now, out bool layout);
                if (!ReferenceEquals(visual, e.Style)) { e.Style = visual; }
                if (layout) { LayoutChanged?.Invoke(e); }
            }
            if (flipActive.Count > 0) { TickFlips(now); }
        }

        // ---------------------------------------------------------------- layout transitions (FLIP)
        //
        // transition: layout does not animate the layout: it is run once, at the final sizes. The window brackets the run with FlipBegin
        // (the boxes of the elements that have the transition, against their parents) and FlipEnd (the same after it): an element that moved
        // or changed its size starts drawn where it was and glides to where it is. Position and size are painted (the painter adds
        // Element.FlipX/Y/W/H and clips the content to the animated box), so a frame costs no layout at any rate. A layout run that moves
        // a running one again continues from where it is drawn.

        internal readonly HashSet<Element> FlipTargets = new();
        private readonly HashSet<Element> flipActive = new();
        private readonly List<FlipSnap> snaps = new();
        private readonly List<Element> deadFlips = new();

        /// <summary>Some element has transition: layout: the window then calls FlipBegin and FlipEnd around each layout run.</summary>
        public bool HasFlip => FlipTargets.Count > 0;

        private static (float X, float Y) RelPos(LayoutBox b) => b.Parent == null ? (b.X, b.Y) : (b.X - b.Parent.X, b.Y - b.Parent.Y);

        /// <summary>Before a layout run: where the elements with a layout transition are now (boxes: the last layout's).</summary>
        public void FlipBegin(IReadOnlyDictionary<Element, LayoutBox> boxes)
        {
            snaps.Clear();
            if (boxes == null || !Enabled()) { return; }
            deadFlips.Clear();
            foreach (var e in FlipTargets)
            {
                if (IsAttached != null && !IsAttached(e)) { deadFlips.Add(e); continue; }
                if (!boxes.TryGetValue(e, out var b)) { continue; }
                var (x, y) = RelPos(b);
                snaps.Add(new FlipSnap { E = e, X = x, Y = y, W = b.W, H = b.H });
            }
            foreach (var e in deadFlips) { FlipTargets.Remove(e); ClearFlip(e); }
        }

        /// <summary>After the layout run: the ones that moved or changed size start gliding (boxes: the new layout's).</summary>
        public void FlipEnd(IReadOnlyDictionary<Element, LayoutBox> boxes)
        {
            if (snaps.Count == 0) { return; }
            double now = Clock();
            foreach (var sn in snaps)
            {
                var e = sn.E;
                if (!boxes.TryGetValue(e, out var b)) { ClearFlip(e); continue; }
                var def = SpecOf(e.BaseStyle ?? e.Style)?.Flip;
                if (def == null) { ClearFlip(e); continue; }
                var (x, y) = RelPos(b);
                float dx = sn.X - x, dy = sn.Y - y, dw = sn.W - b.W, dh = sn.H - b.H;
                // the layout did not move it: a running glide goes on with its own timeline
                if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f && Math.Abs(dw) < 0.5f && Math.Abs(dh) < 0.5f) { continue; }
                if (def.Dur <= 0 && def.Delay <= 0) { ClearFlip(e); continue; }
                // it is drawn at (old layout + current offset); against the new layout that is the old-to-new difference plus the offset
                float fx = dx + e.FlipX, fy = dy + e.FlipY, fw = dw + e.FlipW, fh = dh + e.FlipH;
                var f = e.Flip ??= new FlipState();
                f.Start = now;
                f.Delay = def.Delay;
                f.Dur = Math.Max(def.Dur, 1e-4f);
                f.Ease = def.Ease;
                f.X0 = e.FlipX = fx;
                f.Y0 = e.FlipY = fy;
                f.W0 = e.FlipW = fw;
                f.H0 = e.FlipH = fh;
                flipActive.Add(e);
            }
            snaps.Clear();
        }

        private void TickFlips(double now)
        {
            scratch.Clear();
            scratch.AddRange(flipActive);
            foreach (var e in scratch)
            {
                var f = e.Flip;
                if (f == null || (IsAttached != null && !IsAttached(e))) { ClearFlip(e); continue; }
                double t = (now - f.Start - f.Delay) / f.Dur;
                if (t >= 1) { ClearFlip(e); continue; }
                float k = t <= 0 ? 1f : 1f - f.Ease.At((float)t);
                e.FlipX = f.X0 * k;
                e.FlipY = f.Y0 * k;
                e.FlipW = f.W0 * k;
                e.FlipH = f.H0 * k;
            }
        }

        private void ClearFlip(Element e)
        {
            e.Flip = null;
            e.FlipX = e.FlipY = e.FlipW = e.FlipH = 0;
            flipActive.Remove(e);
        }

        private void ClearFlips()
        {
            foreach (var e in flipActive)
            {
                e.Flip = null;
                e.FlipX = e.FlipY = e.FlipW = e.FlipH = 0;
            }
            flipActive.Clear();
            snaps.Clear();
        }

        // ---------------------------------------------------------------- starting

        private void StartTransitions(ElementAnim a, AnimSpec spec, ComputedStyle oldBase, ComputedStyle fresh, double now)
        {
            bool all = false;
            foreach (var d in spec.Transitions) { if (d.Prop == "all") { all = true; break; } }
            if (all)
            {
                foreach (var prop in Props.Keys) { TryStart(a, prop, DefFor(spec, prop), oldBase, fresh, now); }
                return;
            }
            foreach (var d in spec.Transitions)
            {
                if (Props.ContainsKey(d.Prop)) { TryStart(a, d.Prop, DefFor(spec, d.Prop), oldBase, fresh, now); }
            }
        }

        private static TransitionDef DefFor(AnimSpec spec, string prop)
        {
            TransitionDef found = null;
            foreach (var d in spec.Transitions) { if (d.Prop == prop || d.Prop == "all") { found = d; } }   // the later one wins
            return found;
        }

        private static void TryStart(ElementAnim a, string prop, TransitionDef def, ComputedStyle oldBase, ComputedStyle fresh, double now)
        {
            if (def == null) { return; }
            if (prop == "box-shadow") { TryStartShadow(a, def, oldBase, fresh, now); return; }
            if (!TryVal(oldBase, prop, out var o) || !TryVal(fresh, prop, out var n) || !o.SameKind(n)) { Remove(a, prop); return; }
            if (o.Equals(n)) { return; }   // not changed: a running transition of the property goes on
            if (def.Dur <= 0 && def.Delay <= 0) { Remove(a, prop); return; }
            var from = o;
            foreach (var tw in a.Tweens)
            {
                if (tw.Prop == prop) { from = ValueOf(tw, now); break; }
            }
            Remove(a, prop);
            if (!from.SameKind(n)) { return; }
            a.Tweens.Add(new Tween { Prop = prop, From = from, To = n, Start = now, Delay = def.Delay, Dur = Math.Max(def.Dur, 1e-4f), Ease = def.Ease });
        }

        /// <summary>The colour of a box-shadow transition: its own tween under this name, so the geometry (Val.Shadow) and the colour (Val.Col) each interpolate
        /// as they should. Not a CSS property (no transition-property names it).</summary>
        internal const string ShadowColorProp = "box-shadow#color";

        /// <summary>box-shadow starts two tweens together (the shape and the colour), or none when neither changed. A running pair is taken over at the value it
        /// has now. none is a shadow with no size and a transparent colour, so a shadow fades in and out.</summary>
        private static void TryStartShadow(ElementAnim a, TransitionDef def, ComputedStyle oldBase, ComputedStyle fresh, double now)
        {
            if (!TryVal(oldBase, "box-shadow", out var og) || !TryVal(fresh, "box-shadow", out var ng) || !ShadowColor(oldBase, out var oc) || !ShadowColor(fresh, out var nc))
            {
                Remove(a, "box-shadow"); Remove(a, ShadowColorProp);
                return;
            }
            if (og.Equals(ng) && oc.Equals(nc)) { return; }
            if (def.Dur <= 0 && def.Delay <= 0) { Remove(a, "box-shadow"); Remove(a, ShadowColorProp); return; }
            var fromG = og;
            var fromC = oc;
            foreach (var tw in a.Tweens)
            {
                if (tw.Prop == "box-shadow") { fromG = ValueOf(tw, now); }
                else if (tw.Prop == ShadowColorProp) { fromC = ValueOf(tw, now); }
            }
            Remove(a, "box-shadow"); Remove(a, ShadowColorProp);
            float dur = Math.Max(def.Dur, 1e-4f);
            a.Tweens.Add(new Tween { Prop = "box-shadow", From = fromG, To = ng, Start = now, Delay = def.Delay, Dur = dur, Ease = def.Ease });
            a.Tweens.Add(new Tween { Prop = ShadowColorProp, From = fromC, To = nc, Start = now, Delay = def.Delay, Dur = dur, Ease = def.Ease });
        }

        /// <summary>The colour a box-shadow is drawn with: its own, or the element's colour (currentcolor) when none is written. False when the value is not a
        /// shadow at all.</summary>
        private static bool ShadowColor(ComputedStyle s, out Val c)
        {
            c = Val.Col(Rgba.Transparent);
            string text = s.Get("box-shadow").Trim();
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (!BoxShadow.TryParse(text, out var sh, out _) || sh.IsNone) { return false; }
            c = Val.Col(sh.HasColor ? sh.Color : s.GetColor("color"));
            return true;
        }

        /// <summary>Writes the animated shadow into the clone as the box-shadow text (one shadow, the first; the painter reads it through DecorSpec).</summary>
        private static void WriteShadow(ComputedStyle s, in Val g, in Val c)
        {
            s.Values["box-shadow"] = string.Format(CultureInfo.InvariantCulture, "{0}px {1}px {2}px {3}px {4}", g.A, g.B, g.C, g.D, CssRgba(c.ToRgba()));
        }

        /// <summary>A colour as CSS text the parser reads back (rgba with the alpha as a fraction).</summary>
        private static string CssRgba(Rgba c) =>
            string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3})", c.R, c.G, c.B, (c.A / 255f).ToString("0.###", CultureInfo.InvariantCulture));

        /// <summary>A length in px: u and em as the style resolves them, px as written.</summary>
        private static float Px(ComputedStyle s, Length l)
        {
            if (l.Unit == Unit.U) { return s.ToPx(l.Value.ToString("R", CultureInfo.InvariantCulture) + "u").Value; }
            if (l.Unit == Unit.Em) { return s.ToPx(l.Value.ToString("R", CultureInfo.InvariantCulture) + "em").Value; }
            return l.Value;
        }

        private static void Remove(ElementAnim a, string prop)
        {
            for (int i = a.Tweens.Count - 1; i >= 0; i--) { if (a.Tweens[i].Prop == prop) { a.Tweens.RemoveAt(i); } }
        }

        private static Val ValueOf(Tween tw, double now)
        {
            double t = (now - tw.Start - tw.Delay) / tw.Dur;
            return t <= 0 ? tw.From : t >= 1 ? tw.To : Val.Lerp(tw.From, tw.To, tw.Ease.At((float)t));
        }

        private void StartAnimations(ElementAnim a, AnimSpec spec, ComputedStyle bas, double now)
        {
            a.Anims.Clear();
            foreach (var def in spec.Animations)
            {
                var frames = Keyframes(def.Name);
                if (frames == null) { continue; }
                var run = new Running { Def = def, Start = now };
                foreach (var (offset, props) in frames.Stops)
                {
                    foreach (var kv in props)
                    {
                        if (!Props.ContainsKey(kv.Key) || kv.Key == "box-shadow" || !TryParse(bas, kv.Key, kv.Value, out var v)) { continue; }
                        if (!run.Tracks.TryGetValue(kv.Key, out var list)) { run.Tracks[kv.Key] = list = new List<(float, Val)>(); }
                        list.Add((offset, v));
                    }
                }
                var drop = new List<string>();
                foreach (var kv in run.Tracks)
                {
                    var list = kv.Value;
                    list.Sort((x, y) => x.Offset.CompareTo(y.Offset));
                    bool ok = TryVal(bas, kv.Key, out var baseVal);
                    if (list[0].Offset > 0) { if (ok) { list.Insert(0, (0f, baseVal)); } else { drop.Add(kv.Key); continue; } }
                    if (list[list.Count - 1].Offset < 1) { if (ok) { list.Add((1f, baseVal)); } else { drop.Add(kv.Key); continue; } }
                    foreach (var (_, v) in list) { if (!v.SameKind(list[0].V)) { drop.Add(kv.Key); break; } }
                }
                foreach (var k in drop) { run.Tracks.Remove(k); }
                if (run.Tracks.Count > 0) { a.Anims.Add(run); }
            }
        }

        // ---------------------------------------------------------------- the visual style

        private ComputedStyle Compose(Element e, ElementAnim a, ComputedStyle bas, double now, out bool layoutTouched)
        {
            var over = a.Over;
            over.Clear();
            // lengths are sampled on a time grid (layout-fps), so the layout runs that often; the rest moves every frame
            var spec = SpecOf(bas);
            float fps = spec != null && spec.LayoutFps > 0 ? spec.LayoutFps : LayoutFps();
            double qnow = fps > 0 && fps < 1000 ? Math.Floor(now * fps) / fps : now;
            bool ticking = false;
            for (int i = 0; i < a.Tweens.Count; i++)
            {
                var tw = a.Tweens[i];
                double t = (now - tw.Start - tw.Delay) / tw.Dur;
                if (t >= 1) { a.Tweens.RemoveAt(i--); continue; }   // arrived: the base value shows
                if (tw.From.Kind == AnimKind.Length) { t = (qnow - tw.Start - tw.Delay) / tw.Dur; }
                over[tw.Prop] = t <= 0 ? tw.From : Val.Lerp(tw.From, tw.To, tw.Ease.At((float)t));
                ticking = true;
            }
            for (int i = 0; i < a.Anims.Count; i++)
            {
                if (!Evaluate(a.Anims[i], now, qnow, over, out bool moving)) { a.Anims.RemoveAt(i--); continue; }
                ticking |= moving;
            }
            // the layout is asked for only when a length differs from the last frame (also once when the last one leaves)
            bool lengthsChanged = false;
            int lengths = 0;
            foreach (var kv in over)
            {
                if (kv.Value.Kind != AnimKind.Length) { continue; }
                lengths++;
                if (!a.LayoutPrev.TryGetValue(kv.Key, out var before) || !before.Equals(kv.Value)) { lengthsChanged = true; }
            }
            if (lengthsChanged || lengths != a.LayoutPrev.Count)
            {
                lengthsChanged = true;
                a.LayoutPrev.Clear();
                foreach (var kv in over) { if (kv.Value.Kind == AnimKind.Length) { a.LayoutPrev[kv.Key] = kv.Value; } }
            }
            layoutTouched = lengthsChanged;

            if (over.Count == 0)
            {
                a.Clone = a.CloneOf = null;
                a.Overridden.Clear();
                if (ticking) { active.Add(e); }
                else
                {
                    active.Remove(e);
                    if (a.Tweens.Count == 0 && a.Anims.Count == 0) { e.Anim = null; }
                }
                return bas;
            }

            var clone = a.Clone;
            // box-shadow, text-decoration-color and outline-color are written as text, and the first two are cached per style object (DecorSpec, TextDecor):
            // while one of them moves, or has just stopped moving, the clone is made anew each frame, so the caches read the new values. outline-color is read
            // as text first (currentcolor or not, OutlineSpec), so its animated colour is written as text too
            bool textual = over.ContainsKey("box-shadow") || over.ContainsKey("text-decoration-color") || over.ContainsKey("outline-color")
                || a.Overridden.Contains("box-shadow") || a.Overridden.Contains("text-decoration-color") || a.Overridden.Contains("outline-color");
            if (clone == null || !ReferenceEquals(a.CloneOf, bas) || textual)
            {
                clone = bas.CloneBare();
                a.Clone = clone;
                a.CloneOf = bas;
                a.Overridden.Clear();
            }
            foreach (var prop in a.Overridden) { if (!over.ContainsKey(prop)) { clone.ClearOverride(prop); } }
            a.Overridden.Clear();
            foreach (var kv in over)
            {
                if (kv.Key == ShadowColorProp) { }   // written with the shadow below
                else if (kv.Key == "box-shadow") { WriteShadow(clone, kv.Value, over.TryGetValue(ShadowColorProp, out var sc) ? sc : Val.Col(Rgba.Transparent)); }
                else if (kv.Key == "text-decoration-color" || kv.Key == "outline-color") { clone.Values[kv.Key] = CssRgba(kv.Value.ToRgba()); }
                else { Apply(clone, kv.Key, kv.Value); }
                a.Overridden.Add(kv.Key);
            }
            if (ticking) { active.Add(e); } else { active.Remove(e); }
            return clone;
        }

        private static void Apply(ComputedStyle s, string prop, in Val v)
        {
            switch (v.Kind)
            {
                case AnimKind.Number: s.SetNumber(prop, v.A); break;
                case AnimKind.Color: s.SetColor(prop, v.ToRgba()); break;
                case AnimKind.Length: s.SetLength(prop, new Length(v.A, v.Unit)); break;
                case AnimKind.Pair: s.SetPair(prop, v.A, v.B); break;
            }
        }

        /// <summary>Writes the values of one keyframe animation at the time. False: it has ended without a fill (remove it).
        /// moving: false once it only holds its end state.</summary>
        private static bool Evaluate(Running r, double now, double qnow, Dictionary<string, Val> over, out bool moving)
        {
            var def = r.Def;
            moving = true;
            double p = (now - r.Start - def.Delay) / Math.Max(def.Dur, 1e-4);
            if (p < 0)
            {
                if (def.Fill is AnimFill.Backwards or AnimFill.Both) { Emit(r, 0, 0, over); }
                return true;
            }
            if (!double.IsInfinity(def.Iterations) && p >= def.Iterations)
            {
                if (def.Fill is not (AnimFill.Forwards or AnimFill.Both)) { return false; }
                double whole = Math.Floor(def.Iterations);
                double frac = def.Iterations - whole;
                int last = frac > 0 ? (int)whole : Math.Max(0, (int)whole - 1);
                Emit(r, last, frac > 0 ? (float)frac : 1f, over);
                moving = false;
                return true;
            }
            int iter = (int)Math.Floor(p);
            double pq = (qnow - r.Start - def.Delay) / Math.Max(def.Dur, 1e-4);   // the same, at the grid time the lengths use
            int iterQ = pq < 0 ? 0 : (int)Math.Floor(pq);
            Emit(r, iter, (float)(p - iter), iterQ, pq < 0 ? 0f : (float)(pq - iterQ), over);
            return true;
        }

        private static void Emit(Running r, int iteration, float local, Dictionary<string, Val> over) => Emit(r, iteration, local, iteration, local, over);

        /// <summary>Lengths take the grid time's position (iterationQ, localQ), everything else the real one.</summary>
        private static void Emit(Running r, int iteration, float local, int iterationQ, float localQ, Dictionary<string, Val> over)
        {
            float Progress(int it, float lo)
            {
                bool reverse = r.Def.Dir switch
                {
                    AnimDir.Reverse => true,
                    AnimDir.Alternate => it % 2 == 1,
                    AnimDir.AlternateReverse => it % 2 == 0,
                    _ => false,
                };
                return reverse ? 1 - lo : lo;
            }
            float p = Progress(iteration, local), pq = Progress(iterationQ, localQ);
            foreach (var kv in r.Tracks) { over[kv.Key] = Sample(kv.Value, kv.Value[0].V.Kind == AnimKind.Length ? pq : p, r.Def.Ease); }
        }

        private static Val Sample(List<(float Offset, Val V)> track, float p, Ease ease)
        {
            for (int i = 0; i < track.Count - 1; i++)
            {
                var (o0, v0) = track[i];
                var (o1, v1) = track[i + 1];
                if (p > o1 && i < track.Count - 2) { continue; }
                float span = o1 - o0;
                float local = span <= 0 ? 1 : Math.Clamp((p - o0) / span, 0, 1);
                return Val.Lerp(v0, v1, ease.At(local));
            }
            return track[track.Count - 1].V;
        }

        // ---------------------------------------------------------------- values

        internal static bool TryVal(ComputedStyle s, string prop, out Val v) => TryParse(s, prop, s.Get(prop), out v);

        internal static bool TryParse(ComputedStyle s, string prop, string text, out Val v)
        {
            v = default;
            if (!Props.TryGetValue(prop, out var kind)) { return false; }
            text = text?.Trim() ?? "";
            switch (kind)
            {
                case AnimKind.Number:
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { return false; }
                    v = Val.Num(f);
                    return true;
                case AnimKind.Color:
                    // currentcolor (the default of outline-color and text-decoration-color) is the element's colour, so it can change in a transition
                    if (text.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) { v = Val.Col(s.GetColor("color")); return true; }
                    if (!Rgba.TryParse(text, out var c)) { return false; }
                    v = Val.Col(c);
                    return true;
                case AnimKind.Shadow:
                {
                    // none is a shadow of zero size and no colour (the colour is then transparent, see ShadowColor)
                    if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) { v = Val.Shadow(0, 0, 0, 0); return true; }
                    if (!BoxShadow.TryParse(text, out var sh, out _) || sh.IsNone) { return false; }
                    v = Val.Shadow(Px(s, sh.X), Px(s, sh.Y), Math.Max(0, Px(s, sh.Blur)), Px(s, sh.Spread));
                    return true;
                }
                case AnimKind.Length:
                {
                    if (!Length.TryParse(text, out var l)) { return false; }
                    l = s.ToPx(text);
                    if (l.Unit is not (Unit.Px or Unit.Percent)) { return false; }
                    v = Val.Len(l.Value, l.Unit);
                    return true;
                }
                default:
                {
                    var parts = StyleEngine.SplitSpaces(text);
                    if (parts.Count == 0 || parts.Count > 2) { return false; }
                    if (parts[0].Equals("none", StringComparison.OrdinalIgnoreCase)) { v = Val.Pair(0, 0); return true; }
                    var x = s.ToPx(parts[0]);
                    var y = parts.Count > 1 ? s.ToPx(parts[1]) : Length.Zero;
                    if (x.Unit != Unit.Px || y.Unit != Unit.Px) { return false; }
                    v = Val.Pair(x.Value, y.Value);
                    return true;
                }
            }
        }
    }

    /// <summary>The typewriter effect's state of one element (the painter keeps it): how much of a text is shown at a time.</summary>
    internal sealed class TypeState
    {
        private double start;
        private ulong sig;
        private bool started;
        public bool Done { get; private set; }

        /// <summary>The number of characters to show now; int.MaxValue once the whole text is out. A text that differs from the last one
        /// (a different signature) starts over.</summary>
        public int Visible(ulong signature, int total, double now, float charsPerSecond)
        {
            if (!started || signature != sig) { started = true; sig = signature; start = now; Done = false; }
            if (Done) { return int.MaxValue; }
            int n = (int)Math.Max(0, (now - start) * charsPerSecond);
            if (n >= total) { Done = true; return int.MaxValue; }
            return n;
        }

        /// <summary>Shows the whole text now (the player pressed the confirm key).</summary>
        public void Skip() => Done = true;

        public static ulong Mix(ulong hash, string text)
        {
            foreach (char c in text) { hash = (hash ^ c) * 1099511628211UL; }
            return (hash ^ 0xFF) * 1099511628211UL;
        }

        public const ulong Seed = 14695981039346656037UL;
    }
}
