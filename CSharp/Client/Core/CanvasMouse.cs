using System;

namespace BaroUI.Core
{
    [Flags]
    public enum MouseButtons { None = 0, Left = 1, Right = 2, Middle = 4 }

    [Flags]
    public enum KeyMods { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

    public enum CanvasMouseKind
    {
        /// <summary>The pointer moved over the canvas (the first one is the pointer entering it), or, while a button is held that went down on it, anywhere.</summary>
        Move,
        /// <summary>A button went down on the canvas (or, during a drag, anywhere: another button joins the drag).</summary>
        Down,
        /// <summary>A button that is part of the drag went up; also over the page when the pointer left the canvas.</summary>
        Up,
        /// <summary>The wheel turned over the canvas (or during a drag).</summary>
        Wheel,
        /// <summary>The pointer left the canvas with no button held (clear the hover).</summary>
        Leave,
        /// <summary>The drag was taken away (the window closed, the canvas went away or was covered by a dialog): no Up will come.</summary>
        Cancel,
    }

    /// <summary>One mouse event of a canvas (Window.SetCanvasMouse). Coordinates are pixels from the top-left of the canvas's content box: the same
    /// pixels the draw callback gets as its Rectangle, so (X, Y) is where to draw to mark the pointer; they run outside 0..Width and 0..Height
    /// during a drag. Double and triple clicks are the owner's: Time is when the event happened (seconds), ClickCounter counts by it.</summary>
    public readonly struct CanvasMouseEvent
    {
        public readonly CanvasMouseKind Kind;
        /// <summary>The button of a Down or Up; None for the other kinds.</summary>
        public readonly MouseButtons Button;
        /// <summary>The buttons held after this event.</summary>
        public readonly MouseButtons Held;
        public readonly float X, Y;
        /// <summary>The content box's size in pixels.</summary>
        public readonly float Width, Height;
        /// <summary>A Wheel's turn in notches (up positive); 0 for the other kinds.</summary>
        public readonly float Wheel;
        public readonly KeyMods Mods;
        public readonly double Time;
        /// <summary>The canvas holds the mouse: a button that went down on it is still down.</summary>
        public readonly bool Captured;
        /// <summary>The pointer is on the canvas now (false only during a drag outside it, and for Leave and Cancel).</summary>
        public readonly bool Inside;

        public CanvasMouseEvent(CanvasMouseKind kind, MouseButtons button, MouseButtons held, float x, float y, float width, float height,
            float wheel, KeyMods mods, double time, bool captured, bool inside)
        {
            Kind = kind; Button = button; Held = held; X = x; Y = y; Width = width; Height = height;
            Wheel = wheel; Mods = mods; Time = time; Captured = captured; Inside = inside;
        }

        public bool Ctrl => (Mods & KeyMods.Ctrl) != 0;
        public bool Shift => (Mods & KeyMods.Shift) != 0;
        public bool Alt => (Mods & KeyMods.Alt) != 0;
        public bool Left => Button == MouseButtons.Left;
        public bool Right => Button == MouseButtons.Right;
        public bool Middle => Button == MouseButtons.Middle;

        public override string ToString() => $"{Kind} {Button} ({X:0.#},{Y:0.#}) held={Held} mods={Mods}{(Captured ? " captured" : "")}{(Inside ? "" : " outside")}";
    }

    /// <summary>What the window reads from the mouse for one frame (the game's PlayerInput; the tests make it up).</summary>
    internal struct PointerFrame
    {
        /// <summary>Pointer on the screen, px.</summary>
        public float X, Y;
        /// <summary>Buttons down now.</summary>
        public MouseButtons Held;
        /// <summary>Buttons that went down since the last frame (the game's own edge, so the first frame after the window got the focus gives none).</summary>
        public MouseButtons Pressed;
        /// <summary>Wheel notches since the last frame, up positive.</summary>
        public float Wheel;
        public KeyMods Mods;
        /// <summary>Seconds, any steady clock.</summary>
        public double Time;
        /// <summary>The canvas is the element under the pointer: nothing covers it there, no dialog is over it, it is not disabled.</summary>
        public bool Over;
        /// <summary>The canvas's content box on the screen where it was last painted.</summary>
        public float RectX, RectY, RectW, RectH;
    }

    /// <summary>The mouse of one canvas: turns frames of pointer state into events. Nothing on a canvas is a click: the owner reads Down, Move and Up
    /// (and counts double clicks by Time). A button that goes down on the canvas and is answered with true captures the mouse: Move and Up keep coming
    /// until every captured button is up, wherever the pointer is. The handler's answer to a Wheel is "used": false lets the page scroll under it.
    /// Free of the game, so the tests run it.</summary>
    internal sealed class CanvasMouse
    {
        private MouseButtons captured;
        private bool wasOver, hasLast;
        private float lastX, lastY, lastW, lastH;

        public Func<CanvasMouseEvent, bool> Handler;

        public bool Captured => captured != MouseButtons.None;
        /// <summary>Pointer on the canvas at the last frame (a Leave is still to come when it is not any more).</summary>
        public bool Hovered => wasOver;
        /// <summary>The canvas needs frames: the pointer is over it or a drag is going.</summary>
        public bool Active => wasOver || captured != MouseButtons.None;

        private static readonly MouseButtons[] Each = { MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle };

        /// <summary>One frame. Returns true when the wheel was used here (the page must not scroll).</summary>
        public bool Update(in PointerFrame f)
        {
            if (Handler == null) { captured = MouseButtons.None; wasOver = false; return false; }
            float x = f.X - f.RectX, y = f.Y - f.RectY;
            bool moved = !hasLast || x != lastX || y != lastY;
            bool wheelUsed = false;
            lastW = f.RectW; lastH = f.RectH;
            if (captured == MouseButtons.None)
            {
                if (!f.Over)
                {
                    if (wasOver) { wasOver = false; Send(CanvasMouseKind.Leave, MouseButtons.None, f, x, y, false, 0, false); }
                    hasLast = false;
                    return false;
                }
                if (!wasOver || moved) { Send(CanvasMouseKind.Move, MouseButtons.None, f, x, y, false, 0, true); }
                wasOver = true;
                lastX = x; lastY = y; hasLast = true;
                foreach (var b in Each)
                {
                    if ((f.Pressed & b) != 0 && Send(CanvasMouseKind.Down, b, f, x, y, captured != MouseButtons.None, 0, true)) { captured |= b; }
                }
                if (f.Wheel != 0) { wheelUsed = Send(CanvasMouseKind.Wheel, MouseButtons.None, f, x, y, Captured, f.Wheel, true); }
                return wheelUsed;
            }
            // a drag: everything of the pointer goes here
            if (moved) { Send(CanvasMouseKind.Move, MouseButtons.None, f, x, y, true, 0, f.Over); }
            lastX = x; lastY = y; hasLast = true;
            foreach (var b in Each)
            {
                if ((f.Pressed & b) != 0)
                {
                    captured |= b;
                    Send(CanvasMouseKind.Down, b, f, x, y, true, 0, f.Over);
                }
            }
            if (f.Wheel != 0) { wheelUsed = Send(CanvasMouseKind.Wheel, MouseButtons.None, f, x, y, true, f.Wheel, f.Over); }
            foreach (var b in Each)
            {
                if ((captured & b) == 0 || (f.Held & b) != 0) { continue; }
                captured &= ~b;
                Send(CanvasMouseKind.Up, b, f, x, y, captured != MouseButtons.None, 0, f.Over);
            }
            if (captured == MouseButtons.None)
            {
                // the drag is over: the pointer is on the canvas or the canvas must forget it
                wasOver = f.Over;
                if (!f.Over) { Send(CanvasMouseKind.Leave, MouseButtons.None, f, x, y, false, 0, false); }
            }
            return wheelUsed;
        }

        /// <summary>The canvas lost the mouse for a reason that is no button going up (the window closed, the canvas was hidden or covered):
        /// a drag in progress ends with a Cancel, a hover with a Leave.</summary>
        public void Cancel(double time)
        {
            var was = captured;
            bool over = wasOver;
            captured = MouseButtons.None;
            wasOver = false;
            if (Handler == null) { return; }
            if (was != MouseButtons.None) { Handler(new CanvasMouseEvent(CanvasMouseKind.Cancel, MouseButtons.None, MouseButtons.None, lastX, lastY, lastW, lastH, 0, KeyMods.None, time, false, false)); }
            else if (over) { Handler(new CanvasMouseEvent(CanvasMouseKind.Leave, MouseButtons.None, MouseButtons.None, lastX, lastY, lastW, lastH, 0, KeyMods.None, time, false, false)); }
        }

        private bool Send(CanvasMouseKind kind, MouseButtons button, in PointerFrame f, float x, float y, bool captured, float wheel, bool inside) =>
            Handler(new CanvasMouseEvent(kind, button, f.Held, x, y, f.RectW, f.RectH, wheel, f.Mods, f.Time, captured, inside));
    }

    /// <summary>Counts clicks by time and place for an owner of a canvas (or of anything that gets Down events): 1 a single click, 2 a double one,
    /// 3 a triple one, then 1 again. A click that comes too late, too far or with another button starts again at 1.</summary>
    public sealed class ClickCounter
    {
        public double Interval = 0.4;
        public float Distance = 6;
        private MouseButtons button;
        private double time = double.NegativeInfinity;
        private float x, y;
        private int count;

        public int Count(MouseButtons b, float px, float py, double now)
        {
            bool again = count > 0 && b == button && now - time <= Interval && now >= time
                && Math.Abs(px - x) <= Distance && Math.Abs(py - y) <= Distance;
            count = again ? count % 3 + 1 : 1;
            button = b; time = now; x = px; y = py;
            return count;
        }

        public int Count(in CanvasMouseEvent e) => Count(e.Button, e.X, e.Y, e.Time);

        public void Reset() { count = 0; time = double.NegativeInfinity; }
    }
}
