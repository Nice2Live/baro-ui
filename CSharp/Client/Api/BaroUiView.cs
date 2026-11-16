using System;
using System.Collections.Generic;
using System.Text;
using Barotrauma;
using BaroUI.Core;
using BaroUI.Render;

namespace BaroUI
{
    /// <summary>The C# side of a page — what JavaScript is in a browser (docs/ui/ENGINE_PLAN.md, stage 4).
    /// Subclass it, mark methods [UiHandler] and call them from markup (onclick="craft(r.id)"); keep the model in
    /// Data and change it with Set or by editing the object and calling Refresh. DOM access: Find/FindAll take CSS
    /// selectors; Text, ToggleClass, Style, Append, Remove change the page. Changes that need the server (craft,
    /// buy) go through the package's own messages: Send marks the source element .pending until Done or a timeout,
    /// and the server's answer updates Data — the view never changes game state itself.</summary>
    public abstract class BaroUiView
    {
        public UiWindow Window { get; private set; }
        public Document Document => Window.Document;
        private readonly List<(Element E, double Until)> pending = new();

        internal void Attach(UiWindow window)
        {
            Window = window;
            window.View = this;
            window.Handlers.AddFrom(this);
            OnOpen();
        }

        internal void Frame(float deltaTime)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (Timing.TotalTime >= pending[i].Until) { Done(pending[i].E); }
            }
            OnUpdate(deltaTime);
        }

        internal void AfterInput() => OnAfterInput();

        internal void Closed() => OnClose();

        /// <summary>After the page is loaded, before its first draw.</summary>
        protected virtual void OnOpen() { }
        /// <summary>Every frame while the window is open, at the start of the window's update: BEFORE this frame's mouse and clicks are handled.</summary>
        protected virtual void OnUpdate(float deltaTime) { }
        /// <summary>Every frame, after this frame's mouse, clicks and wheel were handled and before the window draws: the place for what must follow
        /// the input in the same frame (a layer that follows a camera the mouse just moved).</summary>
        protected virtual void OnAfterInput() { }
        protected virtual void OnClose() { }

        /// <summary>Esc from the game, after the window's own popups, dropdowns and modal dialogs had theirs, and before the window closes (or its
        /// Escape event runs). True: the view took the key (it stepped back one thing of its own, e.g. a level inside its page); false: nothing for the
        /// view to step back, the window closes. A view that closes the window itself answers true.</summary>
        protected virtual bool OnEscape() => false;
        internal bool EscapeHook() => OnEscape();

        /// <summary>Back: mouse side button 4 over the window, or Alt+Left, when no element of the page has onback (its handler ran first, see
        /// Core/NavRoute.cs). True: handled, the game does not act on the button then. The view steps back one of its own things (a level, a section).</summary>
        protected virtual bool OnBack() => false;
        /// <summary>Forward: mouse side button 5 over the window, or Alt+Right, when no element has onforward. True: handled.</summary>
        protected virtual bool OnForward() => false;
        internal bool BackHook() => OnBack();
        internal bool ForwardHook() => OnForward();

        // ---------------------------------------------------------------- model

        public object Data { get => Window.Data; set => Window.Data = value; }
        public bool Set(string path, object value) => Window.Set(path, value);
        public void Refresh() => Window.Refresh();

        // ---------------------------------------------------------------- DOM

        public Element Find(string selector) => Query.First(Document.Root, selector);
        public List<Element> FindAll(string selector) => Query.All(Document.Root, selector);

        public string Text(Element e) => e?.TextContent;
        public void Text(Element e, string text) => Window.SetText(e, text);
        public void ToggleClass(Element e, string cls, bool on) => Window.ToggleClass(e, cls, on);
        public bool HasClass(Element e, string cls) => e != null && e.HasClass(cls);

        /// <summary>Sets one inline style property (null removes it), like element.style.prop = value.</summary>
        public void Style(Element e, string property, string value)
        {
            if (e == null) { return; }
            var sb = new StringBuilder();
            foreach (var decl in (e.GetAttribute("style") ?? "").Split(';'))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0 || decl.Substring(0, colon).Trim().Equals(property, StringComparison.OrdinalIgnoreCase)) { continue; }
                sb.Append(decl.Trim()).Append("; ");
            }
            if (value != null) { sb.Append(property).Append(": ").Append(value).Append(';'); }
            string style = sb.ToString().Trim();
            if (style.Length == 0) { e.RemoveAttribute("style"); } else { e.SetAttribute("style", style); }
            Window.RestyleElement(e);
        }

        public void Attr(Element e, string name, string value)
        {
            if (e == null) { return; }
            if (value == null) { e.RemoveAttribute(name); } else { e.SetAttribute(name, value); }
            if (name == "disabled")
            {
                if (value == null) { e.State &= ~NodeState.Disabled; } else { e.State |= NodeState.Disabled; }
            }
            Window.RestyleElement(e);
        }

        /// <summary>Parses an HTML fragment and appends its elements; returns them. Static content: {{…}} and data-*
        /// bindings are compiled only for the page as loaded (use data-for for repeated content).</summary>
        public List<Element> Append(Element parent, string html)
        {
            var added = new List<Element>();
            if (parent == null) { return added; }
            var frag = HtmlParser.Parse(html, Document.File + " (fragment)");
            Document.Diagnostics.AddRange(frag.Diagnostics);
            foreach (var n in frag.Root.Children.ToArray())
            {
                parent.Append(n);
                if (n is Element e) { added.Add(e); }
            }
            Window.RestyleElement(parent);
            Window.Relayout(parent);
            return added;
        }

        public void Remove(Element e)
        {
            if (e?.Parent == null) { return; }
            var parent = e.Parent;
            parent.Remove(e);
            Window.Relayout(parent);
        }

        // ---------------------------------------------------------------- server round trips

        /// <summary>Sends a request (the package's own message) and marks the element .pending and disabled until
        /// Done(source) or the timeout, so a second click cannot send it twice.</summary>
        public void Send(Element source, Action send, float timeoutSeconds = 5)
        {
            if (source != null)
            {
                Window.ToggleClass(source, "pending", true);
                source.State |= NodeState.Disabled;
                pending.Add((source, Timing.TotalTime + timeoutSeconds));
            }
            send();
        }

        public void Done(Element source)
        {
            int i = pending.FindIndex(p => p.E == source);
            if (i < 0) { return; }
            pending.RemoveAt(i);
            source.State &= ~NodeState.Disabled;
            if (source.GetAttribute("disabled") != null) { source.State |= NodeState.Disabled; }
            Window.ToggleClass(source, "pending", false);
        }

        public void Close() => Window.Close();
    }
}
