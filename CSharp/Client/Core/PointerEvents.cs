using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    public enum UiMouseKind
    {
        /// <summary>A button went down (bubbles; true from a handler takes the capture).</summary>
        Down,
        /// <summary>A button went up (bubbles; during a capture it goes to the holder wherever the pointer is).</summary>
        Up,
        /// <summary>The pointer moved (only to elements that ask for "move", and always to the capture holder).</summary>
        Move,
        /// <summary>The pointer came into the element or one of its descendants (does not bubble: every data-mouse element that was entered gets its own).</summary>
        Enter,
        /// <summary>The pointer left the element and all its descendants (does not bubble).</summary>
        Leave,
        /// <summary>The wheel turned (true from a handler means used: the page does not scroll).</summary>
        Wheel,
        /// <summary>Left Up on the element (or inside it) where the left Down was, without a drag in between.</summary>
        Click,
        /// <summary>The second click of a double click, right after its Click.</summary>
        DoubleClick,
        /// <summary>The same as Click for the right button.</summary>
        ContextMenu,
        /// <summary>The pointer rested on one element for HoverDelay seconds (a rich tooltip).</summary>
        Hover,
        /// <summary>The capture was taken away (Esc, window closed, holder gone or covered): no Up will come.</summary>
        Cancel,
    }

    /// <summary>Where elements are on the screen, asked by the pointer router (the window answers from its layout; the tests make it up).</summary>
    public interface IPointerGeometry
    {
        /// <summary>The content box of e in its own layout pixels (x, y, w, h) and the transform from those pixels to the screen
        /// (identity when nothing above it is scaled or moved). False when e has no box (hidden, not laid out).</summary>
        bool Content(Element e, out float x, out float y, out float w, out float h, out UiTransform toScreen);
    }

    /// <summary>One mouse event of an element with data-mouse. The router reuses ONE instance for every event of the window so a frame costs no
    /// allocations: do not keep the event (or its Path) after the handler returns, copy what you need. Current and the Local fields change from
    /// handler to handler while the event bubbles.</summary>
    public sealed class UiMouseEvent
    {
        private readonly List<Element> path;
        private readonly PointerRouter owner;

        internal UiMouseEvent(List<Element> path, PointerRouter owner) { this.path = path; this.owner = owner; }

        public UiMouseKind Kind { get; internal set; }
        /// <summary>The deepest element under the pointer (during a capture: the holder; for Click: the element of the Down).</summary>
        public Element Target { get; internal set; }
        /// <summary>The element whose handler is being called (goes up through the ancestors with data-mouse).</summary>
        public Element Current { get; internal set; }
        /// <summary>The button of a Down, Up, Click, DoubleClick or ContextMenu; None for the rest.</summary>
        public MouseButtons Button { get; internal set; }
        /// <summary>The buttons held after this event.</summary>
        public MouseButtons Held { get; internal set; }
        /// <summary>Pointer on the screen, px.</summary>
        public float X { get; internal set; }
        public float Y { get; internal set; }
        /// <summary>The pointer in the content box of Current, in its own layout px (the same px the canvas callbacks get); the screen point when Current has no box.</summary>
        public float LocalX { get; internal set; }
        public float LocalY { get; internal set; }
        /// <summary>The content box size of Current in its own px; 0 when it has no box.</summary>
        public float Width { get; internal set; }
        public float Height { get; internal set; }
        /// <summary>A Wheel's turn in notches (up positive).</summary>
        public float Wheel { get; internal set; }
        public KeyMods Mods { get; internal set; }
        /// <summary>1, 2 or 3 from the ClickCounter, for Down, Click, DoubleClick and ContextMenu.</summary>
        public int ClickCount { get; internal set; }
        public double Time { get; internal set; }
        /// <summary>A capture is held (after this event for an Up, before it for a Down).</summary>
        public bool Captured { get; internal set; }
        /// <summary>The pointer is over the capture holder now (true when nothing is captured).</summary>
        public bool Inside { get; internal set; }
        /// <summary>For Enter, the element the pointer came from; for Leave, the one it went to (null: outside the window).</summary>
        public Element Related { get; internal set; }
        /// <summary>The screen point where the button of this event went down.</summary>
        public float StartX { get; internal set; }
        public float StartY { get; internal set; }
        /// <summary>The pointer went further than PointerRouter.DragThreshold px from the Down.</summary>
        public bool Dragged { get; internal set; }
        /// <summary>While a capture holder or a drag's ghost holds the pointer: the element under the pointer with that element and everything in it left out
        /// (the window's hit test with it moved away, so pointer-events:none is skipped as usual). Null outside a capture and a drag, and when the window
        /// gives no hit test. Target stays what it was (the holder, or the element under the ghost).</summary>
        public Element Under { get; internal set; }
        /// <summary>Target and all its ancestors to the root, Target first. Reused: do not keep it.</summary>
        public IReadOnlyList<Element> Path => path;

        internal IPointerGeometry Geo;

        public bool Ctrl => (Mods & KeyMods.Ctrl) != 0;
        public bool Shift => (Mods & KeyMods.Shift) != 0;
        public bool Alt => (Mods & KeyMods.Alt) != 0;
        public bool Left => Button == MouseButtons.Left;
        public bool Right => Button == MouseButtons.Right;
        public bool Middle => Button == MouseButtons.Middle;

        /// <summary>The value of data-name on the nearest element of Path, Target first (a card's part: Data("part")); null when none has it.</summary>
        public string Data(string name)
        {
            string attr = "data-" + name;
            for (int i = 0; i < path.Count; i++)
            {
                var v = path[i].GetAttribute(attr);
                if (v != null) { return v; }
            }
            return null;
        }

        /// <summary>The nearest element of Path, Target first, that has the attribute (a card: Up("data-node")); null when none has it.</summary>
        public Element Up(string attr)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (path[i].GetAttribute(attr) != null) { return path[i]; }
            }
            return null;
        }

        /// <summary>The pointer in the content box of another element (the map's world), in that element's own px. False when it has no box.</summary>
        public bool ToLocal(Element el, out float x, out float y)
        {
            x = y = 0;
            if (Geo == null || el == null || !Geo.Content(el, out float bx, out float by, out _, out _, out var xf)) { return false; }
            var back = xf.Inverse();
            x = back.X(X) - bx; y = back.Y(Y) - by;
            return true;
        }

        /// <summary>The event as an old canvas handler wants it (local coordinates of Current). Move, Down, Up, Wheel, Leave and Cancel map 1:1,
        /// Enter becomes Move; Click, DoubleClick, ContextMenu and Hover have no canvas form and come out as Move: filter them first.</summary>
        public CanvasMouseEvent AsCanvas()
        {
            var k = Kind switch
            {
                UiMouseKind.Down => CanvasMouseKind.Down,
                UiMouseKind.Up => CanvasMouseKind.Up,
                UiMouseKind.Wheel => CanvasMouseKind.Wheel,
                UiMouseKind.Leave => CanvasMouseKind.Leave,
                UiMouseKind.Cancel => CanvasMouseKind.Cancel,
                _ => CanvasMouseKind.Move,
            };
            bool btn = k == CanvasMouseKind.Down || k == CanvasMouseKind.Up;
            return new CanvasMouseEvent(k, btn ? Button : MouseButtons.None, Held, LocalX, LocalY, Width, Height,
                k == CanvasMouseKind.Wheel ? Wheel : 0, Mods, Time, Captured, Inside);
        }

        /// <summary>Take the capture for Current now with the buttons held (a drag that starts after a threshold, not on the Down). No effect when no
        /// button is held or a canvas holds the pointer.</summary>
        public void Capture() => owner?.TakeCapture(this);
    }
}
