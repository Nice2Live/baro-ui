using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using Barotrauma;
using BaroUI.Core;
using EventInput;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace BaroUI.Render
{
    /// <summary>Stage 5 controls (docs/ui/ENGINE_PLAN.md) of one window: content of input/select/progress/item/canvas,
    /// focus and the keyboard, the select dropdown, modal dialogs and title tooltips. Only text fields take the
    /// keyboard on a click (buttons and checkboxes do not, so the character keeps moving); Tab from a field walks the
    /// focusable elements. While focused the window is the game's keyboard subscriber; the game clears that on Esc
    /// or when another text box is clicked, and the focus follows on the next frame.</summary>
    public sealed class UiControls : IKeyboardSubscriber
    {
        private const int DropRows = 8;
        private const float TooltipDelay = 0.4f;

        private readonly UiWindow w;
        private readonly TextEdit edit = new();
        private readonly Dictionary<string, Action<SpriteBatch, Rectangle>> canvases = new(StringComparer.Ordinal);
        private readonly HashSet<string> warned = new();
        private readonly List<(Element E, LayoutBox Box)> modals = new();
        // popups and context menus: the state machine is Core/Popup.cs; this class shows, places, paints and hit tests the one open element
        private readonly PopupState popups = new();
        private LayoutBox popupBox;
        private Rectangle popupRect;
        private int popupLayout = -1;
        private bool popupFocusPending;
        private readonly List<Element> popupItems = new();
        private Element rightDown;
        private Point rightAt;

        public Element Focus { get; private set; }
        /// <summary>Bumped when the overlay changes (dropdown, modals): the window's hit test cache depends on it.</summary>
        internal int Version { get; private set; }
        public Element OpenSelect { get; private set; }
        /// <summary>Where the open dropdown was last painted (empty before its first paint).</summary>
        public Rectangle DropArea => dropRect;
        /// <summary>Open modal dialogs, topmost last.</summary>
        public IReadOnlyList<Element> Modals => modals.Select(m => m.E).ToList();
        /// <summary>How many modal dialogs are open and the topmost (null: none), without the list Modals builds: the mouse asks every frame.</summary>
        public int ModalCount => modals.Count;
        public Element TopModal => modals.Count > 0 ? modals[^1].E : null;

        // text field
        private string valueAtFocus;
        private float textScroll;
        private double blink;
        private bool dragging;
        private int cachedVersion = -1;
        private string cachedText;
        private float caretX, selX0, selX1;
        private Rectangle focusRect, openRect;
        /// <summary>The IME box last given to the game, on screen (focusRect is in the coordinates the field is painted in).</summary>
        private Rectangle focusScreen;
        // range: the slider held by the mouse and its value when it was pressed (onchange fires on release if it moved)
        private Element rangeDrag;
        private string rangeStart;
        // dropdown
        private readonly List<Element> options = new();
        private Rectangle dropRect;
        private int rowH, dropFirst, dropHover = -1;
        private bool reopenBlocked;
        // tooltip
        private string tip;
        private Element tipOwner;
        private double tipSince;

        internal UiControls(UiWindow window) { w = window; code = new UiCodeEdit(window, this); popups.Closed += OnPopupClosed; }

        /// <summary>The multi-line editors of the page (&lt;textarea&gt;).</summary>
        private readonly UiCodeEdit code;
        internal UiCodeEdit Code => code;

        public static bool HasContent(Element e) => e.Tag is "input" or "select" or "progress" or "item" or "canvas" or "slot" or "textarea" or "curve" or "chart" or "gauge";

        private UiCharts charts;
        /// <summary>The charts of the window (&lt;curve&gt;, &lt;chart&gt;, &lt;gauge&gt;), made on first use.</summary>
        internal UiCharts Charts => charts ??= new UiCharts(w);

        // game inventories shown by slot elements: the inventory and the RectTransform it had before
        private readonly Dictionary<string, (Inventory Inv, RectTransform Was)> inventories = new(StringComparer.Ordinal);

        /// <summary>Shows a game inventory in the slot elements that name it (&lt;slot inventory="name" index="i"&gt;):
        /// the game's own slots, so dragging, tooltips, stacks and the network work as in the game, as long as the
        /// game updates the inventory (its item is selected). The window owns the slot rectangles while the inventory
        /// is set; null, another inventory or closing the window gives them back to the game.</summary>
        public void SetInventory(string name, Inventory inv)
        {
            if (inventories.TryGetValue(name, out var had))
            {
                if (had.Inv == inv) { return; }
                Release(had);
                inventories.Remove(name);
            }
            if (inv == null) { return; }
            inventories[name] = (inv, inv.RectTransform);
            // a rectangle that never moves: the game rebuilds the slots only when its RectTransform moves
            inv.RectTransform = new RectTransform(new Point(1, 1), parent: null);
            inv.CreateSlots();
            foreach (var s in inv.visualSlots) { s.Disabled = true; }
        }

        public Inventory GetInventory(string name) => name != null && inventories.TryGetValue(name, out var e) ? e.Inv : null;

        private static void Release((Inventory Inv, RectTransform Was) e)
        {
            e.Inv.RectTransform = e.Was;
            e.Inv.visualSlots = null; // rebuilt by the game's next update where it had them
        }

        /// <summary>Before a paint: every shown slot is disabled; painting a visible slot enables it again, so a slot
        /// scrolled out, under a modal or not in the page takes no mouse.</summary>
        internal void BeginPaint()
        {
            foreach (var (inv, _) in inventories.Values)
            {
                if (inv.visualSlots == null) { continue; }
                foreach (var s in inv.visualSlots) { s.Disabled = true; }
            }
        }

        public static bool IsText(Element e) =>
            e is { Tag: "input" } && e.GetAttribute("type")?.ToLowerInvariant() is null or "" or "text" or "number" or "password" or "search";

        public static bool IsCheckbox(Element e) => e is { Tag: "input" } && e.GetAttribute("type")?.ToLowerInvariant() == "checkbox";

        public static bool IsRadio(Element e) => RadioGroup.Is(e);

        public static bool IsRange(Element e) => e is { Tag: "input" } && e.GetAttribute("type")?.ToLowerInvariant() == "range";

        /// <summary>Draws a canvas element (by id) inside its content box; the callback must not end the batch.</summary>
        public void SetCanvas(string id, Action<SpriteBatch, Rectangle> draw)
        {
            if (draw == null) { canvases.Remove(id); } else { canvases[id] = draw; }
        }

        // ---------------------------------------------------------------- focus

        /// <summary>Focuses a control: a text field takes the keyboard and the IME; others take the keyboard only
        /// when focused from it (Tab), so a click on a button does not stop the character.</summary>
        public void SetFocus(Element e, bool selectAll = false)
        {
            if (e == Focus) { return; }
            bool ok = e != null && !UiWindow.IsDisabled(e) && w.BoxOf(e) != null;
            // a move from one field to another keeps :focus-within where both are inside (one move of the chain, no off-and-on of a card's style
            // and its transitions in the same frame), so the old focus is blurred without moving the chain when a new one follows
            var old = BlurCore(moving: ok);
            if (!ok || !w.IsOpen) { return; }   // onblur may have closed the window: no focus, no keyboard for a closed one
            Focus = e;
            e.State |= NodeState.Focus;
            MoveFocusWithin(e, e, old);
            if (IsText(e))
            {
                edit.MaxLength = int.TryParse(e.GetAttribute("maxlength"), out int max) && max >= 0 ? max : int.MaxValue;
                edit.Accept = e.GetAttribute("type") == "number" ? NumberField.Accept : null;
                edit.SetText(e.Value);
                if (selectAll) { edit.SelectAll(); }
                valueAtFocus = e.Value;
                textScroll = 0;
                cachedVersion = -1;
                TextInput.StartTextInput();
                focusRect = ScreenContent(e);
                TextInput.SetTextInputRect(w.ToScreen(e, focusRect));
            }
            else if (UiCodeEdit.Is(e))
            {
                code.OnFocus(e);
                TextInput.StartTextInput();
                TextInput.SetTextInputRect(w.ToScreen(e, ScreenContent(e)));
            }
            GUI.KeyboardDispatcher.Subscriber = this;
            blink = Timing.TotalTime;
            w.Fire(e, "onfocus");
        }

        private readonly List<Element> focusWithin = new();

        /// <summary>Moves :focus-within (the focused element and its ancestors, the page root excluded as for :hover) to target (null: nothing is focused) and
        /// restyles every element whose :focus or :focus-within changed only as far as a rule can see those states (UiWindow.RestyleState, StyleEngine.ComputeState).
        /// flagged is the element whose Focus bit the caller just set or cleared.</summary>
        private void MoveFocusWithin(Element flagged, Element target, Element alsoFlagged = null)
        {
            var next = new List<Element>();
            for (var x = target; x != null && x != w.Document.Root; x = x.Parent) { next.Add(x); }
            var within = new HashSet<Element>();
            foreach (var x in focusWithin) { if (!next.Contains(x)) { x.State &= ~NodeState.FocusWithin; within.Add(x); } }
            foreach (var x in next) { if ((x.State & NodeState.FocusWithin) == 0) { x.State |= NodeState.FocusWithin; within.Add(x); } }
            focusWithin.Clear();
            focusWithin.AddRange(next);
            // each element is restyled for the bits that changed on it: an ancestor that only gained :focus-within is not asked about :focus rules
            var focused = new List<Element>(2);
            if (flagged != null) { focused.Add(flagged); within.Remove(flagged); }
            if (alsoFlagged != null && alsoFlagged != flagged) { focused.Add(alsoFlagged); within.Remove(alsoFlagged); }
            if (within.Count > 0) { w.RestyleState(within, NodeState.FocusWithin); }
            if (focused.Count > 0) { w.RestyleState(focused, NodeState.Focus | NodeState.FocusWithin); }
        }

        public void Blur() => BlurCore(moving: false);

        /// <summary>Takes the focus away and returns the element that had it (null: none). moving: another element takes the focus right after, which
        /// moves :focus-within itself (MoveFocusWithin with this element as alsoFlagged). The Focus bit, the chain and the keyboard are let go even when
        /// committing the value throws, so a failing handler cannot leave a stuck :focus.</summary>
        private Element BlurCore(bool moving)
        {
            CloseDropdown();
            if (Focus == null) { return null; }
            var e = Focus;
            Focus = null;
            dragging = false;
            try
            {
                if (IsText(e))
                {
                    Commit(e);
                    TextInput.StopTextInput();
                }
                else if (UiCodeEdit.Is(e))
                {
                    code.OnBlur(e);
                    TextInput.StopTextInput();
                }
            }
            finally
            {
                e.State &= ~NodeState.Focus;
                if (!moving) { MoveFocusWithin(e, null); }
                if (GUI.KeyboardDispatcher.Subscriber == this) { GUI.KeyboardDispatcher.Subscriber = null; }
            }
            w.Fire(e, "onblur");
            return e;
        }

        bool IKeyboardSubscriber.Selected
        {
            get => Focus != null;
            set { if (!value) { Blur(); } }
        }

        /// <summary>Focusable now: enabled, laid out, not inside a hidden part, inside the top modal when one is open.</summary>
        private bool Focusable(Element e)
        {
            bool kind = Natural(e) || FocusOrder.TabIndex(e) != null || e.GetAttribute("data-item") != null;
            if (!kind || UiWindow.IsDisabled(e) || w.BoxOf(e) == null || e.Style?.Get("visibility") != "visible") { return false; }
            return modals.Count == 0 || UiWindow.IsWithin(e, modals[^1].E) || (popups.Open != null && UiWindow.IsWithin(e, popups.Open));
        }

        /// <summary>A control that takes the keyboard from Tab without a tabindex.</summary>
        private static bool Natural(Element e) =>
            IsText(e) || UiCodeEdit.Is(e) || IsCheckbox(e) || IsRadio(e) || IsRange(e) || e.Tag is "select" or "button" or "curve" || (e.Tag == "a" && e.GetAttribute("onclick") != null);

        private readonly List<Element> tabOrder = new();

        /// <summary>Tab and Shift+Tab: positive tabindex first, then the rest in document order (FocusOrder); the list is read afresh per press (a tabindex can change
        /// without a layout; a press is rare). While a popup is open Tab cycles among the stops inside it (like a menu) and never leaves it: Esc or a choice does.</summary>
        private void FocusNext(int dir)
        {
            var pop = popups.Open;
            if (pop == null) { FocusOrder.Collect(w.Document.Root, Natural, tabOrder); }
            else { FocusOrder.Collect(w.Document.Root, e => Natural(e) || (e.GetAttribute("data-item") != null && UiWindow.IsWithin(e, pop)), tabOrder); }
            var next = FocusOrder.Step(tabOrder, Focus, dir,
                e => Focusable(e) && (pop == null || UiWindow.IsWithin(e, pop)) && (!IsRadio(e) || RadioGroup.IsTabStop(w.Document.Root, e, Usable)));
            if (next == null) { if (pop == null) { Blur(); } return; }
            SetFocus(next, selectAll: true);
        }

        private bool Usable(Element e) => !UiWindow.IsDisabled(e) && w.BoxOf(e) != null;

        /// <summary>Delivers a key to the data-keys handlers of the focused element and its ancestors. True: taken, the default action is off.</summary>
        private bool Key(UiKey key) =>
            Focus != null && !EditsText(Focus, key) && w.KeyRoute.Dispatch(Focus, key, PlayerInput.IsCtrlDown(), PlayerInput.IsShiftDown(), PlayerInput.IsAltDown());

        /// <summary>The focused text field or code editor uses this key to edit: it does not go to a data-keys around it (KeyRouter.EditsText).</summary>
        private static bool EditsText(Element e, UiKey key)
        {
            bool code = UiCodeEdit.Is(e);
            return (code || IsText(e)) && KeyRouter.EditsText(key, code, e.GetAttribute("type") == "number");
        }

        /// <summary>Esc from the game (UiWindow.OnEscape) for data-keys. True: a handler took it.</summary>
        internal bool KeyEscape() => Key(UiKey.Escape);

        /// <summary>Checks a radio and unchecks the rest of its group; onchange fires on the newly checked one. The model gets its value through any radio of the group with data-model.</summary>
        private void CheckRadio(Element e)
        {
            var changed = new List<Element>();
            if (!RadioGroup.Check(w.Document.Root, e, changed)) { return; }
            foreach (var x in changed) { w.RestyleElement(x); }
            var members = new List<Element>();
            RadioGroup.Members(w.Document.Root, e, members);
            var owner = e.GetAttribute("data-model") != null ? e : members.FirstOrDefault(m => m.GetAttribute("data-model") != null);
            if (owner != null)
            {
                if (!w.Template.WriteModel(owner, RadioGroup.ValueOf(e))) { return; }   // the model did not take it (warned): no onchange for a change that did not happen
                w.Refresh();
            }
            w.Fire(e, "onchange");
        }

        // ---------------------------------------------------------------- frame and mouse (called by the window)

        internal void Frame()
        {
            if (Focus != null)
            {
                // the game took the keyboard (Esc, another text box) or the element went away
                if (GUI.KeyboardDispatcher.Subscriber != this || w.BoxOf(Focus) == null) { Blur(); }
                else
                {
                    Character.DisableControls = true;
                    // the page's data changed the field under the caret (a sent chat line cleared it): the field shows it, typing goes on from there
                    if (IsText(Focus) && Focus.GetAttribute("type") != "number" && Focus.Value != edit.Text)
                    {
                        edit.SetText(Focus.Value);
                        valueAtFocus = Focus.Value;
                        cachedVersion = -1;
                    }
                }
            }
            if (OpenSelect != null && w.BoxOf(OpenSelect) == null) { CloseDropdown(); }
            // the popup's element went away (a data-if) once a layout has run since it opened
            if (popups.Open != null && w.LayoutCount > popupLayout && w.BoxOf(popups.Open) == null) { popups.Close(PopupCloseReason.Code); }
            if (dragging && !PlayerInput.PrimaryMouseButtonHeld()) { dragging = false; }
            if (dragging && Focus != null) { edit.MoveTo(IndexAt(Focus, PlayerInput.MousePosition.X), true); }
            if (rangeDrag != null) { FrameRange(); }
            code.Frame();
            if (PlayerInput.IsAltDown()) { AccessKeys(); }
        }

        internal void Hover(Element target, Point p)
        {
            dropHover = OpenSelect != null && dropRect.Contains(p) ? dropFirst + (p.Y - dropRect.Y) / Math.Max(1, rowH) : -1;
            Element owner = null;
            string text = null;
            if (OpenSelect == null)
            {
                for (var e = target; e != null; e = e.Parent)
                {
                    if (e.GetAttribute("title") is { Length: > 0 } t) { owner = e; text = t; break; }
                }
            }
            if (owner != tipOwner || text != tip) { tipOwner = owner; tip = text; tipSince = Timing.TotalTime; }
        }

        /// <summary>The primary button went down. target: the element under the mouse, null when the mouse is not on
        /// the window (a click into the game drops the focus).</summary>
        internal void MouseDown(Element target, Point p)
        {
            reopenBlocked = false;
            if (OpenSelect != null)
            {
                if (dropRect.Contains(p)) { return; }   // chosen on release
                reopenBlocked = UiWindow.IsWithin(target, OpenSelect);
                CloseDropdown();
            }
            // <dialog closedby="any">: a press on the backdrop closes it (a filter panel, a popup), as in HTML
            if (modals.Count > 0 && modals[^1].E.GetAttribute("closedby") == "any"
                && !new Rectangle((int)modals[^1].Box.X, (int)modals[^1].Box.Y, (int)Math.Ceiling(modals[^1].Box.W), (int)Math.Ceiling(modals[^1].Box.H)).Contains(p))
            {
                CloseDialog(modals[^1].E);
                return;
            }
            // a slider: the thumb jumps under the mouse and follows it until the button goes up; the keyboard stays with the game
            if (target != null && IsRange(target) && !UiWindow.IsDisabled(target))
            {
                if (FocusOrder.TabIndex(target) != null && Focusable(target)) { SetFocus(target); }
                else if (Focus != null && Focus != target) { Blur(); }
                rangeDrag = target;
                rangeStart = NumberField.Format(RangeField.Value(target));
                MoveRange(target);
                return;
            }
            // a multi-line editor: focus, caret, selection, its bars
            if (target != null && code.MouseDown(target, p)) { return; }
            // a click on a file target gives it the focus, so Ctrl+V reaches it
            if (target != null && w.FileTargets.TargetOf(target) is { } fileTarget && !UiWindow.IsDisabled(fileTarget) && !IsText(target)) { SetFocus(fileTarget); }
            var field = target;
            while (field != null && !IsText(field)) { field = field.Parent; }
            if (field != null && !UiWindow.IsDisabled(field))
            {
                bool had = Focus == field;
                SetFocus(field);
                edit.MoveTo(IndexAt(field, p.X), had && PlayerInput.IsShiftDown());
                blink = Timing.TotalTime;
                dragging = true;
                return;
            }
            // an explicit tabindex takes the keyboard on a press (a card, a list, a button that opted in); controls without one do not, so the character keeps moving
            var stop = target;
            while (stop != null && FocusOrder.TabIndex(stop) == null) { stop = stop.Parent; }
            if (stop != null && Focusable(stop)) { SetFocus(stop); return; }
            if (Focus != null && !(target != null && UiWindow.IsWithin(target, Focus))) { Blur(); }
        }

        /// <summary>A click (press and release on the same element). True: handled here, no onclick.</summary>
        internal bool Click(Element e, Point p)
        {
            if (OpenSelect != null && dropRect.Contains(p))
            {
                var select = OpenSelect;
                int row = dropFirst + (p.Y - dropRect.Y) / Math.Max(1, rowH), count = options.Count;
                CloseDropdown();   // clears options
                if (row >= 0 && row < count) { Choose(select, row); }
                return true;
            }
            if (IsCheckbox(e)) { Toggle(e); return false; }
            if (IsRadio(e)) { CheckRadio(e); return false; }
            if (e.Tag == "label") { return ClickLabel(e); }
            var sel = e;
            while (sel != null && sel.Tag != "select") { sel = sel.Parent; }
            if (sel != null)
            {
                if (!reopenBlocked) { OpenDropdown(sel); }
                reopenBlocked = false;
                return false;
            }
            return false;
        }

        private bool ClickLabel(Element label)
        {
            var target = label.GetAttribute("for") is { } id ? w.Find(id) : label.Descendants().FirstOrDefault(d => d.Tag is "input" or "select");
            if (target == null || UiWindow.IsDisabled(target) || target == label) { return false; }
            if (IsCheckbox(target)) { Toggle(target); }
            else if (IsRadio(target)) { CheckRadio(target); }
            else if (IsText(target)) { SetFocus(target, selectAll: true); }
            else if (target.Tag == "select") { OpenDropdown(target); }
            return false;
        }

        // ---------------------------------------------------------------- popups and context menus

        /// <summary>The open popup (null: none).</summary>
        public Element Popup => popups.Open;
        /// <summary>The element a popup was opened for (the card of a context menu, the anchor of a menu button); null: a bare point.</summary>
        public Element PopupAnchor => popups.Anchor;

        /// <summary>Raised after a popup closed and was hidden: the element, why.</summary>
        public event Action<Element, PopupCloseReason> PopupClosed;

        public bool IsPopupOpen(Element popup) => popups.IsOpen(popup);

        public void ShowPopup(Element popup, PopupRequest request)
        {
            if (popup == null || popup.Tag == "dialog" || popup.GetAttribute("data-popup") == null) { return; }
            if (!popups.Show(popup, request)) { Place(); Version++; return; }   // shown again: it only moves
            Blur();
            popup.SetAttribute("open", "");
            w.RestyleElement(popup);
            w.Relayout(popup.Parent);
            popupLayout = w.LayoutCount;
            popupFocusPending = true;
            Version++;
        }

        public bool ClosePopup(Element popup = null, PopupCloseReason reason = PopupCloseReason.Code) =>
            (popup == null || popups.IsOpen(popup)) && popups.Close(reason);

        private void OnPopupClosed(Element popup, PopupCloseReason reason)
        {
            popupBox = null; popupRect = Rectangle.Empty; popupFocusPending = false;
            if (Focus != null && UiWindow.IsWithin(Focus, popup)) { Blur(); }
            popup.RemoveAttribute("open");
            w.RestyleElement(popup);
            w.Relayout(popup.Parent);
            Version++;
            // a failing onclose must not keep the subscribers from hearing that the popup closed
            try { w.Fire(popup, "onclose"); }
            finally { PopupClosed?.Invoke(popup, reason); }
        }

        /// <summary>Esc: closes the popup before anything else but a pointer capture. True: one was open.</summary>
        internal bool ClosePopupEscape() => popups.Escape();

        /// <summary>A button went down. True: the press is swallowed (a left press outside an open popup closed it).</summary>
        internal bool OnPopupPress(Element target, bool leftOrMiddle) => popups.Press(target, leftOrMiddle) == Core.PopupPress.Swallowed;

        /// <summary>How many times a popup opened afresh so far (read before an onclick runs and given back to AfterClick).</summary>
        internal int PopupSerial => popups.Serial;

        /// <summary>An onclick ran (mouse or Enter): a choice inside the popup closes it unless data-keep-open, or unless the handler closed it and opened it again (serial).</summary>
        internal void AfterClick(Element target, Element popupBefore, int serial = -1) => popups.Clicked(target, popupBefore, serial);

        /// <summary>The right button, once a frame, after the mouse router ran: a right press and release on an element inside data-contextmenu="id" opens that
        /// popup at the pointer, unless a SetMouse handler answered true for the ContextMenu event (handled).</summary>
        internal void ContextFrame(bool mouseOn, Element target, Point p, bool handled)
        {
            if (PlayerInput.SecondaryMouseButtonDown()) { rightDown = mouseOn ? target : null; rightAt = p; return; }
            if (rightDown == null || PlayerInput.SecondaryMouseButtonHeld()) { return; }
            var down = rightDown;
            rightDown = null;
            float dx = p.X - rightAt.X, dy = p.Y - rightAt.Y;
            var carrier = PopupRules.ContextCarrier(down, mouseOn ? target : null, (float)Math.Sqrt(dx * dx + dy * dy), handled);
            if (carrier == null || UiWindow.IsDisabled(carrier)) { return; }
            w.ShowPopup(carrier.GetAttribute("data-contextmenu"), p.X, p.Y, carrier);
        }

        /// <summary>Puts the open popup (just laid out) where its request says (Core/Popup.cs), by shifting its box.</summary>
        private void Place()
        {
            var pop = popups.Open;
            if (pop == null || w.BoxOf(pop) is not { } box) { popupBox = null; popupRect = Rectangle.Empty; return; }
            var req = popups.Request;
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            var anchorRect = req?.Placement != null && req.Anchor != null ? w.ScreenRect(req.Anchor) : Rectangle.Empty;
            var at = anchorRect.Width > 0 && anchorRect.Height > 0
                ? PopupPlace.Beside(new PopupRect(anchorRect.X, anchorRect.Y, anchorRect.Width, anchorRect.Height), box.W, box.H, sw, sh, req.Placement)
                : PopupPlace.AtPoint(req?.X ?? 0, req?.Y ?? 0, box.W, box.H, sw, sh);
            UiWindow.ShiftBox(box, (float)Math.Round(at.X - box.X), (float)Math.Round(at.Y - box.Y));
            popupBox = box;
            popupRect = new Rectangle((int)box.X, (int)box.Y, (int)Math.Ceiling(box.W), (int)Math.Ceiling(box.H));
        }

        /// <summary>Up, Down, Home and End inside the popup move the focus among its items. True: used.</summary>
        private bool PopupKey(Element focused, Keys key)
        {
            if (!(key is Keys.Up or Keys.Down or Keys.Home or Keys.End)) { return false; }
            // a field, a list, a slider or a radio inside the menu keeps its own arrow keys (the menu's items are buttons and data-item blocks)
            if (OpenSelect != null || IsText(focused) || UiCodeEdit.Is(focused) || focused.Tag == "select" || IsRange(focused) || IsRadio(focused)) { return false; }
            PopupRules.Items(popups.Open, Usable, popupItems);
            var next = PopupRules.Move(popupItems, focused, key == Keys.Up ? UiKey.ArrowUp : key == Keys.Down ? UiKey.ArrowDown : key == Keys.Home ? UiKey.Home : UiKey.End);
            if (next != null) { SetFocus(next); }
            return true;
        }

        /// <summary>The wheel over the dropdown scrolls it. True: used.</summary>
        internal bool Wheel(Point p, float notches)
        {
            if (OpenSelect == null && code.Wheel(p, notches)) { return true; }
            if (OpenSelect == null && WheelNumber(p, notches)) { return true; }
            if (OpenSelect == null || !dropRect.Contains(p)) { return false; }
            dropFirst = Math.Clamp(dropFirst - Math.Sign(notches), 0, Math.Max(0, options.Count - DropRows));
            return true;
        }

        /// <summary>The wheel over a number field (the one under the pointer, focused or not) steps it, up is up, once per notch. Focused: as an arrow key
        /// (oninput now, onchange when the field is left). Not focused: the value changes at once and oninput and onchange run. At a limit nothing changes
        /// and the wheel is not used, so the page under it scrolls. True: used.</summary>
        private bool WheelNumber(Point p, float notches)
        {
            var e = w.ElementAt(p);
            if (e is not { Tag: "input" } || e.GetAttribute("type") != "number" || UiWindow.IsDisabled(e)) { return false; }
            int dir = Math.Sign(notches);
            if (dir == 0) { return false; }
            double? min = NumberField.Attr(e, "min"), max = NumberField.Attr(e, "max"), step = NumberField.Attr(e, "step");
            if (Focus == e)
            {
                if (NumberField.Stepped(edit.Text, dir, min, max, step) == edit.Text) { return false; }
                StepNumber(e, dir);
                return true;
            }
            string current = e.GetAttribute("value") ?? "";
            string next = NumberField.Stepped(current, dir, min, max, step);
            if (next == current) { return false; }
            e.SetAttribute("value", next);
            if (e.GetAttribute("data-model") != null && w.Template.WriteModel(e, NumberField.TryParse(next, out double v) ? v : null)) { w.Refresh(); }
            w.Fire(e, "oninput");
            w.Fire(e, "onchange");
            return true;
        }

        /// <summary>Overlay first: the dropdown, then the top modal dialog (outside it, a backdrop that keeps the
        /// click from the game and from the page under it).</summary>
        internal bool HitOverlay(Painter painter, Point p, out Element e, out Rectangle rect, out LayoutBox box)
        {
            e = null; rect = Rectangle.Empty; box = null;
            if (OpenSelect != null && dropRect.Contains(p)) { e = OpenSelect; rect = dropRect; box = w.BoxOf(OpenSelect); return true; }
            if (popupBox != null && popupRect.Contains(p) && painter.HitTest(popupBox, p, out rect, out box) is { } inPopup) { e = inPopup; return true; }
            if (modals.Count == 0) { return false; }
            var top = modals[^1];
            e = painter.HitTest(top.Box, p, out rect, out box);
            if (e == null) { e = top.E; box = top.Box; rect = new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight); }
            return true;
        }

        /// <summary>After a layout: open dialogs become modal, painted last; one placed with top/left auto is
        /// centred on the canvas. Removed elements lose the focus and the dropdown.</summary>
        private readonly List<Element> openDialogs = new();

        /// <summary>Open dialogs in document order, without an iterator per level (runs after every layout).</summary>
        private void FindOpenDialogs(Element e)
        {
            var kids = e.Children;
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i] is not Element c) { continue; }
                if (c.Tag == "dialog" && c.GetAttribute("open") != null) { openDialogs.Add(c); }
                FindOpenDialogs(c);
            }
        }

        internal void AfterLayout(Painter painter, float cx, float cy, float cw, float ch)
        {
            modals.Clear();
            painter.Deferred.Clear();
            // rows of a long list come and go: drop the item lookups now and then, the visible ones refill
            if (itemPrefabs.Count > 1024) { itemPrefabs.Clear(); }
            openDialogs.Clear();
            FindOpenDialogs(w.Document.Root);
            foreach (var d in openDialogs)
            {
                if (w.BoxOf(d) is not { } box) { continue; }
                if (d.Style.Get("top") == "auto" && d.Style.Get("bottom") == "auto" && d.Style.Get("left") == "auto" && d.Style.Get("right") == "auto")
                {
                    UiWindow.ShiftBox(box, (float)Math.Round(cx + (cw - box.W) / 2 - box.X), (float)Math.Round(cy + (ch - box.H) / 2 - box.Y));
                }
                modals.Add((d, box));
                painter.Deferred.Add(d);
                WarnScaledDialog(d);
            }
            popupBox = null; popupRect = Rectangle.Empty;
            if (popups.Open != null)
            {
                Place();
                if (popupBox != null) { painter.Deferred.Add(popups.Open); }
            }
            Version++;
            if (Focus != null && w.BoxOf(Focus) == null) { Blur(); }
            if (popupFocusPending && popupBox != null)
            {
                popupFocusPending = false;
                PopupRules.Items(popups.Open, Usable, popupItems);
                if (popupItems.Count > 0) { SetFocus(popupItems[0]); }
            }
            if (modals.Count > 0 && (Focus == null || !UiWindow.IsWithin(Focus, modals[^1].E))
                && !(Focus != null && popups.Open != null && UiWindow.IsWithin(Focus, popups.Open))   // a menu opened over the dialog keeps the focus it took
                && modals[^1].E.Descendants().FirstOrDefault(x => x.GetAttribute("autofocus") != null && Focusable(x)) is { } auto)
            {
                SetFocus(auto, selectAll: true);
            }
        }

        /// <summary>A modal dialog is painted over the screen from a zero offset, unscaled, whatever its ancestors do: one log line per source line if one of them has a scale.</summary>
        private void WarnScaledDialog(Element d)
        {
            for (var a = d.Parent; a != null; a = a.Parent)
            {
                if (a.Style == null || UiTransform.ScaleOf(a, a.Style) == 1) { continue; }
                if (warned.Add("scale:dialog" + d.Line)) { UiPlugin.Log($"{w.Name}: <dialog> line {d.Line} is inside a block with scale: it opens over the screen at normal size, the scale of its ancestors does not apply to it"); }
                return;
            }
        }

        internal bool CloseDropdownIfOpen()
        {
            if (OpenSelect == null) { return false; }
            CloseDropdown();
            return true;
        }

        /// <summary>Esc with no focus: closes the top modal dialog. True: one was closed.</summary>
        internal bool CloseTopModal()
        {
            if (modals.Count == 0) { return false; }
            CloseDialog(modals[^1].E);
            return true;
        }

        public void ShowModal(Element dialog)
        {
            if (dialog == null || dialog.Tag != "dialog" || dialog.GetAttribute("open") != null) { return; }
            popups.Close(PopupCloseReason.Code);   // a popup belongs to the page it was opened on
            Blur();
            dialog.SetAttribute("open", "");
            w.RestyleElement(dialog);
            w.Relayout(dialog.Parent);
        }

        public void CloseDialog(Element dialog)
        {
            if (dialog == null || dialog.GetAttribute("open") == null) { return; }
            if (Focus != null && UiWindow.IsWithin(Focus, dialog)) { Blur(); }
            dialog.RemoveAttribute("open");
            w.RestyleElement(dialog);
            w.Relayout(dialog.Parent);
            w.Fire(dialog, "onclose");
        }

        internal void Dispose()
        {
            rangeDrag = null;
            popups.Closed -= OnPopupClosed;
            Blur();
            code.Dispose();
            canvases.Clear();
            foreach (var e in inventories.Values) { Release(e); }
            inventories.Clear();
        }

        // ---------------------------------------------------------------- values

        private void Toggle(Element e)
        {
            if (e.Checked) { e.RemoveAttribute("checked"); } else { e.SetAttribute("checked", ""); }
            w.RestyleElement(e);
            if (e.GetAttribute("data-model") != null && w.Template.WriteModel(e, e.Checked)) { w.Refresh(); }
            w.Fire(e, "onchange");
        }

        private void Edited()
        {
            blink = Timing.TotalTime;
            var e = Focus;
            e.SetAttribute("value", edit.Text);
            if (e.GetAttribute("type") != "number" && e.GetAttribute("data-model") != null && w.Template.WriteModel(e, edit.Text)) { w.Refresh(); }
            w.Fire(e, "oninput");
        }

        /// <summary>Leaving a field or Enter: a number is parsed, snapped and clamped (an unreadable one goes back to
        /// the value it had), then the model gets it and onchange runs if it changed.</summary>
        private void Commit(Element e)
        {
            string text = e.GetAttribute("value") ?? "";
            object model = text;
            if (e.GetAttribute("type") == "number")
            {
                if (text.Trim().Length == 0) { model = null; }
                else if (NumberField.TryParse(text, out double v))
                {
                    v = NumberField.Clamp(v, NumberField.Attr(e, "min"), NumberField.Attr(e, "max"), NumberField.Attr(e, "step"));
                    text = NumberField.Format(v);
                    model = v;
                }
                else { text = valueAtFocus ?? ""; model = NumberField.TryParse(text, out double old) ? old : null; }
                e.SetAttribute("value", text);
                if (Focus == e) { edit.SetText(text); }
                if (e.GetAttribute("data-model") != null && w.Template.WriteModel(e, model)) { w.Refresh(); }
            }
            if (text != valueAtFocus) { valueAtFocus = text; w.Fire(e, "onchange"); }
        }

        private void StepNumber(Element e, int dir)
        {
            edit.SetText(NumberField.Stepped(edit.Text, dir, NumberField.Attr(e, "min"), NumberField.Attr(e, "max"), NumberField.Attr(e, "step")));
            edit.SelectAll();
            Edited();
        }

        // ---------------------------------------------------------------- range

        /// <summary>The thumb is a circle as high as 4/5 of the content box; its centre runs from half a thumb in to half a thumb short of the end.</summary>
        private static int ThumbSize(Rectangle cr) => Math.Max(6, (int)Math.Round(cr.Height * 0.8f));

        /// <summary>The value under the mouse of a held slider (a click on the track too), through the model and oninput.</summary>
        private void MoveRange(Element e)
        {
            var cr = ScreenContent(e);
            if (cr.Width <= 0) { return; }
            int d = ThumbSize(cr);
            // cr is in the slider's layout space; inside a scaled block the mouse has to be taken there
            double f = cr.Width > d ? (w.ToLocal(e, PlayerInput.MousePosition).X - (cr.X + d / 2f)) / (cr.Width - d) : 0;
            SetRange(e, RangeField.FromFraction(f, RangeField.Min(e), RangeField.Max(e), RangeField.Step(e)));
        }

        /// <summary>Writes a slider's value: the attribute, the model, oninput. No change, no call.</summary>
        private void SetRange(Element e, double v)
        {
            if (e.GetAttribute("value") != null && RangeField.Value(e) == v) { return; }
            e.SetAttribute("value", NumberField.Format(v));
            if (e.GetAttribute("data-model") != null && w.Template.WriteModel(e, v)) { w.Refresh(); }
            w.Fire(e, "oninput");
        }

        private void FrameRange()
        {
            var e = rangeDrag;
            if (w.BoxOf(e) == null || UiWindow.IsDisabled(e)) { rangeDrag = null; return; }
            if (PlayerInput.PrimaryMouseButtonHeld()) { MoveRange(e); return; }
            rangeDrag = null;
            if (NumberField.Format(RangeField.Value(e)) != rangeStart) { w.Fire(e, "onchange"); }
        }

        /// <summary>Arrows move one step, PageUp/PageDown ten, Home/End go to the ends; every press is a change.</summary>
        private void KeyRange(Element e, Keys key)
        {
            double min = RangeField.Min(e), max = RangeField.Max(e), cur = RangeField.Value(e), unit = RangeField.Unit(e);
            double v = key switch
            {
                Keys.Left or Keys.Down => cur - unit,
                Keys.Right or Keys.Up => cur + unit,
                Keys.PageDown => cur - 10 * unit,
                Keys.PageUp => cur + 10 * unit,
                Keys.Home => min,
                Keys.End => max,
                _ => double.NaN,
            };
            if (double.IsNaN(v)) { return; }
            v = RangeField.Snap(v, min, max, RangeField.Step(e));
            if (v == cur) { return; }
            SetRange(e, v);
            w.Fire(e, "onchange");
        }

        private static List<Element> OptionsOf(Element select) => select.Descendants().Where(o => o.Tag == "option").ToList();

        private static string OptionValue(Element o) => o.GetAttribute("value") ?? o.TextContent.Trim();

        public static int SelectedIndex(Element select, List<Element> opts)
        {
            string v = select.GetAttribute("value");
            if (v != null) { int i = opts.FindIndex(o => OptionValue(o) == v); if (i >= 0) { return i; } }
            int s = opts.FindIndex(o => o.GetAttribute("selected") != null);
            return s >= 0 ? s : opts.Count > 0 ? 0 : -1;
        }

        private void Choose(Element select, int index)
        {
            var opts = OptionsOf(select);
            if (index < 0 || index >= opts.Count || UiWindow.IsDisabled(opts[index])) { return; }
            string v = OptionValue(opts[index]);
            if (select.GetAttribute("value") == v && SelectedIndex(select, opts) == index) { return; }
            select.SetAttribute("value", v);
            if (select.GetAttribute("data-model") != null && w.Template.WriteModel(select, v)) { w.Refresh(); }
            w.Fire(select, "onchange");
        }

        private void OpenDropdown(Element select)
        {
            if (UiWindow.IsDisabled(select)) { return; }
            options.Clear();
            options.AddRange(OptionsOf(select));
            if (options.Count == 0) { return; }
            OpenSelect = select;
            int sel = SelectedIndex(select, options);
            dropFirst = Math.Clamp(sel - DropRows / 2, 0, Math.Max(0, options.Count - DropRows));
            dropHover = sel;
            dropRect = openRect = Rectangle.Empty;
            Version++;
        }

        private void CloseDropdown()
        {
            OpenSelect = null;
            options.Clear();
            dropRect = Rectangle.Empty;
            dropHover = -1;
            Version++;
        }

        // ---------------------------------------------------------------- keyboard

        public void ReceiveTextInput(char c)
        {
            try { TextCore(c); } catch (Exception ex) { w.Fail(ex); }
        }

        private void TextCore(char c)
        {
            if (Focus != null && UiCodeEdit.Is(Focus)) { code.TextInput(Focus, c); return; }
            if (Focus != null && IsText(Focus) && edit.Insert(c.ToString())) { Edited(); }
        }

        public void ReceiveTextInput(string text)
        {
            try { TextCore(text); } catch (Exception ex) { w.Fail(ex); }
        }

        private void TextCore(string text)
        {
            if (Focus != null && UiCodeEdit.Is(Focus)) { code.TextInput(Focus, text); return; }
            if (Focus != null && IsText(Focus) && edit.Insert(text)) { Edited(); }
        }

        public void ReceiveEditingInput(string text, int start, int length) { }

        /// <summary>The keyboard comes from the game, not from the window's update: a handler that throws here is dealt with as in the mouse path (logged, the window closes).</summary>
        public void ReceiveCommandInput(char command)
        {
            try { CommandCore(command); } catch (Exception ex) { w.Fail(ex); }
        }

        private void CommandCore(char command)
        {
            if (command == '\b' && Key(UiKey.Backspace)) { return; }
            if (Focus != null && UiCodeEdit.Is(Focus)) { if (!PlayerInput.IsAltDown()) { code.Command(Focus, command); } return; }
            // Ctrl+V on a file target (SetFileInput): files, a picture or a path from the clipboard
            if (Focus != null && command == (char)0x16 && !IsText(Focus) && w.FileTargets.Paste(Focus)) { return; }
            if (Focus == null || !IsText(Focus) || PlayerInput.IsAltDown()) { return; }
            bool ctrl = PlayerInput.IsCtrlDown();
            switch (command)
            {
                case '\b':
                    if (edit.Backspace(ctrl)) { Edited(); }
                    break;
                case (char)0x1:
                    edit.SelectAll();
                    break;
                case (char)0x3:
                    if (edit.HasSelection && Focus.GetAttribute("type") != "password") { Clipboard.SetText(edit.Selected); }
                    break;
                case (char)0x18:
                    if (edit.HasSelection && Focus.GetAttribute("type") != "password")
                    {
                        Clipboard.SetText(edit.Selected);
                        if (edit.DeleteSelection()) { Edited(); }
                    }
                    break;
                case (char)0x16:
                    if (edit.Insert(Clipboard.GetText() ?? "")) { Edited(); }
                    break;
                case (char)0x1A:
                    if (edit.Undo()) { Edited(); }
                    break;
            }
        }

        public void ReceiveSpecialInput(Keys key)
        {
            try { SpecialCore(key); } catch (Exception ex) { w.Fail(ex); }
        }

        private void SpecialCore(Keys key)
        {
            var e = Focus;
            if (e == null) { return; }
            blink = Timing.TotalTime;
            bool shift = PlayerInput.IsShiftDown(), ctrl = PlayerInput.IsCtrlDown();
            // data-keys handlers first (Esc comes through UiWindow.OnEscape): a handler that answers true stops the default action
            if (KeyRouter.FromGameKey(key.ToString()) is { } routed && routed != UiKey.Escape && Key(routed)) { return; }
            if (!w.IsOpen || Focus != e) { return; }   // a data-keys handler closed the window or moved the focus: the key is done
            if (popups.Open != null && UiWindow.IsWithin(e, popups.Open) && PopupKey(e, key)) { return; }
            // in a code editor Tab is an indent (ctrl+Tab moves on)
            if (UiCodeEdit.Is(e) && !(key == Keys.Tab && ctrl)) { if (!PlayerInput.IsAltDown()) { code.Special(e, key, shift, ctrl); } return; }
            if (key == Keys.Tab) { FocusNext(shift ? -1 : 1); return; }
            if (e.Tag == "curve") { if (!PlayerInput.IsAltDown()) { Charts.Key(e, key); } return; }   // arrows nudge the selected point, Delete removes it
            if (OpenSelect != null)
            {
                switch (key)
                {
                    case Keys.Up: dropHover = Math.Max(0, dropHover - 1); KeepVisible(); return;
                    case Keys.Down: dropHover = Math.Min(options.Count - 1, dropHover + 1); KeepVisible(); return;
                    case Keys.Enter:
                    case Keys.Space:
                        var s = OpenSelect;
                        int row = dropHover;
                        CloseDropdown();
                        Choose(s, row);
                        return;
                }
                return;
            }
            if (IsText(e))
            {
                switch (key)
                {
                    case Keys.Left: edit.Left(ctrl, shift); break;
                    case Keys.Right: edit.Right(ctrl, shift); break;
                    case Keys.Home: edit.Home(shift); break;
                    case Keys.End: edit.End(shift); break;
                    case Keys.Delete: if (edit.Delete(ctrl)) { Edited(); } break;
                    case Keys.Up when e.GetAttribute("type") == "number": StepNumber(e, 1); break;
                    case Keys.Down when e.GetAttribute("type") == "number": StepNumber(e, -1); break;
                    case Keys.Enter:
                        Commit(e);
                        // onenter: the field handles Enter itself (a chat line); without it Enter clicks the page's data-default button
                        if (e.GetAttribute("onenter") != null) { w.Fire(e, "onenter"); } else { Default(e); }
                        break;
                }
                return;
            }
            if (e.Tag == "select")
            {
                var opts = OptionsOf(e);
                int i = SelectedIndex(e, opts);
                switch (key)
                {
                    case Keys.Up: Choose(e, Math.Max(0, i - 1)); break;
                    case Keys.Down: Choose(e, Math.Min(opts.Count - 1, i + 1)); break;
                    case Keys.Enter:
                    case Keys.Space: OpenDropdown(e); break;
                }
                return;
            }
            if (IsRange(e))
            {
                KeyRange(e, key);
                return;
            }
            if (IsRadio(e))
            {
                switch (key)
                {
                    case Keys.Enter:
                    case Keys.Space: CheckRadio(e); break;
                    case Keys.Up:
                    case Keys.Left:
                    case Keys.Down:
                    case Keys.Right:
                        var next = RadioGroup.Neighbor(w.Document.Root, e, key is Keys.Up or Keys.Left ? -1 : 1, Usable);
                        if (next != null) { SetFocus(next); CheckRadio(next); }
                        break;
                }
                return;
            }
            if (key is Keys.Enter or Keys.Space)
            {
                if (IsCheckbox(e)) { Toggle(e); } else { w.Click(e); }
            }
        }

        private void KeepVisible()
        {
            if (dropHover < dropFirst) { dropFirst = dropHover; }
            if (dropHover >= dropFirst + DropRows) { dropFirst = dropHover - DropRows + 1; }
        }

        /// <summary>Enter in a field clicks the data-default button of its dialog (or of the page).</summary>
        private void Default(Element from)
        {
            Element scope = from;
            while (scope.Parent != null && scope.Tag != "dialog") { scope = scope.Parent; }
            var button = scope.Descendants().FirstOrDefault(d => d.GetAttribute("data-default") != null && !UiWindow.IsDisabled(d) && w.BoxOf(d) != null);
            if (button != null) { w.Click(button); }
        }

        /// <summary>Alt+letter or digit: clicks (or focuses a field with) the element whose accesskey it is.</summary>
        private void AccessKeys()
        {
            foreach (var e in w.Document.Root.Descendants())
            {
                if (e.GetAttribute("accesskey") is not { Length: 1 } k || KeyOf(k[0]) is not { } key || !PlayerInput.KeyHit(key)) { continue; }
                if (!Focusable(e) && !(e.GetAttribute("onclick") != null && w.BoxOf(e) != null && !UiWindow.IsDisabled(e))) { continue; }
                if (IsText(e) || e.Tag == "select") { SetFocus(e, selectAll: true); }
                else if (IsCheckbox(e)) { Toggle(e); }
                else if (IsRadio(e)) { CheckRadio(e); }
                else { w.Click(e); }
                return;
            }
        }

        private static Keys? KeyOf(char c)
        {
            c = char.ToUpperInvariant(c);
            if (c is >= 'A' and <= 'Z') { return Keys.A + (c - 'A'); }
            if (c is >= '0' and <= '9') { return Keys.D0 + (c - '0'); }
            return null;
        }

        // ---------------------------------------------------------------- paint

        /// <summary>The content box where the box was painted (rect: its painted border box, scroll applied).</summary>
        private static Rectangle Content(LayoutBox b, Rectangle rect) =>
            Painter.SnapRect(rect.X + b.Border.Left + b.Padding.Left, rect.Y + b.Border.Top + b.Padding.Top, b.ContentWidth, b.ContentHeight);

        /// <summary>The content box outside painting, in the coordinates it is painted in: layout position moved by the translate and FLIP offset of the element and
        /// of every block around it, minus the scroll of the areas around it (ScreenMap). That is the screen except inside a scaled block (w.ToScreen / w.ToLocal
        /// go between the two).</summary>
        private Rectangle ScreenContent(Element e)
        {
            var b = w.BoxOf(e);
            if (b == null || !w.PlaceOf(e, out var place)) { return Rectangle.Empty; }
            return Painter.SnapRect(b.ContentX + place.Dx, b.ContentY + place.Dy, b.ContentWidth, b.ContentHeight);
        }

        private static Color Accent(ComputedStyle s, float opacity, float alpha = 1)
        {
            var c = s.GetColor("accent-color");
            if (c.A == 0) { c = new Rgba(0x40, 0xc8, 0xbe); }
            return Painter.Tint(c, opacity * alpha);
        }

        private string Shown(Element e) =>
            Focus == e ? (e.GetAttribute("type") == "password" ? new string('*', edit.Text.Length) : edit.Text)
                : e.GetAttribute("type") == "password" ? new string('*', e.Value.Length) : e.Value;

        private int IndexAt(Element e, float mouseX)
        {
            if (w.BoxOf(e) == null) { return 0; }
            string s = Shown(e);
            var cr = Focus == e && focusRect.Width > 0 ? focusRect : ScreenContent(e);
            float x = w.ToLocal(e, new Vector2(mouseX, 0)).X - cr.X + (Focus == e ? textScroll : 0);
            return TextEdit.IndexAt(s, x, k => w.Fonts.Width(s.Substring(0, k), e.Style));
        }

        internal void PaintContent(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            try
            {
                switch (e.Tag)
                {
                    case "input" when IsCheckbox(e):
                        if (e.Checked)
                        {
                            int inset = Math.Max(2, rect.Width / 4);
                            var inner = new Rectangle(rect.X + inset, rect.Y + inset, rect.Width - 2 * inset, rect.Height - 2 * inset);
                            p.FillRounded(inner, Painter.RadiusOf(s, rect) / 2, Accent(s, opacity));
                        }
                        break;
                    case "input" when IsRadio(e):
                        if (e.Checked)
                        {
                            int inset = Math.Max(2, rect.Width / 4);
                            var inner = new Rectangle(rect.X + inset, rect.Y + inset, rect.Width - 2 * inset, rect.Height - 2 * inset);
                            p.FillRounded(inner, inner.Width / 2, Accent(s, opacity));
                        }
                        break;
                    case "input" when IsRange(e):
                        PaintRange(p, b, s, rect, opacity);
                        break;
                    case "input" when IsText(e):
                        PaintField(p, b, s, rect, opacity);
                        break;
                    case "select":
                        PaintSelect(p, b, s, rect, opacity);
                        break;
                    case "progress":
                        PaintProgress(p, b, s, rect, opacity);
                        break;
                    case "item":
                        PaintItem(p, b, s, rect, opacity);
                        break;
                    case "canvas":
                        PaintCanvas(p, b, rect, e);
                        break;
                    case "curve":
                    case "chart":
                    case "gauge":
                        Charts.Paint(p, e, s, Content(b, rect), opacity);
                        break;
                    case "slot":
                        PaintSlot(p, b, rect, e);
                        break;
                    case "textarea":
                        code.Paint(p, b, s, rect, opacity, Content(b, rect));
                        break;
                }
            }
            catch (Exception ex)
            {
                if (warned.Add(e.Tag + e.Line)) { UiPlugin.Log($"{w.Name}: <{e.Tag}> line {e.Line}: {ex.Message}"); }
            }
        }

        private void PaintField(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            var cr = Content(b, rect);
            // the IME box goes where the field is on screen: compared on screen, so a zoom of a block around the field moves it too
            if (Focus == e)
            {
                var abs = p.Abs(cr);   // on screen, also when the paint runs in a rounded target (Painter.Abs)
                var scr = p.ToScreen(abs);
                if (abs != focusRect || scr != focusScreen) { focusRect = abs; focusScreen = scr; TextInput.SetTextInputRect(scr); }
            }
            // clip across the padding vertically: a field lower than the font's line keeps its glyphs whole
            int bt = (int)Math.Round(b.Border.Top), bb = (int)Math.Round(b.Border.Bottom);
            var clip = new Rectangle(cr.X, rect.Y + bt, cr.Width, Math.Max(cr.Height, rect.Height - bt - bb));
            if (!p.PushClip(clip, out var saved)) { return; }
            var fonts = w.Fonts;
            float lh = fonts.NormalLineHeight(s), top = cr.Y + (cr.Height - lh) / 2, baseline = top + fonts.Ascent(s);
            string shown = Shown(e);
            bool focused = Focus == e;
            if (focused)
            {
                if (cachedVersion != edit.Version || cachedText != shown)
                {
                    cachedVersion = edit.Version;
                    cachedText = shown;
                    caretX = fonts.Width(shown.Substring(0, edit.Caret), s);
                    selX0 = fonts.Width(shown.Substring(0, edit.SelStart), s);
                    selX1 = fonts.Width(shown.Substring(0, edit.SelEnd), s);
                }
                if (caretX - textScroll > cr.Width - 2) { textScroll = caretX - cr.Width + 2; }
                if (caretX - textScroll < 0) { textScroll = caretX; }
                textScroll = Math.Max(0, textScroll);
            }
            float x0 = cr.X - (focused ? textScroll : 0);
            if (focused && edit.HasSelection)
            {
                p.FillRect(Painter.SnapRect(x0 + selX0, top, selX1 - selX0, lh), Accent(s, opacity, 0.4f));
            }
            var color = s.GetColor("color");
            if (shown.Length > 0) { fonts.Draw(p.Batch, shown, x0, baseline, Painter.Tint(color, opacity), s); }
            else if (e.GetAttribute("placeholder") is { Length: > 0 } ph)
            {
                fonts.Draw(p.Batch, ph, cr.X, baseline, Painter.Tint(color, opacity * 0.45f), s);
            }
            if (focused && (Timing.TotalTime - blink) % 1.0 < 0.55)
            {
                var cc = s.Get("caret-color") is "auto" or null ? color : s.GetColor("caret-color");
                p.FillRect(Painter.SnapRect(x0 + caretX, top, Math.Max(1, w.UnitPx * 1.5f), lh), Painter.Tint(cc, opacity));
            }
            p.PopClip(saved);
        }

        /// <summary>A slider: a thin track in the text colour, the part up to the thumb and the thumb in accent-color; the thumb lights on hover and
        /// while held, and a ring marks the keyboard focus. The element itself is transparent (UA sheet), so the page may paint behind it.</summary>
        private void PaintRange(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            var cr = Content(b, rect);
            if (cr.Width <= 0 || cr.Height <= 0) { return; }
            int d = ThumbSize(cr), th = Math.Max(2, (int)Math.Round(cr.Height * 0.3f));
            double f = RangeField.Fraction(RangeField.Value(e), RangeField.Min(e), RangeField.Max(e));
            int cx = cr.X + d / 2 + (int)Math.Round((cr.Width - d) * f), ty = cr.Y + (cr.Height - th) / 2;
            p.FillRounded(new Rectangle(cr.X, ty, cr.Width, th), th / 2, Painter.Tint(s.GetColor("color"), opacity * 0.22f));
            p.FillRounded(new Rectangle(cr.X, ty, Math.Max(0, cx - cr.X), th), th / 2, Accent(s, opacity));
            var thumb = new Rectangle(cx - d / 2, cr.Y + (cr.Height - d) / 2, d, d);
            if (Focus == e) { p.FillRounded(new Rectangle(thumb.X - 3, thumb.Y - 3, d + 6, d + 6), d / 2 + 3, Accent(s, opacity, 0.35f)); }
            bool lit = rangeDrag == e || (e.State & NodeState.Hover) != 0;
            var color = Accent(s, opacity);
            p.FillRounded(thumb, d / 2, lit ? Color.Lerp(color, Color.White, 0.3f) : color);
        }

        private void PaintSelect(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            var cr = Content(b, rect);
            var opts = OpenSelect == e ? options : OptionsOf(e);
            int i = SelectedIndex(e, opts);
            var fonts = w.Fonts;
            float lh = fonts.NormalLineHeight(s), baseline = cr.Y + (cr.Height - lh) / 2 + fonts.Ascent(s);
            var color = Painter.Tint(s.GetColor("color"), opacity);
            if (i >= 0 && p.PushClip(cr, out var saved))
            {
                fonts.Draw(p.Batch, opts[i].TextContent.Trim(), cr.X, baseline, color, s);
                p.PopClip(saved);
            }
            // chevron in the right padding: rows of shrinking width
            int size = Math.Max(3, (int)Math.Round(s.FontSizePx * 0.3f));
            int cx = rect.Right - (int)Math.Round(b.Border.Right + b.Padding.Right / 2), cy = rect.Center.Y - size / 2;
            for (int r = 0; r < size; r++) { p.FillRect(new Rectangle(cx - (size - r), cy + r, 2 * (size - r), 1), color); }
            if (OpenSelect == e) { openRect = w.ScreenRect(e); }   // the list is drawn and hit on the screen, over everything, at the select's size there (translate, scroll, scale all in)
        }

        private Rectangle DropRect(Rectangle select, int count)
        {
            int n = Math.Min(count, DropRows), h = n * select.Height;
            int y = select.Bottom + h <= GameMain.GraphicsHeight ? select.Bottom : Math.Max(0, select.Y - h);
            return new Rectangle(select.X, y, select.Width, h);
        }

        private static void PaintProgress(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            var cr = Content(b, rect);
            double max = NumberField.Attr(e, "max") ?? 1;
            if (max <= 0) { max = 1; }
            int radius = Painter.RadiusOf(s, rect);
            if (NumberField.Attr(e, "value") is { } v)
            {
                float frac = (float)Math.Clamp(v / max, 0, 1);
                p.FillRounded(new Rectangle(cr.X, cr.Y, (int)Math.Round(cr.Width * frac), cr.Height), radius, Accent(s, opacity));
                return;
            }
            // no value: a segment sweeping across
            float t = (float)(Timing.TotalTime % 1.5 / 1.5);
            int seg = Math.Max(1, cr.Width / 3), x = cr.X + (int)((cr.Width + seg) * t) - seg;
            var r = Rectangle.Intersect(new Rectangle(x, cr.Y, seg, cr.Height), cr);
            p.FillRounded(r, radius, Accent(s, opacity));
        }

        /// <summary>The prefab each &lt;item&gt; was found for, by the id it was looked up with: the lookup (an
        /// Identifier, the prefab table) runs when the attribute changes, not every frame (stage 7, 0 B paint).</summary>
        private readonly Dictionary<Element, (string Id, ItemPrefab Prefab)> itemPrefabs = new();

        /// <summary>The sprite each &lt;item sprite="kind:id"&gt; was found for (UiSprites), by the key it was looked up with.</summary>
        private readonly Dictionary<string, Sprite> spriteIcons = new(StringComparer.Ordinal);

        /// <summary>&lt;item sprite="talent:id"&gt;: an icon of the game that is not an item (a talent, a faction, a mission, a job; see UiSprites). Drawn whole
        /// in the content box, tinted with the element's <c>color</c>, so the page decides the colour (a locked talent dim, a faction in its own colour).</summary>
        private void PaintSpriteIcon(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity, string key)
        {
            if (!spriteIcons.TryGetValue(key, out var sprite))
            {
                sprite = UiSprites.Find(key);
                spriteIcons[key] = sprite;
                if (sprite == null && warned.Add("sprite:" + key)) { UiPlugin.Log($"{w.Name}: <item sprite=\"{key}\">: no such icon"); }
            }
            if (sprite?.Texture == null) { return; }   // reading Texture loads a lazy sprite (its source rectangle is empty before)
            var cr = Content(b, rect);
            var src = sprite.SourceRect;
            if (src.Width <= 0 || src.Height <= 0 || cr.Width <= 0 || cr.Height <= 0) { return; }
            float scale = Math.Min(cr.Width / (float)src.Width, cr.Height / (float)src.Height);
            sprite.Draw(p.Batch, cr.Center.ToVector2(), Painter.Tint(s.GetColor("color"), opacity), new Vector2(src.Width / 2f, src.Height / 2f), 0, scale);
        }

        private void PaintItem(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            string spriteKey = e.GetAttribute("sprite");
            if (!string.IsNullOrEmpty(spriteKey)) { PaintSpriteIcon(p, b, s, rect, opacity, spriteKey); return; }
            string id = e.GetAttribute("prefab") ?? e.GetAttribute("id-prefab");
            if (string.IsNullOrEmpty(id)) { return; }
            if (!itemPrefabs.TryGetValue(e, out var known) || !ReferenceEquals(known.Id, id))
            {
                ItemPrefab.Prefabs.TryGet(id.ToIdentifier(), out var found);
                if (found == null && warned.Add("item:" + id)) { UiPlugin.Log($"{w.Name}: <item prefab=\"{id}\"> line {e.Line}: no such item"); }
                itemPrefabs[e] = known = (id, found);
            }
            var prefab = known.Prefab;
            if (prefab == null) { return; }
            var sprite = prefab.InventoryIcon ?? prefab.Sprite;
            if (sprite == null) { return; }
            var cr = Content(b, rect);
            var src = sprite.SourceRect;
            if (src.Width <= 0 || src.Height <= 0 || cr.Width <= 0 || cr.Height <= 0) { return; }
            float scale = Math.Min(cr.Width / (float)src.Width, cr.Height / (float)src.Height);
            var tint = prefab.InventoryIcon != null ? prefab.InventoryIconColor : prefab.SpriteColor;
            sprite.Draw(p.Batch, cr.Center.ToVector2(), tint * opacity, new Vector2(src.Width / 2f, src.Height / 2f), 0, scale);
            if (int.TryParse(e.GetAttribute("count"), out int count) && count > 1)
            {
                string text = count.ToString();
                var fonts = w.Fonts;
                float tx = cr.Right - fonts.Width(text, s), ty = cr.Bottom - fonts.Descent(s);
                fonts.Draw(p.Batch, text, tx + 1, ty + 1, Color.Black * (0.8f * opacity), s);
                fonts.Draw(p.Batch, text, tx, ty, Painter.Tint(s.GetColor("color"), opacity), s);
            }
        }

        private void PaintCanvas(Painter p, LayoutBox b, Rectangle rect, Element e)
        {
            string id = e.Id;
            if (id != null) { w.CanvasInput.Painted(e, p.Abs(Content(b, rect)), p.Xf); }   // the mouse handler (SetCanvasMouse) needs the box even without a draw callback
            if (id == null || !canvases.TryGetValue(id, out var draw)) { return; }
            var cr = Content(b, rect);
            if (!p.PushClip(cr, out var saved)) { return; }
            try { draw(p.Batch, cr); }
            catch (Exception ex)
            {
                canvases.Remove(id);
                UiPlugin.Log($"{w.Name}: canvas '{id}' failed and was removed: {ex}");
            }
            finally { p.PopClip(saved); }
        }

        /// <summary>A game inventory slot (SetInventory): its rectangle is the content box, the part inside the clip
        /// takes the mouse; drawn by the game (frame, item, stack, condition, quality, highlight).</summary>
        private void PaintSlot(Painter p, LayoutBox b, Rectangle rect, Element e)
        {
            // the game draws a slot at the screen rectangle it was given, on its own: it knows nothing of the batch's matrix of a scaled block
            if (!p.Xf.IsIdentity)
            {
                if (warned.Add("scale:slot" + e.Line)) { UiPlugin.Log($"{w.Name}: <slot> line {e.Line} is inside a block with scale: inventory cells are not supported there and are not drawn"); }
                return;
            }
            var inv = GetInventory(e.GetAttribute("inventory"));
            var vs = inv?.visualSlots;
            if (vs == null || !int.TryParse(e.GetAttribute("index"), out int i) || i < 0 || i >= vs.Length) { return; }
            var slot = vs[i] ??= new VisualSlot(Rectangle.Empty);
            var cr = Content(b, rect);
            var shown = Rectangle.Intersect(cr, p.Clip);
            slot.Rect = cr;
            slot.InteractRect = shown;
            bool covered = modals.Count > 0 && !UiWindow.IsWithin(e, modals[^1].E);
            slot.Disabled = shown.Width <= 0 || shown.Height <= 0 || covered;
            if (shown.Width <= 0 || shown.Height <= 0) { return; }
            var items = inv.slots[i].Items;
            // the item stays drawn in its slot unless all of it is being dragged out (the game's rule)
            bool drawItem = Inventory.DraggingItems.Count == 0 || slot.MouseOn();
            for (int k = 0; !drawItem && k < items.Count; k++) { drawItem = !Inventory.DraggingItems.Contains(items[k]); }
            slot.SlotSprite = SlotFrame(p, e, slot);
            Inventory.DrawSlot(p.Batch, inv, slot, items.Count > 0 ? items[0] : null, i, drawItem);
        }

        // the frames of slots (-bbe-slot-frame): the sprite a slot had when the window made it (an equipment cell may have its own), and one sprite per
        // picture; a value seen before costs one lookup per frame
        private readonly ConditionalWeakTable<VisualSlot, StrongBox<Sprite>> slotOwnFrame = new();
        private readonly Dictionary<string, Sprite> slotFrames = new(StringComparer.Ordinal);
        private static Sprite noFrame;

        /// <summary>The sprite the game draws under the item of a slot: the game's (game), a clear one (none: the page's background, border and radius of
        /// the &lt;slot&gt; element show and the game draws the item, its condition and the stack count only) or the page's picture (url(file), stretched to the cell).
        /// A picture that cannot be found falls back to the game's frame with one line in the log.</summary>
        private Sprite SlotFrame(Painter p, Element e, VisualSlot slot)
        {
            var own = slotOwnFrame.GetValue(slot, s => new StrongBox<Sprite>(s.SlotSprite)).Value;
            string v = e.Style?.Get("-bbe-slot-frame")?.Trim() ?? "game";
            if (v.Length == 0 || v == "game") { return own; }
            if (v == "none")
            {
                if (noFrame == null)
                {
                    var clear = new Texture2D(GameMain.Instance.GraphicsDevice, 1, 1);
                    clear.SetData(new[] { Color.Transparent });
                    noFrame = new Sprite(clear, new Rectangle(0, 0, 1, 1), null);
                }
                return noFrame;
            }
            if (slotFrames.TryGetValue(v, out var known)) { return known ?? own; }
            Sprite made = null;
            if (ImageCss.TryCall(v, "url", out string url) && ImageCss.ResolvePath(p.PageFolder(), url, File.Exists, out string full, out string problem))
            {
                var tex = w.Images.Get(full);
                if (tex != null) { made = new Sprite(tex, new Rectangle(0, 0, tex.Width, tex.Height), null); }
                else { UiPlugin.Log($"{w.Name}: <slot> line {e.Line}: -bbe-slot-frame {v}: the picture could not be read"); }
            }
            else { UiPlugin.Log($"{w.Name}: <slot> line {e.Line}: -bbe-slot-frame {v}: not game, none or a picture of the package"); }
            slotFrames[v] = made;
            return made ?? own;
        }

        /// <summary>On top of the page: modal dialogs over a dimmed backdrop, the open dropdown, the tooltip.</summary>
        internal void PaintOverlay(Painter p)
        {
            var screen = new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            foreach (var (_, box) in modals)
            {
                p.FillRect(screen, Color.Black * 0.5f);
                p.PaintSubtree(box);
            }
            if (popupBox != null) { p.PaintSubtree(popupBox); }   // over the modals too: a menu opened inside a dialog must show
            if (OpenSelect != null && w.BoxOf(OpenSelect) is { } sb && openRect.Width > 0) { PaintDropdown(p, sb); }
            if (tip != null && OpenSelect == null && Timing.TotalTime - tipSince > TooltipDelay && w.BoxOf(tipOwner) is { } tb)
            {
                GUIComponent.DrawToolTip(p.Batch, tip, w.ScreenRect(tipOwner));
            }
        }

        private void PaintDropdown(Painter p, LayoutBox b)
        {
            var s = b.Element.Style;
            var rect = openRect;
            var next = DropRect(rect, options.Count);
            if (next != dropRect) { dropRect = next; Version++; }
            rowH = rect.Height;
            var bg = s.GetColor("background-color");
            p.FillRect(dropRect, bg.A > 0 ? Painter.Tint(new Rgba(bg.R, bg.G, bg.B), 1) : new Color(15, 30, 33));
            var fonts = w.Fonts;
            int sel = SelectedIndex(b.Element, options);
            float lh = fonts.NormalLineHeight(s);
            var color = Painter.Tint(s.GetColor("color"), 1);
            int padL = (int)Math.Round(b.Border.Left + b.Padding.Left);
            for (int r = 0; r < DropRows && dropFirst + r < options.Count; r++)
            {
                int i = dropFirst + r;
                var row = new Rectangle(dropRect.X, dropRect.Y + r * rowH, dropRect.Width, rowH);
                if (i == dropHover) { p.FillRect(row, Accent(s, 1, 0.35f)); }
                else if (i == sel) { p.FillRect(row, Accent(s, 1, 0.15f)); }
                bool off = UiWindow.IsDisabled(options[i]);
                fonts.Draw(p.Batch, options[i].TextContent.Trim(), row.X + padL, row.Y + (rowH - lh) / 2 + fonts.Ascent(s),
                    off ? color * 0.45f : color, s);
            }
            p.StrokeRounded(dropRect, 0, Math.Max(1, (int)Math.Round(w.UnitPx)), Accent(s, 1));
            if (options.Count > DropRows)
            {
                int th = Math.Max(4, dropRect.Height * DropRows / options.Count);
                int ty = dropRect.Y + (dropRect.Height - th) * dropFirst / Math.Max(1, options.Count - DropRows);
                p.FillRect(new Rectangle(dropRect.Right - 4, ty, 3, th), color * 0.5f);
            }
        }
    }
}
