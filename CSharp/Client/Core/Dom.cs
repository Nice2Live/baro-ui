using System;
using System.Collections.Generic;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>A parse or style problem: never thrown, collected for the error overlay and the log.</summary>
    public readonly struct Diagnostic
    {
        public readonly string File;
        public readonly int Line;
        public readonly string Message;
        public Diagnostic(string file, int line, string message) { File = file; Line = line; Message = message; }
        public override string ToString() => $"{File}:{Line}: {Message}";
    }

    /// <summary>Interaction states that :hover/:active/:focus/:focus-within/:disabled/:drag-over/:dragging select on (:checked reads the checked attribute). FocusWithin is set on the focused element and all its ancestors.  DragOver: the drop target under a drag that takes its kind; Dragging: the element being dragged (Core/DragDrop.cs).</summary>
    [Flags]
    public enum NodeState { None = 0, Hover = 1, Active = 2, Focus = 4, Disabled = 8, FocusWithin = 16, DragOver = 32, Dragging = 64 }

    public abstract class Node
    {
        public Element Parent { get; internal set; }
        public int Line { get; internal set; }
        public abstract string TextContent { get; }
    }

    public sealed class TextNode : Node
    {
        public string Text;
        public TextNode(string text) { Text = text; }
        public override string TextContent => Text;
    }

    public sealed class Element : Node
    {
        private static readonly char[] Space = { ' ', '\t', '\n', '\r', '\f' };

        /// <summary>Lower-case tag name.</summary>
        public readonly string Tag;
        public readonly List<Node> Children = new();
        private readonly Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> classes;
        public NodeState State;
        /// <summary>Set by the cascade (StyleEngine.Compute): what layout and paint read. While a transition or an animation runs
        /// it is a private copy of BaseStyle with the animated values written over.</summary>
        public ComputedStyle Style;
        /// <summary>The cascade's own result, shared between elements; the children inherit from it.</summary>
        internal ComputedStyle BaseStyle;

        /// <summary>The ::before and ::after boxes of this element (StyleEngine.ResolvePseudo): carrier elements that are NOT in Children, so queries, templates and
        /// handlers never see them. Style is null when the element has no such box. Layout lays them out as its first and last children; hits on them go to the element.</summary>
        internal Element PseudoBefore, PseudoAfter;
        /// <summary>For a carrier: the rule style it was made from and the text it shows (a new style object is made only when one of them changes).</summary>
        internal ComputedStyle PseudoSource;
        internal string PseudoText;
        /// <summary>The element a ::before / ::after box belongs to (null for a real element). Hits and focus on the box are the element's.</summary>
        public Element PseudoHost { get; internal set; }

        // Numeric overrides (SetTranslate/SetScale): like element.style in a browser they replace the CSS value of that property, transitions and
        // animations of it included, and go around the style string and the style cache, so a thing that moves every frame costs nothing in either.
        // Not copied by Clone: they belong to this live element, not to the markup.
        // The setters are internal: a page sets them through UiWindow.SetTranslate/SetScale, which also refreshes the hit cache of the mouse.
        private float ovTx, ovTy, ovScale;
        private bool ovTranslate, ovScaleOn;

        /// <summary>A numeric translate (pixels) is set and replaces the CSS one.</summary>
        public bool HasTranslate => ovTranslate;
        /// <summary>The numeric translate in pixels (0, 0 when none is set).</summary>
        public (float X, float Y) TranslateOverride => ovTranslate ? (ovTx, ovTy) : (0, 0);
        /// <summary>The numeric scale (already cleaned), null when none is set.</summary>
        public float? ScaleOverride => ovScaleOn ? ovScale : null;

        /// <summary>Sets the translate in pixels; true when it changed. NaN and infinity are ignored (false).</summary>
        internal bool SetTranslate(float x, float y)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y)) { return false; }
            if (ovTranslate && ovTx == x && ovTy == y) { return false; }
            ovTranslate = true; ovTx = x; ovTy = y;
            return true;
        }

        /// <summary>Back to the CSS translate; true when an override was set.</summary>
        internal bool ClearTranslate()
        {
            if (!ovTranslate) { return false; }
            ovTranslate = false; ovTx = ovTy = 0;
            return true;
        }

        /// <summary>Sets the scale (about the block's corner, see UiTransform.CleanScale); true when it changed.</summary>
        internal bool SetScale(float s)
        {
            if (!float.IsFinite(s)) { return false; }
            s = UiTransform.CleanScale(s);
            if (ovScaleOn && ovScale == s) { return false; }
            ovScaleOn = true; ovScale = s;
            return true;
        }

        /// <summary>Back to the CSS scale; true when an override was set.</summary>
        internal bool ClearScale()
        {
            if (!ovScaleOn) { return false; }
            ovScaleOn = false; ovScale = 0;
            return true;
        }
        /// <summary>Running transitions and keyframe animations (Animator); null when there are none.</summary>
        internal ElementAnim Anim;
        /// <summary>The animation declaration the element's keyframe animations were started for (kept after they end, so a restyle does not play them again).</summary>
        internal string AnimText;
        /// <summary>The typewriter effect's state (the painter's).</summary>
        internal TypeState Typing;
        /// <summary>A layout transition running on the element (Animator.FlipEnd), and where it is drawn against its layout box right now:
        /// the offsets of the position and of the size. The painter adds them; the layout itself never sees them.</summary>
        internal FlipState Flip;
        internal float FlipX, FlipY, FlipW, FlipH;
        /// <summary>Left out of the layout (data-if false) without leaving the tree.</summary>
        public bool Hidden;
        /// <summary>Template scope of a data-for copy (loop variable); looked up through the parents.</summary>
        public object Scope;

        public Element(string tag) { Tag = tag.ToLowerInvariant(); }

        public IReadOnlyDictionary<string, string> Attributes => attributes;
        public string Id => GetAttribute("id");
        /// <summary>A control's current value (input text, select option); this.value in markup.</summary>
        public string Value => GetAttribute("value") ?? "";
        /// <summary>A checkbox's state; this.checked in markup.</summary>
        public bool Checked => GetAttribute("checked") != null;

        public string GetAttribute(string name) => attributes.TryGetValue(name, out var v) ? v : null;

        public void SetAttribute(string name, string value)
        {
            attributes[name] = value;
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase)) { classes = null; }
        }

        public void RemoveAttribute(string name)
        {
            attributes.Remove(name);
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase)) { classes = null; }
        }

        public IReadOnlyCollection<string> Classes
        {
            get
            {
                if (classes == null)
                {
                    classes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var c in (GetAttribute("class") ?? "").Split(Space, StringSplitOptions.RemoveEmptyEntries)) { classes.Add(c); }
                }
                return classes;
            }
        }

        public bool HasClass(string name) => ((HashSet<string>)Classes).Contains(name);

        public void ToggleClass(string name, bool on)
        {
            if (HasClass(name) == on) { return; }
            var set = new List<string>(Classes);
            if (on) { set.Add(name); } else { set.Remove(name); }
            SetAttribute("class", string.Join(" ", set));
        }

        public void Append(Node child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
        }

        public void Insert(int index, Node child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Insert(Math.Clamp(index, 0, Children.Count), child);
        }

        /// <summary>Deep copy: attributes, text, line numbers; no style, state or scope.</summary>
        public Element Clone()
        {
            var e = new Element(Tag) { Line = Line };
            foreach (var kv in attributes) { e.attributes[kv.Key] = kv.Value; }
            foreach (var c in Children)
            {
                Node copy = c is Element ce ? ce.Clone() : new TextNode(((TextNode)c).Text) { Line = c.Line };
                copy.Parent = e;
                e.Children.Add(copy);
            }
            return e;
        }

        public void Remove(Node child)
        {
            if (Children.Remove(child)) { child.Parent = null; }
        }

        /// <summary>Element children only, in order.</summary>
        public IEnumerable<Element> ElementChildren()
        {
            foreach (var c in Children) { if (c is Element e) { yield return e; } }
        }

        /// <summary>Depth-first, in document order; one iterator and one stack, not one iterator per level.</summary>
        public IEnumerable<Element> Descendants()
        {
            var stack = new List<(Element E, int I)> { (this, 0) };
            while (stack.Count > 0)
            {
                var (e, i) = stack[^1];
                if (i >= e.Children.Count) { stack.RemoveAt(stack.Count - 1); continue; }
                stack[^1] = (e, i + 1);
                if (e.Children[i] is Element c)
                {
                    yield return c;
                    stack.Add((c, 0));
                }
            }
        }

        /// <summary>Position among the parent's element children (0-based), -1 for the root.</summary>
        public int ElementIndex(out int count)
        {
            count = 0;
            if (Parent == null) { return -1; }
            int index = -1;
            var siblings = Parent.Children;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] is not Element) { continue; }
                if (siblings[i] == this) { index = count; }
                count++;
            }
            return index;
        }

        public override string TextContent
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var c in Children) { sb.Append(c.TextContent); }
                return sb.ToString();
            }
        }

        public override string ToString() => $"<{Tag}{(Id != null ? "#" + Id : "")}{(Classes.Count > 0 ? "." + string.Join(".", Classes) : "")}>";
    }

    public sealed class Document
    {
        public readonly Element Root = new("#root");
        public readonly List<Diagnostic> Diagnostics = new();
        /// <summary>Contents of &lt;style&gt; elements, in document order.</summary>
        public readonly List<(string Css, int Line)> Styles = new();
        public string File = "html";

        /// <summary>data-viewport="screen" on &lt;html&gt; or &lt;body&gt;: the page's viewport is the whole screen instead of the 1600u x 900u canvas
        /// centred on it, so position: fixed and the page's edges are the real edges of the screen (HUD pieces that hang from a corner).</summary>
        public bool ScreenViewport
        {
            get
            {
                foreach (var e in Root.Descendants())
                {
                    if (e.Tag is "html" or "body" && e.GetAttribute("data-viewport") == "screen") { return true; }
                }
                return false;
            }
        }

        public Element GetElementById(string id)
        {
            foreach (var e in Root.Descendants()) { if (e.Id == id) { return e; } }
            return null;
        }
    }
}
