using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>The mouse of one window, free of the game (so the tests run it). Elements opt in with data-mouse="name" (and data-mouse-events="down up
    /// click dblclick contextmenu wheel enter leave move hover"; default: all but move and hover); the owner registers a handler per name with Set.
    /// Events start at the deepest element under the pointer and bubble through the ancestors with data-mouse; true stops the bubbling, and for a Down
    /// it also takes the capture: Move, Up and Wheel then go to the holder (and its ancestors) wherever the pointer is, the hover is frozen, until
    /// every captured button is up. Enter and Leave do not bubble. Click is a left Up on the element (or inside it) where the left Down was; unlike a
    /// browser it is NOT sent when the pointer went more than DragThreshold px between the two (a card that was dragged is not clicked); ContextMenu
    /// is the same for the right button. Canvases (SetCanvas) keep their own CanvasMouse and swallow their events: nothing above them gets any. A
    /// canvas and a data-mouse holder never hold the pointer together. The router reuses one event and its lists: a steady frame allocates nothing.</summary>
    internal sealed class PointerRouter
    {
        /// <summary>Px from the Down after which the gesture is a drag (no Click).</summary>
        public const float DragThreshold = 4f;
        /// <summary>Seconds the pointer must rest on one element for a Hover event.</summary>
        public const double HoverDelay = 0.4;

        private struct DownState { public bool Active; public Element Target; public float X, Y; public bool Dragged; public int Count; }

        private sealed class CanvasEntry { public Element El; public readonly CanvasMouse Mouse = new(); }

        private static readonly MouseButtons[] Each = { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle };

        private readonly Dictionary<string, Func<UiMouseEvent, bool>> handlers = new();
        private readonly List<CanvasEntry> canvases = new();
        private readonly UiMouseEvent ev;
        private readonly List<Element> path = new(), chain = new(), next = new();
        private readonly DownState[] downs = new DownState[3];
        private readonly ClickCounter counter = new();
        private Element holder;
        private MouseButtons capButtons;
        private IPointerGeometry geo;
        private PointerFrame lastF;
        private bool hasLast;
        private float lastX, lastY;
        private Element hoverTgt;
        private double hoverSince;
        private bool hoverFired, mouseOver, canvasCaptured, canvasActive;
        private Element dragging, underEx, underVal;
        private bool underDone;

        public PointerRouter() { ev = new UiMouseEvent(path, this); }

        // A handler may close the window, clear its own name or cancel the router while it runs: all of those reach CancelData, which reuses the one
        // event object the running dispatch is still filling and reading. So while a handler runs, a cancel is only noted and done when the dispatch is
        // over (Flush); a handler that throws while it holds the pointer gets its Cancel the same way, so its owner can end the gesture.
        private int dispatching;
        private bool pendingCancel;
        private double pendingTime;
        private Element answered;
        private readonly List<(string Name, Func<UiMouseEvent, bool> Fn, Element El)> failedHolders = new();

        /// <summary>Called when a handler throws (it is removed); the window logs it. Argument: the handler's name (canvas: "canvas#id").</summary>
        public Action<string, Exception> OnError;
        /// <summary>The capture holder (or a canvas) is still attached, shown, enabled and not covered by a dialog; false during a capture cancels it.</summary>
        public Func<Element, bool> IsAlive;
        /// <summary>The window's hit test at a screen point (px) with one element and its descendants left out: UiMouseEvent.Under during a capture or a drag.
        /// Null: no Under. Called at most once per frame and element; an exception is reported to OnError as "under" and Under is null.</summary>
        public Func<Element, float, float, Element> UnderExcept;

        /// <summary>The data-mouse holder has the pointer (a button that went down on it, or a Capture(), is still down).</summary>
        public bool Captured => holder != null;
        public Element CaptureHolder => holder;
        /// <summary>Someone needs the pointer now (a capture, a drag of a canvas, a button that went down here, the pointer over a data-mouse element):
        /// the window stretches MouseRect and disables the character's controls while this is true.</summary>
        public bool Active => holder != null || canvasCaptured || canvasActive || mouseOver || downs[0].Active || downs[1].Active || downs[2].Active;
        /// <summary>A data-mouse capture or a canvas drag is going: the window stretches its MouseRect to the whole screen only while this is true
        /// (Active also counts plain hovering, which only disables the character's controls).</summary>
        public bool HoldsPointer => holder != null || canvasCaptured;
        /// <summary>The latest right press was taken: a capture holder or a canvas had it, or a data-mouse handler answered true for its Down. The window then
        /// leaves data-contextmenu closed for that press (the taker opens its own menu). Stays as it was until the next right press.</summary>
        public bool RightTaken { get; private set; }
        /// <summary>Any handler or canvas is registered: the window has to call Update every frame while this is true.</summary>
        public bool Any => handlers.Count > 0 || canvases.Count > 0;

        public bool Has(string name) => name != null && handlers.ContainsKey(name);

        /// <summary>Register (or replace) the handler of data-mouse="name"; null removes it. If that name holds the capture it gets a Cancel first.</summary>
        public void Set(string name, Func<UiMouseEvent, bool> handler)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            if (handler == null && holder != null && HolderName() == name) { if (dispatching > 0) { Defer(lastF.Time); } else { CancelData(lastF.Time); } }
            if (handler == null) { handlers.Remove(name); } else { handlers[name] = handler; }
        }

        /// <summary>Give a canvas the old CanvasMouse form of the mouse; null removes it (a drag in progress ends with a Cancel).</summary>
        public void SetCanvas(Element canvas, Func<CanvasMouseEvent, bool> handler)
        {
            if (canvas == null) { return; }
            int i = IndexOfCanvas(canvas);
            if (handler == null)
            {
                if (i < 0) { return; }
                var en = canvases[i];
                canvases.RemoveAt(i);
                if (en.Mouse.Active) { try { en.Mouse.Cancel(lastF.Time); } catch (Exception ex) { OnError?.Invoke(CanvasName(en), ex); } }
                // the drag it held is over now, not at the next Update (the window's MouseRect reads HoldsPointer)
                canvasCaptured = canvasActive = false;
                for (int k = 0; k < canvases.Count; k++) { canvasCaptured |= canvases[k].Mouse.Captured; canvasActive |= canvases[k].Mouse.Active; }
                return;
            }
            if (i < 0) { canvases.Add(new CanvasEntry { El = canvas }); i = canvases.Count - 1; }
            canvases[i].Mouse.Handler = handler;
        }

        /// <summary>One frame. f.Over: the pointer is on this window (GUI.MouseOn == host); target: the hit-test result (deepest element) or null.
        /// dragged: the element a drag (DragDrop) carries, or null; its Under is found with it left out. True when the wheel was used (the page must not scroll).</summary>
        public bool Update(in PointerFrame f, Element target, IPointerGeometry geometry, Element dragged = null)
        {
            geo = geometry; ev.Geo = geometry; lastF = f;
            dragging = dragged; underDone = false;
            if (holder != null && IsAlive != null && !IsAlive(holder)) { CancelData(f.Time); }
            bool wheelUsed = UpdateCanvases(f, target);
            if (canvasCaptured) { if ((f.Pressed & MouseButtons.Right) != 0) { RightTaken = true; } Flush(); return wheelUsed; }

            Element tgt = f.Over ? target : null;
            bool moved = !hasLast || f.X != lastX || f.Y != lastY;
            // a button that went down and went far is a drag: no Click
            for (int i = 0; i < 3; i++)
            {
                ref var d = ref downs[i];
                if (!d.Active || d.Dragged) { continue; }
                float dx = f.X - d.X, dy = f.Y - d.Y;
                if (dx * dx + dy * dy > DragThreshold * DragThreshold) { d.Dragged = true; }
            }
            bool wasCaptured = holder != null;
            if (!wasCaptured) { Track(tgt, f); }

            if (moved)
            {
                if (holder != null) { Prep(UiMouseKind.Move, holder, MouseButtons.None, f, 0, 0, true, Inside(tgt), null); Bubble(holder, holder); }
                else if (tgt != null && !OnCanvas(tgt)) { Prep(UiMouseKind.Move, tgt, MouseButtons.None, f, 0, 0, false, true, null); Bubble(tgt, null); }
            }

            for (int i = 0; i < 3; i++)
            {
                var b = Each[i];
                if ((f.Pressed & b) == 0) { continue; }
                if (b == MouseButtons.Right) { RightTaken = holder != null || (tgt != null && OnCanvas(tgt)); }
                if (holder != null)
                {
                    downs[i] = new DownState { Active = true, Target = holder, X = f.X, Y = f.Y, Count = counter.Count(b, f.X, f.Y, f.Time) };
                    capButtons |= b;
                    Prep(UiMouseKind.Down, holder, b, f, 0, downs[i].Count, true, Inside(tgt), null);
                    Bubble(holder, holder);
                }
                else if (tgt != null)
                {
                    int count = counter.Count(b, f.X, f.Y, f.Time);
                    if (OnCanvas(tgt)) { continue; }
                    downs[i] = new DownState { Active = true, Target = tgt, X = f.X, Y = f.Y, Count = count };
                    Prep(UiMouseKind.Down, tgt, b, f, 0, count, false, true, null);
                    if (Bubble(tgt, null))
                    {
                        // true on a Down takes the capture for the handler that said it
                        holder = answered; capButtons |= b;
                        if (b == MouseButtons.Right) { RightTaken = true; }
                    }
                }
            }

            if (f.Wheel != 0)
            {
                if (holder != null) { Prep(UiMouseKind.Wheel, holder, MouseButtons.None, f, f.Wheel, 0, true, Inside(tgt), null); wheelUsed |= Bubble(holder, holder); }
                else if (tgt != null && !OnCanvas(tgt)) { Prep(UiMouseKind.Wheel, tgt, MouseButtons.None, f, f.Wheel, 0, false, true, null); wheelUsed |= Bubble(tgt, null); }
            }

            for (int i = 0; i < 3; i++)
            {
                var b = Each[i];
                if ((f.Held & b) != 0 || !(downs[i].Active || (capButtons & b) != 0)) { continue; }
                var d = downs[i];
                capButtons &= ~b;
                bool captured = capButtons != MouseButtons.None;
                if (holder != null)
                {
                    Prep(UiMouseKind.Up, holder, b, f, 0, d.Count, captured, Inside(tgt), null);
                    Bubble(holder, holder);
                }
                else if (tgt != null && !OnCanvas(tgt))
                {
                    Prep(UiMouseKind.Up, tgt, b, f, 0, d.Count, false, true, null);
                    Bubble(tgt, null);
                }
                downs[i] = default;
                if (capButtons == MouseButtons.None) { holder = null; }
                // Click: Up inside the subtree of the Down target, no drag in between; sent along the Down target's path
                if (d.Active && d.Target != null && !d.Dragged && tgt != null && IsWithin(tgt, d.Target) && !OnCanvas(d.Target))
                {
                    if (b == MouseButtons.Left)
                    {
                        Fire(UiMouseKind.Click, d, b, f);
                        if (d.Count == 2) { Fire(UiMouseKind.DoubleClick, d, b, f); }
                    }
                    else if (b == MouseButtons.Right) { Fire(UiMouseKind.ContextMenu, d, b, f); }
                }
            }

            if (wasCaptured && holder == null) { Track(tgt, f); }

            // hover: the pointer rests on one element
            if (holder == null && tgt != null)
            {
                if (moved || tgt != hoverTgt) { hoverTgt = tgt; hoverSince = f.Time; hoverFired = false; }
                else if (!hoverFired && f.Time - hoverSince >= HoverDelay)
                {
                    hoverFired = true;
                    if (!OnCanvas(tgt)) { Prep(UiMouseKind.Hover, tgt, MouseButtons.None, f, 0, 0, false, true, null); Bubble(tgt, null); }
                }
            }
            else { hoverTgt = null; }

            if (tgt == null && holder == null) { hasLast = false; }
            else { lastX = f.X; lastY = f.Y; hasLast = true; }
            Flush();
            return wheelUsed;
        }

        /// <summary>The pointer was taken away for a reason that is no button going up (Esc, the window closed, a dialog over the holder): the holder
        /// and the canvases get a Cancel (no Up), the hovered elements a Leave.</summary>
        public void Cancel(double time)
        {
            if (dispatching > 0) { Defer(time); return; }
            CancelData(time);
            for (int i = canvases.Count - 1; i >= 0; i--)
            {
                var en = canvases[i];
                if (!en.Mouse.Active) { continue; }
                try { en.Mouse.Cancel(time); }
                catch (Exception ex) { canvases.Remove(en); OnError?.Invoke(CanvasName(en), ex); }
            }
            canvasCaptured = canvasActive = false;
        }

        private void CancelData(double time)
        {
            var f = lastF;
            f.Time = time; f.Held = MouseButtons.None; f.Pressed = MouseButtons.None; f.Wheel = 0;
            var h = holder;
            holder = null; capButtons = MouseButtons.None;
            Array.Clear(downs, 0, 3);
            if (h != null)
            {
                var name = h.GetAttribute("data-mouse");
                if (!string.IsNullOrEmpty(name) && handlers.TryGetValue(name, out var fn))
                {
                    Prep(UiMouseKind.Cancel, h, MouseButtons.None, f, 0, 0, false, false, null);
                    Invoke(name, fn, h);
                }
            }
            for (int i = 0; i < chain.Count; i++) { Notify(UiMouseKind.Leave, chain[i], chain[0], null, f); }
            chain.Clear(); hoverTgt = null; mouseOver = false; hasLast = false;
        }

        /// <summary>UiMouseEvent.Capture().</summary>
        internal void TakeCapture(UiMouseEvent e)
        {
            if (canvasCaptured || e.Current == null || e.Held == MouseButtons.None) { return; }
            if (holder != null && holder != e.Current) { return; }
            holder = e.Current; capButtons |= e.Held;
        }

        // ---------- canvases (the old CanvasMouse, one per canvas) ----------

        private bool UpdateCanvases(in PointerFrame f, Element target)
        {
            bool wheel = false;
            canvasCaptured = canvasActive = false;
            bool dataCaptured = holder != null;
            for (int i = 0; i < canvases.Count; i++)
            {
                var en = canvases[i];
                float x = 0, y = 0, w = 0, h = 0; var xf = UiTransform.Identity;
                bool alive = geo != null && geo.Content(en.El, out x, out y, out w, out h, out xf) && (IsAlive == null || IsAlive(en.El));
                if (!alive)
                {
                    if (en.Mouse.Active) { try { en.Mouse.Cancel(f.Time); } catch (Exception ex) { canvases.RemoveAt(i--); OnError?.Invoke(CanvasName(en), ex); } }
                    continue;
                }
                bool over = f.Over && !dataCaptured && target == en.El;
                if (!over && !en.Mouse.Active) { continue; }
                var g = f;
                g.Over = over;
                if (dataCaptured) { g.Pressed = MouseButtons.None; }
                g.RectX = x; g.RectY = y; g.RectW = w; g.RectH = h;
                if (!xf.IsIdentity) { var back = xf.Inverse(); g.X = back.X(f.X); g.Y = back.Y(f.Y); }
                try { wheel |= en.Mouse.Update(g); }
                catch (Exception ex) { canvases.RemoveAt(i--); OnError?.Invoke(CanvasName(en), ex); continue; }
                canvasCaptured |= en.Mouse.Captured;
                canvasActive |= en.Mouse.Active;
            }
            return wheel;
        }

        private int IndexOfCanvas(Element el)
        {
            for (int i = 0; i < canvases.Count; i++) { if (canvases[i].El == el) { return i; } }
            return -1;
        }

        /// <summary>The element is a canvas with a mouse handler: it swallows the events (nothing above it gets any).</summary>
        private bool OnCanvas(Element target) => canvases.Count > 0 && IndexOfCanvas(target) >= 0;

        private static string CanvasName(CanvasEntry en) => "canvas#" + en.El.Id;

        // ---------- data-mouse delivery ----------

        private string HolderName() => holder?.GetAttribute("data-mouse");

        private bool Inside(Element tgt) => holder == null || (tgt != null && IsWithin(tgt, holder));

        private static bool IsWithin(Element e, Element ancestor)
        {
            for (; e != null; e = e.Parent) { if (e == ancestor) { return true; } }
            return false;
        }

        /// <summary>Fill the shared event (and its Path, from target up) for the next Bubble or Invoke.</summary>
        private void Prep(UiMouseKind kind, Element target, MouseButtons button, in PointerFrame f, float wheel, int count, bool captured, bool inside, Element related)
        {
            path.Clear();
            for (var e = target; e != null; e = e.Parent) { path.Add(e); }
            var v = ev;
            v.Kind = kind; v.Target = target; v.Current = null; v.Button = button; v.Held = f.Held;
            v.X = f.X; v.Y = f.Y; v.Wheel = wheel; v.Mods = f.Mods; v.ClickCount = count; v.Time = f.Time;
            v.Captured = captured; v.Inside = inside; v.Related = related; v.Under = UnderAt(f);
            // where the gesture began: the event's own button, else any held button that went down here
            var d = default(DownState);
            if (button != MouseButtons.None) { d = downs[Idx(button)]; }
            else { for (int i = 0; i < 3; i++) { if ((f.Held & Each[i]) != 0 && downs[i].Active) { d = downs[i]; break; } } }
            if (d.Active) { v.StartX = d.X; v.StartY = d.Y; v.Dragged = d.Dragged; } else { v.StartX = f.X; v.StartY = f.Y; v.Dragged = false; }
        }

        /// <summary>UiMouseEvent.Under: the hit test with the capture holder (else the dragged element) left out; null when neither is there.</summary>
        private Element UnderAt(in PointerFrame f)
        {
            Element ex = holder ?? dragging;
            if (ex == null || UnderExcept == null) { return null; }
            if (!underDone || underEx != ex)
            {
                underEx = ex; underDone = true; underVal = null;
                try { underVal = UnderExcept(ex, f.X, f.Y); }
                catch (Exception e) { OnError?.Invoke("under", e); }
            }
            return underVal;
        }

        private static int Idx(MouseButtons b) => b == MouseButtons.Left ? 0 : b == MouseButtons.Right ? 1 : 2;

        /// <summary>Click, DoubleClick or ContextMenu along the path of the element where the button went down.</summary>
        private void Fire(UiMouseKind kind, DownState d, MouseButtons b, in PointerFrame f)
        {
            Prep(kind, d.Target, b, f, 0, d.Count, holder != null, true, null);
            ev.StartX = d.X; ev.StartY = d.Y; ev.Dragged = d.Dragged;
            Bubble(d.Target, null);
        }

        /// <summary>Call the handlers from start up through the ancestors; true when one answered true (the bubbling stops there, ev.Current is it).
        /// forced: this element gets the event even when its data-mouse-events does not list the kind (the capture holder).</summary>
        private bool Bubble(Element start, Element forced)
        {
            answered = null;
            for (var el = start; el != null; el = el.Parent)
            {
                var name = el.GetAttribute("data-mouse");
                if (string.IsNullOrEmpty(name) || !handlers.TryGetValue(name, out var fn)) { continue; }
                if (el != forced && !Wants(el, ev.Kind)) { continue; }
                if (Invoke(name, fn, el)) { answered = el; return true; }
            }
            return false;
        }

        private void Defer(double time)
        {
            pendingCancel = true;
            pendingTime = Math.Max(pendingTime, time);
        }

        /// <summary>The cancels asked for while a handler ran, now that no dispatch is going.</summary>
        private void Flush()
        {
            if (dispatching > 0) { return; }
            for (int i = 0; i < failedHolders.Count; i++)
            {
                var (name, fn, el) = failedHolders[i];
                var f = lastF;
                f.Held = MouseButtons.None; f.Pressed = MouseButtons.None; f.Wheel = 0;
                Prep(UiMouseKind.Cancel, el, MouseButtons.None, f, 0, 0, false, false, null);
                ev.Current = el;
                try { fn(ev); } catch (Exception ex) { OnError?.Invoke(name, ex); }
            }
            failedHolders.Clear();
            if (pendingCancel) { pendingCancel = false; double t = pendingTime; pendingTime = 0; Cancel(t); }
        }

        private bool Invoke(string name, Func<UiMouseEvent, bool> fn, Element el)
        {
            var v = ev;
            v.Current = el;
            v.LocalX = v.X; v.LocalY = v.Y; v.Width = v.Height = 0;
            if (geo != null && geo.Content(el, out float x, out float y, out float w, out float h, out var xf))
            {
                var back = xf.Inverse();
                v.LocalX = back.X(v.X) - x; v.LocalY = back.Y(v.Y) - y; v.Width = w; v.Height = h;
            }
            dispatching++;
            try { return fn(v); }
            catch (Exception ex)
            {
                handlers.Remove(name);
                if (holder != null && HolderName() == name)
                {
                    // the gesture of the failed handler ends with a Cancel after this dispatch (Flush), so its owner leaves its drag mode
                    failedHolders.Add((name, fn, holder));
                    holder = null; capButtons = MouseButtons.None;
                    Array.Clear(downs, 0, 3);
                }
                OnError?.Invoke(name, ex);
                return false;
            }
            finally { dispatching--; }
        }

        /// <summary>Enter or Leave for one element of the hover chain: no bubbling, only that element's own handler.</summary>
        private void Notify(UiMouseKind kind, Element el, Element target, Element related, in PointerFrame f)
        {
            var name = el.GetAttribute("data-mouse");
            if (string.IsNullOrEmpty(name) || !handlers.TryGetValue(name, out var fn) || !Wants(el, kind)) { return; }
            Prep(kind, target, MouseButtons.None, f, 0, 0, false, true, related);
            Invoke(name, fn, el);
        }

        /// <summary>The hover chain follows the target: elements that left get Leave (deepest first), elements that came in get Enter (outermost first).</summary>
        private void Track(Element tgt, in PointerFrame f)
        {
            Element old = chain.Count > 0 ? chain[0] : null;
            if (tgt == old) { return; }
            next.Clear();
            for (var e = tgt; e != null; e = e.Parent) { next.Add(e); }
            for (int i = 0; i < chain.Count; i++)
            {
                if (!next.Contains(chain[i])) { Notify(UiMouseKind.Leave, chain[i], old, tgt, f); }
            }
            for (int i = next.Count - 1; i >= 0; i--)
            {
                if (!chain.Contains(next[i])) { Notify(UiMouseKind.Enter, next[i], tgt, old, f); }
            }
            chain.Clear(); chain.AddRange(next);
            mouseOver = false;
            for (int i = 0; i < chain.Count; i++)
            {
                var name = chain[i].GetAttribute("data-mouse");
                if (!string.IsNullOrEmpty(name) && handlers.ContainsKey(name)) { mouseOver = true; break; }
            }
        }

        private static bool Wants(Element el, UiMouseKind kind)
        {
            var list = el.GetAttribute("data-mouse-events");
            if (list == null) { return kind != UiMouseKind.Move && kind != UiMouseKind.Hover; }
            string token = kind switch
            {
                UiMouseKind.Down => "down", UiMouseKind.Up => "up", UiMouseKind.Move => "move", UiMouseKind.Enter => "enter",
                UiMouseKind.Leave => "leave", UiMouseKind.Wheel => "wheel", UiMouseKind.Click => "click", UiMouseKind.DoubleClick => "dblclick",
                UiMouseKind.ContextMenu => "contextmenu", UiMouseKind.Hover => "hover", _ => null,
            };
            return token == null || HasToken(list, token);
        }

        private static bool HasToken(string list, string token)
        {
            int i = 0;
            while (i < list.Length)
            {
                while (i < list.Length && char.IsWhiteSpace(list[i])) { i++; }
                int s = i;
                while (i < list.Length && !char.IsWhiteSpace(list[i])) { i++; }
                if (i - s == token.Length && string.CompareOrdinal(list, s, token, 0, token.Length) == 0) { return true; }
            }
            return false;
        }
    }
}
