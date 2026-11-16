using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    public enum UiDropPhase
    {
        /// <summary>The pointer came over a target that takes the dragged kind (it has :drag-over now).</summary>
        Enter,
        /// <summary>The pointer moved over that target.</summary>
        Over,
        /// <summary>The pointer left the target, or the drag was cancelled over it (:drag-over is gone).</summary>
        Leave,
        /// <summary>The button went up over a target that takes the kind: true from the handler accepts, false refuses (the drag then ends with a Cancel).</summary>
        Drop,
        /// <summary>The drag ended without a drop (Esc, released elsewhere or refused, source removed): goes to the handler named by the source's data-drag-name.</summary>
        Cancel,
    }

    /// <summary>One event of a drag between blocks. The machine reuses ONE instance: do not keep it, copy what you need.</summary>
    public sealed class UiDropEvent
    {
        public UiDropPhase Phase { get; internal set; }
        /// <summary>data-drag of the source: what is carried.</summary>
        public string Kind { get; internal set; }
        /// <summary>data-drag-value of the source (null when it has none).</summary>
        public string Value { get; internal set; }
        /// <summary>The element being dragged (the element with data-drag).</summary>
        public Element Source { get; internal set; }
        /// <summary>The element with data-drop under the pointer (null for a Cancel that came without a target).</summary>
        public Element Target { get; internal set; }
        /// <summary>The pointer on the screen, px.</summary>
        public float X { get; internal set; }
        public float Y { get; internal set; }
        /// <summary>The pointer in the content box of Target, in its own layout px (like UiMouseEvent.LocalX); the screen point when it has no box.</summary>
        public float LocalX { get; internal set; }
        public float LocalY { get; internal set; }
        public float Width { get; internal set; }
        public float Height { get; internal set; }
        public KeyMods Mods { get; internal set; }
        public double Time { get; internal set; }
        public bool Ctrl => (Mods & KeyMods.Ctrl) != 0;
        public bool Shift => (Mods & KeyMods.Shift) != 0;
        public bool Alt => (Mods & KeyMods.Alt) != 0;
    }

    /// <summary>What the drag machine needs from the window (the tests make it up).</summary>
    internal interface IDragHost
    {
        /// <summary>The element is in the page, shown, enabled and not covered by a dialog.</summary>
        bool Alive(Element e);
        /// <summary>The deepest element under the pointer (px, screen) that is not the source or inside it; and the source is put at the translate (dx, dy)
        /// afterwards (the ghost follows the pointer). Null: nothing is under.</summary>
        Element Under(Element source, float x, float y, float dx, float dy);
        /// <summary>The source goes back to where its CSS puts it.</summary>
        void Unghost(Element source);
        /// <summary>Set or clear a state bit (DragOver, Dragging) and restyle what a rule can see of it.</summary>
        void SetState(Element e, NodeState bit, bool on);
    }

    /// <summary>Drag and drop between blocks, free of the game (the tests run it). An element with data-drag="kind" is dragged with the left button; a
    /// target has data-drop="kind1 kind2" (or "*"), and data-drop-name="name" whose handler (Set) gets Enter, Over, Leave and Drop. A press on a draggable
    /// element (Press) that goes further than PointerRouter.DragThreshold px starts the drag, unless a data-mouse handler took that press (blocked) or the
    /// press was on a field. While it goes the target under the pointer (the source and its ghost excluded: IDragHost.Under) gets :drag-over if it
    /// takes the kind, the source has :dragging and follows the pointer by its numeric translate. Releasing over a target that takes the kind fires Drop
    /// (true from the handler accepts); every other end (Esc, release elsewhere, refused, the source or the target gone) is a Cancel to the handler named
    /// by the source's data-drag-name. The click of the pressed element is swallowed (Swallow) until the button is up. Allocates nothing in a steady frame.</summary>
    internal sealed class DragDrop
    {
        private readonly IDragHost host;
        private readonly Dictionary<string, Func<UiDropEvent, bool>> handlers = new();
        private readonly UiDropEvent ev = new();
        private Element pending, source, over;
        private float px, py, lastX, lastY;
        private string kind, value;
        private bool hasLast, swallowEnds, pendingCancel;
        private int dispatching;
        private double pendingTime;
        private PointerFrame lastF;

        public DragDrop(IDragHost host) { this.host = host; }

        /// <summary>Where elements are, for the local point of an event.</summary>
        public IPointerGeometry Geo;
        /// <summary>Called when a handler throws (it is removed); argument: its name.</summary>
        public Action<string, Exception> OnError;

        /// <summary>A drag is going (the source follows the pointer).</summary>
        public bool Active => source != null;
        public Element Source => source;
        /// <summary>The target under the pointer that takes the kind (it has :drag-over); null otherwise.</summary>
        public Element Over => over;
        /// <summary>Frames must be fed (Frame): a press waits for its threshold, a drag goes, or the click of one is still to be swallowed.</summary>
        public bool Busy => pending != null || source != null || Swallow;
        /// <summary>The element under a drag or a drag that has just ended is not clicked: true from the start of the drag until the frame of the button release
        /// (the window checks it before it sends a click).</summary>
        public bool Swallow { get; private set; }

        public bool Has(string name) => name != null && handlers.ContainsKey(name);

        /// <summary>The translate the ghost gets: the pointer's travel in screen px (dx, dy) divided by the scale of the blocks around the source (the translate is applied
        /// before that scale, so a scaled parent would otherwise move the ghost by more or less than the pointer), added to the source's CSS translate (baseX, baseY), so a
        /// card that has its own translate does not jump while it is carried. A parent scale that is not positive counts as 1.</summary>
        public static (float X, float Y) GhostShift(float dx, float dy, float parentScale, float baseX, float baseY)
        {
            float s = parentScale > 0 && float.IsFinite(parentScale) ? parentScale : 1;
            return (baseX + dx / s, baseY + dy / s);
        }

        /// <summary>Register (or replace) the handler of data-drop-name / data-drag-name = name; null removes it.</summary>
        public void Set(string name, Func<UiDropEvent, bool> handler)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            if (handler == null) { handlers.Remove(name); } else { handlers[name] = handler; }
        }

        /// <summary>The nearest element at or above e with a non-empty data-drag, unless a field (input, textarea, select, slot) is on the way (they own their
        /// mouse), or it is disabled; else null.</summary>
        public static Element DraggableOf(Element e)
        {
            for (; e != null; e = e.Parent)
            {
                if ((e.State & NodeState.Disabled) != 0 || e.GetAttribute("disabled") != null) { return null; }
                if (!string.IsNullOrEmpty(e.GetAttribute("data-drag"))) { return e; }
                if (e.Tag is "input" or "textarea" or "select" or "slot") { return null; }
            }
            return null;
        }

        /// <summary>Whether a data-drop list has the kind ("*" takes any).</summary>
        public static bool Accepts(string list, string kind)
        {
            if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(kind)) { return false; }
            int i = 0;
            while (i < list.Length)
            {
                while (i < list.Length && char.IsWhiteSpace(list[i])) { i++; }
                int s = i;
                while (i < list.Length && !char.IsWhiteSpace(list[i])) { i++; }
                int n = i - s;
                if (n == 1 && list[s] == '*') { return true; }
                if (n == kind.Length && n > 0 && string.CompareOrdinal(list, s, kind, 0, n) == 0) { return true; }
            }
            return false;
        }

        /// <summary>The target of a drop: the nearest element at or above e with data-drop that takes the kind and is not disabled; null when none does.</summary>
        public static Element TargetOf(Element e, string kind)
        {
            for (; e != null; e = e.Parent)
            {
                var list = e.GetAttribute("data-drop");
                if (list != null && Accepts(list, kind) && (e.State & NodeState.Disabled) == 0 && e.GetAttribute("disabled") == null) { return e; }
            }
            return null;
        }

        /// <summary>The left button went down on target (x, y px). A draggable element on its path arms the drag; false when there is none or a drag goes.</summary>
        public bool Press(Element target, float x, float y)
        {
            if (source != null) { return false; }
            pending = DraggableOf(target);
            px = x; py = y;
            return pending != null;
        }

        /// <summary>One frame while Busy. f.Over: the pointer is on this window (else nothing is under it). blocked: a data-mouse handler or a scroll bar holds
        /// the pointer, so the press is not a drag.</summary>
        public void Frame(in PointerFrame f, bool blocked)
        {
            lastF = f;
            bool held = (f.Held & MouseButtons.Left) != 0;
            if (Swallow && swallowEnds) { Swallow = swallowEnds = false; }
            if (pending != null)
            {
                if (!held || blocked || !host.Alive(pending)) { pending = null; return; }
                float dx = f.X - px, dy = f.Y - py;
                if (dx * dx + dy * dy <= PointerRouter.DragThreshold * PointerRouter.DragThreshold) { return; }
                Begin();
                hasLast = false;
            }
            if (Swallow && !held) { swallowEnds = true; }
            if (source == null) { return; }
            if (!host.Alive(source)) { End(false); Done(); return; }
            // the pointer's target: under it, the source and its ghost left out
            var under = host.Under(source, f.X, f.Y, f.X - px, f.Y - py);
            var tgt = f.Over ? TargetOf(under, kind) : null;
            if (over != null && !host.Alive(over))
            {
                // the target went away or was covered: it still gets its Leave and loses :drag-over
                var gone = over;
                over = null;
                host.SetState(gone, NodeState.DragOver, false);
                Fire(UiDropPhase.Leave, gone, f);
                if (source == null) { Done(); return; }
            }
            bool moved = !hasLast || f.X != lastX || f.Y != lastY;
            lastX = f.X; lastY = f.Y; hasLast = true;
            if (tgt != over)
            {
                var old = over;
                over = tgt;
                if (old != null) { host.SetState(old, NodeState.DragOver, false); Fire(UiDropPhase.Leave, old, f); }
                if (source != null && tgt != null && over == tgt) { host.SetState(tgt, NodeState.DragOver, true); Fire(UiDropPhase.Enter, tgt, f); }
            }
            else if (tgt != null && moved) { Fire(UiDropPhase.Over, tgt, f); }
            // a handler of this frame closed the window or cancelled the drag (a pending cancel): no Drop after that
            if (source != null && !held) { End(!pendingCancel); }
            Done();
        }

        /// <summary>Esc, a window closed or a dialog opened: the drag ends with a Cancel (Leave first when over a target). True when there was a drag.</summary>
        public bool Cancel(double time)
        {
            pending = null;
            if (source == null) { return false; }
            if (dispatching > 0) { pendingCancel = true; pendingTime = Math.Max(pendingTime, time); return true; }
            lastF.Time = time;
            End(false);
            return true;
        }

        private void Done()
        {
            if (pendingCancel && dispatching == 0) { pendingCancel = false; double t = pendingTime; pendingTime = 0; Cancel(t); }
        }

        private void Begin()
        {
            source = pending; pending = null;
            kind = source.GetAttribute("data-drag");
            value = source.GetAttribute("data-drag-value");
            Swallow = true; swallowEnds = false;
            host.SetState(source, NodeState.Dragging, true);
        }

        /// <summary>The drag is over. drop: the button went up, so a target that takes the kind gets the Drop.</summary>
        private void End(bool drop)
        {
            var src = source; var tgt = over;
            var f = lastF;
            source = null; over = null; pending = null;
            // the ghost goes first: a restyle that fails below must not leave the card shifted
            host.Unghost(src);
            host.SetState(src, NodeState.Dragging, false);
            if (tgt != null) { host.SetState(tgt, NodeState.DragOver, false); }
            bool accepted = false;
            if (drop && tgt != null) { accepted = Fire(UiDropPhase.Drop, tgt, f, src); }
            else if (tgt != null) { Fire(UiDropPhase.Leave, tgt, f, src); }
            if (!accepted)
            {
                var name = src.GetAttribute("data-drag-name");
                if (!string.IsNullOrEmpty(name) && handlers.TryGetValue(name, out var fn)) { Call(name, fn, UiDropPhase.Cancel, null, f, src); }
            }
            Done();
        }

        private bool Fire(UiDropPhase phase, Element target, in PointerFrame f, Element src = null)
        {
            var name = target.GetAttribute("data-drop-name");
            if (string.IsNullOrEmpty(name) || !handlers.TryGetValue(name, out var fn)) { return false; }
            return Call(name, fn, phase, target, f, src ?? source);
        }

        private bool Call(string name, Func<UiDropEvent, bool> fn, UiDropPhase phase, Element target, in PointerFrame f, Element src)
        {
            var v = ev;
            v.Phase = phase; v.Source = src; v.Target = target; v.X = f.X; v.Y = f.Y; v.Mods = f.Mods; v.Time = f.Time;
            // what was picked up when the drag began, not what the element says now (a list row of a stock may be bound to another item by then)
            v.Kind = src != null ? kind : null; v.Value = src != null ? value : null;
            v.LocalX = f.X; v.LocalY = f.Y; v.Width = v.Height = 0;
            if (target != null && Geo != null && Geo.Content(target, out float x, out float y, out float w, out float h, out var xf))
            {
                var back = xf.Inverse();
                v.LocalX = back.X(f.X) - x; v.LocalY = back.Y(f.Y) - y; v.Width = w; v.Height = h;
            }
            dispatching++;
            try { return fn(v); }
            catch (Exception ex) { handlers.Remove(name); OnError?.Invoke(name, ex); return false; }
            finally { dispatching--; }
        }
    }
}
