using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>One HTML/CSS page on screen (docs/ui/ENGINE_PLAN.md, stage 3). The viewport is a 1600u x 900u canvas
    /// centred on the game window; the page places itself in it with CSS. Drawn by a single GUICustomComponent whose mouse area is whatever painted
    /// box is under the cursor, so transparent parts let the game through. Hover/active states restyle only the
    /// affected subtree and skip the layout when only colours changed. Exceptions close the window, not the game.</summary>
    public sealed class UiWindow
    {
        /// <summary>Paint-only properties: a change in these alone keeps the layout.</summary>
        private static readonly HashSet<string> PaintOnly = new(StringComparer.Ordinal)
        {
            "color", "background-color", "background-image", "border-top-color", "border-right-color", "border-bottom-color",
            "border-left-color", "opacity", "visibility", "cursor", "pointer-events", "scrollbar-color",
            "transition", "animation", "translate", "scale", "transform-origin", "typewriter",
            "transition-property", "transition-duration", "transition-timing-function", "transition-delay",
            "animation-name", "animation-duration", "animation-timing-function", "animation-delay",
            "animation-iteration-count", "animation-direction", "animation-fill-mode", "layout-fps",
            // drawn outside the layout: an outline or a text shadow that changes on :focus or :hover must not relayout the page
            "text-shadow", "outline-width", "outline-style", "outline-color", "outline-offset", "box-shadow", "text-decoration-color",
        };

        public string Name { get; }
        public Document Document { get; }
        public LayoutBox Root { get; private set; }
        public bool IsOpen { get; private set; } = true;

        /// <summary>A typewriter text (the typewriter property) is still coming out.</summary>
        public bool IsTyping => painter.IsTyping;

        /// <summary>Shows every typewriter text in full at once.</summary>
        public void SkipTyping() => painter.SkipTyping();

        /// <summary>Transitions or keyframe animations are moving something.</summary>
        public bool IsAnimating => animator.Running > 0;
        /// <summary>The element pressed and released under the mouse (the deepest one; walk up for handlers).</summary>
        public event Action<UiWindow, Element> Clicked;
        public float UnitPx { get; private set; }
        public int LayoutCount { get; private set; }
        public double LastStyleMs { get; private set; }
        public double LastLayoutMs { get; private set; }
        public double LastUpdateMs { get; private set; }
        /// <summary>{{…}}, data-for/if/class bindings of the page (stage 4).</summary>
        public Template Template { get; }
        /// <summary>Methods the markup calls (onclick="name(args)").</summary>
        public Handlers Handlers { get; } = new();
        /// <summary>The C# side of the page, when opened with one.</summary>
        public BaroUiView View { get; internal set; }
        /// <summary>Focus, keyboard, dropdown, modal dialogs, canvases (stage 5).</summary>
        public UiControls Controls { get; }
        /// <summary>The mouse of the page's canvases (SetCanvasMouse).</summary>
        internal UiCanvasInput CanvasInput { get; }
        /// <summary>The mouse of the whole window: data-mouse elements (SetMouse) and canvases (SetCanvasMouse) go through this one router.</summary>
        internal PointerRouter Router { get; } = new();
        /// <summary>The keys of blocks (data-keys, SetKeys): they come only while an element of this window has the focus.</summary>
        internal KeyRouter KeyRoute { get; } = new();
        /// <summary>Drag and drop between blocks (data-drag, data-drop, SetDrop): Core/DragDrop.cs; the window is its host.</summary>
        internal DragDrop Drag { get; private set; }
        /// <summary>Where elements are, for the router.</summary>
        internal IPointerGeometry PointerGeo { get; }
        /// <summary>Pictures from files for the draw callbacks of canvases (Images.Draw(sb, path, box)); freed with the window.</summary>
        public UiImages Images { get; }
        /// <summary>The window's &lt;video&gt; elements: decoders and frames (Render/UiVideos.cs); closed with the window.</summary>
        internal UiVideos Videos { get; }
        /// <summary>Elements that take dropped files and pasted files, paths and pictures (SetFileInput).</summary>
        internal UiFileTargets FileTargets { get; }
        /// <summary>Esc with nothing focused and no modal open. No handler: the window closes.</summary>
        public event Action<UiWindow> Escape;

        /// <summary>The model the bindings read. Setting it, Set or Refresh re-evaluates them before the next draw.</summary>
        public object Data
        {
            get => Template.Data;
            set { Template.Data = value; dataDirty = true; }
        }

        private readonly StyleEngine styles;
        private readonly UiFonts fonts;
        private readonly Painter painter;
        /// <summary>The window's texture budget: every texture of the window (pictures of canvases and CSS, shadows, gradients, corner masks) counts in it.
        /// Over either limit the idle textures go first, the longest unused first; a texture of this or the last frame is never released.</summary>
        private readonly TextureLedger budget = new(WindowBudgetBytes, WindowBudgetCount);
        public const long WindowBudgetBytes = 256L * 1024 * 1024;
        public const int WindowBudgetCount = 4096;
        private readonly Animator animator;
        private readonly Host host;
        private readonly Dictionary<Element, float> scroll = new();
        private static readonly Dictionary<Element, LayoutBox> NoBoxes = new();
        private IReadOnlyDictionary<Element, LayoutBox> boxOf = NoBoxes;
        /// <summary>Kept between layouts: Update re-lays only what changed (stage 7).</summary>
        private LayoutEngine engine;
        /// <summary>Elements whose text, visibility, children or layout style changed since the last layout.</summary>
        private readonly HashSet<Element> dirtyLayout = new();
        /// <summary>Containers whose element children were added, removed or moved since the last layout: LayoutEngine.Update brings their child boxes in place.</summary>
        private readonly HashSet<Element> dirtyChildren = new();
        private readonly HashSet<string> logged = new();
        private Point screen;
        /// <summary>The page asked for the whole screen as its viewport (data-viewport="screen"), not the centred canvas.</summary>
        private readonly bool screenViewport;
        private bool restyleAll = true, relayout = true, dataDirty = true;
        private readonly List<Element> hoverChain = new(), activeChain = new();
        private Element pressed;
        private Point hitPoint = new(int.MinValue, int.MinValue);
        private int hitStamp = -1, stamp, hitOverlay = -1;
        /// <summary>Bumped by anything that moves paint without a relayout (a restyle, a running animation, a numeric translate/scale): part of the hit cache key, so a still mouse does not keep a stale answer.</summary>
        private int paintStamp, hitPaint = -1;
        private Element hitElement;
        private LayoutBox hitBox;
        private Rectangle hitRect;

        internal UiWindow(string name, Document doc, IEnumerable<StyleSheet> sheets, UiFonts fonts, string pageDir = null)
        {
            Name = name;
            Document = doc;
            screenViewport = doc.ScreenViewport;
            this.fonts = fonts;
            styles = new StyleEngine(pageDir, sheets.Prepend(CssParser.Parse(PopupPlace.Css, "popup")).ToArray());   // data-popup blocks are hidden until opened
            Images = new UiImages(name, budget);
            Drawing = new UiDraw(budget);
            painter = new Painter(fonts, Images, budget) { Scroll = scroll, BoxOf = BoxOf };
            Videos = new UiVideos(this, budget, () => painter.PageFolder());   // <video> elements: decoders, frames, events (docs/ui/AUTHORING.md, «Видео»)
            painter.Videos = Videos;
            // transitions and keyframe animations: the cascade asks the animator for every element's style; the window moves it each frame
            animator = new Animator
            {
                Clock = () => Timing.TotalTime,
                Keyframes = n => styles.KeyframeDefs.TryGetValue(n, out var k) ? k : null,
                IsAttached = e => Within(e, Document.Root),
                LayoutChanged = e => dirtyLayout.Add(e.Parent ?? e),
                Enabled = () => BaroUi.Animations,
                LayoutFps = () => BaroUi.LayoutFps,
            };
            painter.TypewriterOn = () => BaroUi.Animations;
            styles.Animator = animator;
            styles.PseudoBoxMade = h => dirtyLayout.Add(h.Parent ?? h);   // a ::before/::after box that appeared is not in the before/after style lists of a restyle
            host = new Host(this);
            Template = new Template(doc) { Virtual = new VirtualHost(this) };
            Controls = new UiControls(this);
            CanvasInput = new UiCanvasInput(this);
            PointerGeo = new PointerGeometry(this);
            Drag = new DragDrop(new DragHost(this)) { Geo = PointerGeo, OnError = (n, ex) => UiPlugin.Log($"{Name}: drop handler '{n}' failed and was removed: {ex}") };
            Router.IsAlive = PointerAlive;
            Router.OnError = RouterError;
            Router.UnderExcept = UnderExcept;
            KeyRoute.OnError = (n, ex) => UiPlugin.Log($"{Name}: key handler '{n}' failed and was removed: {ex}");
            FileTargets = new UiFileTargets(this);
            painter.Controls = Controls;
            Handlers.AddBuiltin("close", new Action(Close));
            Handlers.AddBuiltin("showModal", new Action<string>(ShowModal));
            Handlers.AddBuiltin("closeDialog", new Action<string>(CloseDialog));
            Handlers.AddBuiltin("focus", new Action<string>(FocusId));
            Handlers.AddBuiltin("play", new Action<string>(id => VideoCommand(id, VideoCmd.Play)));
            Handlers.AddBuiltin("pause", new Action<string>(id => VideoCommand(id, VideoCmd.Pause)));
            Handlers.AddBuiltin("stop", new Action<string>(id => VideoCommand(id, VideoCmd.Pause)));
            Handlers.AddBuiltin("restart", new Action<string>(id => VideoCommand(id, VideoCmd.Restart)));
            foreach (var e in doc.Root.Descendants())
            {
                if (e.GetAttribute("disabled") != null) { e.State |= NodeState.Disabled; }
            }
            // parse problems are known now: Problems (and the banner) must not wait for the first layout
            Report(doc.Diagnostics);
            Report(styles.Diagnostics);
        }

        /// <summary>Reads an .html file and the stylesheets it links (rel="stylesheet", paths relative to the page
        /// and inside its folder).</summary>
        public static UiWindow FromFile(string htmlPath, UiFonts fonts, string name = null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(htmlPath));
            var doc = HtmlParser.Parse(File.ReadAllText(htmlPath), Path.GetFileName(htmlPath));
            Localizer.Apply(doc);
            var sheets = new List<StyleSheet>();
            var files = new List<string>();
            foreach (var link in doc.Root.Descendants().Where(e => e.Tag == "link").ToList())
            {
                string href = link.GetAttribute("href");
                if (!string.Equals(link.GetAttribute("rel"), "stylesheet", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(href)) { continue; }
                string path = Path.GetFullPath(Path.Combine(dir, href));
                if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    doc.Diagnostics.Add(new Diagnostic(doc.File, link.Line, $"stylesheet '{href}' not found next to the page"));
                    continue;
                }
                sheets.Add(CssParser.Parse(File.ReadAllText(path), Path.GetFileName(path)));
                files.Add(path);
            }
            var w = new UiWindow(name ?? Path.GetFileNameWithoutExtension(htmlPath), doc, sheets, fonts, dir) { SourcePath = Path.GetFullPath(htmlPath) };
            files.Insert(0, w.SourcePath);
            foreach (var f in files) { w.sources.Add((f, File.GetLastWriteTimeUtc(f))); }
            return w;
        }

        /// <summary>The .html the window was read from (null: built from strings).</summary>
        /// <summary>The page's html file; the painter resolves relative picture paths (url(...), &lt;img src&gt;) against its folder, for a window hosted in an
        /// item too (one not in BaroUi.Windows).</summary>
        public string SourcePath
        {
            get => sourcePath;
            private set { sourcePath = value; if (painter != null) { painter.PageDir = value == null ? null : Path.GetDirectoryName(value); } }
        }
        private string sourcePath;
        private readonly List<(string Path, DateTime Stamp)> sources = new();

        /// <summary>True when the page or one of its stylesheets was saved since it was read (bbe_ui watch).</summary>
        internal bool SourcesChanged()
        {
            foreach (var (path, stamp) in sources)
            {
                if (File.GetLastWriteTimeUtc(path) != stamp) { return true; }
            }
            return false;
        }

        /// <summary>A reload failed: wait for the next save instead of retrying every poll.</summary>
        internal void MarkSourcesSeen()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                try { sources[i] = (sources[i].Path, File.GetLastWriteTimeUtc(sources[i].Path)); } catch (IOException) { }
            }
        }

        /// <summary>Every problem found so far (parse, style, layout, handlers), each once: the error banner and bbe_ui problems.</summary>
        public IReadOnlyList<Diagnostic> Problems => problems;
        private readonly List<Diagnostic> problems = new();

        /// <summary>Hot reload: the new window of the same page takes the model, the view, event subscribers and
        /// scroll positions (by element id) of the old one, which is released without OnClose.</summary>
        internal void TakeOver(UiWindow old)
        {
            Template.Data = old.Template.Data;
            dataDirty = true;
            Clicked = old.Clicked;
            Escape = old.Escape;
            foreach (var kv in old.scroll)
            {
                var e = kv.Key.Id != null ? Find(kv.Key.Id) : null;
                if (e != null) { scroll[e] = kv.Value; }
            }
            old.IsOpen = false;
            try { old.Controls.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the keyboard failed: {e}"); }
            try { old.CanvasInput.Dispose(); old.Router.Cancel(Timing.TotalTime); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the canvas mouse failed: {e}"); }
            try { old.Drag.Cancel(Timing.TotalTime); } catch (Exception e) { UiPlugin.Log($"{Name}: cancelling the drag failed: {e}"); }
            try { old.Images.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the pictures failed: {e}"); }
            try { old.Drawing.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the drawing kit failed: {e}"); }
            old.painter.Dispose();
            try { old.fonts.Release(old.styles.FontFaces); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the fonts failed: {e}"); }
            old.View?.Attach(this);
        }

        // ---------------------------------------------------------------- DOM access (stage 4 grows this)

        public Element Find(string id) => Document.GetElementById(id);

        /// <summary>A &lt;video&gt; of this window by its id: Play, Pause, Stop, Restart, Volume, IsPlaying, Duration (Api/BaroUiVideo.cs).</summary>
        public BaroUI.BaroUiVideo Video(string id) => new BaroUI.BaroUiVideo(this, id);

        /// <summary>play(id), pause(id), stop(id), restart(id) from the markup: the built-in handlers of every window.</summary>
        private void VideoCommand(string id, VideoCmd cmd) => Videos.Command(Find(id), cmd);

        public void SetText(Element e, string text)
        {
            if (e == null || e.TextContent == text) { return; }
            e.Children.Clear();
            e.Append(new TextNode(text));
            dirtyLayout.Add(e);
        }

        /// <summary>Writes a value into the model at a dotted path (a dictionary model is created when there is none).</summary>
        public bool Set(string path, object value)
        {
            Template.Data ??= new Dictionary<string, object>(StringComparer.Ordinal);
            dataDirty = true;
            if (Template.SetPath(Template.Data, path, value)) { return true; }
            UiPlugin.Log($"{Name}: cannot set '{path}' on {Template.Data.GetType().Name}");
            return false;
        }

        /// <summary>The model changed underneath (a C# object): re-evaluate the bindings before the next draw.</summary>
        public void Refresh() => dataDirty = true;

        /// <summary>The tree changed shape (elements added or moved): style and lay out everything again.</summary>
        public void Invalidate() { restyleAll = true; relayout = true; }

        /// <summary>Lay out again (text or visibility changed).</summary>
        public void Relayout() => relayout = true;

        /// <summary>Lay out again where e is (its text, children or visibility changed): only that part of the page.</summary>
        public void Relayout(Element e)
        {
            if (e != null) { dirtyLayout.Add(e); } else { relayout = true; }
        }

        /// <summary>Restyles an element after a class, attribute or inline style change.</summary>
        public void RestyleElement(Element e) => Restyle(e);

        /// <summary>Sets the numeric translate (pixels) of e, replacing its CSS one: no restyle, no relayout, no string. Use it for what moves every frame.</summary>
        public void SetTranslate(Element e, float x, float y) { if (Mine(e) && e.SetTranslate(x, y)) { PaintChanged(); } }

        /// <summary>Back to the CSS translate of e.</summary>
        public void ClearTranslate(Element e) { if (Mine(e) && e.ClearTranslate()) { PaintChanged(); } }

        /// <summary>Sets the numeric scale of e (about its corner), replacing its CSS one: no restyle, no relayout.</summary>
        public void SetScale(Element e, float s) { if (Mine(e) && e.SetScale(s)) { PaintChanged(); } }

        /// <summary>Back to the CSS scale of e.</summary>
        public void ClearScale(Element e) { if (Mine(e) && e.ClearScale()) { PaintChanged(); } }

        /// <summary>Only paint changed: the hit cache is stale. The window paints every frame (no picture is kept), so nothing else needs a nudge.</summary>
        private void PaintChanged() => paintStamp++;

        /// <summary>The element is in this window's page now: an element kept by a caller after a data-if removed it (or of another window) is not
        /// moved, and does not renew the hit cache every frame for nothing.</summary>
        private bool Mine(Element e) => e != null && Document != null && Within(e, Document.Root);

        /// <summary>A click as if the mouse pressed and released on the element (tests, keyboard).</summary>
        public void Click(Element e)
        {
            if (e != null && !Disabled(e)) { OnClick(e); }
        }

        // ---------------------------------------------------------------- popups and context menus (Core/Popup.cs, docs/ui/AUTHORING.md)

        /// <summary>Shows the <c>data-popup</c> block with this id at a screen point (a context menu); anchor is the element it was opened for (PopupAnchor). It opens to the
        /// left or above the point when it would leave the screen, and is pushed inside. One popup at a time: another one open closes (Replaced).</summary>
        public void ShowPopup(string id, float screenX, float screenY, Element anchor = null) =>
            Controls.ShowPopup(Find(id), new PopupRequest { X = screenX, Y = screenY, Anchor = anchor });

        /// <summary>Shows the popup below, above, right or left of the anchor's ScreenRect, flipping to the opposite side when it does not fit.</summary>
        public void ShowPopup(string id, Element anchor, string placement = "below")
        {
            if (anchor == null) { UiPlugin.Log($"{Name}: ShowPopup('{id}'): no anchor element"); return; }
            Controls.ShowPopup(Find(id), new PopupRequest { Anchor = anchor, Placement = PopupPlace.Normalize(placement) });
        }

        /// <summary>Closes the popup with this id, or the open one without an id (reason Code).</summary>
        public void ClosePopup(string id = null) => Controls.ClosePopup(id != null ? Find(id) : null);

        public bool IsPopupOpen(string id) => id != null && Controls.IsPopupOpen(Find(id));

        /// <summary>The element the open popup was opened for (null: none open, or opened at a bare point).</summary>
        public Element PopupAnchor => Controls.PopupAnchor;

        /// <summary>A popup closed: the popup element and why (outside click, Esc, an item chosen, code, another popup). Its onclose attribute runs first.</summary>
        public event Action<Element, PopupCloseReason> PopupClosed
        {
            add => Controls.PopupClosed += value;
            remove => Controls.PopupClosed -= value;
        }

        /// <summary>Opens a dialog by id as modal (the page under it takes no input until it closes).</summary>
        public void ShowModal(string id) => Controls.ShowModal(Find(id));

        /// <summary>Closes a dialog by id, or the top modal one without an id; runs its onclose.</summary>
        public void CloseDialog(string id = null)
        {
            if (id != null) { Controls.CloseDialog(Find(id)); } else { Controls.CloseTopModal(); }
        }

        public void FocusId(string id) => Controls.SetFocus(Find(id), selectAll: true);

        /// <summary>Draws into &lt;canvas id="..."&gt;: the content box, clipped; the callback must not end the batch.</summary>
        public void SetCanvas(string id, Action<SpriteBatch, Rectangle> draw) => Controls.SetCanvas(id, draw);

        /// <summary>The drawing kit (docs/ui/DESIGN_CHARTS.md 2.1) for the callbacks of SetCanvas: strokes, dots, arcs, sectors, areas, text. Its textures are in
        /// this window's budget.</summary>
        public UiDraw Drawing { get; private set; }

        /// <summary>The mouse of &lt;canvas id="..."&gt;: Down, Move, Up of each button, the wheel, Ctrl/Shift/Alt, positions in pixels from the content box's
        /// corner; a button pressed on the canvas holds the mouse until it is up (Move and Up come from anywhere on the screen). The handler's answer:
        /// for a Down, true takes the press (the drag follows), false leaves it; for a Wheel, true means used (the page does not scroll). Double clicks
        /// are counted by the owner (ClickCounter). Null takes the handler away.</summary>
        public void SetCanvasMouse(string id, Func<CanvasMouseEvent, bool> handler) => CanvasInput.Set(id, handler);

        /// <summary>Like the other overload, for a handler that always takes what it gets (a Down starts a drag, the wheel is used).</summary>
        public void SetCanvasMouse(string id, Action<CanvasMouseEvent> handler) =>
            CanvasInput.Set(id, handler == null ? null : e => { handler(e); return true; });

        /// <summary>Takes the mouse handler of a canvas away (a drag in progress ends with a Cancel).</summary>
        public void ClearCanvasMouse(string id) => CanvasInput.Set(id, null);

        /// <summary>The handler of data-mouse="name" (docs/ui/AUTHORING.md): Down, Up, Click, Wheel, Enter, Leave and more, bubbling from the deepest element
        /// under the pointer. true stops the bubbling and, for a Down, takes the capture (Move and Up then come from anywhere until the button is up); for a
        /// Wheel true means used. Null takes the handler away (a capture it held ends with a Cancel).</summary>
        public void SetMouse(string name, Func<UiMouseEvent, bool> handler) =>
            Router.Set(name, handler == null ? null : e => { bool used = handler(e); if (used && e.Kind == UiMouseKind.ContextMenu) { contextAnswered = true; } return used; });

        /// <summary>A SetMouse handler answered true for a ContextMenu event this frame: data-contextmenu stays closed.</summary>
        private bool contextAnswered;

        /// <summary>Like the other overload, for a handler that always takes what it gets.</summary>
        public void SetMouse(string name, Action<UiMouseEvent> handler) =>
            SetMouse(name, handler == null ? null : e => { handler(e); return true; });

        /// <summary>Takes the handler of data-mouse="name" away.</summary>
        public void ClearMouse(string name) => Router.Set(name, null);

        /// <summary>The handler of data-drop-name="name" (a target: Enter, Over, Leave, Drop; true from a Drop accepts it) and of data-drag-name="name" (a source: Cancel),
        /// for elements with data-drag="kind" (carried) and data-drop="kind1 kind2" (accepted). One handler per name, like SetMouse and SetKeys: the event has the
        /// Source and the Target, so one handler serves a whole list of cards. Null takes it away. See docs/ui/AUTHORING.md.</summary>
        public void SetDrop(string name, Func<UiDropEvent, bool> handler) => Drag.Set(name, handler);

        /// <summary>Like the other overload, for a handler that accepts every Drop.</summary>
        public void SetDrop(string name, Action<UiDropEvent> handler) =>
            Drag.Set(name, handler == null ? null : e => { handler(e); return true; });

        /// <summary>Takes the handler of data-drop-name="name" away.</summary>
        public void ClearDrop(string name) => Drag.Set(name, null);

        /// <summary>The handler of data-key-name="name" on elements with data-keys="Enter Delete ...": it gets the press while that element or something inside it
        /// has the focus (give it tabindex), bubbling from the focused element up. true stops the bubbling and the default action (Space on a checkbox,
        /// Tab, Esc closing). The game gives keys to the window only while an element is focused: otherwise the character keeps its keys.</summary>
        public void SetKeys(string name, Func<UiKeyEvent, bool> handler) => KeyRoute.Set(name, handler);

        /// <summary>Like the other overload, for a handler that always takes what it gets.</summary>
        public void SetKeys(string name, Action<UiKeyEvent> handler) =>
            KeyRoute.Set(name, handler == null ? null : e => { handler(e); return true; });

        /// <summary>Takes the handler of data-key-name="name" away.</summary>
        public void ClearKeys(string name) => KeyRoute.Set(name, null);

        /// <summary>The element with this id takes files: dropped on it from Explorer (the game has no file dialog) and pasted with Ctrl+V while it has the
        /// focus (give it tabindex="0"; a click focuses it): files copied in Explorer, a path as text, a picture from the clipboard. accept: ".png,.jpg",
        /// "image/*" or null for any file. The handler's answer: true took it; false lets the next target (an ancestor) try. Null takes the handler away.</summary>
        public void SetFileInput(string id, Func<UiFileInput, bool> handler, string accept = null) => FileTargets.Set(id, handler, accept);

        public void SetFileInput(string id, Action<UiFileInput> handler, string accept = null) =>
            FileTargets.Set(id, handler == null ? null : f => { handler(f); return true; }, accept);

        public void ClearFileInput(string id) => FileTargets.Set(id, (Func<UiFileInput, bool>)null, null);

        /// <summary>The editing model of &lt;textarea id="..."&gt; (Core/CodeEdit.cs): the text, the caret and the selection, GoTo, Replace, Undo. A change
        /// through it reaches the value attribute, the data-model and oninput at the next frame, like typing. Null: no such textarea.</summary>
        public CodeEdit GetCodeEdit(string id) => Controls.Code.Model(Find(id));

        /// <summary>Scrolls a textarea to its caret at the next paint (after GoTo from code).</summary>
        public void RevealCaret(string id) => Controls.Code.Reveal(Find(id));

        /// <summary>A game inventory for the &lt;slot inventory="name" index="i"&gt; elements (UiControls.SetInventory);
        /// null gives it back to the game.</summary>
        public void SetInventory(string name, Inventory inventory) => Controls.SetInventory(name, inventory);

        /// <summary>Esc from the game: the top modal closes first, then the window (or its Escape handler). False: not handled.</summary>
        internal bool OnEscape()
        {
            if (!IsOpen) { return false; }
            // Esc ends a drag (canvas or data-mouse capture); nothing else closes on that press
            if (Drag.Active) { Drag.Cancel(Timing.TotalTime); return true; }
            if (Router.HoldsPointer) { Router.Cancel(Timing.TotalTime); return true; }
            if (Controls.ClosePopupEscape()) { return true; }
            if (!IsOpen) { return true; }   // an onclose or PopupClosed handler closed the window: nothing more of this Esc
            if (Controls.KeyEscape()) { return true; }
            if (!IsOpen) { return true; }
            if (Controls.CloseDropdownIfOpen() || Controls.CloseTopModal()) { return true; }
            if (!IsOpen) { return true; }
            // the view steps back its own levels (BaroUiView.OnEscape) before the window goes
            if (View?.EscapeHook() == true) { return true; }
            if (!IsOpen) { return true; }
            if (Escape != null) { Escape(this); } else { Close(); }
            return true;
        }

        public void ToggleClass(Element e, string cls, bool on)
        {
            if (e == null || e.HasClass(cls) == on) { return; }
            e.ToggleClass(cls, on);
            Restyle(e);
        }

        public float GetScroll(Element e) => scroll.TryGetValue(e, out float v) ? v : 0;

        /// <summary>Scrolls an overflow area by dy px, clamped; false when it is already at that end.</summary>
        public bool ScrollBy(Element e, float dy)
        {
            if (e == null || !boxOf.TryGetValue(e, out var b)) { return false; }
            // a block that does not clip (overflow lost through a class change) cannot be scrolled: its old scroll goes to 0, else the painter (which
            // only scrolls clipping blocks) and ScreenMap/the hit test (which read this value) would disagree by it
            float max = b.ClipY ? Math.Max(0, b.ScrollHeight - (b.H - b.Border.Vertical)) : 0;
            float old = GetScroll(e), now = Math.Clamp(old + dy, 0, max);
            if (Math.Abs(now - old) < 0.01f) { return false; }
            scroll[e] = now;
            stamp++;
            if (Template.HasVirtual) { dataDirty = true; }
            return true;
        }

        /// <summary>The element under a screen point (null: nothing painted there).</summary>
        public Element ElementAt(Point p) { Hit(p); return hitElement; }

        /// <summary>The element's box in layout coordinates: not moved by <c>translate</c>, scroll or <c>scale</c> (GetScrollAbove gives the scroll; ScreenRect the
        /// whole of it). Fine for sizes; for a place on screen inside a scaled block use ScreenRect.</summary>
        public LayoutBox BoxOf(Element e) => e != null && boxOf.TryGetValue(e, out var b) ? b : null;

        /// <summary>Where the element's border box is on screen: its layout position moved by the <c>translate</c> and FLIP offset of it and of every block around
        /// it, the scroll of the areas around it, and taken through the <c>scale</c> of all of them (ScreenMap). Empty: no box.</summary>
        public Rectangle ScreenRect(Element e)
        {
            var b = BoxOf(e);
            if (b == null || !PlaceOf(e, out var p)) { return Rectangle.Empty; }
            return MapOut(p.M, Painter.SnapRect(b.X + p.Dx, b.Y + p.Dy, Math.Max(0, b.W + p.Fw), Math.Max(0, b.H + p.Fh)));
        }

        private Func<Element, LayoutBox> placeBox;
        private Func<Element, float> placeScroll;
        private Func<Element, bool> placeModal;

        /// <summary>The element's offset (translate, FLIP, scroll above) and matrix to the screen, from the layout as it is now: the one answer for the
        /// painter's coordinates (see ScreenMap). False: no box.</summary>
        internal bool PlaceOf(Element e, out ScreenMap.Placement place)
        {
            placeBox ??= BoxOf;
            placeScroll ??= a => scroll.TryGetValue(a, out float v) ? v : 0;
            placeModal ??= a => painter.Deferred.Contains(a);
            return ScreenMap.Of(e, placeBox, placeScroll, placeModal, out place);
        }

        private static Rectangle MapOut(in UiTransform t, Rectangle r)
        {
            if (t.IsIdentity) { return r; }
            var q = t.MapRect(r.X, r.Y, r.Width, r.Height, outward: false);
            return new Rectangle(q.X, q.Y, q.W, q.H);
        }

        /// <summary>A rectangle in the coordinates the element is painted in (layout px plus the offsets of PlaceOf: what PaintContent gets and what the controls
        /// keep) to screen pixels; the same rectangle outside scaled blocks.</summary>
        internal Rectangle ToScreen(Element e, Rectangle r) => PlaceOf(e, out var p) ? MapOut(p.M, r) : r;

        /// <summary>A screen point in the element's layout space (what the painter draws in; the mouse of a scaled block's controls is read here), with the
        /// fraction kept.</summary>
        internal Vector2 ToLocal(Element e, Vector2 screenPoint)
        {
            if (!PlaceOf(e, out var p) || p.M.IsIdentity) { return screenPoint; }
            var back = p.M.Inverse();
            return new Vector2(back.X(screenPoint.X), back.Y(screenPoint.Y));
        }

        /// <summary>The scale an element is drawn at, all scaled blocks around it multiplied (1: none), as of the last paint.</summary>
        internal float ScaleOf(Element e) => PlaceOf(e, out var p) ? p.M.S : 1;

        /// <summary>How far the scroll areas around e move it up on screen.</summary>
        public float GetScrollAbove(Element e)
        {
            float up = 0;
            for (var a = e?.Parent; a != null; a = a.Parent) { if (scroll.TryGetValue(a, out float v)) { up += v; } }
            return up;
        }

        /// <summary>The engine's fonts (the families of the page's text): the argument of Window.Drawing.Text.</summary>
        public UiFonts Fonts => fonts;

        /// <summary>Runs the element's on-event handler (onchange, oninput, onfocus...) and reports what it warned.</summary>
        /// <summary>Whether an element is in this window's document now (a chart's registry entry is dropped when it is not).</summary>
        internal bool Attached(Element e) => Mine(e);

        /// <summary>Runs the element's on&lt;event&gt; handler; arg is the event's argument, read in the handler as $arg (onpick: the picked index).</summary>
        internal void Fire(Element e, string eventAttr, object arg = null)
        {
            if (!IsOpen || e == null) { return; }
            Template.Dispatch(e, eventAttr, Handlers, out _, arg);
            Report(Document.Diagnostics);
        }

        /// <summary>Back (back true) or forward: the onback/onforward handler of the element under the pointer and its ancestors, else of the focused
        /// element and its ancestors (Core/NavRoute.cs); else the view's OnBack/OnForward. True: something took it.</summary>
        internal bool Navigate(bool back, Element under)
        {
            if (!IsOpen) { return false; }
            string attr = back ? NavRoute.Back : NavRoute.Forward;
            var e = NavRoute.Target(under, Controls.Focus, attr);
            if (e != null && Template.Dispatch(e, attr, Handlers, out _))
            {
                Report(Document.Diagnostics);
                return true;
            }
            Report(Document.Diagnostics);
            if (!IsOpen) { return true; }
            return (back ? View?.BackHook() : View?.ForwardHook()) == true;
        }

        public void Close()
        {
            if (!IsOpen) { return; }
            IsOpen = false;
            try { Controls.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the keyboard failed: {e}"); }
            try { CanvasInput.Dispose(); Router.Cancel(Timing.TotalTime); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the canvas mouse failed: {e}"); }
            try { Drag.Cancel(Timing.TotalTime); } catch (Exception e) { UiPlugin.Log($"{Name}: cancelling the drag failed: {e}"); }
            try { Images.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the pictures failed: {e}"); }
            try { Videos.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the videos failed: {e}"); }
            try { Drawing.Dispose(); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the drawing kit failed: {e}"); }
            try { fonts.Release(styles.FontFaces); } catch (Exception e) { UiPlugin.Log($"{Name}: releasing the fonts failed: {e}"); }
            try { View?.Closed(); } catch (Exception e) { UiPlugin.Log($"{Name}: OnClose failed: {e}"); }
            BaroUi.Forget(this);
        }

        // ---------------------------------------------------------------- frame

        /// <summary>Call where the game adds its own UI (an item component's AddToGUIUpdateList, a screen hook):
        /// added later than that, the window is drawn but gets no input (docs/ui/RECON.md, section 3).</summary>
        public void AddToGUIUpdateList(int order = 0)
        {
            if (!IsOpen) { return; }
            ShownAt = Timing.TotalTime;
            long t = Stopwatch.GetTimestamp();
            try { EnsureLayout(); } catch (Exception e) { Fail(e); return; }
            stats.Ensure(Ms(t));
            host.AddToGUIUpdateList(order: order);
        }

        private readonly FrameStats stats = new();

        /// <summary>The owner's per-frame work for this window, in the numbers logged every 10 s (FrameStats).</summary>
        public void ProbeHost(double ms) => stats.Host(ms);

        /// <summary>The game's own update of the item the window replaces (FrameStats).</summary>
        public void ProbeGame(double ms) => stats.Game(ms);

        /// <summary>When the window was last added to the GUI (Esc goes to the newest one on screen).</summary>
        internal double ShownAt { get; private set; }

        private void EnsureLayout()
        {
            // frames whose animations move what the mouse hits renew the hit cache; the frame that ends such an animation does too (hence before and after)
            bool moving = animator.MovesHit;
            animator.Tick();
            if (moving || animator.MovesHit) { paintStamp++; }
            if (dataDirty)
            {
                dataDirty = false;
                long sw = Stopwatch.GetTimestamp();
                // the first update reads styles (a long data-for in a scroll area becomes virtual): compute them first
                if (Document.Root.Style == null) { styles.Compute(Document); }
                var changes = Template.Update();
                if (changes.Shown != null) { foreach (var e in changes.Shown) { animator.Enter(e); } }
                if (changes.Structure || changes.Reshaped != null)
                {
                    // only the new roots and, when a rule reads the sibling position, the children it can reach; the layout of a structural change stays whole
                    paintStamp++;
                    // RestyleOnly: every child a sibling rule can reach is restyled by that call itself, so none needs the pass over its following siblings (that made it n squared)
                    if (restyleAll || !Template.RestyleStructure(changes, styles, Document.Root, RestyleOnly)) { restyleAll = true; }
                    // a change of the children of a container (Reshaped: an added, removed or moved child) lays out that container's children only; the rest keeps its boxes
                    if (changes.Reshaped != null) { dirtyChildren.UnionWith(changes.Reshaped); }
                    if (changes.Structure && changes.Reshaped == null) { relayout = true; }
                }
                if (changes.Restyle != null) { RestyleBatch(changes.Restyle); }
                if (changes.Layout)
                {
                    // a container in Reshaped (its children changed) is laid out by its children (dirtyChildren), not as a whole
                    if (changes.Relayout != null)
                    {
                        foreach (var e in changes.Relayout) { if (changes.Reshaped == null || !changes.Reshaped.Contains(e)) { dirtyLayout.Add(e); } }
                    }
                    else { relayout = true; }
                }
                LastUpdateMs = Ms(sw);
                Report(Document.Diagnostics);
            }
            var size = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            bool resized = false;
            if (size != screen)
            {
                screen = size;
                resized = true;
                UnitPx = Math.Min(size.X / 1600f, size.Y / 900f) * GameSettings.CurrentConfig.Graphics.HUDScale;
                styles.UnitPx = UnitPx;
                painter.UnitPx = UnitPx;
                host.RectTransform.NonScaledSize = size;
                restyleAll = true;
            }
            if (restyleAll)
            {
                long sw = Stopwatch.GetTimestamp();
                styles.Compute(Document);
                LastStyleMs = Ms(sw);
                restyleAll = false;
                relayout = true;
            }
            if (!relayout && dirtyLayout.Count == 0 && dirtyChildren.Count == 0) { return; }
            long t = Stopwatch.GetTimestamp();
            // the page is designed for a 1600u x 900u canvas, centred on screens of another aspect (ultrawide); a page that hangs from the
            // screen's own edges (data-viewport="screen") gets the whole screen, the unit stays the same
            float cw = screenViewport ? screen.X : 1600 * UnitPx, ch = screenViewport ? screen.Y : 900 * UnitPx;
            float ox = screenViewport ? 0 : (float)Math.Round((screen.X - cw) / 2), oy = screenViewport ? 0 : (float)Math.Round((screen.Y - ch) / 2);
            // layout transitions: where the elements that have one are before the run and after it (not on the first run or a resize)
            bool flip = animator.HasFlip && !resized && engine != null && Root != null;
            if (flip) { animator.FlipBegin(engine.Boxes); }
            if (relayout || engine == null || Root == null)
            {
                engine ??= new LayoutEngine(fonts) { Images = painter };   // an img without a CSS size takes its picture's pixels
                Root = engine.Run(Document, cw, ch, ox, oy);
                LastFullLayout = true;
            }
            else
            {
                Root = engine.Update(dirtyLayout, cw, ch, ox, oy, dirtyChildren);
                LastFullLayout = false;
            }
            dirtyLayout.Clear();
            dirtyChildren.Clear();
            boxOf = engine.Boxes;
            if (flip) { animator.FlipEnd(boxOf); }
            LastLayoutMs = Ms(t);
            relayout = false;
            LayoutCount++;
            stamp++;
            foreach (var e in scroll.Keys.ToList())
            {
                if (!boxOf.ContainsKey(e)) { scroll.Remove(e); continue; }
                ScrollBy(e, 0);
            }
            Controls.AfterLayout(painter, ox, oy, cw, ch);
            // a virtual list drawn before its rows were measured: bind again with the real row height
            if (Template.Remeasure) { Template.Remeasure = false; dataDirty = true; }
            Report(Document.Diagnostics);
            Report(styles.Diagnostics);
            Report(engine.Diagnostics);
        }

        // timings without a Stopwatch object: EnsureLayout runs every frame a view refreshes
        private static double Ms(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Whether the last layout was a full one (Run) or incremental (Update).</summary>
        public bool LastFullLayout { get; private set; }

        internal static void ShiftBox(LayoutBox root, float dx, float dy) => Shift(root, dx, dy);

        private static void Shift(LayoutBox b, float dx, float dy)
        {
            if (dx == 0 && dy == 0) { return; }
            b.X += dx;
            b.Y += dy;
            foreach (var f in b.Text) { f.X += dx; f.Y += dy; f.Baseline += dy; }
            foreach (var c in b.Children) { Shift(c, dx, dy); }
        }

        /// <summary>How many entries of each diagnostics list were seen: only new ones are formatted (every frame a
        /// view refreshes, so no string per old problem).</summary>
        private readonly Dictionary<List<Diagnostic>, int> reported = new();

        private void Report(List<Diagnostic> list)
        {
            reported.TryGetValue(list, out int from);
            if (from == list.Count) { return; }
            if (from > list.Count) { from = 0; }
            reported[list] = list.Count;
            for (int i = from; i < list.Count; i++)
            {
                var d = list[i];
                string text = d.ToString();
                if (logged.Add(text)) { problems.Add(d); UiPlugin.Log($"{Name}: {text}"); }
            }
        }

        private void Update(float deltaTime)
        {
            View?.Frame(deltaTime);
            UiImages.Sweep();
            if (!IsOpen) { return; }
            Videos.Frame(Timing.TotalTime);   // <video>: decoders, frames, onended and onerror (before the input and the draw)
            Report(Videos.Problems);
            bool mouseOn = GUI.MouseOn == host;
            var mp = PlayerInput.MousePosition.ToPoint();
            // drag and drop between blocks (data-drag/data-drop): before the hit test, which sees the dragged element where the ghost put it; a data-mouse capture or a scroll bar drag makes the press no drag
            if (Drag.Busy)
            {
                var df = new PointerFrame { X = mp.X, Y = mp.Y, Over = mouseOn, Time = Timing.TotalTime };
                if (PlayerInput.PrimaryMouseButtonHeld()) { df.Held |= MouseButtons.Left; }
                if (PlayerInput.IsCtrlDown()) { df.Mods |= KeyMods.Ctrl; }
                if (PlayerInput.IsShiftDown()) { df.Mods |= KeyMods.Shift; }
                if (PlayerInput.IsAltDown()) { df.Mods |= KeyMods.Alt; }
                Drag.Frame(df, Router.Captured || barDrag != null);
                if (!IsOpen) { return; }
            }
            if (Drag.Active) { Character.DisableControls = true; }
            var target = mouseOn ? ElementAt(mp) : null;
            // while a handler holds the pointer (a drag), :hover stays where it was: a card carried over a toolbar does not light its buttons up
            if (!Router.HoldsPointer && !Drag.Active) { SetChain(hoverChain, target, NodeState.Hover); }
            Controls.Frame();
            if (!Router.HoldsPointer && !Drag.Active) { Controls.Hover(target, mp); }
            LayoutBox barBox = null;
            Rectangle thumb = default;
            // an open popup: a press outside closes it; a left one goes nowhere else (no click on what is under, no focus change), a right one goes on
            bool popupSwallow = false;
            if (Controls.Popup != null)
            {
                if (PlayerInput.PrimaryMouseButtonDown()) { popupSwallow = Controls.OnPopupPress(mouseOn ? target : null, true); }
                else if (PlayerInput.SecondaryMouseButtonDown()) { Controls.OnPopupPress(mouseOn ? target : null, false); }
            }
            var bar = mouseOn && !popupSwallow ? BarAt(mp, out barBox, out thumb) : null;
            painter.HotBar = barDrag ?? bar;
            if (barDrag != null)
            {
                if (!PlayerInput.PrimaryMouseButtonHeld()) { barDrag = null; }
                else { ScrollBy(barDrag, barFrom + (ToLocal(barDrag, new Vector2(mp.X, mp.Y)).Y - barGrab) * barRatio - GetScroll(barDrag)); }
            }
            else if (bar != null && PlayerInput.PrimaryMouseButtonDown())
            {
                // the bar is held: a click on the track first brings the thumb's middle under the mouse
                float max = Math.Max(0, barBox.ScrollHeight - (barBox.H - barBox.Border.Vertical));
                float travel = Math.Max(1, barBox.H - barBox.Border.Vertical - thumb.Height);
                barRatio = max / travel;
                // the thumb and the bar's length are in the box's own layout space (inside a scaled block that is not the screen): the mouse is taken there
                var lm = ToLocal(bar, new Vector2(mp.X, mp.Y));
                if (!thumb.Contains((int)Math.Floor(lm.X), (int)Math.Floor(lm.Y))) { ScrollBy(bar, (lm.Y - (thumb.Y + thumb.Height / 2f)) * barRatio); }
                barDrag = bar;
                barGrab = lm.Y;
                barFrom = GetScroll(bar);
            }
            else if (PlayerInput.PrimaryMouseButtonDown() && !popupSwallow)
            {
                Controls.MouseDown(mouseOn ? target : null, mp);
                if (mouseOn && target != null && !Disabled(target))
                {
                    pressed = target;
                    Drag.Press(target, mp.X, mp.Y);
                    SetChain(activeChain, target, NodeState.Active);
                }
            }
            if (pressed != null && !PlayerInput.PrimaryMouseButtonHeld())
            {
                var p = pressed;
                pressed = null;
                SetChain(activeChain, null, NodeState.Active);
                if (PlayerInput.PrimaryMouseButtonClicked() && !Drag.Swallow && target != null && Within(target, p) && !Controls.Click(p, mp)) { OnClick(p); }
            }
            if (!IsOpen) { return; }   // the click closed the window: its mouse router is cancelled, nothing more this frame
            // the mouse router (data-mouse elements and canvases): after the press went to its scroll bar or the control it is on, before the wheel scrolls the page
            contextAnswered = false;
            bool canvasWheel = CanvasInput.Frame(mouseOn, target, barDrag != null || popupSwallow);
            if (!IsOpen) { return; }   // a canvas or data-mouse handler closed the window
            // the right press taken by a capture holder, a canvas or a data-mouse handler that answered true for its Down is handled too: its owner opens its own menu
            Controls.ContextFrame(mouseOn, target, mp, contextAnswered || Router.RightTaken);   // data-contextmenu: after the router, which tells whether a handler took the ContextMenu
            if (!IsOpen) { return; }
            if (mouseOn && PlayerInput.ScrollWheelSpeed != 0 && !canvasWheel && !Controls.Wheel(mp, PlayerInput.ScrollWheelSpeed / 120f)) { Wheel(PlayerInput.ScrollWheelSpeed); }
            // the game's cursor for the element's cursor value (it is inherited, so this is the nearest ancestor that says something); Default leaves the game's own
            if (target != null && !Disabled(target) && CssCursor.Of(target.Style?.Get("cursor")) is var cur && cur != CssCursor.Default) { GUI.MouseCursor = (CursorState)cur; }
            if (IsOpen) { View?.AfterInput(); }
        }

        private void OnClick(Element e)
        {
            Clicked?.Invoke(this, e);
            if (!IsOpen) { return; }
            var popup = Controls.Popup;
            int serial = Controls.PopupSerial;
            Template.Dispatch(e, "onclick", Handlers, out _);
            Report(Document.Diagnostics);
            if (popup != null && IsOpen) { Controls.AfterClick(e, popup, serial); }
        }

        /// <summary>The start of the frame, before anything of it is drawn (BaroUi.PrePass; the game's Draw prefix, tools/ui_host): renders the children of the
        /// rounded overflow blocks into their targets (Render/RoundedPass.cs). The window is laid out already (its update did it). Controls.BeginPaint runs
        /// here too, so the controls the pass paints are enabled for this frame.</summary>
        internal void PrePass(SpriteBatch sb)
        {
            if (!IsOpen) { return; }
            Controls.BeginPaint();
            painter.PrePass(sb, Root);
        }

        private void Draw(SpriteBatch sb)
        {
            long d0 = Stopwatch.GetTimestamp();
            EnsureLayout();
            bool timed = SelfTest.Timing(this);
            long alloc = timed ? GC.GetAllocatedBytesForCurrentThread() : 0, t0 = timed ? Stopwatch.GetTimestamp() : 0;
            if (!painter.PrePassReady) { Controls.BeginPaint(); }
            painter.Paint(sb, Root);
            if (timed)
            {
                SelfTest.AddPaint((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, GC.GetAllocatedBytesForCurrentThread() - alloc);
            }
            stats.Paint(Name, Ms(d0));
            if (UiDebug.Active) { UiDebug.Draw(this, sb); }
            if (captureFile != null)
            {
                string file = captureFile;
                captureFile = null;
                var b = (captureId != null ? BoxOf(Find(captureId)) : null) ?? Root;
                string error = SavePng(sb, new Rectangle((int)b.X - 8, (int)b.Y - 8, (int)b.W + 16, (int)b.H + 16), file);
                UiPlugin.Log(error == null ? $"{Name}: captured {file}" : $"{Name}: capture {file} failed: {error}");
            }
            BaroUi.AfterDraw(this, sb);
        }

        private string captureFile, captureId;

        /// <summary>Saves what is on screen around the element with this id (the whole page without one) as a PNG
        /// after the window's next draw: self tests, comparison with a mockup.</summary>
        public void CaptureNextDraw(string file, string id = null) { captureFile = file; captureId = id; }

        /// <summary>The back buffer inside r (clipped to the screen) as a PNG; null or the error. Inside a draw: the
        /// sprite batch is flushed first and begun again after.</summary>
        internal static string SavePng(SpriteBatch sb, Rectangle r, string file)
        {
            var gd = sb.GraphicsDevice;
            r = Rectangle.Intersect(r, new Rectangle(0, 0, gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight));
            if (r.Width <= 0 || r.Height <= 0) { return "nothing on screen"; }
            sb.End();
            try
            {
                var data = new Color[r.Width * r.Height];
                gd.GetBackBufferData(r, data, 0, data.Length);
                for (int i = 0; i < data.Length; i++) { data[i].A = 255; }
                using var tex = new Texture2D(gd, r.Width, r.Height);
                using var fs = File.Create(file);
                tex.SetData(data);
                tex.SaveAsPng(fs, r.Width, r.Height);
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally { sb.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable); }
        }

        private Element barDrag;
        private float barGrab, barFrom, barRatio;

        /// <summary>The scroll area whose bar is under the mouse (the hit box and the areas around it), with its box
        /// and the thumb in the box's own layout space (the screen outside scaled blocks; inside one, the mouse is taken there to compare);
        /// a thin bar over the content takes the mouse a little wider than it is drawn.</summary>
        private Element BarAt(Point mp, out LayoutBox box, out Rectangle thumb)
        {
            thumb = default;
            for (box = hitBox; box != null; box = box.Parent)
            {
                if (box.Element == null || !box.ClipY) { continue; }
                if (!PlaceOf(box.Element, out var place) || !painter.BarRects(box, place.Dx, place.Dy, GetScroll(box.Element), out var track, out thumb, out bool gutter)) { continue; }
                if (!gutter) { track.Inflate((int)Math.Ceiling(3 * UnitPx), 0); }
                var lm = ToLocal(box.Element, new Vector2(mp.X, mp.Y));
                if (track.Contains((int)Math.Floor(lm.X), (int)Math.Floor(lm.Y))) { return box.Element; }
            }
            return null;
        }

        private void Wheel(int speed)
        {
            float notches = speed / 120f;
            for (var b = hitBox; b != null; b = b.Parent)
            {
                if (b.Element == null || !b.ClipY || b.Element.Style.Get("overflow-y") is not ("auto" or "scroll")) { continue; }
                float step = 3 * fonts.NormalLineHeight(b.Element.Style);
                if (ScrollBy(b.Element, -notches * step)) { return; }
            }
        }

        // ---------------------------------------------------------------- states

        /// <summary>Moves a :hover/:active chain (the element and its ancestors) to a new target and restyles the
        /// subtrees whose state changed.</summary>
        private void SetChain(List<Element> chain, Element target, NodeState flag)
        {
            if (chain.Count > 0 ? chain[0] == target : target == null) { return; }
            // the page root is never in a chain: over bare background the chain stays empty, nothing to do (and nothing to allocate) every frame
            if (chain.Count == 0 && target == Document.Root) { return; }
            var next = new List<Element>();
            for (var e = target; e != null && e != Document.Root; e = e.Parent) { next.Add(e); }
            var changed = new HashSet<Element>();
            foreach (var e in chain) { if (!next.Contains(e)) { e.State &= ~flag; changed.Add(e); } }
            foreach (var e in next) { if ((e.State & flag) == 0) { e.State |= flag; changed.Add(e); } }
            chain.Clear();
            chain.AddRange(next);
            RestyleState(changed, flag);
        }

        private readonly List<Element> stateOrder = new();
        private readonly List<(Element E, ComputedStyle Old)> stateTouched = new();
        private readonly HashSet<Element> stateCovered = new();

        /// <summary>The elements whose state bits (hover, active, focus, focus-within) just changed: each is restyled only as far as some rule can see
        /// that state (StyleEngine.ComputeState), not its whole subtree. Before this, the pointer entering a window restyled the whole page (6006
        /// elements, ~280 ms on the map, docs/ui/PERF.md). Every changed element is asked, parents first; one an ancestor's full restyle already covered
        /// is skipped. Class, attribute and style changes still go through Restyle.</summary>
        internal void RestyleState(IEnumerable<Element> changed, NodeState flag)
        {
            if (restyleAll) { paintStamp++; return; }
            stateOrder.Clear();
            foreach (var e in changed)
            {
                if (e.Style == null) { restyleAll = true; paintStamp++; return; }
                if (Within(e, Document.Root)) { stateOrder.Add(e); }   // removed from the tree: nothing to do
            }
            if (stateOrder.Count == 0) { return; }
            paintStamp++;
            try { RestyleStateCore(flag); }
            catch
            {
                // the state bits and the chains are already moved: without this the elements half restyled here would stay stale until something
                // else touched them; the next frame restyles the whole page instead
                restyleAll = true;
                throw;
            }
        }

        private void RestyleStateCore(NodeState flag)
        {
            stateOrder.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            stateCovered.Clear();
            bool layout = false;
            foreach (var e in stateOrder)
            {
                if (CoveredByState(e)) { continue; }
                stateTouched.Clear();
                styles.ComputeState(e, flag, stateTouched, out bool whole);
                if (whole) { stateCovered.Add(e); }
                if (layout) { continue; }
                foreach (var (el, old) in stateTouched)
                {
                    // the parent: a change of display can move the element into or out of an inline run
                    if (!ReferenceEquals(old, el.Style) && AffectsLayout(old, el.Style)) { dirtyLayout.Add(e.Parent ?? e); layout = true; break; }
                }
            }
        }

        private bool CoveredByState(Element e)
        {
            for (var x = e.Parent; x != null; x = x.Parent) { if (stateCovered.Contains(x)) { return true; } }
            return false;
        }

        private static int Depth(Element e)
        {
            int d = 0;
            for (var x = e.Parent; x != null; x = x.Parent) { d++; }
            return d;
        }

        /// <summary>Elements the current RestyleBatch has restyled already as followers of an earlier one (null outside a batch).</summary>
        private HashSet<Element> batchCovered;

        /// <summary>Restyles a list of elements whose classes, attributes or styles all changed already: a following sibling that a + or ~ rule reaches is restyled
        /// once for the whole list, not once per changed element before it.</summary>
        private void RestyleBatch(List<Element> changed)
        {
            if (!styles.ReadsSiblingCombinators) { foreach (var e in changed) { Restyle(e); } return; }
            batchCovered = new HashSet<Element>();
            try { foreach (var e in changed) { Restyle(e); } }
            finally { batchCovered = null; }
        }

        private void Restyle(Element e) => Restyle(e, true);

        /// <summary>An element that is restyled together with every sibling a + or ~ rule can reach (RestyleStructure restyles them all itself).</summary>
        private void RestyleOnly(Element e) => Restyle(e, false);

        private void Restyle(Element e, bool following)
        {
            paintStamp++;
            if (restyleAll || e.Style == null) { restyleAll = true; return; }
            if (!Within(e, Document.Root)) { return; }   // removed from the tree (a data-for copy that went away)
            if (following && batchCovered != null && batchCovered.Contains(e)) { return; }   // an earlier element of the batch restyled it, after all changes were made
            var before = new List<(Element E, ComputedStyle S)>();
            Collect(e, before);
            styles.Compute(e);
            // a + or ~ rule: a class, attribute or style change of e can change the following siblings (and what is in them) too
            if (following && styles.ReadsSiblingCombinators)
            {
                var reached = new List<Element>();
                if (batchCovered != null) { styles.FollowingReached(e, reached, batchCovered); } else { styles.FollowingReached(e, reached); }
                foreach (var f in reached) { Collect(f, before); styles.Compute(f); }
            }
            // a :has() rule: the ancestors and earlier siblings whose match can depend on e (their styles are recorded in before for the layout check)
            if (styles.ReadsHasSelectors) { styles.ComputeHas(e, before); }
            foreach (var (el, old) in before)
            {
                // the parent: a change of display can move the element into or out of an inline run
                if (!ReferenceEquals(old, el.Style) && AffectsLayout(old, el.Style)) { dirtyLayout.Add(e.Parent ?? e); return; }
            }
        }

        private static void Collect(Element e, List<(Element, ComputedStyle)> into)
        {
            into.Add((e, e.Style));
            // the ::before / ::after boxes are laid out with their element: their style changes are changes of it
            if (e.PseudoBefore != null) { into.Add((e.PseudoBefore, e.PseudoBefore.Style)); }
            if (e.PseudoAfter != null) { into.Add((e.PseudoAfter, e.PseudoAfter.Style)); }
            foreach (var c in e.ElementChildren()) { Collect(c, into); }
        }

        private static bool AffectsLayout(ComputedStyle a, ComputedStyle b)
        {
            if (a == null || b == null || a.FontSizePx != b.FontSizePx) { return true; }
            foreach (var kv in a.Values)
            {
                if (PaintOnly.Contains(kv.Key)) { continue; }
                if (!b.Values.TryGetValue(kv.Key, out var v) || v != kv.Value) { return true; }
            }
            foreach (var k in b.Values.Keys)
            {
                if (!PaintOnly.Contains(k) && !a.Values.ContainsKey(k)) { return true; }
            }
            return false;
        }

        internal static bool IsDisabled(Element e) => Disabled(e);

        /// <summary>The one alive rule of the mouse router (canvases and data-mouse holders): the box exists, nothing above is disabled, and no modal dialog
        /// covers it (a dialog opened over a drag ends the drag).</summary>
        private bool PointerAlive(Element el)
        {
            if (el == null || BoxOf(el) == null || Disabled(el)) { return false; }
            var top = Controls.TopModal;
            return top == null || Within(el, top);
        }

        private void RouterError(string name, Exception ex)
        {
            if (name.StartsWith("canvas#", StringComparison.Ordinal))
            {
                string id = name.Substring(7);
                CanvasInput.Dropped(id);
                UiPlugin.Log($"{Name}: canvas '{id}' mouse handler failed and was removed: {ex}");
            }
            else { UiPlugin.Log($"{Name}: mouse handler '{name}' failed and was removed: {ex}"); }
        }

        /// <summary>What the drag machine asks of the window: the ghost is the source's numeric translate; the element under the pointer is found with the source put out of
        /// the way for the one hit test (nothing is painted in between), so the target is the real element under the ghost.</summary>
        private sealed class DragHost : IDragHost
        {
            private readonly UiWindow w;
            private readonly List<Element> one = new(1);
            public DragHost(UiWindow window) { w = window; }

            public bool Alive(Element e) => w.Mine(e) && w.PointerAlive(e);

            public Element Under(Element source, float x, float y, float dx, float dy)
            {
                // the ghost's translate is applied before the scale of the blocks around the source: the pointer's travel is divided by it, and the source's own CSS translate stays under it
                float scale = source.Parent != null && w.PlaceOf(source.Parent, out var place) ? place.M.S : 1;
                var (bx, by) = source.Style != null ? source.Style.GetPair("translate") : (0f, 0f);
                var (gx, gy) = DragDrop.GhostShift(dx, dy, scale, bx, by);
                w.SetTranslate(source, 100000, 100000);
                Element under;
                try { under = w.ElementAt(new Point((int)Math.Floor(x), (int)Math.Floor(y))); }
                finally { w.SetTranslate(source, gx, gy); }
                return under;
            }

            public void Unghost(Element source) => w.ClearTranslate(source);

            public void SetState(Element e, NodeState bit, bool on)
            {
                if (((e.State & bit) != 0) == on) { return; }
                if (on) { e.State |= bit; } else { e.State &= ~bit; }
                one.Clear(); one.Add(e);
                w.RestyleState(one, bit);
            }
        }

        /// <summary>Content boxes for the router: a painted canvas as UiCanvasInput recorded it; any other element in the coordinates it is painted in (translate, FLIP and
        /// scroll above applied, like ScreenRect) with the matrix to the screen.</summary>
        private sealed class PointerGeometry : IPointerGeometry
        {
            private readonly UiWindow w;
            public PointerGeometry(UiWindow window) { w = window; }

            public bool Content(Element e, out float x, out float y, out float width, out float height, out UiTransform toScreen)
            {
                x = y = width = height = 0; toScreen = UiTransform.Identity;
                if (e == null) { return false; }
                if (w.CanvasInput.TryPainted(e, out var r, out var xf)) { x = r.X; y = r.Y; width = r.Width; height = r.Height; toScreen = xf; return true; }
                var b = w.BoxOf(e);
                if (b == null) { return false; }
                if (!w.PlaceOf(e, out var p)) { return false; }
                var c = Painter.SnapRect(b.ContentX + p.Dx, b.ContentY + p.Dy, b.ContentWidth, b.ContentHeight);
                x = c.X; y = c.Y; width = c.Width; height = c.Height;
                toScreen = p.M;
                return true;
            }
        }
        internal static bool IsWithin(Element e, Element ancestor) => Within(e, ancestor);

        private static bool Disabled(Element e)
        {
            for (; e != null; e = e.Parent) { if ((e.State & NodeState.Disabled) != 0) { return true; } }
            return false;
        }

        private static bool Within(Element e, Element ancestor)
        {
            for (; e != null; e = e.Parent) { if (e == ancestor) { return true; } }
            return false;
        }

        // ---------------------------------------------------------------- hit test, host

        /// <summary>UiMouseEvent.Under: the element under a screen point with ex (the capture holder or the dragged ghost) and everything in it moved far away for
        /// the one hit test, then put back where it was (its numeric translate, or the CSS one). pointer-events:none is skipped by the hit test as usual.</summary>
        private Element UnderExcept(Element ex, float x, float y)
        {
            if (ex == null || !Mine(ex) || !PointerAlive(ex)) { return null; }
            bool had = ex.HasTranslate;
            var (ox, oy) = ex.TranslateOverride;
            SetTranslate(ex, 100000, 100000);
            try { return ElementAt(new Point((int)Math.Floor(x), (int)Math.Floor(y))); }
            finally { if (had) { SetTranslate(ex, ox, oy); } else { ClearTranslate(ex); } }
        }

        private void Hit(Point p)
        {
            if (Root == null) { hitElement = null; hitBox = null; hitRect = Rectangle.Empty; return; }
            if (p == hitPoint && hitStamp == stamp && hitPaint == paintStamp && hitOverlay == Controls.Version) { return; }
            hitPoint = p;
            hitStamp = stamp;
            hitPaint = paintStamp;
            hitOverlay = Controls.Version;
            if (Controls.HitOverlay(painter, p, out hitElement, out hitRect, out hitBox)) { return; }
            hitElement = painter.HitTest(Root, p, out hitRect, out hitBox);
        }

        private Rectangle MouseRect()
        {
            // a canvas or a data-mouse element holds the mouse (a drag): the whole screen is the window's, the game under the pointer sees neither the move nor the release
            if (Router.HoldsPointer || Drag.Active) { return new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight); }
            Hit(PlayerInput.MousePosition.ToPoint());
            return hitElement != null ? hitRect : Rectangle.Empty;
        }

        internal void Fail(Exception e)
        {
            UiPlugin.Log($"{Name}: closed after an error: {e}");
            Close();
        }

        internal void Dispose() => painter.Dispose();

        /// <summary>Tells a virtual data-for where its scroll area is and how tall a row is (after a layout).</summary>
        private sealed class VirtualHost : IVirtualHost
        {
            private readonly UiWindow w;
            public VirtualHost(UiWindow window) { w = window; }

            public bool Measure(Element container, Element listStart, Element row, Element nextRow,
                out float scroll, out float view, out float listTop, out float rowPitch)
            {
                scroll = view = listTop = rowPitch = 0;
                var cb = w.BoxOf(container);
                var sb = w.BoxOf(listStart);
                var rb = w.BoxOf(row);
                if (cb == null || sb == null || rb == null || rb.H <= 0) { return false; }
                scroll = w.GetScroll(container);
                view = cb.H - cb.Border.Vertical;
                listTop = sb.Y - (cb.Y + cb.Border.Top);
                var nb = w.BoxOf(nextRow);
                rowPitch = nb != null && nb.Y > rb.Y ? nb.Y - rb.Y : rb.H + rb.Margin.Vertical;
                return true;
            }
        }

        private sealed class Host : GUICustomComponent
        {
            private readonly UiWindow w;

            public Host(UiWindow window) : base(new RectTransform(new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight)))
            {
                w = window;
                CanBeFocused = true;
                // no closures per frame: try/catch inline
                OnDraw = (sb, _) =>
                {
                    if (!w.IsOpen) { return; }
                    try { w.Draw(sb); } catch (Exception e) { w.Fail(e); }
                };
                OnUpdate = (dt, _) =>
                {
                    if (!w.IsOpen) { return; }
                    try { w.Update(dt); } catch (Exception e) { w.Fail(e); }
                };
            }

            public override Rectangle MouseRect => w.IsOpen && CanBeFocused ? w.MouseRect() : Rectangle.Empty;
        }
    }
}
