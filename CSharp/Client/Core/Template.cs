using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>Marks a method of a view (or any handler object) callable from markup: onclick="craft(r.id)".</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UiHandlerAttribute : Attribute
    {
        public readonly string Name;
        public UiHandlerAttribute(string name = null) { Name = name; }
    }

    /// <summary>What an update changed, so the window does only the work needed.</summary>
    public struct TemplateChanges
    {
        /// <summary>Elements were added (new copies or spacers): something structural happened, lay out everything. Styles need only the Created
        /// roots and, when a rule reads the sibling position, the Reshaped containers (RestyleStructure).</summary>
        public bool Structure;
        /// <summary>The roots of the elements this update created (a new data-for copy, a new spacer): each needs its first style, with its subtree.</summary>
        public List<Element> Created;
        /// <summary>The containers whose list of children changed (an insert, a remove, a move): their children's positions moved, and so did the earlier siblings of
        /// each child (the sibling combinators + and ~): RestyleStructure restyles the children a position or sibling-combinator compound can match.</summary>
        public List<Element> Reshaped;
        /// <summary>Text, visibility or order changed: lay out again.</summary>
        public bool Layout;
        /// <summary>Where (stage 7): the elements whose text, visibility or children changed, for an incremental
        /// layout. Layout without a list means everything.</summary>
        public List<Element> Relayout;
        /// <summary>Class, style or state changed on these elements: restyle their subtrees.</summary>
        public List<Element> Restyle;
        /// <summary>Elements that a data-if turned on (they were hidden): their keyframe animations start over.</summary>
        public List<Element> Shown;
        public bool Any => Structure || Layout || (Restyle != null && Restyle.Count > 0);
    }

    /// <summary>What a virtual data-for asks the window after a layout: the scroll area's offset and height, where
    /// the list starts inside it and the row pitch (distance between two rows, or one row's height). All in px.</summary>
    public interface IVirtualHost
    {
        bool Measure(Element container, Element listStart, Element row, Element nextRow, out float scroll, out float view, out float listTop, out float rowPitch);
    }

    /// <summary>Data binding over a parsed document (docs/ui/ENGINE_PLAN.md, stage 4): {{expr}} and {{expr | format}}
    /// in text and attributes, data-if, data-class="name: expr; …", data-disabled, data-for="x in list" with
    /// optional data-key. Update() evaluates every binding against the data and touches only nodes whose value
    /// changed; data-for keeps copies by key, so a row keeps its element (and :hover) while its data changes.
    /// Stage 5: data-model="path" binds a control both ways (WriteModel); a data-for inside a scroll area with many
    /// items (or data-virtual) creates only the visible rows plus a margin, with spacers for the rest — rows must
    /// all have the same height.</summary>
    public sealed class Template
    {
        private readonly Document doc;
        private readonly Block root;
        private readonly Expr.Context ctx = new();
        private readonly HashSet<string> warned = new();
        public object Data { get => ctx.Data; set => ctx.Data = value; }
        public int BindingCount => root.Count();
        /// <summary>Set by the window: measures virtual lists after a layout.</summary>
        public IVirtualHost Virtual;
        /// <summary>A virtual list was drawn without knowing its row height: update again after the next layout.</summary>
        public bool Remeasure;
        /// <summary>Some data-for is virtual now: a scroll must update the bindings.</summary>
        public bool HasVirtual { get; private set; }
        /// <summary>Row elements created by all data-for so far (virtual lists reuse rows, so this stays small).</summary>
        public int RowsCreated { get; private set; }
        /// <summary>Lists at least this long inside an overflow:auto/scroll parent are virtual without data-virtual.</summary>
        public static int AutoVirtual = 100;

        public Template(Document doc)
        {
            this.doc = doc;
            ctx.Warn = m => Warn(0, m);
            root = new Block(this);
            Compile(doc.Root, root);
        }

        private void Warn(int line, string message)
        {
            if (warned.Add(message)) { doc.Diagnostics.Add(new Diagnostic(doc.File, line, message)); }
        }

        // ---------------------------------------------------------------- compile

        private abstract class Binding
        {
            public Element Element;
            public abstract void Update(Template t, ref TemplateChanges ch);
        }

        private sealed class Part
        {
            public string Literal;
            public Expr Expr;
            public string Format;
        }

        /// <summary>A bound piece of the tree: the document, or one data-for copy.</summary>
        private sealed class Block
        {
            public readonly Template T;
            public readonly List<Binding> Bindings = new();
            public Block(Template t) { T = t; }
            public int Count()
            {
                int n = 0;
                foreach (var b in Bindings) { n += b is ForBinding f ? 1 + f.CountCopies() : 1; }
                return n;
            }
        }

        private void Compile(Element e, Block block)
        {
            if (e.Tag is "style" or "script") { return; }
            string forSpec = e.GetAttribute("data-for");
            if (forSpec != null && e.Parent != null)
            {
                CompileFor(e, forSpec, block);
                return;
            }
            string cond = e.GetAttribute("data-if");
            if (cond != null && Parse(cond, e) is { } ce) { block.Bindings.Add(new IfBinding { Element = e, Expr = ce }); }
            string cls = e.GetAttribute("data-class");
            if (cls != null)
            {
                foreach (var item in cls.Split(';'))
                {
                    int colon = item.IndexOf(':');
                    if (colon <= 0) { if (item.Trim().Length > 0) { Warn(e.Line, $"data-class '{item.Trim()}': expected 'name: expression'"); } continue; }
                    if (Parse(item.Substring(colon + 1), e) is { } x)
                    {
                        block.Bindings.Add(new ClassBinding { Element = e, Class = item.Substring(0, colon).Trim(), Expr = x });
                    }
                }
            }
            string model = e.GetAttribute("data-model");
            if (model != null && Parse(model, e) is { } me) { block.Bindings.Add(new ModelBinding { Element = e, Expr = me }); }
            string dis = e.GetAttribute("data-disabled");
            if (dis != null && Parse(dis, e) is { } de) { block.Bindings.Add(new DisabledBinding { Element = e, Expr = de }); }
            foreach (var kv in new List<KeyValuePair<string, string>>(e.Attributes))
            {
                if (kv.Value.Contains("{{") && !kv.Key.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    && SplitParts(kv.Value, e) is { } parts)
                {
                    block.Bindings.Add(new AttrBinding { Element = e, Name = kv.Key, Parts = parts });
                }
            }
            foreach (var c in e.Children.ToArray())
            {
                if (c is Element ce2) { Compile(ce2, block); }
                else if (c is TextNode tn && tn.Text.Contains("{{") && SplitParts(tn.Text, e) is { } parts)
                {
                    block.Bindings.Add(new TextBinding { Element = e, Node = tn, Parts = parts });
                }
            }
        }

        private void CompileFor(Element e, string spec, Block block)
        {
            int inAt = spec.IndexOf(" in ", StringComparison.Ordinal);
            string name = inAt > 0 ? spec.Substring(0, inAt).Trim() : null;
            var list = inAt > 0 ? Parse(spec.Substring(inAt + 4), e) : null;
            if (name == null || list == null)
            {
                if (inAt <= 0) { Warn(e.Line, $"data-for '{spec}': expected 'item in list'"); }
                return;
            }
            string keySpec = e.GetAttribute("data-key");
            var key = keySpec != null ? Parse(keySpec, e) : null;
            // the original becomes the template; an invisible anchor keeps its place in the parent
            var anchor = new Element("template") { Hidden = true, Line = e.Line };
            var parent = e.Parent;
            parent.Insert(parent.Children.IndexOf(e), anchor);
            parent.Remove(e);
            string virt = e.GetAttribute("data-virtual");
            e.RemoveAttribute("data-for");
            e.RemoveAttribute("data-key");
            e.RemoveAttribute("data-virtual");
            block.Bindings.Add(new ForBinding
            {
                Element = anchor, Template = e, Var = name, List = list, Key = key, Outer = block,
                Mode = virt == null ? null : virt is "off" or "false" ? "off" : "on",
            });
        }

        private Expr Parse(string text, Element e)
        {
            var x = Expr.Parse(text, out string error);
            if (x == null) { Warn(e.Line, error); }
            return x;
        }

        private List<Part> SplitParts(string text, Element e)
        {
            var parts = new List<Part>();
            int pos = 0;
            while (pos < text.Length)
            {
                int open = text.IndexOf("{{", pos, StringComparison.Ordinal);
                if (open < 0) { parts.Add(new Part { Literal = text.Substring(pos) }); break; }
                if (open > pos) { parts.Add(new Part { Literal = text.Substring(pos, open - pos) }); }
                int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { Warn(e.Line, $"'{{{{' without '}}}}' in '{text.Trim()}'"); parts.Add(new Part { Literal = text.Substring(open) }); break; }
                string inner = text.Substring(open + 2, close - open - 2);
                string format = null;
                int bar = inner.LastIndexOf('|');
                if (bar >= 0 && (bar == 0 || inner[bar - 1] != '|') && (bar + 1 >= inner.Length || inner[bar + 1] != '|'))
                {
                    format = inner.Substring(bar + 1).Trim();
                    inner = inner.Substring(0, bar);
                }
                var x = Parse(inner, e);
                parts.Add(x != null ? new Part { Expr = x, Format = format } : new Part { Literal = "" });
                pos = close + 2;
            }
            return parts;
        }

        // ---------------------------------------------------------------- update

        /// <summary>Re-evaluates every binding; returns what changed.</summary>
        public TemplateChanges Update()
        {
            var ch = new TemplateChanges();
            HasVirtual = false;
            UpdateBlock(root, ref ch);
            return ch;
        }

        private void UpdateBlock(Block b, ref TemplateChanges ch)
        {
            foreach (var binding in b.Bindings) { binding.Update(this, ref ch); }
        }

        private object Eval(Expr x, Element at)
        {
            ctx.Element = at;
            try { return x.Eval(ctx); }
            catch (Exception ex) { Warn(at.Line, $"expression failed: {ex.Message}"); return null; }
        }

        private readonly StringBuilder render = new();

        /// <summary>The bound text, or null when it equals current: an unchanged binding makes no new string.</summary>
        private string RenderIfChanged(List<Part> parts, Element at, string current)
        {
            if (parts.Count == 1 && parts[0].Expr != null && parts[0].Format == null && Eval(parts[0].Expr, at) is string single)
            {
                return single == current ? null : single;
            }
            render.Clear();
            foreach (var p in parts)
            {
                if (p.Expr != null) { Expr.AppendText(render, Eval(p.Expr, at), p.Format); } else { render.Append(p.Literal); }
            }
            if (current != null && render.Equals(current.AsSpan())) { return null; }
            return render.ToString();
        }

        private string Render(List<Part> parts, Element at)
        {
            if (parts.Count == 1 && parts[0].Expr != null) { return Expr.ToText(Eval(parts[0].Expr, at), parts[0].Format); }
            var sb = new StringBuilder();
            foreach (var p in parts) { sb.Append(p.Expr != null ? Expr.ToText(Eval(p.Expr, at), p.Format) : p.Literal); }
            return sb.ToString();
        }

        /// <summary>A pooled row is reused on every update of a scrolled virtual list: walked by index, no enumerator (the update of an unchanged page allocates nothing).</summary>
        private static void ResetPooled(Element e)
        {
            const NodeState states = NodeState.Hover | NodeState.Active | NodeState.Focus | NodeState.FocusWithin | NodeState.DragOver | NodeState.Dragging;
            e.State &= ~states;
            e.ClearTranslate();
            e.ClearScale();
            var kids = e.Children;
            for (int i = 0; i < kids.Count; i++) { if (kids[i] is Element c) { ResetPooled(c); } }
        }

        private static void AddCreated(ref TemplateChanges ch, Element e) => (ch.Created ??= new List<Element>()).Add(e);

        private static void AddReshaped(ref TemplateChanges ch, Element container)
        {
            var list = ch.Reshaped ??= new List<Element>();
            if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1], container)) { list.Add(container); }
        }

        private static bool Attached(Element e, Element root)
        {
            for (; e != null; e = e.Parent) { if (ReferenceEquals(e, root)) { return true; } }
            return false;
        }

        private static bool UnderAny(HashSet<Element> set, Element e)
        {
            for (; e != null; e = e.Parent) { if (set.Contains(e)) { return true; } }
            return false;
        }

        /// <summary>What a structural update needs from the styles, instead of restyling the whole page: each created root gets its style with its subtree (its
        /// parent is styled already), and when some selector reads the sibling position (:first-child, :last-child, :nth-child) or the earlier siblings (+, ~: a
        /// compound after such a combinator) the children of a reshaped container that such a compound can match are restyled with their subtrees (restyle: the window's Restyle, which also checks the layout; null:
        /// styles.Compute). Removed elements need nothing. False: a created root has no styled parent, restyle the whole page and nothing was done.
        /// Which children: every one such a compound can match, not only those after the change (the update does not say where), the same over-approximation.</summary>
        public static bool RestyleStructure(in TemplateChanges ch, StyleEngine styles, Element docRoot, Action<Element> restyle = null)
        {
            restyle ??= styles.Compute;
            HashSet<Element> fresh = null;
            if (ch.Created != null && ch.Created.Count > 0)
            {
                fresh = new HashSet<Element>(ch.Created);
                foreach (var e in ch.Created)
                {
                    if (!Attached(e, docRoot) || UnderAny(fresh, e.Parent)) { continue; }
                    if (e.Parent?.Style == null) { return false; }
                }
                foreach (var e in ch.Created)
                {
                    if (!Attached(e, docRoot) || UnderAny(fresh, e.Parent)) { continue; }
                    styles.Compute(e);
                    // a :has() of an earlier element (a + or ~ inside it, or a child compound) can see the new one: its anchors are restyled too
                    if (styles.ReadsHasSelectors) { foreach (var a in styles.AnchorsOf(e)) { restyle(a); } }
                }
            }
            if (ch.Reshaped != null && (styles.ReadsSiblingPosition || styles.ReadsHasSelectors))
            {
                var seen = new HashSet<Element>();
                foreach (var c in ch.Reshaped)
                {
                    if (!seen.Add(c) || !Attached(c, docRoot) || (fresh != null && UnderAny(fresh, c))) { continue; }
                    // a :has() of the container reads its children, which changed: the container is restyled with its subtree
                    if (styles.ReadsHasSelectors) { restyle(c); continue; }
                    foreach (var kid in c.ElementChildren())
                    {
                        if (styles.SiblingPositionReaches(kid)) { restyle(kid); }
                    }
                }
            }
            return true;
        }

        private static void AddRestyle(ref TemplateChanges ch, Element e) => (ch.Restyle ??= new List<Element>()).Add(e);

        private static void AddLayout(ref TemplateChanges ch, Element e)
        {
            ch.Layout = true;
            (ch.Relayout ??= new List<Element>()).Add(e);
        }

        private sealed class TextBinding : Binding
        {
            public TextNode Node;
            public List<Part> Parts;

            public override void Update(Template t, ref TemplateChanges ch)
            {
                string v = t.RenderIfChanged(Parts, Element, Node.Text);
                if (v == null) { return; }
                Node.Text = v;
                AddLayout(ref ch, Element);
            }
        }

        private sealed class AttrBinding : Binding
        {
            public string Name;
            public List<Part> Parts;
            private string last;

            public override void Update(Template t, ref TemplateChanges ch)
            {
                string v = t.RenderIfChanged(Parts, Element, last);
                if (v == null) { return; }
                last = v;
                Element.SetAttribute(Name, v);
                if (Name is "class" or "style" or "id") { AddRestyle(ref ch, Element); }
                // a bound icon name: the glyph is its text, so the line is laid out again
                if (Name == "data-icon" && Icons.Apply(Element)) { AddLayout(ref ch, Element); }
            }
        }

        private sealed class IfBinding : Binding
        {
            public Expr Expr;
            public override void Update(Template t, ref TemplateChanges ch)
            {
                bool hidden = !Expr.Truthy(t.Eval(Expr, Element));
                if (hidden == Element.Hidden) { return; }
                Element.Hidden = hidden;
                if (!hidden) { (ch.Shown ??= new List<Element>()).Add(Element); }
                AddLayout(ref ch, Element);
            }
        }

        private sealed class ClassBinding : Binding
        {
            public string Class;
            public Expr Expr;
            public override void Update(Template t, ref TemplateChanges ch)
            {
                bool on = Expr.Truthy(t.Eval(Expr, Element));
                if (on == Element.HasClass(Class)) { return; }
                Element.ToggleClass(Class, on);
                AddRestyle(ref ch, Element);
            }
        }

        private sealed class DisabledBinding : Binding
        {
            public Expr Expr;
            public override void Update(Template t, ref TemplateChanges ch)
            {
                bool on = Expr.Truthy(t.Eval(Expr, Element));
                if (on == ((Element.State & NodeState.Disabled) != 0)) { return; }
                if (on) { Element.State |= NodeState.Disabled; Element.SetAttribute("disabled", ""); }
                else { Element.State &= ~NodeState.Disabled; Element.RemoveAttribute("disabled"); }
                AddRestyle(ref ch, Element);
            }
        }

        /// <summary>data-model: model -> control. The control writes back through WriteModel.</summary>
        private sealed class ModelBinding : Binding
        {
            public Expr Expr;
            private string last, radioLast;
            private bool radioSeen;

            private static void Match(Element radio, object v, ref TemplateChanges ch)
            {
                bool match = v != null && Expr.ToText(v) == RadioGroup.ValueOf(radio);
                if (match == radio.Checked) { return; }
                if (match) { radio.SetAttribute("checked", ""); } else { radio.RemoveAttribute("checked"); }
                AddRestyle(ref ch, radio);
            }

            public override void Update(Template t, ref TemplateChanges ch)
            {
                var v = t.Eval(Expr, Element);
                if (Element.Tag == "input" && Element.GetAttribute("type") == "checkbox")
                {
                    bool on = Expr.Truthy(v);
                    if (on == Element.Checked) { return; }
                    if (on) { Element.SetAttribute("checked", ""); } else { Element.RemoveAttribute("checked"); }
                    AddRestyle(ref ch, Element);
                    return;
                }
                if (RadioGroup.Is(Element))
                {
                    // a radio is checked while the model equals its value; the radios of its group without a data-model of their own follow it too (one is enough per group)
                    Match(Element, v, ref ch);
                    string now = v == null ? null : Expr.ToText(v);
                    if (radioSeen && now == radioLast) { return; }
                    radioSeen = true; radioLast = now;
                    var root = Element;
                    while (root.Parent != null) { root = root.Parent; }
                    var members = new List<Element>();
                    RadioGroup.Members(root, Element, members);
                    foreach (var m in members) { if (m != Element && m.GetAttribute("data-model") == null) { Match(m, v, ref ch); } }
                    return;
                }
                string s = Expr.ToText(v);
                if (s == last) { return; }
                last = s;
                Element.SetAttribute("value", s);
            }
        }

        private sealed class Copy
        {
            public Element Root;
            public Expr.LoopScope Scope;
            public Block Block;
        }

        private sealed class ForBinding : Binding
        {
            private const int Margin = 6, FirstRows = 40;
            public Element Template;
            public string Var;
            public Expr List, Key;
            public Block Outer;
            /// <summary>data-virtual: "on", "off", or null (automatic).</summary>
            public string Mode;
            private readonly List<Copy> copies = new();
            private readonly Dictionary<object, Copy> byKey = new();
            private readonly Stack<Copy> pool = new();
            private Element top, bottom;
            private double topPx, bottomPx;
            private float pitch;
            private int tries;
            private readonly List<object> items = new();
            private Element probe;
            private Expr.LoopScope probeScope;

            public int CountCopies()
            {
                int n = 0;
                foreach (var c in copies) { n += c.Block.Count(); }
                return n;
            }

            public override void Update(Template t, ref TemplateChanges ch)
            {
                items.Clear();
                var source = t.Eval(List, Element);
                if (source is IList list) { for (int i = 0; i < list.Count; i++) { items.Add(list[i]); } }
                else if (source is IEnumerable seq and not string) { foreach (var item in seq) { items.Add(item); } }
                var outer = FindScope(Element);
                var parent = Element.Parent;
                int first = 0, last = items.Count;
                bool virt = Mode == "on" || (Mode == null && items.Count >= AutoVirtual
                    && parent.Style?.Get("overflow-y") is "auto" or "scroll");
                bool changed = false;
                if (virt)
                {
                    t.HasVirtual = true;
                    if (top == null)
                    {
                        top = Spacer();
                        bottom = Spacer();
                        topPx = bottomPx = 0;
                        parent.Insert(parent.Children.IndexOf(Element) + 1, top);
                        parent.Insert(parent.Children.IndexOf(top) + 1, bottom);
                        ch.Structure = true;
                        AddCreated(ref ch, top);
                        AddCreated(ref ch, bottom);
                        AddReshaped(ref ch, parent);
                    }
                    if (copies.Count > 0 && t.Virtual != null && t.Virtual.Measure(parent, top, copies[0].Root, copies.Count > 1 ? copies[1].Root : null,
                        out float scroll, out float view, out float listTop, out float rh) && rh > 0.5f)
                    {
                        pitch = rh;
                        first = Math.Clamp((int)Math.Floor((scroll - listTop) / pitch) - Margin, 0, items.Count);
                        last = Math.Clamp((int)Math.Ceiling((scroll - listTop + view) / pitch) + Margin, first, items.Count);
                    }
                    else
                    {
                        // nothing measured yet: a first screenful, the real range after the next layout
                        last = Math.Min(items.Count, FirstRows);
                        if (tries++ < 3) { t.Remeasure = true; }
                    }
                    SetHeight(top, first * pitch, ref topPx, ref ch);
                    SetHeight(bottom, (items.Count - last) * pitch, ref bottomPx, ref ch);
                }
                else if (top != null)
                {
                    parent.Remove(top);
                    parent.Remove(bottom);
                    top = bottom = null;
                    pool.Clear();
                    changed = true;
                    AddReshaped(ref ch, parent);
                    AddLayout(ref ch, parent);
                }

                // nothing moved: the same objects in the same copies, in order (a view that refreshes every frame)
                bool same = copies.Count == last - first;
                for (int i = first; same && i < last; i++)
                {
                    var c = copies[i - first];
                    same = ReferenceEquals(c.Scope.Value, items[i]) && c.Scope.Index == i && c.Scope.Outer == outer;
                }
                if (same)
                {
                    foreach (var c in copies) { t.UpdateBlock(c.Block, ref ch); }
                    return;
                }

                var next = new List<Copy>(last - first);
                var used = new HashSet<Copy>();
                for (int i = first; i < last; i++)
                {
                    object key = i;
                    if (Key != null)
                    {
                        probeScope ??= new Expr.LoopScope();
                        probeScope.Name = Var; probeScope.Value = items[i]; probeScope.Index = i; probeScope.Outer = outer;
                        probe ??= new Element("template");
                        probe.Scope = probeScope;
                        probe.Parent = Element.Parent;
                        key = t.Eval(Key, probe) ?? i;
                    }
                    if (!byKey.TryGetValue(key, out var copy) || used.Contains(copy))
                    {
                        if (virt && pool.Count > 0)
                        {
                            // a row that scrolled away takes the new item: no new elements, no full restyle
                            copy = pool.Pop();
                            // the row starts clean for its new item: no :hover/:active/:focus/:focus-within/:drag-over/:dragging and no numeric translate or scale of the old one,
                            // on the row and everything in it (the window's state chains drop these elements at their next change)
                            ResetPooled(copy.Root);
                            copy.Scope.Value = items[i];
                            copy.Scope.Index = i;
                            copy.Scope.Outer = outer;
                            AddRestyle(ref ch, copy.Root);
                        }
                        else
                        {
                            var scope = new Expr.LoopScope { Name = Var, Value = items[i], Index = i, Outer = outer };
                            copy = new Copy { Root = Template.Clone(), Block = new Block(t) };
                            copy.Root.Scope = scope;
                            copy.Scope = scope;
                            t.Compile(copy.Root, copy.Block);
                            ch.Structure = true;
                            AddCreated(ref ch, copy.Root);
                            t.RowsCreated++;
                        }
                        changed = true;
                    }
                    else
                    {
                        // keep the element, give it the new item
                        copy.Scope.Value = items[i];
                        copy.Scope.Index = i;
                        copy.Scope.Outer = outer;
                    }
                    used.Add(copy);
                    if (i - first >= copies.Count || copies[i - first] != copy) { changed = true; }
                    next.Add(copy);
                }
                if (next.Count != copies.Count) { changed = true; }
                if (changed)
                {
                    foreach (var c in copies)
                    {
                        parent.Remove(c.Root);
                        if (virt && !used.Contains(c)) { pool.Push(c); }
                    }
                    int at = parent.Children.IndexOf(top ?? Element) + 1;
                    foreach (var c in next) { parent.Insert(at++, c.Root); }
                    byKey.Clear();
                    for (int i = 0; i < next.Count; i++)
                    {
                        object key = Key != null ? t.Eval(Key, next[i].Root) ?? i + first : i + first;
                        byKey[key] = next[i];
                    }
                    copies.Clear();
                    copies.AddRange(next);
                    AddReshaped(ref ch, parent);
                    AddLayout(ref ch, parent);
                }
                foreach (var c in copies) { t.UpdateBlock(c.Block, ref ch); }
            }

            private static Element Spacer()
            {
                var e = new Element("div");
                e.SetAttribute("data-spacer", "");
                e.SetAttribute("style", Style(0));
                return e;
            }

            private static string Style(float px) =>
                $"height: {Math.Max(0, Math.Round(px)).ToString(System.Globalization.CultureInfo.InvariantCulture)}px; flex: none; margin: 0; padding: 0; border-width: 0";

            /// <summary>last: the height the spacer has now (rounded px), so an unchanged one builds no style string.</summary>
            private static void SetHeight(Element spacer, float px, ref double last, ref TemplateChanges ch)
            {
                double h = Math.Max(0, Math.Round(px));
                if (h == last) { return; }
                last = h;
                spacer.SetAttribute("style", Style(px));
                AddRestyle(ref ch, spacer);
            }

            private static Expr.LoopScope FindScope(Element e)
            {
                for (; e != null; e = e.Parent) { if (e.Scope is Expr.LoopScope s) { return s; } }
                return null;
            }
        }

        // ---------------------------------------------------------------- events

        private readonly Dictionary<string, (string Name, List<Expr> Args)> calls = new(StringComparer.Ordinal);

        /// <summary>Runs the handler named in the nearest on&lt;event&gt; attribute at or above target, its arguments
        /// evaluated where the attribute is (so r.id means that row's r; this is that element). False when there is no
        /// such attribute or no such handler (a warning then).</summary>
        public bool Dispatch(Element target, string eventAttr, Handlers handlers, out Element source, object arg = null)
        {
            source = target;
            while (source != null && source.GetAttribute(eventAttr) == null) { source = source.Parent; }
            if (source == null) { return false; }
            string spec = source.GetAttribute(eventAttr);
            if (!calls.TryGetValue(spec, out var call))
            {
                if (!Expr.ParseCall(spec, out var name, out var args, out string error))
                {
                    Warn(source.Line, error);
                    call = (null, null);
                }
                else { call = (name, args); }
                calls[spec] = call;
            }
            if (call.Name == null) { return false; }
            var values = new object[call.Args.Count];
            ctx.Arg = arg;
            for (int i = 0; i < values.Length; i++) { values[i] = Eval(call.Args[i], source); }
            ctx.Arg = null;
            string err = $"no handler '{call.Name}'";
            if (handlers == null || !handlers.TryInvoke(call.Name, values, out err))
            {
                Warn(source.Line, $"{eventAttr}=\"{spec}\": {err}");
                return false;
            }
            return true;
        }

        private readonly Dictionary<string, Expr> models = new(StringComparer.Ordinal);

        /// <summary>The value of a model path as data-model reads it (for the controls that draw a value: charts, curves, gauges). Null when the path is
        /// missing or does not parse.</summary>
        public object ReadModel(string spec, Element e)
        {
            if (string.IsNullOrEmpty(spec)) { return null; }
            if (!models.TryGetValue(spec, out var x)) { models[spec] = x = Parse(spec, e); }
            return x == null ? null : Eval(x, e);
        }

        /// <summary>A control changed: writes its value where its data-model points (a.b sets b on a; a loop item's
        /// members are writable, the loop variable itself is not). False with a warning when it cannot.</summary>
        public bool WriteModel(Element e, object value)
        {
            string spec = e.GetAttribute("data-model");
            if (spec == null) { return false; }
            if (!models.TryGetValue(spec, out var x)) { models[spec] = x = Parse(spec, e); }
            switch (x)
            {
                case Expr.Get g when g.Key == null:
                    if (Eval(g.Target, e) is { } target) { if (SetPath(target, g.Member_, value)) { return true; } }
                    else if (Dotted(g) is { } path && !IsLoopName(e, path.Split('.')[0]))
                    {
                        // a.b.c on the model with missing levels: dictionaries get them created
                        ctx.Data ??= new Dictionary<string, object>(StringComparer.Ordinal);
                        if (SetPath(ctx.Data, path, value)) { return true; }
                    }
                    break;
                case Expr.Get g:
                    object owner = Eval(g.Target, e), key = Eval(g.Key, e);
                    if (owner is IList list && Expr.IsNumber(key))
                    {
                        int i = (int)Expr.ToNumber(key);
                        if (i >= 0 && i < list.Count) { list[i] = value; return true; }
                    }
                    else if (owner is IDictionary<string, object> d && key != null)
                    {
                        d[Expr.ToText(key)] = value;
                        return true;
                    }
                    break;
                case Expr.Name n when !IsLoopName(e, n.Id) && n.Id != "this":
                    ctx.Data ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    if (SetPath(ctx.Data, n.Id, value)) { return true; }
                    break;
            }
            if (x != null) { Warn(e.Line, $"data-model '{spec}' cannot be written"); }
            return false;
        }

        private static string Dotted(Expr x) => x switch
        {
            Expr.Name n when n.Id != "this" => n.Id,
            Expr.Get g when g.Key == null && Dotted(g.Target) is { } head => head + "." + g.Member_,
            _ => null,
        };

        private static bool IsLoopName(Element e, string name)
        {
            for (; e != null; e = e.Parent)
            {
                for (var s = e.Scope as Expr.LoopScope; s != null; s = s.Outer) { if (s.Name == name) { return true; } }
                if (e.Scope != null) { return false; }
            }
            return false;
        }

        // ---------------------------------------------------------------- data

        /// <summary>Writes a value at a dotted path: dictionaries get missing levels created; objects get a public
        /// property or field set (converted to its type). False when the path cannot be written.</summary>
        public static bool SetPath(object data, string path, object value)
        {
            var names = path.Split('.');
            object obj = data;
            for (int i = 0; i < names.Length - 1; i++)
            {
                object next = Expr.Member(obj, names[i], null);
                if (next == null && obj is IDictionary<string, object> d)
                {
                    next = new Dictionary<string, object>(StringComparer.Ordinal);
                    d[names[i]] = next;
                }
                if (next == null) { return false; }
                obj = next;
            }
            string last = names[^1];
            switch (obj)
            {
                case IDictionary<string, object> d: d[last] = value; return true;
                case IDictionary nd: nd[last] = value; return true;
                case null: return false;
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var p = obj.GetType().GetProperty(last, flags);
            if (p != null && p.CanWrite) { p.SetValue(obj, Handlers.ConvertTo(value, p.PropertyType)); return true; }
            var f = obj.GetType().GetField(last, flags);
            if (f != null && !f.IsInitOnly) { f.SetValue(obj, Handlers.ConvertTo(value, f.FieldType)); return true; }
            return false;
        }
    }

    /// <summary>Named C# handlers the markup can call. Arguments are converted to the parameter types (numbers
    /// arrive as double from expressions); missing trailing arguments take their defaults.</summary>
    public sealed class Handlers
    {
        private readonly Dictionary<string, (MethodInfo M, object Target)> methods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (MethodInfo M, object Target)> builtins = new(StringComparer.Ordinal);

        public void Add(string name, Delegate d) => methods[name] = (d.Method, d.Target);

        /// <summary>The window's own handlers (showModal, closeDialog, close): used when the view has none of that name.</summary>
        public void AddBuiltin(string name, Delegate d) => builtins[name] = (d.Method, d.Target);

        /// <summary>Registers every [UiHandler] method of an object (public or not), under its own name or the one given.</summary>
        public void AddFrom(object target)
        {
            for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var attr = m.GetCustomAttribute<UiHandlerAttribute>();
                    if (attr != null) { methods.TryAdd(attr.Name ?? m.Name, (m, target)); }
                }
            }
        }

        public bool Has(string name) => methods.ContainsKey(name) || builtins.ContainsKey(name);

        public bool TryInvoke(string name, object[] args, out string error)
        {
            if (!methods.TryGetValue(name, out var h) && !builtins.TryGetValue(name, out h)) { error = $"no handler '{name}'"; return false; }
            var ps = h.M.GetParameters();
            if (args.Length > ps.Length) { error = $"'{name}' takes {ps.Length} argument(s), got {args.Length}"; return false; }
            var call = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < args.Length) { call[i] = ConvertTo(args[i], ps[i].ParameterType); }
                else if (ps[i].HasDefaultValue) { call[i] = ps[i].DefaultValue; }
                else { error = $"'{name}' needs argument '{ps[i].Name}'"; return false; }
            }
            h.M.Invoke(h.Target, call);
            error = null;
            return true;
        }

        public static object ConvertTo(object v, Type type)
        {
            if (v == null) { return type.IsValueType ? Activator.CreateInstance(type) : null; }
            if (type.IsInstanceOfType(v)) { return v; }
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(string)) { return Expr.ToText(v); }
            if (t == typeof(bool)) { return Expr.Truthy(v); }
            if (t.IsEnum) { return Enum.Parse(t, Expr.ToText(v), ignoreCase: true); }
            if (t.IsPrimitive || t == typeof(decimal))
            {
                return Convert.ChangeType(Expr.ToNumber(v), t, System.Globalization.CultureInfo.InvariantCulture);
            }
            return v;
        }
    }
}
