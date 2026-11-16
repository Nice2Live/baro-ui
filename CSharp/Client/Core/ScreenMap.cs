using System;

namespace BaroUI.Core
{
    /// <summary>Where an element's layout pixels land on the screen: the one place that knows the painter's order. The Painter (paint and hit test) calls
    /// the per-box steps here as it walks down; <see cref="Of"/> walks the same steps from the root to one element, so a text field's rectangle, the IME,
    /// a tooltip, the router and the mouse agree with the picture at any depth of <c>translate</c>, FLIP offsets, scroll and nested <c>scale</c>.
    /// The order, per box from the root down: its translate (the numeric override or the style) and its FLIP offset move it and everything in it; then its
    /// <c>scale</c> about its own corner after that move; then, for its children only, the scroll of a clipping box is taken off. Pure arithmetic, no game types.</summary>
    internal static class ScreenMap
    {
        /// <summary>Step 1 for a box: its translate and FLIP offset are added to the offset it is painted at (dx, dy); fw, fh: the animated growth of its size.</summary>
        public static void Place(Element el, ComputedStyle s, ref float dx, ref float dy, out float fw, out float fh)
        {
            fw = fh = 0;
            var (tx, ty) = UiTransform.TranslateOf(el, s);
            dx += tx;
            dy += ty;
            if (el.Flip != null) { dx += el.FlipX; dy += el.FlipY; fw = el.FlipW; fh = el.FlipH; }
        }

        /// <summary>Step 1b, for a box with position: sticky (LayoutBox.Sticky), after its translate: the vertical offset that keeps it at its top: or bottom
        /// inside the nearest scrolling ancestor (overflow-y other than visible), and never out of its containing block. dy: the offset the box is painted at
        /// before this step (its own translate and the steps of its ancestors included). 0 when there is no scrolling ancestor. Layout does not change: the
        /// box keeps its layout place, only the paint and the hit test move it. The scrolling ancestor's visible part is its padding box as painted (its own
        /// offset, not its scroll); the containing block is the parent's content box as its children are painted (the parent's scroll taken off), and a scrolling
        /// parent does not limit the box (its scrollable height is not known here).</summary>
        public static float StickyOffset(Element el, LayoutBox b, float dy, Func<Element, LayoutBox> boxOf, Func<Element, float> scrollOf, Func<Element, bool> isOwnRoot)
        {
            if (b == null || !b.Sticky || el == null || el.Parent == null) { return 0; }
            var s = el.Style ?? b.Style;
            Element scroller = null;
            LayoutBox sb = null;
            for (var p = el.Parent; p != null; p = p.Parent)
            {
                var pb = boxOf(p);
                if (pb != null && pb.ClipY) { scroller = p; sb = pb; break; }
            }
            if (scroller == null) { return 0; }
            if (!Of(scroller, boxOf, scrollOf, isOwnRoot, out var sp)) { return 0; }
            float viewTop = sb.Y + sp.Dy + sb.Border.Top;
            float viewBottom = sb.Y + sp.Dy + sb.H - sb.Border.Bottom;
            var parent = el.Parent;
            var pbox = boxOf(parent);
            if (pbox == null || !Of(parent, boxOf, scrollOf, isOwnRoot, out var pp)) { return 0; }
            // a scrolling parent: its scrollable height is not known here (its box is the visible part), so the block does not limit the box
            float childrenDy = ScrollStep(pp.Dy, scrollOf(parent));
            float blockTop = pbox.ClipY ? float.NegativeInfinity : pbox.Y + childrenDy + pbox.Border.Top + pbox.Padding.Top;
            float blockBottom = pbox.ClipY ? float.PositiveInfinity : pbox.Y + childrenDy + pbox.H - pbox.Border.Bottom - pbox.Padding.Bottom;
            float natural = b.Y + dy;
            float h = b.H;
            var top = Edge(s, "top", viewBottom - viewTop);
            if (top.HasValue)
            {
                float want = viewTop + top.Value;
                float off = natural < want ? want - natural : 0;
                return Math.Min(off, blockBottom - h - natural);
            }
            var bottom = Edge(s, "bottom", viewBottom - viewTop);
            if (bottom.HasValue)
            {
                float want = viewBottom - bottom.Value - h;
                float off = natural > want ? want - natural : 0;
                return Math.Max(off, blockTop - natural);
            }
            return 0;
        }

        /// <summary>A sticky edge in px (px, u and em are px already; a percentage of basis); null for auto.</summary>
        private static float? Edge(ComputedStyle s, string prop, float basis)
        {
            var l = s.GetLength(prop);
            return l.Unit switch
            {
                Unit.Px => l.Value,
                Unit.Percent => basis * l.Value / 100f,
                Unit.None => l.Value == 0 ? 0f : null,   // a bare 0
                _ => null,
            };
        }

        /// <summary>Step 2: the scale of a box with offset (dx, dy) already placed: about its corner after its translate, in the coordinates it is painted in.</summary>
        public static UiTransform ScaleAbout(LayoutBox b, float dx, float dy, float scale) => UiTransform.Scale(b.X + dx, b.Y + dy, scale);

        /// <summary>Step 3: the vertical offset its children are painted at inside a scrolled box.</summary>
        public static float ScrollStep(float dy, float scrollY) => dy - scrollY;

        /// <summary>One element's place: <see cref="M"/> maps the coordinates the element is painted in (layout px plus (Dx, Dy)) to the screen, scale of the
        /// element itself included; (Fw, Fh) is its animated growth. Scroll of the areas around it is already in Dy.</summary>
        public readonly struct Placement
        {
            public readonly UiTransform M;
            public readonly float Dx, Dy, Fw, Fh;
            public Placement(UiTransform m, float dx, float dy, float fw, float fh) { M = m; Dx = dx; Dy = dy; Fw = fw; Fh = fh; }

            public static Placement None => new(UiTransform.Identity, 0, 0, 0, 0);

            /// <summary>From the element's own layout px (a box's X/Y/W/H) to screen px: the offset, then M.</summary>
            public UiTransform ToScreen => M.After(new UiTransform(1, Dx, Dy));
        }

        /// <summary>The place of e, walking its ancestors from the root. boxOf: the layout box of an element (null: none, such an ancestor is skipped);
        /// scrollOf: the vertical scroll of a scroll area (0 when it has none); isOwnRoot: an element painted on its own, over everything, from a zero offset
        /// with no ancestor (an open modal dialog), the walk starts there. False: e has no box.</summary>
        public static bool Of(Element e, Func<Element, LayoutBox> boxOf, Func<Element, float> scrollOf, Func<Element, bool> isOwnRoot, out Placement place)
        {
            place = Placement.None;
            if (e == null || boxOf(e) == null) { return false; }
            var m = UiTransform.Identity;
            float dx = 0, dy = 0, fw = 0, fh = 0;
            Walk(e, e, boxOf, scrollOf, isOwnRoot, ref m, ref dx, ref dy, ref fw, ref fh);
            place = new Placement(m, dx, dy, fw, fh);
            return true;
        }

        /// <summary>The ancestors first (down from the root), then the element itself: the order the painter nests its calls in.</summary>
        private static void Walk(Element a, Element e, Func<Element, LayoutBox> boxOf, Func<Element, float> scrollOf, Func<Element, bool> isOwnRoot,
            ref UiTransform m, ref float dx, ref float dy, ref float fw, ref float fh)
        {
            if (a.Parent != null && !(isOwnRoot != null && isOwnRoot(a))) { Walk(a.Parent, e, boxOf, scrollOf, isOwnRoot, ref m, ref dx, ref dy, ref fw, ref fh); }
            var b = boxOf(a);
            if (b == null) { return; }
            var s = a.Style ?? b.Style;
            Place(a, s, ref dx, ref dy, out fw, out fh);
            dy += StickyOffset(a, b, dy, boxOf, scrollOf, isOwnRoot);
            float sc = UiTransform.ScaleOf(a, s);
            if (sc != 1) { m = m.After(ScaleAbout(b, dx, dy, sc)); }
            if (a != e) { dy = ScrollStep(dy, scrollOf(a)); }
        }
    }
}
