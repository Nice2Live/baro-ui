using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BaroUI.Core
{
    /// <summary>The pixel size of the picture an element shows (an img's src), for the layout of an img without a CSS width or height. The Render side fills it
    /// (Painter, which reads the file through the window's picture cache); Core reads no files. False: no picture (no src, not found): the img is 0 x 0.</summary>
    internal interface IImageSizes
    {
        bool TryGetSize(Element e, out int width, out int height);
    }

    /// <summary>Box layout (docs/ui/ENGINE_PLAN.md, stage 2): block flow with margin collapsing, inline runs with
    /// line breaking and baselines, flex (wrap, grow/shrink with min/max freezing, auto margins), grid (px/%/fr/auto,
    /// repeat, span), relative and absolute positioning, overflow extents. Output: a LayoutBox tree in absolute px.</summary>
    internal sealed class LayoutEngine
    {
        private const float Eps = 0.01f;
        private readonly ITextMeasurer m;
        public readonly List<Diagnostic> Diagnostics = new();
        private readonly HashSet<string> warned = new();

        public LayoutEngine(ITextMeasurer measurer) { m = measurer; }

        /// <summary>Where the natural size of an img's picture comes from (the Render side fills it: it reads the file). Null: an img without a CSS size is 0 x 0.</summary>
        public IImageSizes Images { get; set; }

        /// <summary>The box of every element that has one, kept up to date by Run and Update.</summary>
        public IReadOnlyDictionary<Element, LayoutBox> Boxes => boxes;
        private readonly Dictionary<Element, LayoutBox> boxes = new();
        private LayoutBox root;

        /// <summary>Builds the box tree and lays it out; (ox, oy) moves the result (a centred canvas).</summary>
        public LayoutBox Run(Document doc, float viewportWidth, float viewportHeight, float ox = 0, float oy = 0)
        {
            boxes.Clear();
            root = Build(doc.Root) ?? new LayoutBox { Element = doc.Root, Style = doc.Root.Style };
            root.BfcRoot = true;
            Layout(root, viewportWidth, viewportHeight, viewportWidth, viewportHeight, shrink: false);
            FixUp(root);
            Finish(root, ox, oy, root);
            return root;
        }

        /// <summary>Stage 7, incremental layout: rebuilds only the boxes of the changed elements (text, visibility,
        /// children, layout-affecting style) and lays the tree out again. Boxes outside the changed paths keep their
        /// previous result when their constraints are the same (Layout's memo), so a changed number in one row costs
        /// that row and its ancestors, not the page. children: containers whose element children changed (added, removed, moved): their child boxes are
        /// brought in place (SyncChildren), so a card added to a page of 300 costs the card and its ancestors. Same result as Run (checked by tools/ui_test).</summary>
        public LayoutBox Update(IEnumerable<Element> changed, float viewportWidth, float viewportHeight, float ox = 0, float oy = 0, IEnumerable<Element> children = null)
        {
            if (root == null) { throw new InvalidOperationException("Update before Run"); }
            Restore(root);
            rebuilt.Clear();
            if (children != null) { foreach (var c in children) { SyncOrRebuild(c); } }
            foreach (var e in changed) { Rebuild(e); }
            Layout(root, viewportWidth, viewportHeight, viewportWidth, viewportHeight, shrink: false);
            FixUp(root);
            Finish(root, ox, oy, root);
            return root;
        }

        /// <summary>Puts every box and fragment back to its parent-relative position (what Finish made absolute).</summary>
        private static void Restore(LayoutBox b)
        {
            b.X = b.RelX;
            b.Y = b.RelY;
            foreach (var f in b.Text) { f.X = f.RelX; f.Y = f.RelY; f.Baseline = f.RelBaseline; }
            foreach (var c in b.Children) { Restore(c); }
        }

        private readonly HashSet<LayoutBox> rebuilt = new();

        private void Rebuild(Element e)
        {
            var top = e;
            while (top?.Parent != null) { top = top.Parent; }
            if (top != root.Element) { return; }   // removed from the page (a data-for copy): its parent is in the list too
            // the nearest element with a box of its own: not an inline-block inside an anonymous run (the run's
            // items point at it) and not an inline span (its words live in the block's run)
            LayoutBox old = null;
            for (var a = e; a != null; a = a.Parent)
            {
                if (!boxes.TryGetValue(a, out old)) { continue; }
                while (old.Parent != null && (old.Parent.Element == null || old.IsAtom))
                {
                    old = old.Parent.Element != null ? old.Parent : old.Parent.Parent;
                }
                break;
            }
            if (old == null || old.Parent == null || old == root) { RebuildRoot(); return; }
            for (var p = old; p != null; p = p.Parent) { if (rebuilt.Contains(p)) { return; } }   // inside a box rebuilt already
            var parent = old.Parent;
            int at = parent.Children.IndexOf(old);
            if (at < 0) { return; }   // already replaced by another change in this update
            Forget(old);
            var fresh = Build(old.Element);
            // a block that vanishes from block flow may leave two inline runs that must merge: rebuild the parent
            if (fresh == null && parent.Kind == BoxKind.Flow && parent.Element != null) { Rebuild(parent.Element); return; }
            if (fresh == null) { parent.Children.RemoveAt(at); }
            else
            {
                if (parent.Kind is BoxKind.Flex or BoxKind.Grid && !fresh.OutOfFlow) { fresh.BfcRoot = true; }
                fresh.Parent = parent;
                parent.Children[at] = fresh;
                rebuilt.Add(fresh);
            }
            for (var p = parent; p != null; p = p.Parent) { p.LastLayout = p.LastCall = null; p.SizeCount = 0; p.MinContent = p.MaxContent = null; }
        }

        /// <summary>The children of a container changed (a card added or removed, a row moved in or out): its child boxes are brought to its element children
        /// in place. A child box whose element left is dropped, a new element child is built where it belongs, and the other children keep their boxes (a
        /// page of 300 cards in one container does not build them again). False: a child box is not an element's, or the order changed; nothing is
        /// guaranteed then and the caller rebuilds the container.</summary>
        private bool SyncChildren(LayoutBox box)
        {
            var el = box.Element;
            if (el.PseudoBefore?.Style != null || el.PseudoAfter?.Style != null) { return false; }   // the pseudo boxes are not element children: rebuild
            var kids = box.Children;
            var dom = new List<Element>();
            foreach (var n in el.Children)
            {
                if (n is Element d) { dom.Add(d); }
                else if (n is not TextNode t || !string.IsNullOrWhiteSpace(t.Text)) { return false; }   // text runs: not in this path
            }
            int i = 0, j = 0;
            bool changed = false;
            while (i < kids.Count || j < dom.Count)
            {
                if (i < kids.Count && kids[i].Element == null) { return false; }   // an anonymous run or an atom of text
                if (i < kids.Count && j < dom.Count && ReferenceEquals(kids[i].Element, dom[j])) { i++; j++; continue; }
                if (i < kids.Count && kids[i].Element.Parent != el)
                {
                    Forget(kids[i]);   // its element left this container
                    kids.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (j < dom.Count && !boxes.ContainsKey(dom[j]))
                {
                    var fresh = Build(dom[j]);   // a new element, or one shown now (display none before): built where it belongs
                    if (fresh != null)
                    {
                        if (box.Kind is BoxKind.Flex or BoxKind.Grid && !fresh.OutOfFlow) { fresh.BfcRoot = true; }
                        fresh.Parent = box;
                        kids.Insert(i, fresh);
                        rebuilt.Add(fresh);
                        i++;
                        changed = true;
                    }
                    j++;
                    continue;
                }
                return false;   // the next element child has a box elsewhere, or the child boxes are out of the element's order
            }
            if (changed) { for (var p = box; p != null; p = p.Parent) { p.LastLayout = p.LastCall = null; p.SizeCount = 0; p.MinContent = p.MaxContent = null; } }
            return true;
        }

        /// <summary>SyncChildren, or the container rebuilt when that is not possible (Rebuild of the container's own element).</summary>
        private void SyncOrRebuild(Element container)
        {
            if (!boxes.TryGetValue(container, out var box) || box.Element == null) { Rebuild(container); return; }
            for (var p = box; p != null; p = p.Parent) { if (rebuilt.Contains(p)) { return; } }   // inside a box rebuilt already
            if (!SyncChildren(box)) { Rebuild(container); }
        }

        private void RebuildRoot()
        {
            var doc = root.Element;
            rebuilt.Clear();
            boxes.Clear();
            root = Build(doc) ?? new LayoutBox { Element = doc, Style = doc.Style };
            root.BfcRoot = true;
            rebuilt.Add(root);
        }

        private void Forget(LayoutBox b)
        {
            if (b.Element != null && boxes.TryGetValue(b.Element, out var mine) && mine == b) { boxes.Remove(b.Element); }
            foreach (var c in b.Children) { Forget(c); }
        }

        private void Warn(Element e, string message)
        {
            if (warned.Add(message)) { Diagnostics.Add(new Diagnostic("layout", e?.Line ?? 0, message)); }
        }

        // ---------------------------------------------------------------- box tree

        private LayoutBox Build(Element e)
        {
            var s = e.Style;
            if (s == null) { return null; }
            string display = s.Get("display");
            if (display == "none" || e.Hidden) { return null; }
            var b = new LayoutBox { Element = e, Style = s };
            if (Images != null && (e.Tag == "img" || e.Tag == "video") && Images.TryGetSize(e, out int naturalW, out int naturalH)) { b.NaturalW = naturalW; b.NaturalH = naturalH; }
            boxes[e] = b;
            b.Kind = display is "flex" or "inline-flex" ? BoxKind.Flex : display is "grid" or "inline-grid" ? BoxKind.Grid : BoxKind.Flow;
            string position = s.Get("position");
            b.OutOfFlow = position is "absolute" or "fixed";
            b.Sticky = position == "sticky";
            b.ClipX = s.Get("overflow-x") != "visible";
            b.ClipY = s.Get("overflow-y") != "visible";
            if (b.OutOfFlow || b.ClipX || b.ClipY || display == "flow-root") { b.BfcRoot = true; }
            if (display is "table" or "table-row" or "table-cell" or "list-item")
            {
                Warn(e, $"display: {display} is laid out as block");
            }

            if (b.Kind == BoxKind.Flow)
            {
                var run = new List<InlineItem>();
                ComputedStyle space = null;
                AddFlowKids(b, e, run, ref space);
                FlushRun(b, run);
            }
            else
            {
                if (e.PseudoBefore is { Style: not null } pbefore) { AttachFlexChild(b, pbefore); }
                foreach (var child in e.Children)
                {
                    if (child is Element ce) { AttachFlexChild(b, ce); }
                    else if (child is TextNode t && !string.IsNullOrWhiteSpace(t.Text))
                    {
                        // anonymous flex/grid item around loose text
                        var run = new List<InlineItem>();
                        ComputedStyle space = null;
                        AddText(run, t.Text, e.Style, e, ref space);
                        IndentItem.Add(run, e.Style);
                        var anon = new LayoutBox { Kind = BoxKind.InlineRun, Style = e.Style, Inline = run, BfcRoot = true };
                        Attach(b, anon);
                    }
                }
                if (e.PseudoAfter is { Style: not null } pafter) { AttachFlexChild(b, pafter); }
            }
            return b;
        }

        private static void Attach(LayoutBox parent, LayoutBox child)
        {
            child.Parent = parent;
            parent.Children.Add(child);
        }

        /// <summary>A flex or grid item: an element child (or a ::before / ::after box) with its own box.</summary>
        private void AttachFlexChild(LayoutBox b, Element ce)
        {
            var cb = Build(ce);
            if (cb == null) { return; }
            if (!cb.OutOfFlow) { cb.BfcRoot = true; }
            Attach(b, cb);
        }

        /// <summary>The inline content of a flow box in order: its ::before box, its children, its ::after box (see StyleEngine.ResolvePseudo).</summary>
        private void AddFlowKids(LayoutBox b, Element e, List<InlineItem> run, ref ComputedStyle space)
        {
            if (e.PseudoBefore is { Style: not null } before) { AddFlowChild(b, before, run, e, ref space); }
            foreach (var child in e.Children) { AddFlowChild(b, child, run, e, ref space); }
            if (e.PseudoAfter is { Style: not null } after) { AddFlowChild(b, after, run, e, ref space); }
        }

        private void AddFlowChild(LayoutBox b, Node node, List<InlineItem> run, Element owner, ref ComputedStyle space)
        {
            if (node is TextNode t) { AddText(run, t.Text, owner.Style, owner, ref space); return; }
            var e = (Element)node;
            var s = e.Style;
            if (s == null) { return; }
            string display = s.Get("display");
            if (display == "none" || e.Hidden) { return; }
            if (e.Tag == "br") { run.Add(new BreakItem { Style = s }); space = null; return; }
            bool outOfFlow = s.Get("position") is "absolute" or "fixed";
            if (outOfFlow)
            {
                var ab = Build(e);
                if (ab != null) { Attach(b, ab); }
                return;
            }
            if (display == "inline")
            {
                AddFlowKids(b, e, run, ref space);
                return;
            }
            if (display.StartsWith("inline", StringComparison.Ordinal))
            {
                var atom = Build(e);
                if (atom == null) { return; }
                atom.IsAtom = atom.BfcRoot = true;
                run.Add(new AtomItem { Box = atom, Style = s, SpaceBefore = space != null, SpaceStyle = space });
                space = null;
                return;
            }
            FlushRun(b, run);
            space = null;
            var block = Build(e);
            if (block != null) { Attach(b, block); }
        }

        private static void FlushRun(LayoutBox b, List<InlineItem> run)
        {
            if (run.Count == 0) { return; }
            bool content = false;
            foreach (var i in run) { if (i is WordItem || i is AtomItem) { content = true; break; } }
            if (content)
            {
                var anon = new LayoutBox { Kind = BoxKind.InlineRun, Style = b.Style, Inline = new List<InlineItem>(run) };
                if (!b.Children.Exists(c => !c.OutOfFlow)) { IndentItem.Add(anon.Inline, b.Style); }   // text-indent: the first line of the block only
                foreach (var i in run) { if (i is AtomItem a) { Attach(anon, a.Box); } }
                Attach(b, anon);
            }
            run.Clear();
        }

        private static void AddText(List<InlineItem> run, string text, ComputedStyle style, Element owner, ref ComputedStyle space)
        {
            string ws = style.Get("white-space");
            string transform = style.Get("text-transform");
            if (ws is "pre" or "pre-wrap" or "pre-line" or "break-spaces")
            {
                // every newline is a line break; pre keeps a line as one unbreakable word, the others break it at the spaces
                // (runs of spaces collapse in them, which pre-wrap should keep: the engine has no use for that yet)
                bool wrap = ws is "pre-wrap" or "pre-line" or "break-spaces";
                var lines = text.Replace("\r", "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) { run.Add(new BreakItem { Style = style }); }
                    if (wrap) { space = null; AddWords(run, lines[i], style, owner, transform, ref space); space = null; }
                    else if (lines[i].Length > 0) { run.Add(new WordItem { Text = Transform(lines[i], transform), Style = style, Owner = owner, SpaceBefore = false }); }
                }
                space = null;
                return;
            }
            AddWords(run, text, style, owner, transform, ref space);
        }

        private static void AddWords(List<InlineItem> run, string text, ComputedStyle style, Element owner, string transform, ref ComputedStyle space)
        {
            int k = 0;
            bool glue = RuGlue(text, style, owner);
            WordItem open = null;      // the glued item being built (Russian pairs, Core/RuText.cs), its last raw word in lastRaw
            string lastRaw = null;
            while (k < text.Length)
            {
                if (IsSpace(text[k])) { space ??= style; k++; continue; }
                int start = k;
                while (k < text.Length && !IsSpace(text[k])) { k++; }
                if (!glue)
                {
                    run.Add(new WordItem { Text = Transform(text.Substring(start, k - start), transform), Style = style, Owner = owner, SpaceBefore = space != null, SpaceStyle = space });
                    space = null;
                    continue;
                }
                string raw = text.Substring(start, k - start);
                if (open != null && space != null && RuText.Glue(lastRaw, raw))
                {
                    // one unbreakable item with an ordinary space inside: the text stays as written, only no line break can fall here
                    open.Text = string.Concat(open.Text, " ", Transform(raw, transform));
                }
                else
                {
                    open = new WordItem { Text = Transform(raw, transform), Style = style, Owner = owner, SpaceBefore = space != null, SpaceStyle = space };
                    run.Add(open);
                }
                lastRaw = raw;
                space = null;
            }
        }

        /// <summary>Whether the Russian non-breaking pairs apply to a text of this element: -bbe-ru-glue on/off, or auto (the default): lang of the
        /// element or the nearest ancestor that has one is ru (or there is none) and the text has Cyrillic letters.</summary>
        private static bool RuGlue(string text, ComputedStyle style, Element owner)
        {
            string mode = style.Get("-bbe-ru-glue");
            if (mode == "off") { return false; }
            if (!RuText.HasCyrillic(text)) { return false; }
            if (mode == "on") { return true; }
            string lang = null;
            for (var e = owner; e != null && lang == null; e = e.Parent) { lang = e.GetAttribute("lang"); }
            return RuText.LangOn(lang);
        }

        private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        private static string Transform(string s, string transform) => transform switch
        {
            "uppercase" => s.ToUpperInvariant(),
            "lowercase" => s.ToLowerInvariant(),
            "capitalize" => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s,
            _ => s,
        };

        // ---------------------------------------------------------------- shared helpers

        private static float? Len(ComputedStyle s, string prop, float? basis)
        {
            var l = s.GetLength(prop);
            switch (l.Unit)
            {
                case Unit.Px: return l.Value;
                case Unit.None: return l.Value == 0 ? 0 : null;
                case Unit.Percent: return basis.HasValue ? basis.Value * l.Value / 100f : null;
                default: return null;
            }
        }

        private static void ResolveEdges(LayoutBox b, float cbW)
        {
            if (b.Element == null) { b.Margin = b.Border = b.Padding = default; return; }
            var s = b.Style;
            b.Margin = new Edges
            {
                Top = Len(s, "margin-top", cbW) ?? 0, Right = Len(s, "margin-right", cbW) ?? 0,
                Bottom = Len(s, "margin-bottom", cbW) ?? 0, Left = Len(s, "margin-left", cbW) ?? 0,
            };
            b.Padding = new Edges
            {
                Top = Math.Max(0, Len(s, "padding-top", cbW) ?? 0), Right = Math.Max(0, Len(s, "padding-right", cbW) ?? 0),
                Bottom = Math.Max(0, Len(s, "padding-bottom", cbW) ?? 0), Left = Math.Max(0, Len(s, "padding-left", cbW) ?? 0),
            };
            b.Border = new Edges
            {
                Top = BorderWidth(s, "border-top-width"), Right = BorderWidth(s, "border-right-width"),
                Bottom = BorderWidth(s, "border-bottom-width"), Left = BorderWidth(s, "border-left-width"),
            };
        }

        /// <summary>A border is a whole number of device pixels (at least one), the way the painter draws it (Painter.BorderPx): the box
        /// inside the border begins exactly where the border ends, so a child flush with the border edge (a corner bracket, a tab sitting
        /// on a line) has no one-pixel gap or overlap with it at any screen scale.</summary>
        private static float BorderWidth(ComputedStyle s, string prop)
        {
            float w = Math.Max(0, Len(s, prop, null) ?? 0);
            return w <= 0 ? 0 : Math.Max(1f, (float)Math.Floor(w + 0.001f));
        }

        private static bool AutoMargin(LayoutBox b, string side) => b.Element != null && b.Style.GetLength(side switch
        {
            "left" => "margin-left", "right" => "margin-right", "top" => "margin-top", _ => "margin-bottom",
        }).IsAuto;

        /// <summary>A specified width/height as a border-box size, or null (auto, or % of an indefinite size).</summary>
        private static float? SpecSize(LayoutBox b, string prop, float? basis)
        {
            if (b.Element == null) { return null; }
            float? v = Len(b.Style, prop, basis);
            if (v == null) { return prop is "width" or "height" ? NaturalSize(b, prop) : null; }   // min-/max- sides have no natural size
            if (b.Style.Get("box-sizing") == "content-box")
            {
                v += prop.Contains("width") ? b.Padding.Horizontal + b.Border.Horizontal : b.Padding.Vertical + b.Border.Vertical;
            }
            return v;
        }

        /// <summary>A side the CSS leaves to auto, of an img with a picture: the picture's pixels (IImageSizes), or, when the other side is given in px, that side
        /// with the picture's proportions (width: 100px alone gives the height of the picture at that width). Null for anything else.</summary>
        private static float? NaturalSize(LayoutBox b, string prop)
        {
            if (b.NaturalW <= 0 || b.NaturalH <= 0) { return null; }
            bool width = prop == "width";
            var other = Len(b.Style, width ? "height" : "width", null);
            if (other is float o && o > 0)
            {
                return width ? o * b.NaturalW / b.NaturalH : o * b.NaturalH / b.NaturalW;
            }
            return width ? b.NaturalW : b.NaturalH;
        }

        private static float ClampW(LayoutBox b, float w, float? cbW, float autoMin = 0)
        {
            float min = SpecSize(b, "min-width", cbW) ?? autoMin;
            float max = SpecSize(b, "max-width", cbW) ?? float.PositiveInfinity;
            return Math.Max(Math.Max(Math.Min(w, max), min), b.Padding.Horizontal + b.Border.Horizontal);
        }

        private static float ClampH(LayoutBox b, float h, float? cbH, float autoMin = 0)
        {
            float min = SpecSize(b, "min-height", cbH) ?? autoMin;
            float max = SpecSize(b, "max-height", cbH) ?? float.PositiveInfinity;
            return Math.Max(Math.Max(Math.Min(h, max), min), b.Padding.Vertical + b.Border.Vertical);
        }

        private float LineHeight(ComputedStyle s)
        {
            if (s.LineHeightOf == m) { return s.LineHeightPx; }
            s.LineHeightPx = LineHeightOnce(s);
            s.LineHeightOf = m;
            return s.LineHeightPx;
        }

        private float LineHeightOnce(ComputedStyle s)
        {
            string v = s.Get("line-height");
            if (v == "normal" || !Length.TryParse(v, out var l)) { return m.NormalLineHeight(s); }
            return l.Unit == Unit.None ? l.Value * s.FontSizePx : s.ToPx(v).Unit == Unit.Px ? s.ToPx(v).Value : m.NormalLineHeight(s);
        }

        private static float Collapse(float a, float b) =>
            Math.Max(Math.Max(a, 0), Math.Max(b, 0)) + Math.Min(Math.Min(a, 0), Math.Min(b, 0));

        private static float Gap(ComputedStyle s, string prop, float? basis) => Math.Max(0, Len(s, prop, basis) ?? 0);

        // ---------------------------------------------------------------- dispatch

        /// <summary>Lays out b at the given containing block. forcedW/H: border-box sizes imposed by a flex/grid
        /// parent or the root. shrink: width is shrink-to-fit (atoms, absolutes) instead of filling the line.</summary>
        private void Layout(LayoutBox b, float cbW, float? cbH, float? forcedW, float? forcedH, bool shrink)
        {
            // flex and grid lay an item out several times (measure, then final size); same input, same result.
            // A hit for constraints other than the last real layout's only sets the sizes: FixUp lays the box out
            // for real afterwards if those constraints turn out to be its final ones.
            var key = (cbW, cbH, forcedW, forcedH, shrink);
            b.LastCall = key;
            for (int i = 0; i < b.SizeCount; i++)
            {
                ref var e = ref b.SizeAt(i);
                if (!e.Key.Equals(key)) { continue; }
                b.W = e.W; b.H = e.H; b.Baseline = e.Baseline; b.MarginTopOut = e.MarginTopOut; b.MarginBottomOut = e.MarginBottomOut;
                b.ScrollWidth = e.ScrollWidth; b.ScrollHeight = e.ScrollHeight; b.Margin = e.Margin; b.Border = e.Border; b.Padding = e.Padding;
                return;
            }
            LayoutReal(b, cbW, cbH, forcedW, forcedH, shrink);
            b.LastLayout = key;
            if (b.SizeCount == 2 && b.SizesMore == null) { b.SizesMore = new SizeEntry[2]; }
            int slot = b.SizeCount < 4 ? b.SizeCount++ : (b.SizeNext++ & 3);
            b.SizeAt(slot) = new SizeEntry
            {
                Key = key, W = b.W, H = b.H, Baseline = b.Baseline, MarginTopOut = b.MarginTopOut, MarginBottomOut = b.MarginBottomOut,
                ScrollWidth = b.ScrollWidth, ScrollHeight = b.ScrollHeight, Margin = b.Margin, Border = b.Border, Padding = b.Padding,
            };
        }

        /// <summary>After a pass: a box whose final call was a size-cache hit for other constraints than its last
        /// real layout is laid out for real (same sizes, its children and text move into place), top-down.</summary>
        private void FixUp(LayoutBox b)
        {
            if (b.LastCall.HasValue && (!b.LastLayout.HasValue || !b.LastCall.Value.Equals(b.LastLayout.Value)))
            {
                var (cbW, cbH, fw, fh, shrink) = b.LastCall.Value;
                LayoutReal(b, cbW, cbH, fw, fh, shrink);
                b.LastLayout = b.LastCall;
            }
            foreach (var c in b.Children) { if (!c.OutOfFlow) { FixUp(c); } }
        }

        private void LayoutReal(LayoutBox b, float cbW, float? cbH, float? forcedW, float? forcedH, bool shrink)
        {
            ResolveEdges(b, cbW);
            float w;
            if (forcedW.HasValue) { w = forcedW.Value; }
            else
            {
                float? spec = SpecSize(b, "width", cbW);
                float avail = Math.Max(0, cbW - b.Margin.Horizontal);
                if (spec.HasValue) { w = spec.Value; }
                else if (shrink || b.Kind == BoxKind.InlineRun && b.BfcRoot)
                {
                    Intrinsic(b, out float min, out float max);
                    w = Math.Min(Math.Max(min, avail), max);
                }
                else { w = avail; }
                w = ClampW(b, w, cbW);
            }
            b.W = w;
            b.Baseline = float.NaN;
            float? specH = forcedH ?? (SpecSize(b, "height", cbH) is float sh ? ClampH(b, sh, cbH) : (float?)null);
            switch (b.Kind)
            {
                case BoxKind.InlineRun: LayoutRun(b, b.ContentWidth); b.H = specH ?? b.H; break;
                case BoxKind.Flow: LayoutFlow(b, cbH, specH); break;
                case BoxKind.Flex: LayoutFlex(b, cbH, specH); break;
                case BoxKind.Grid: LayoutGrid(b, cbH, specH); break;
            }
            b.MarginTopOut = b.Kind == BoxKind.Flow ? b.MarginTopOut : b.Margin.Top;
            b.MarginBottomOut = b.Kind == BoxKind.Flow ? b.MarginBottomOut : b.Margin.Bottom;
            Extent(b);
        }

        private static void Extent(LayoutBox b)
        {
            float right = b.W - b.Border.Horizontal, bottom = b.H - b.Border.Vertical;   // padding box size
            foreach (var c in b.Children)
            {
                if (c.OutOfFlow) { continue; }
                right = Math.Max(right, c.X - b.Border.Left + c.W + c.Margin.Right + b.Padding.Right);
                bottom = Math.Max(bottom, c.Y - b.Border.Top + c.H + c.Margin.Bottom + b.Padding.Bottom);
            }
            foreach (var t in b.Text)
            {
                right = Math.Max(right, t.X - b.Border.Left + t.Width);
                bottom = Math.Max(bottom, t.Y - b.Border.Top + t.Height);
            }
            b.ScrollWidth = right;
            b.ScrollHeight = bottom;
        }

        // ---------------------------------------------------------------- block flow

        private void LayoutFlow(LayoutBox b, float? cbH, float? specH)
        {
            float cw = b.ContentWidth;
            float? contentH = specH.HasValue ? Math.Max(0, specH.Value - b.Padding.Vertical - b.Border.Vertical) : (float?)null;
            bool canTop = !b.BfcRoot && b.Border.Top == 0 && b.Padding.Top == 0;
            bool canBottom = !b.BfcRoot && b.Border.Bottom == 0 && b.Padding.Bottom == 0 && !specH.HasValue;
            float y = 0, pending = 0;
            bool first = true;
            b.MarginTopOut = b.Margin.Top;
            b.MarginBottomOut = b.Margin.Bottom;
            LayoutBox last = null;
            foreach (var c in b.Children)
            {
                if (c.OutOfFlow) { continue; }
                Layout(c, cw, contentH, null, null, shrink: false);
                float top = c.MarginTopOut;
                if (first && canTop) { b.MarginTopOut = Collapse(b.Margin.Top, top); }
                else { y += first ? top : Collapse(pending, top); }
                first = false;
                float x = c.Margin.Left;
                if (c.Element != null && AutoMargin(c, "left") && AutoMargin(c, "right")) { x = Math.Max(0, (cw - c.W) / 2); }
                else if (c.Element != null && AutoMargin(c, "left")) { x = Math.Max(0, cw - c.W - c.Margin.Right); }
                c.X = b.Border.Left + b.Padding.Left + x;
                c.Y = b.Border.Top + b.Padding.Top + y;
                y += c.H;
                pending = c.MarginBottomOut;
                last = c;
            }
            if (last != null)
            {
                if (canBottom) { b.MarginBottomOut = Collapse(b.Margin.Bottom, pending); }
                else { y += pending; }
                if (!float.IsNaN(last.Baseline)) { b.Baseline = last.Y + last.Baseline; }
            }
            b.H = specH ?? ClampH(b, y + b.Padding.Vertical + b.Border.Vertical, cbH);
            if (b.ClipY || b.ClipX) { b.Baseline = float.NaN; }
        }

        // ---------------------------------------------------------------- inline runs

        /// <summary>One item on a line. A struct in reused lists: a layout of a few thousand words allocates
        /// only the fragments it produces.</summary>
        private struct Placed
        {
            public InlineItem Item;
            public float X, Width;
        }

        // scratch for LayoutRun: every nested Layout call happens before they are used, so one set per engine is enough
        private readonly List<Placed> placed = new();
        private readonly List<int> lineStart = new();
        private readonly System.Text.StringBuilder merged = new();

        /// <summary>The width of a text with letter-spacing and word-spacing added (Core/TextDecor.cs): the measurer's width is per (font, text) and stays so,
        /// the spacing is added here, outside its cache.</summary>
        private float TW(string text, ComputedStyle st)
        {
            float w = m.Width(text, st);
            var sp = TextSpacing.Of(st);
            return sp.Any ? w + sp.Extra(text) : w;
        }

        private float ItemWidth(InlineItem it) => it switch
        {
            WordItem w => TW(w.Text, w.Style),
            AtomItem a => a.Box.W + a.Box.Margin.Horizontal,
            _ => 0,
        };

        private void LayoutRun(LayoutBox b, float avail)
        {
            b.Text.Clear();
            var s = b.Style;
            bool nowrap = s.Get("white-space") is "nowrap" or "pre";
            var items = b.Inline;
            for (int k = 0; k < items.Count; k++)
            {
                if (items[k] is AtomItem a) { Layout(a.Box, avail, null, null, null, shrink: true); }
            }

            placed.Clear();
            lineStart.Clear();
            lineStart.Add(0);
            float x = 0;
            int i = 0;
            // text-indent: the marker at the head of the items moves the start of the first line (negative: it hangs out to the left)
            if (items.Count > 0 && items[0] is IndentItem ind) { x = ind.Resolve(avail); i = 1; }
            bool wrapAny = !nowrap && s.Get("overflow-wrap") != "normal";
            while (i < items.Count)
            {
                if (items[i] is BreakItem) { lineStart.Add(placed.Count); x = 0; i++; continue; }
                // a chunk: items glued without a space between them
                int j = i + 1;
                while (j < items.Count && !(items[j] is BreakItem) && !items[j].SpaceBefore) { j++; }
                bool lineEmpty = placed.Count == lineStart[^1];
                float lead = items[i].SpaceBefore && !lineEmpty ? TW(" ", items[i].SpaceStyle ?? items[i].Style) : 0;
                float chunk = 0;
                for (int k = i; k < j; k++) { chunk += ItemWidth(items[k]); }
                if (!lineEmpty && !nowrap && x + lead + chunk > avail + Eps)
                {
                    lineStart.Add(placed.Count);
                    x = 0;
                    lead = 0;
                }
                x += lead;
                for (int k = i; k < j; k++)
                {
                    float w = ItemWidth(items[k]);
                    if (wrapAny && w > avail + Eps && items[k] is WordItem big && big.Style.Get("overflow-wrap") != "normal")
                    {
                        x = BreakWord(big, avail, x);
                        continue;
                    }
                    placed.Add(new Placed { Item = items[k], X = x, Width = w });
                    x += w;
                }
                i = j;
            }
            // a trailing break leaves no empty last line (one line at least)
            if (lineStart.Count > 1 && lineStart[^1] == placed.Count) { lineStart.RemoveAt(lineStart.Count - 1); }
            lineStart.Add(placed.Count);   // sentinel: line n is placed[lineStart[n] .. lineStart[n + 1])
            // line-clamp: n lines stay, the last of them ends with an ellipsis
            int clamp = ClampOf(s);
            bool clamped = clamp > 0 && lineStart.Count - 1 > clamp;
            if (clamped) { lineStart.RemoveRange(clamp + 1, lineStart.Count - clamp - 1); }

            float strutA = m.Ascent(s), strutD = m.Descent(s), strutL = LineHeight(s);
            float y = 0;
            string align = s.Get("text-align");
            bool ellipsis = nowrap && b.Parent != null && (b.Parent.ClipX || b.ClipX) && s.Get("text-overflow") == "ellipsis";
            for (int li = 0; li + 1 < lineStart.Count; li++)
            {
                int from = lineStart[li], to = lineStart[li + 1];
                float half = (strutL - strutA - strutD) / 2;
                float above = strutA + half, below = strutD + half;
                for (int k = from; k < to; k++)
                {
                    var item = placed[k].Item;
                    if (item is WordItem w)
                    {
                        float a = m.Ascent(w.Style), d = m.Descent(w.Style), l = LineHeight(w.Style), h = (l - a - d) / 2;
                        above = Math.Max(above, a + h);
                        below = Math.Max(below, d + h);
                    }
                    else if (item is AtomItem at)
                    {
                        var ab = at.Box;
                        float bl = float.IsNaN(ab.Baseline) ? ab.H + ab.Margin.Bottom : ab.Baseline;
                        above = Math.Max(above, ab.Margin.Top + bl);
                        below = Math.Max(below, ab.H + ab.Margin.Bottom - bl);
                    }
                }
                float baseline = y + above;
                float width = to == from ? 0 : placed[to - 1].X + placed[to - 1].Width;
                bool force = clamped && li == clamp - 1;
                if (force || ellipsis && width > avail + Eps)
                {
                    to = Truncate(from, to, avail, force);
                    width = to == from ? 0 : placed[to - 1].X + placed[to - 1].Width;
                }
                float shift = align switch
                {
                    "center" => (avail - width) / 2,
                    "right" or "end" => avail - width,
                    _ => 0,
                };
                if (shift < 0 && align != "right" && align != "end") { shift = 0; }
                TextFragment prev = null;
                float prevEnd = float.NaN;
                bool building = false;   // merged holds prev's text (more than one word)
                for (int k = from; k < to; k++)
                {
                    var p = placed[k];
                    float px = p.X + shift;
                    if (p.Item is WordItem w)
                    {
                        float a = m.Ascent(w.Style);
                        // merge words of one owner and style into a single draw
                        if (prev != null && prev.Owner == w.Owner && prev.Style == w.Style && Math.Abs(prevEnd - px) < 0.5f + TW(" ", w.Style))
                        {
                            if (!building) { merged.Clear().Append(prev.Text); building = true; }
                            if (px - prevEnd > Eps) { merged.Append(' '); }
                            merged.Append(w.Text);
                            prev.Width = px + p.Width - prev.X;
                        }
                        else
                        {
                            if (building) { prev.Text = merged.ToString(); building = false; }
                            prev = new TextFragment
                            {
                                Text = w.Text, X = px, Width = p.Width, Y = baseline - a, Height = a + m.Descent(w.Style),
                                Baseline = baseline, Style = w.Style, Owner = w.Owner,
                            };
                            b.Text.Add(prev);
                        }
                        prevEnd = px + p.Width;
                    }
                    else if (p.Item is AtomItem at)
                    {
                        if (building) { prev.Text = merged.ToString(); building = false; }
                        var ab = at.Box;
                        float bl = float.IsNaN(ab.Baseline) ? ab.H + ab.Margin.Bottom : ab.Baseline;
                        ab.X = px + ab.Margin.Left;
                        ab.Y = baseline - bl;
                        prev = null;
                    }
                }
                if (building) { prev.Text = merged.ToString(); }
                b.Baseline = baseline;
                y += above + below;
            }
            // fragment coordinates are relative to the run box (it has no edges)
            b.H = y;
        }

        /// <summary>line-clamp of a style: the line count, 0 for none or a value that is not a positive whole number.</summary>
        private static int ClampOf(ComputedStyle s) =>
            int.TryParse(s.Get("line-clamp"), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        /// <summary>overflow-wrap: a word wider than a whole line is placed in pieces: each piece the longest prefix that still fits (one character at
        /// least), a new line after every piece but the last. x is where the word starts on the current line; returns where the last piece ends.</summary>
        private float BreakWord(WordItem w, float avail, float x)
        {
            string rest = w.Text;
            while (true)
            {
                bool lineEmpty = placed.Count == lineStart[^1];
                float room = avail - x;
                int lo = 0, hi = rest.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (TW(rest.Substring(0, mid), w.Style) <= room + Eps) { lo = mid; } else { hi = mid - 1; }
                }
                if (lo == rest.Length)
                {
                    float rw = TW(rest, w.Style);
                    placed.Add(new Placed { Item = ReferenceEquals(rest, w.Text) ? w : Piece(w, rest), X = x, Width = rw });
                    return x + rw;
                }
                if (lo == 0 && !lineEmpty) { lineStart.Add(placed.Count); x = 0; continue; }
                if (lo == 0) { lo = 1; }
                if (lo < rest.Length && char.IsLowSurrogate(rest[lo]) && lo > 1) { lo--; }   // not between the halves of a surrogate pair
                string head = rest.Substring(0, lo);
                placed.Add(new Placed { Item = Piece(w, head), X = x, Width = TW(head, w.Style) });
                lineStart.Add(placed.Count);
                x = 0;
                rest = rest.Substring(lo);
            }
        }

        private static WordItem Piece(WordItem w, string text) =>
            new WordItem { Text = text, Style = w.Style, Owner = w.Owner, SpaceBefore = false };

        /// <summary>Cuts the line placed[from..to) so it ends with "…" within avail; returns the new end. force: a clamped line, which gets the
        /// ellipsis even when it fits whole.</summary>
        private int Truncate(int from, int to, float avail, bool force = false)
        {
            int lastWord = -1;
            for (int k = to - 1; k >= from; k--) { if (placed[k].Item is WordItem) { lastWord = k; break; } }
            if (lastWord < 0) { return to; }
            float ell = TW("…", placed[lastWord].Item.Style);
            for (int k = to - 1; k >= from; k--)
            {
                var p = placed[k];
                if (p.X + ell <= avail && p.Item is WordItem w)
                {
                    // the longest prefix that fits, by bisection (each probe measures one string)
                    int lo = 0, hi = w.Text.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (p.X + TW(w.Text.Substring(0, mid), w.Style) + ell <= avail) { lo = mid; } else { hi = mid - 1; }
                    }
                    // a clamped line: when not even one character of this word fits, the ellipsis goes after the word before
                    if (force && lo == 0 && k > from) { continue; }
                    var cut = new WordItem { Text = w.Text.Substring(0, lo) + "…", Style = w.Style, Owner = w.Owner, SpaceBefore = w.SpaceBefore };
                    placed[k] = new Placed { Item = cut, X = p.X, Width = TW(cut.Text, w.Style) };
                    return k + 1;
                }
            }
            return from;
        }

        // ---------------------------------------------------------------- intrinsic widths

        /// <summary>Min-content and max-content border-box widths.</summary>
        private void Intrinsic(LayoutBox b, out float min, out float max)
        {
            if (b.MinContent.HasValue) { min = b.MinContent.Value; max = b.MaxContent.Value; return; }
            float edges = b.Padding.Horizontal + b.Border.Horizontal;
            if (b.Element != null)
            {
                // edges are resolved against the parent; % padding counts as 0 here
                float pb = (Len(b.Style, "padding-left", null) ?? 0) + (Len(b.Style, "padding-right", null) ?? 0)
                           + (Len(b.Style, "border-left-width", null) ?? 0) + (Len(b.Style, "border-right-width", null) ?? 0);
                edges = pb;
            }
            float? spec = b.Element == null ? null : Len(b.Style, "width", null) ?? NaturalSize(b, "width");
            if (spec.HasValue)
            {
                float v = b.Style.Get("box-sizing") == "content-box" ? spec.Value + edges : spec.Value;
                min = max = v;
            }
            else
            {
                min = max = 0;
                switch (b.Kind)
                {
                    case BoxKind.InlineRun:
                        RunIntrinsic(b, out min, out max);
                        break;
                    case BoxKind.Flow:
                        foreach (var c in b.Children)
                        {
                            if (c.OutOfFlow) { continue; }
                            Intrinsic(c, out float cmin, out float cmax);
                            float mh = MarginsPx(c);
                            min = Math.Max(min, cmin + mh);
                            max = Math.Max(max, cmax + mh);
                        }
                        break;
                    case BoxKind.Flex:
                    {
                        bool row = !b.Style.Get("flex-direction").StartsWith("column", StringComparison.Ordinal);
                        bool wrap = b.Style.Get("flex-wrap") != "nowrap";
                        float gap = Gap(b.Style, "column-gap", null);
                        int n = 0;
                        foreach (var c in b.Children)
                        {
                            if (c.OutOfFlow) { continue; }
                            Intrinsic(c, out float cmin, out float cmax);
                            float mh = MarginsPx(c);
                            if (row)
                            {
                                min = wrap ? Math.Max(min, cmin + mh) : min + cmin + mh;
                                max += cmax + mh;
                                n++;
                            }
                            else
                            {
                                min = Math.Max(min, cmin + mh);
                                max = Math.Max(max, cmax + mh);
                            }
                        }
                        if (row && n > 1) { max += gap * (n - 1); if (!wrap) { min += gap * (n - 1); } }
                        break;
                    }
                    case BoxKind.Grid:
                        GridIntrinsic(b, out min, out max);
                        break;
                }
                min += edges;
                max += edges;
            }
            float lo = b.Element == null ? 0 : Len(b.Style, "min-width", null) ?? 0;
            float hi = b.Element == null ? float.PositiveInfinity : Len(b.Style, "max-width", null) ?? float.PositiveInfinity;
            min = Math.Max(Math.Min(min, hi), lo);
            max = Math.Max(Math.Min(max, hi), lo);
            max = Math.Max(max, min);
            b.MinContent = min;
            b.MaxContent = max;
        }

        private static float MarginsPx(LayoutBox c) =>
            c.Element == null ? 0 : (Len(c.Style, "margin-left", null) ?? 0) + (Len(c.Style, "margin-right", null) ?? 0);

        private float WidestChar(string text, ComputedStyle st)
        {
            float widest = 0;
            for (int k = 0; k < text.Length; k++)
            {
                int len = char.IsHighSurrogate(text[k]) && k + 1 < text.Length ? 2 : 1;
                widest = Math.Max(widest, TW(text.Substring(k, len), st));
                k += len - 1;
            }
            return widest;
        }

        private void RunIntrinsic(LayoutBox b, out float min, out float max)
        {
            min = max = 0;
            float lineW = 0, chunk = 0, indent = 0;   // text-indent counts in the first line's max-content only (a percentage as 0)
            var items = b.Inline;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it is IndentItem ind) { indent = Math.Max(0, ind.Resolve(0)); continue; }
                if (it is BreakItem) { max = Math.Max(max, lineW + indent); indent = 0; lineW = 0; chunk = 0; continue; }
                float w;
                if (it is AtomItem a)
                {
                    Intrinsic(a.Box, out float amin, out float amax);
                    float mh = MarginsPx(a.Box);
                    w = amax + mh;
                    if (it.SpaceBefore || chunk == 0) { chunk = 0; }
                    chunk += amin + mh;
                }
                else
                {
                    var word = (WordItem)it;
                    w = TW(word.Text, it.Style);
                    if (it.SpaceBefore) { chunk = 0; }
                    // overflow-wrap: anywhere: a break may fall between any two characters, so the word's smallest part is one character
                    if (it.Style.Get("overflow-wrap") == "anywhere") { chunk = Math.Max(chunk, WidestChar(word.Text, it.Style)); }
                    else { chunk += w; }
                }
                min = Math.Max(min, chunk);
                if (it.SpaceBefore && lineW > 0) { lineW += TW(" ", it.SpaceStyle ?? it.Style); }
                lineW += w;
            }
            max = Math.Max(max, lineW + indent);
        }

        // ---------------------------------------------------------------- flex

        private sealed class FlexItem
        {
            public LayoutBox Box;
            public float Base, Hyp, Min, Max, Main, Cross, Grow, Shrink, MarginMain, MarginCross;
            public bool Frozen, AutoStart, AutoEnd;
            public string Align;

            public void Reset(LayoutBox box)
            {
                Box = box;
                Base = Hyp = Min = Max = Main = Cross = Grow = Shrink = MarginMain = MarginCross = 0;
                Frozen = AutoStart = AutoEnd = false;
                Align = null;
            }
        }

        // LayoutFlex nests (an item is laid out inside its parent's pass): rent and give back, no shared scratch
        private readonly Stack<FlexItem> itemPool = new();
        private readonly Stack<List<FlexItem>> itemListPool = new();
        private readonly Stack<List<int>> intListPool = new();
        private readonly Stack<List<float>> floatListPool = new();

        private static List<T> Rent<T>(Stack<List<T>> pool)
        {
            var l = pool.Count > 0 ? pool.Pop() : new List<T>();
            l.Clear();
            return l;
        }

        private void LayoutFlex(LayoutBox b, float? cbH, float? specH)
        {
            var s = b.Style;
            float cw = b.ContentWidth;
            float? contentH = specH.HasValue ? Math.Max(0, specH.Value - b.Padding.Vertical - b.Border.Vertical) : (float?)null;
            string dir = s.Get("flex-direction");
            bool row = !dir.StartsWith("column", StringComparison.Ordinal);
            bool reverse = dir.EndsWith("reverse", StringComparison.Ordinal);
            bool wrap = s.Get("flex-wrap") != "nowrap";
            float colGap = Gap(s, "column-gap", cw), rowGap = Gap(s, "row-gap", contentH);
            float mainGap = row ? colGap : rowGap, crossGap = row ? rowGap : colGap;
            string alignItems = s.Get("align-items");
            if (alignItems == "baseline") { Warn(b.Element, "align-items: baseline is laid out as flex-start"); }

            var items = Rent(itemListPool);
            foreach (var c in FlexOrder(b))
            {
                ResolveEdges(c, cw);
                var it = itemPool.Count > 0 ? itemPool.Pop() : new FlexItem();
                it.Reset(c);
                var cs = c.Style;
                it.Grow = c.Element == null ? 0 : Math.Max(0, cs.GetNumber("flex-grow"));
                it.Shrink = c.Element == null ? 1 : Math.Max(0, cs.GetNumber("flex-shrink", 1));
                string self = c.Element == null ? "auto" : cs.Get("align-self");
                it.Align = self == "auto" ? alignItems : self;
                it.MarginMain = row ? c.Margin.Horizontal : c.Margin.Vertical;
                it.MarginCross = row ? c.Margin.Vertical : c.Margin.Horizontal;
                it.AutoStart = AutoMargin(c, row ? "left" : "top");
                it.AutoEnd = AutoMargin(c, row ? "right" : "bottom");
                float? mainAvailForPct = row ? cw : contentH;

                // cross size for column items is needed before their content height is known
                if (!row)
                {
                    float? specW = SpecSize(c, "width", cw);
                    bool stretch = it.Align is "stretch" or "normal" && specW == null && !AutoMargin(c, "left") && !AutoMargin(c, "right");
                    float crossW;
                    if (specW.HasValue) { crossW = specW.Value; }
                    else if (stretch) { crossW = Math.Max(0, cw - c.Margin.Horizontal); }
                    else { Intrinsic(c, out float imin, out float imax); crossW = Math.Min(Math.Max(imin, cw - c.Margin.Horizontal), imax); }
                    it.Cross = ClampW(c, crossW, cw);
                }

                Length basis = c.Element == null ? Length.Auto : cs.GetLength("flex-basis");
                float? baseSize = basis.Unit == Unit.Px ? basis.Value
                    : basis.Unit == Unit.Percent && mainAvailForPct.HasValue ? mainAvailForPct.Value * basis.Value / 100f
                    : basis.Unit == Unit.None && basis.Value == 0 ? 0 : (float?)null;
                if (baseSize.HasValue && c.Element != null && cs.Get("box-sizing") == "content-box")
                {
                    baseSize += row ? c.Padding.Horizontal + c.Border.Horizontal : c.Padding.Vertical + c.Border.Vertical;
                }
                float? specMain = row ? SpecSize(c, "width", cw) : SpecSize(c, "height", contentH);
                float contentMain;
                if (row)
                {
                    Intrinsic(c, out float imin, out float imax);
                    contentMain = imax;
                    bool visible = !(c.ClipX || c.ClipY);
                    it.Min = SpecSize(c, "min-width", cw) ?? (visible ? Math.Min(imin, specMain ?? imin) : 0);
                    it.Max = SpecSize(c, "max-width", cw) ?? float.PositiveInfinity;
                }
                else
                {
                    Layout(c, cw, contentH, it.Cross, null, shrink: false);
                    contentMain = c.H;
                    bool visible = !(c.ClipX || c.ClipY);
                    it.Min = SpecSize(c, "min-height", contentH) ?? (visible ? Math.Min(contentMain, specMain ?? contentMain) : 0);
                    it.Max = SpecSize(c, "max-height", contentH) ?? float.PositiveInfinity;
                }
                float edgesMain = row ? c.Padding.Horizontal + c.Border.Horizontal : c.Padding.Vertical + c.Border.Vertical;
                it.Min = Math.Max(it.Min, edgesMain);
                // a border-box basis cannot go below padding + border (Chrome floors the content-box size at 0)
                it.Base = Math.Max(baseSize ?? specMain ?? contentMain, edgesMain);
                it.Hyp = Math.Max(Math.Min(it.Base, it.Max), it.Min);
                items.Add(it);
            }

            float? mainDef = row ? cw : contentH;
            float hypSum = 0;
            foreach (var it in items) { hypSum += it.Hyp + it.MarginMain; }
            float mainSize = mainDef ?? hypSum + mainGap * Math.Max(0, items.Count - 1);
            if (!mainDef.HasValue)
            {
                float outer = ClampH(b, mainSize + b.Padding.Vertical + b.Border.Vertical, cbH);
                mainSize = outer - b.Padding.Vertical - b.Border.Vertical;
            }

            // lines: line li is items[starts[li] .. starts[li + 1])
            var starts = Rent(intListPool);
            float used = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                float outer = it.Hyp + it.MarginMain;
                bool lineHasItems = starts.Count > 0 && starts[^1] < i;
                if (wrap && lineHasItems && used + mainGap + outer > mainSize + Eps) { used = 0; lineHasItems = false; }
                if (!lineHasItems && (starts.Count == 0 || starts[^1] < i)) { starts.Add(i); }
                used += (lineHasItems ? mainGap : 0) + outer;
            }
            int lineCount = starts.Count;
            starts.Add(items.Count);

            for (int li = 0; li < lineCount; li++)
            {
                int n = starts[li + 1] - starts[li];
                ResolveFlexible(items, starts[li], starts[li + 1], mainSize - mainGap * Math.Max(0, n - 1));
            }

            // cross sizes
            foreach (var it in items)
            {
                var c = it.Box;
                if (row)
                {
                    float? specCross = SpecSize(c, "height", contentH);
                    Layout(c, cw, contentH, it.Main, specCross.HasValue ? ClampH(c, specCross.Value, contentH) : (float?)null, shrink: false);
                    it.Cross = c.H;
                }
                else
                {
                    Layout(c, cw, contentH, it.Cross, it.Main, shrink: false);
                }
            }
            float? crossDef = row ? contentH : cw;
            var lineCross = Rent(floatListPool);
            float crossSum = 0;
            for (int li = 0; li < lineCount; li++)
            {
                float mx = 0;
                for (int k = starts[li]; k < starts[li + 1]; k++) { mx = Math.Max(mx, items[k].Cross + items[k].MarginCross); }
                lineCross.Add(mx);
                crossSum += mx;
            }
            if (!wrap && crossDef.HasValue && lineCount == 1) { lineCross[0] = crossDef.Value; }
            else if (wrap && crossDef.HasValue && lineCount > 0)
            {
                string ac = s.Get("align-content");
                float extra = crossDef.Value - crossSum - crossGap * (lineCount - 1);
                if (extra > 0 && (ac == "normal" || ac == "stretch" || ac == "")) { for (int k = 0; k < lineCross.Count; k++) { lineCross[k] += extra / lineCross.Count; } }
            }

            // stretch, then place
            float crossPos = 0;
            for (int li = 0; li < lineCount; li++)
            {
                int from = starts[li], to = starts[li + 1], count = to - from;
                for (int k = from; k < to; k++)
                {
                    var it = items[k];
                    var c = it.Box;
                    bool crossAuto = row ? SpecSize(c, "height", contentH) == null : SpecSize(c, "width", cw) == null;
                    bool autoCross = AutoMargin(c, row ? "top" : "left") || AutoMargin(c, row ? "bottom" : "right");
                    if (it.Align is "stretch" or "normal" && crossAuto && !autoCross)
                    {
                        float target = Math.Max(0, lineCross[li] - it.MarginCross);
                        target = row ? ClampH(c, target, contentH) : ClampW(c, target, cw);
                        if (Math.Abs(target - it.Cross) > Eps || row)
                        {
                            if (row) { Layout(c, cw, contentH, it.Main, target, shrink: false); }
                            else { Layout(c, cw, contentH, target, it.Main, shrink: false); }
                            it.Cross = target;
                        }
                    }
                }

                float mainUsed = 0;
                int autos = 0;
                for (int k = from; k < to; k++)
                {
                    mainUsed += items[k].Main + items[k].MarginMain;
                    autos += (items[k].AutoStart ? 1 : 0) + (items[k].AutoEnd ? 1 : 0);
                }
                float free = mainSize - mainUsed - mainGap * Math.Max(0, count - 1);
                float start = 0, between = 0, autoShare = 0;
                if (autos > 0 && free > 0) { autoShare = free / autos; }
                else
                {
                    string jc = s.Get("justify-content");
                    if (reverse) { jc = jc switch { "flex-start" or "start" or "normal" => "flex-end", "flex-end" or "end" => "flex-start", _ => jc }; }
                    switch (jc)
                    {
                        case "flex-end": case "end": case "right": start = free; break;
                        case "center": start = free / 2; break;
                        case "space-between": if (count > 1 && free > 0) { between = free / (count - 1); } break;
                        case "space-around": if (free > 0) { between = free / count; start = between / 2; } else { start = free / 2; } break;
                        case "space-evenly": if (free > 0) { between = free / (count + 1); start = between; } else { start = free / 2; } break;
                    }
                }
                float pos = start;
                for (int k = 0; k < count; k++)
                {
                    var it = items[reverse ? to - 1 - k : from + k];
                    var c = it.Box;
                    float mStart = (row ? c.Margin.Left : c.Margin.Top) + (it.AutoStart ? autoShare : 0);
                    float mEnd = (row ? c.Margin.Right : c.Margin.Bottom) + (it.AutoEnd ? autoShare : 0);
                    pos += mStart;
                    float crossOff = row ? c.Margin.Top : c.Margin.Left;
                    float spare = lineCross[li] - it.Cross - it.MarginCross;
                    bool autoA = AutoMargin(c, row ? "top" : "left"), autoB = AutoMargin(c, row ? "bottom" : "right");
                    if (autoA && autoB) { crossOff += Math.Max(0, spare) / 2; }
                    else if (autoA) { crossOff += Math.Max(0, spare); }
                    else if (!autoB)
                    {
                        switch (it.Align)
                        {
                            case "flex-end": case "end": crossOff += spare; break;
                            case "center": crossOff += spare / 2; break;
                        }
                    }
                    float mainCoord = pos, crossCoord = crossPos + crossOff;
                    c.X = b.Border.Left + b.Padding.Left + (row ? mainCoord : crossCoord);
                    c.Y = b.Border.Top + b.Padding.Top + (row ? crossCoord : mainCoord);
                    pos += it.Main + mEnd + mainGap + between;
                }
                crossPos += lineCross[li] + crossGap;
            }
            float crossTotal = crossGap * Math.Max(0, lineCount - 1);
            foreach (float lc in lineCross) { crossTotal += lc; }
            float contentHeight = row ? crossTotal : mainSize;
            b.H = specH ?? ClampH(b, contentHeight + b.Padding.Vertical + b.Border.Vertical, cbH);
            var firstItem = items.Count > 0 ? items[0].Box : null;
            if (firstItem != null && !float.IsNaN(firstItem.Baseline)) { b.Baseline = firstItem.Y + firstItem.Baseline; }

            foreach (var it in items) { it.Box = null; itemPool.Push(it); }
            itemListPool.Push(items);
            intListPool.Push(starts);
            floatListPool.Push(lineCross);
        }

        /// <summary>In-flow children in "order" order (stable); the children list itself when no item sets order.
        /// Flex and grid items both.</summary>
        private static List<LayoutBox> FlexOrder(LayoutBox b)
        {
            bool ordered = false, outOfFlow = false;
            foreach (var c in b.Children)
            {
                if (c.OutOfFlow) { outOfFlow = true; }
                else if (c.Element != null && c.Style.GetNumber("order") != 0) { ordered = true; }
            }
            if (!ordered && !outOfFlow) { return b.Children; }
            var list = new List<LayoutBox>(b.Children.Count);
            foreach (var c in b.Children) { if (!c.OutOfFlow) { list.Add(c); } }
            if (ordered) { list = list.OrderBy(c => (int)(c.Element == null ? 0 : c.Style.GetNumber("order"))).ToList(); }
            return list;
        }

        /// <summary>CSS Flexbox §9.7: grow or shrink with min/max violations frozen in rounds (items[from..to)).</summary>
        private static void ResolveFlexible(List<FlexItem> items, int from, int to, float space)
        {
            float sumHyp = 0;
            for (int k = from; k < to; k++) { sumHyp += items[k].Hyp + items[k].MarginMain; }
            bool grow = sumHyp < space;
            for (int k = from; k < to; k++)
            {
                var it = items[k];
                it.Main = it.Hyp;
                it.Frozen = (grow ? it.Grow == 0 : it.Shrink == 0) || (grow && it.Base > it.Hyp) || (!grow && it.Base < it.Hyp);
            }
            float Free()
            {
                float f = space;
                for (int k = from; k < to; k++) { f -= (items[k].Frozen ? items[k].Main : items[k].Base) + items[k].MarginMain; }
                return f;
            }
            float initialFree = Free();
            for (int round = 0; round < to - from + 1; round++)
            {
                int open = 0;
                float factors = 0, scaled = 0;
                for (int k = from; k < to; k++)
                {
                    var it = items[k];
                    if (it.Frozen) { continue; }
                    open++;
                    factors += grow ? it.Grow : it.Shrink;
                    scaled += it.Shrink * it.Base;
                }
                if (open == 0) { break; }
                float free = Free();
                if (factors < 1 && Math.Abs(initialFree * factors) < Math.Abs(free)) { free = initialFree * factors; }
                float total = 0;
                for (int k = from; k < to; k++)
                {
                    var it = items[k];
                    if (it.Frozen) { continue; }
                    it.Main = grow ? it.Base + (factors > 0 ? free * it.Grow / factors : 0)
                                   : it.Base + (scaled > 0 ? free * (it.Shrink * it.Base) / scaled : 0);
                    float clamped = Math.Max(Math.Min(it.Main, it.Max), it.Min);
                    total += clamped - it.Main;
                    it.Main = clamped;
                }
                for (int k = from; k < to; k++)
                {
                    var it = items[k];
                    if (it.Frozen) { continue; }
                    if (Math.Abs(total) < Eps) { it.Frozen = true; }
                    else if (total > 0 && it.Main <= it.Min + Eps) { it.Frozen = true; }
                    else if (total < 0 && it.Main >= it.Max - Eps) { it.Frozen = true; }
                }
            }
        }

        // ---------------------------------------------------------------- grid

        private enum TrackKind { Px, Percent, Fr, Auto }

        private struct Track
        {
            public TrackKind Kind;
            public float Value;
        }

        private List<Track> ParseTracks(LayoutBox b, string spec)
        {
            var tracks = new List<Track>();
            if (spec == "none" || string.IsNullOrWhiteSpace(spec)) { return tracks; }
            foreach (var tok in ExpandRepeat(spec))
            {
                if (Length.TryParse(tok, out var l))
                {
                    var px = b.Style.ToPx(tok);
                    switch (px.Unit)
                    {
                        case Unit.Px: tracks.Add(new Track { Kind = TrackKind.Px, Value = px.Value }); continue;
                        case Unit.Percent: tracks.Add(new Track { Kind = TrackKind.Percent, Value = px.Value }); continue;
                        case Unit.Fr: tracks.Add(new Track { Kind = TrackKind.Fr, Value = px.Value }); continue;
                        case Unit.Auto: tracks.Add(new Track { Kind = TrackKind.Auto }); continue;
                    }
                }
                if (tok is "min-content" or "max-content") { tracks.Add(new Track { Kind = TrackKind.Auto }); continue; }
                Warn(b.Element, $"grid track '{tok}' is not supported, auto used");
                tracks.Add(new Track { Kind = TrackKind.Auto });
            }
            return tracks;
        }

        private static IEnumerable<string> ExpandRepeat(string spec)
        {
            foreach (var tok in StyleEngine.SplitSpaces(spec))
            {
                if (tok.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) && tok.EndsWith(")", StringComparison.Ordinal))
                {
                    string inner = tok.Substring(7, tok.Length - 8);
                    int comma = inner.IndexOf(',');
                    if (comma > 0 && int.TryParse(inner.Substring(0, comma).Trim(), out int n))
                    {
                        var body = StyleEngine.SplitSpaces(inner.Substring(comma + 1));
                        for (int i = 0; i < Math.Min(n, 1000); i++) { foreach (var t in body) { yield return t; } }
                        continue;
                    }
                }
                yield return tok;
            }
        }

        /// <summary>grid-column: auto | N | span N | N / M | N / span M → zero-based start (-1 = auto) and span.</summary>
        private static void ParsePlacement(LayoutBox c)
        {
            c.GridColumnStart = -1;
            c.GridColumnSpan = 1;
            if (c.Element == null) { return; }
            string spec = c.Style.Get("grid-column");
            if (spec == "auto") { return; }
            var parts = spec.Split('/');
            int? Num(string t) => int.TryParse(t.Trim(), out int v) ? v : null;
            int? Span(string t)
            {
                t = t.Trim();
                return t.StartsWith("span", StringComparison.Ordinal) && int.TryParse(t.Substring(4).Trim(), out int v) ? v : null;
            }
            string a = parts[0];
            if (Span(a) is int sa) { c.GridColumnSpan = Math.Max(1, sa); return; }
            if (Num(a) is int start)
            {
                c.GridColumnStart = Math.Max(0, start - 1);
                if (parts.Length > 1)
                {
                    if (Span(parts[1]) is int sb) { c.GridColumnSpan = Math.Max(1, sb); }
                    else if (Num(parts[1]) is int end) { c.GridColumnSpan = Math.Max(1, end - start); }
                }
            }
        }

        private List<(LayoutBox Box, int Row, int Col)> Place(LayoutBox b, int columns)
        {
            var taken = new HashSet<(int, int)>();
            var placed = new List<(LayoutBox, int, int)>(b.Children.Count);
            bool Free(int r, int from, int span)
            {
                for (int k = from; k < from + span; k++) { if (taken.Contains((r, k))) { return false; } }
                return true;
            }
            int row = 0, col = 0;
            var order = FlexOrder(b);
            bool anyExplicit = false;
            foreach (var c in order) { ParsePlacement(c); anyExplicit |= c.GridColumnStart >= 0; }
            if (!anyExplicit)
            {
                // auto placement only: the cursor never meets a taken cell, no occupancy set needed
                foreach (var c in order)
                {
                    int span = Math.Min(c.GridColumnSpan, columns);
                    if (col + span > columns) { row++; col = 0; }
                    placed.Add((c, row, col));
                    col += span;
                }
                return placed;
            }
            foreach (var c in order)
            {
                int span = Math.Min(c.GridColumnSpan, columns);
                if (c.GridColumnStart >= 0)
                {
                    int start = Math.Min(c.GridColumnStart, columns - span);
                    int r = 0;
                    while (!Free(r, start, span)) { r++; }
                    for (int k = 0; k < span; k++) { taken.Add((r, start + k)); }
                    placed.Add((c, r, start));
                    continue;
                }
                while (true)
                {
                    if (col + span > columns) { row++; col = 0; }
                    if (Free(row, col, span)) { break; }
                    col++;
                }
                for (int k = 0; k < span; k++) { taken.Add((row, col + k)); }
                placed.Add((c, row, col));
                col += span;
            }
            return placed;
        }

        /// <summary>avail: content width minus gaps (what the tracks share); percentBasis: the content width.</summary>
        private float[] ColumnWidths(LayoutBox b, List<Track> tracks, List<(LayoutBox Box, int Row, int Col)> placed, float avail, bool forMin, bool intrinsicOnly, float percentBasis = 0)
        {
            int n = tracks.Count;
            var w = new float[n];
            var minC = new float[n];
            var maxC = new float[n];
            foreach (var (box, _, col) in placed)
            {
                if (Math.Min(box.GridColumnSpan, n) != 1) { continue; }
                Intrinsic(box, out float imin, out float imax);
                float mh = MarginsPx(box);
                // as for flex items (CSS): the automatic minimum is the content only for min-width: auto on a box that does not clip;
                // a set min-width (0 keeps a long name from widening its column) or a clipping box minimizes to that
                imin = SpecSize(box, "min-width", percentBasis > 0 ? percentBasis : null) ?? (box.ClipX || box.ClipY ? 0 : imin);
                minC[col] = Math.Max(minC[col], imin + mh);
                maxC[col] = Math.Max(maxC[col], imax + mh);
            }
            float fixedSum = 0, frSum = 0;
            for (int i = 0; i < n; i++)
            {
                switch (tracks[i].Kind)
                {
                    case TrackKind.Px: w[i] = tracks[i].Value; break;
                    case TrackKind.Percent: w[i] = intrinsicOnly ? 0 : percentBasis * tracks[i].Value / 100f; break;
                    case TrackKind.Auto: w[i] = forMin ? minC[i] : maxC[i]; break;
                    case TrackKind.Fr: w[i] = forMin ? minC[i] : intrinsicOnly ? maxC[i] : 0; frSum += tracks[i].Value; break;
                }
                if (tracks[i].Kind != TrackKind.Fr) { fixedSum += w[i]; }
            }
            if (intrinsicOnly) { return w; }
            float free = avail - fixedSum;
            if (frSum > 0)
            {
                var fixedFr = new bool[n];
                for (int round = 0; round < n + 1; round++)
                {
                    float left = free - Enumerable.Range(0, n).Where(i => tracks[i].Kind == TrackKind.Fr && fixedFr[i]).Sum(i => w[i]);
                    float sum = Enumerable.Range(0, n).Where(i => tracks[i].Kind == TrackKind.Fr && !fixedFr[i]).Sum(i => tracks[i].Value);
                    float unit = sum > 0 ? Math.Max(0, left) / Math.Max(1, sum) : 0;
                    if (sum > 0 && sum < 1) { unit = Math.Max(0, left) / 1f; }
                    bool changed = false;
                    for (int i = 0; i < n; i++)
                    {
                        if (tracks[i].Kind != TrackKind.Fr || fixedFr[i]) { continue; }
                        w[i] = unit * tracks[i].Value;
                        if (w[i] < minC[i]) { w[i] = minC[i]; fixedFr[i] = true; changed = true; }
                    }
                    if (!changed) { break; }
                }
            }
            else if (free > Eps)
            {
                var autos = Enumerable.Range(0, n).Where(i => tracks[i].Kind == TrackKind.Auto).ToList();
                foreach (var i in autos) { w[i] += free / autos.Count; }
            }
            else if (free < -Eps)
            {
                // squeeze auto tracks toward their min-content
                var autos = Enumerable.Range(0, n).Where(i => tracks[i].Kind == TrackKind.Auto).ToList();
                float room = autos.Sum(i => w[i] - minC[i]);
                if (room > 0) { foreach (var i in autos) { w[i] -= Math.Min(-free, room) * (w[i] - minC[i]) / room; } }
            }
            return w;
        }

        private void GridIntrinsic(LayoutBox b, out float min, out float max)
        {
            var tracks = ParseTracks(b, b.Style.Get("grid-template-columns"));
            if (tracks.Count == 0) { tracks.Add(new Track { Kind = TrackKind.Auto }); }
            var placed = Place(b, tracks.Count);
            float gap = Gap(b.Style, "column-gap", null) * Math.Max(0, tracks.Count - 1);
            min = ColumnWidths(b, tracks, placed, 0, forMin: true, intrinsicOnly: true).Sum() + gap;
            max = ColumnWidths(b, tracks, placed, 0, forMin: false, intrinsicOnly: true).Sum() + gap;
        }

        private void LayoutGrid(LayoutBox b, float? cbH, float? specH)
        {
            var s = b.Style;
            float cw = b.ContentWidth;
            float? contentH = specH.HasValue ? Math.Max(0, specH.Value - b.Padding.Vertical - b.Border.Vertical) : (float?)null;
            var tracks = ParseTracks(b, s.Get("grid-template-columns"));
            if (tracks.Count == 0) { tracks.Add(new Track { Kind = TrackKind.Auto }); }
            float colGap = Gap(s, "column-gap", cw), rowGap = Gap(s, "row-gap", contentH);
            int n = tracks.Count;
            var placed = Place(b, n);
            var widths = ColumnWidths(b, tracks, placed, cw - colGap * Math.Max(0, n - 1), forMin: false, intrinsicOnly: false, percentBasis: cw);
            var xs = new float[n + 1];
            for (int i = 0; i < n; i++) { xs[i + 1] = xs[i] + widths[i] + colGap; }

            int rows = placed.Count == 0 ? 0 : placed.Max(p => p.Row) + 1;
            var rowTracks = ParseTracks(b, s.Get("grid-template-rows"));
            var heights = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                if (r < rowTracks.Count && rowTracks[r].Kind == TrackKind.Px) { heights[r] = rowTracks[r].Value; }
            }
            string alignItems = s.Get("align-items");
            foreach (var (c, r, col) in placed)
            {
                int span = Math.Min(c.GridColumnSpan, n - col);
                float areaW = xs[col + span] - xs[col] - colGap;
                ResolveEdges(c, areaW);
                float? specW = SpecSize(c, "width", areaW);
                float w = specW ?? Math.Max(0, areaW - c.Margin.Horizontal);
                Layout(c, areaW, null, ClampW(c, w, areaW), null, shrink: false);
                bool fixedRow = r < rowTracks.Count && rowTracks[r].Kind == TrackKind.Px;
                // an fr row of a definite grid grows only by its items' automatic minimum, as in CSS (1fr = minmax(auto, 1fr)):
                // min-height when set, 0 for a scroll container, else the content — as for flex items
                bool frRow = contentH.HasValue && r < rowTracks.Count && rowTracks[r].Kind == TrackKind.Fr;
                float need = c.H;
                if (frRow) { need = SpecSize(c, "min-height", contentH) ?? (c.ClipY ? 0 : c.H); }
                if (!fixedRow) { heights[r] = Math.Max(heights[r], need + c.Margin.Vertical); }
            }
            if (contentH.HasValue) { SizeRows(rowTracks, heights, contentH.Value - rowGap * Math.Max(0, rows - 1)); }
            var ys = new float[rows + 1];
            for (int r = 0; r < rows; r++) { ys[r + 1] = ys[r] + heights[r] + rowGap; }
            foreach (var (c, r, col) in placed)
            {
                int span = Math.Min(c.GridColumnSpan, n - col);
                float areaW = xs[col + span] - xs[col] - colGap;
                string self = c.Element == null ? "auto" : c.Style.Get("align-self");
                string align = self == "auto" ? alignItems : self;
                float spare = heights[r] - c.H - c.Margin.Vertical;
                if (align is "stretch" or "normal" && SpecSize(c, "height", heights[r]) == null)
                {
                    Layout(c, areaW, heights[r], c.W, ClampH(c, heights[r] - c.Margin.Vertical, heights[r]), shrink: false);
                    spare = 0;
                }
                float dy = align switch { "center" => spare / 2, "end" or "flex-end" => spare, _ => 0 };
                float dx = c.Margin.Left;
                if (AutoMargin(c, "left") && AutoMargin(c, "right")) { dx = Math.Max(0, (areaW - c.W) / 2); }
                c.X = b.Border.Left + b.Padding.Left + xs[col] + dx;
                c.Y = b.Border.Top + b.Padding.Top + ys[r] + c.Margin.Top + dy;
            }
            float contentHeight = rows == 0 ? 0 : ys[rows] - rowGap;
            b.H = specH ?? ClampH(b, contentHeight + b.Padding.Vertical + b.Border.Vertical, cbH);
        }

        /// <summary>Row tracks against a definite grid height: % of it; fr rows share what the others leave, each
        /// at least its content (like 1fr = minmax(auto, 1fr)). Without a definite height they stay content-sized.</summary>
        private static void SizeRows(List<Track> tracks, float[] heights, float space)
        {
            float frSum = 0, fixedSum = 0;
            for (int r = 0; r < heights.Length; r++)
            {
                var kind = r < tracks.Count ? tracks[r].Kind : TrackKind.Auto;
                if (kind == TrackKind.Percent) { heights[r] = Math.Max(heights[r], space * tracks[r].Value / 100f); }
                if (kind == TrackKind.Fr) { frSum += tracks[r].Value; } else { fixedSum += heights[r]; }
            }
            if (frSum <= 0) { return; }
            // an fr row whose content needs more than its share keeps its content; the rest share again
            var grown = new bool[heights.Length];
            for (bool again = true; again;)
            {
                again = false;
                float left = space - fixedSum, sum = 0;
                for (int r = 0; r < heights.Length; r++) { if (r < tracks.Count && tracks[r].Kind == TrackKind.Fr && !grown[r]) { sum += tracks[r].Value; } }
                if (sum <= 0) { return; }
                for (int r = 0; r < heights.Length; r++)
                {
                    if (r >= tracks.Count || tracks[r].Kind != TrackKind.Fr || grown[r]) { continue; }
                    float share = Math.Max(0, left) * tracks[r].Value / sum;
                    if (heights[r] > share + Eps) { grown[r] = true; fixedSum += heights[r]; again = true; break; }
                }
                if (again) { continue; }
                for (int r = 0; r < heights.Length; r++)
                {
                    if (r < tracks.Count && tracks[r].Kind == TrackKind.Fr && !grown[r]) { heights[r] = Math.Max(0, left) * tracks[r].Value / sum; }
                }
            }
        }

        // ---------------------------------------------------------------- absolute coordinates

        private void Finish(LayoutBox b, float ox, float oy, LayoutBox root)
        {
            b.RelX = b.X;
            b.RelY = b.Y;
            b.X += ox;
            b.Y += oy;
            if (b.Element != null && b.Style.Get("position") == "relative")
            {
                float? l = Len(b.Style, "left", null), r = Len(b.Style, "right", null), t = Len(b.Style, "top", null), bt = Len(b.Style, "bottom", null);
                b.X += l ?? -(r ?? 0);
                b.Y += t ?? -(bt ?? 0);
            }
            foreach (var f in b.Text)
            {
                f.RelX = f.X;
                f.RelY = f.Y;
                f.RelBaseline = f.Baseline;
                f.X += b.X;
                f.Y += b.Y;
                f.Baseline += b.Y;
            }
            foreach (var c in b.Children) { if (!c.OutOfFlow) { Finish(c, b.X, b.Y, root); } }
            foreach (var c in b.Children) { if (c.OutOfFlow) { PlaceAbsolute(c, b, root); } }
        }

        private void PlaceAbsolute(LayoutBox a, LayoutBox parent, LayoutBox root)
        {
            var cb = parent;
            while (cb != root && (cb.Element == null || cb.Style.Get("position") == "static")) { cb = cb.Parent ?? root; }
            float cbX = cb.X + cb.Border.Left, cbY = cb.Y + cb.Border.Top;
            float cbW = cb.W - cb.Border.Horizontal, cbH = cb.H - cb.Border.Vertical;
            var s = a.Style;
            ResolveEdges(a, cbW);
            float? l = Len(s, "left", cbW), r = Len(s, "right", cbW), t = Len(s, "top", cbH), bt = Len(s, "bottom", cbH);
            float? w = SpecSize(a, "width", cbW);
            if (w == null && l.HasValue && r.HasValue) { w = Math.Max(0, cbW - l.Value - r.Value - a.Margin.Horizontal); }
            float? h = SpecSize(a, "height", cbH);
            if (h == null && t.HasValue && bt.HasValue) { h = Math.Max(0, cbH - t.Value - bt.Value - a.Margin.Vertical); }
            float availW = Math.Max(0, cbW - (l ?? 0) - (r ?? 0));
            Layout(a, availW, cbH, w.HasValue ? ClampW(a, w.Value, cbW) : (float?)null, h.HasValue ? ClampH(a, h.Value, cbH) : (float?)null, shrink: true);
            FixUp(a);
            float x = l.HasValue ? cbX + l.Value + a.Margin.Left
                : r.HasValue ? cbX + cbW - r.Value - a.Margin.Right - a.W
                : parent.ContentX + a.Margin.Left;
            float y = t.HasValue ? cbY + t.Value + a.Margin.Top
                : bt.HasValue ? cbY + cbH - bt.Value - a.Margin.Bottom - a.H
                : parent.ContentY + a.Margin.Top;
            a.X = 0;
            a.Y = 0;
            Finish(a, x, y, root);
        }
    }
}
