using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>Why a popup closed.</summary>
    public enum PopupCloseReason { Outside, Escape, Chosen, Code, Replaced }

    /// <summary>What a press did to the open popup (PopupState.Press).</summary>
    internal enum PopupPress
    {
        /// <summary>No popup open, or the press is inside it: goes on as usual.</summary>
        Pass,
        /// <summary>A left (or middle) press outside: the popup closed and the press goes nowhere (no focus change, no click on what is under).</summary>
        Swallowed,
        /// <summary>A right press outside: the popup closed and the press goes on, so a data-contextmenu under it opens its own menu.</summary>
        ClosedPass,
    }

    /// <summary>A rectangle on the screen in px.</summary>
    internal readonly struct PopupRect
    {
        public readonly float X, Y, W, H;
        public PopupRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public float Right => X + W;
        public float Bottom => Y + H;
    }

    /// <summary>Where a popup is put: the top-left corner and the side that was used ("" for a point; below, above, right, left for an anchor).</summary>
    internal readonly struct PopupPos
    {
        public readonly float X, Y;
        public readonly string Side;
        public PopupPos(float x, float y, string side) { X = x; Y = y; Side = side; }
    }

    /// <summary>Placement math of popups and context menus, free of the game. The popup always ends up inside [0,W]x[0,H] of the screen (when it is
    /// larger than the screen, its top-left corner is at 0).</summary>
    internal static class PopupPlace
    {
        /// <summary>The CSS the window adds before the page's own sheets: a data-popup block is hidden until it has the open attribute (!important, so a
        /// page rule like <c>#menu { display: flex }</c> cannot show it early), and an open one is positioned absolutely over the page.</summary>
        public const string Css = "[data-popup]:not([open]) { display: none !important; } [data-popup] { position: absolute; z-index: 1000; }";

        /// <summary>A menu at the pointer: its corner at (px, py); when it would leave the screen to the right or below it opens to the left or above
        /// the pointer, and when that does not fit either it is pushed back inside.</summary>
        public static PopupPos AtPoint(float px, float py, float w, float h, float sw, float sh)
        {
            float x = px, y = py;
            if (x + w > sw && px - w >= 0) { x = px - w; }
            if (y + h > sh && py - h >= 0) { y = py - h; }
            return new PopupPos(Clamp(x, w, sw), Clamp(y, h, sh), "");
        }

        /// <summary>A popup beside an anchor rectangle (below, above, right, left; anything else is below). Along that axis it flips to the opposite side when
        /// the preferred one does not fit, and when neither fits it takes the side with more room; across it, it aligns with the anchor's near edge, then its
        /// far edge, then is pushed inside the screen.</summary>
        public static PopupPos Beside(PopupRect a, float w, float h, float sw, float sh, string placement = "below")
        {
            string side = Normalize(placement);
            bool vertical = side is "below" or "above";
            float lo = vertical ? a.Y : a.X, hi = vertical ? a.Bottom : a.Right, size = vertical ? h : w, room = vertical ? sh : sw;
            bool first = side is "below" or "right";   // placed after the anchor
            float Pos(bool after) => after ? hi : lo - size;
            bool Fits(float v) => v >= 0 && v + size <= room;
            float along = Pos(first);
            if (!Fits(along))
            {
                float other = Pos(!first);
                if (Fits(other)) { first = !first; along = other; }
                else
                {
                    bool after = room - hi >= lo;
                    first = after; along = Pos(after);
                }
            }
            side = vertical ? (first ? "below" : "above") : (first ? "right" : "left");
            float across, acrossSize = vertical ? w : h, acrossRoom = vertical ? sw : sh;
            float near = vertical ? a.X : a.Y, far = vertical ? a.Right : a.Bottom;
            across = near;
            if (across + acrossSize > acrossRoom && far - acrossSize >= 0) { across = far - acrossSize; }
            along = Clamp(along, size, room);
            across = Clamp(across, acrossSize, acrossRoom);
            return vertical ? new PopupPos(across, along, side) : new PopupPos(along, across, side);
        }

        public static string Normalize(string placement) =>
            placement is "below" or "above" or "right" or "left" ? placement : "below";

        private static float Clamp(float v, float size, float room) => Math.Max(0, Math.Min(v, room - size));
    }

    /// <summary>Where a popup was asked to appear: at a point, or beside an element.</summary>
    public sealed class PopupRequest
    {
        public float X, Y;
        public Element Anchor;
        /// <summary>Null: a point (X, Y); else below/above/right/left of the anchor.</summary>
        public string Placement;
    }

    /// <summary>Rules of popups that need no layout: the pure choices of the window's glue. Items and choices are found from the markup.</summary>
    internal static class PopupRules
    {
        /// <summary>The nearest element from <c>down</c> up with data-contextmenu, when a right press that went down on <c>down</c> and up on <c>up</c> is a
        /// context menu: the pointer did not travel more than PointerRouter.DragThreshold, the release is inside the element of the press, and no
        /// SetMouse handler answered true for the ContextMenu event. Null: no menu.</summary>
        public static Element ContextCarrier(Element down, Element up, float distance, bool handlerAnswered)
        {
            if (handlerAnswered || down == null || up == null || distance > PointerRouter.DragThreshold) { return null; }
            bool inside = false;
            for (var e = up; e != null; e = e.Parent) { if (e == down) { inside = true; break; } }
            if (!inside) { return null; }
            for (var e = down; e != null; e = e.Parent)
            {
                if (!string.IsNullOrEmpty(e.GetAttribute("data-contextmenu"))) { return e; }
            }
            return null;
        }

        /// <summary>A click inside the popup (target, the deepest element) chooses an item, which closes the popup: an item is a button, a link with onclick,
        /// a data-item or anything with onclick between the target and the popup. data-keep-open on any element of that chain keeps it open. A click on the
        /// padding or on plain text of the popup does neither.</summary>
        public static bool IsChoice(Element target, Element popup)
        {
            bool item = false, inside = false;
            for (var e = target; e != null; e = e.Parent)
            {
                if (e == popup) { inside = true; break; }
                if (e.GetAttribute("data-keep-open") != null) { return false; }
                if (e.Tag == "button" || e.GetAttribute("data-item") != null || e.GetAttribute("onclick") != null) { item = true; }
            }
            return item && inside;
        }

        /// <summary>Is an item of the arrow-key list: a button, a link with onclick or a data-item, and usable.</summary>
        public static void Items(Element popup, Func<Element, bool> usable, List<Element> into)
        {
            into.Clear();
            foreach (var e in popup.Descendants())
            {
                if ((e.Tag == "button" || e.GetAttribute("data-item") != null || (e.Tag == "a" && e.GetAttribute("onclick") != null)) && usable(e)) { into.Add(e); }
            }
        }

        /// <summary>The item the key moves to: Up and Down step with a wrap at the ends, Home and End jump. Without a current item Down is the first and Up
        /// the last. Null: not a movement key or no items.</summary>
        public static Element Move(IReadOnlyList<Element> items, Element current, UiKey key)
        {
            int n = items.Count;
            if (n == 0) { return null; }
            int i = -1;
            for (int k = 0; k < n; k++) { if (items[k] == current) { i = k; break; } }
            switch (key)
            {
                case UiKey.ArrowDown: return items[i < 0 ? 0 : (i + 1) % n];
                case UiKey.ArrowUp: return items[i < 0 ? n - 1 : (i - 1 + n) % n];
                case UiKey.Home: return items[0];
                case UiKey.End: return items[n - 1];
                default: return null;
            }
        }
    }

    /// <summary>The one open popup of a window and what closes it. The window's glue shows and hides the element in response to <see cref="Closed"/> and
    /// to Show; this class decides. Only one popup is open at a time: showing another closes the first (reason Replaced); showing the open one again just
    /// moves it. There are no nested submenus.</summary>
    internal sealed class PopupState
    {
        public Element Open { get; private set; }
        /// <summary>Counts the times a popup was opened afresh (Show returned true): a click that closed and opened the same popup again has a new number.</summary>
        public int Serial { get; private set; }
        public Element Anchor { get; private set; }
        public PopupRequest Request { get; private set; }

        /// <summary>Raised after the popup is no longer Open (so a handler may show another one).</summary>
        public event Action<Element, PopupCloseReason> Closed;

        public bool IsOpen(Element popup) => popup != null && Open == popup;

        /// <summary>Makes this the open popup; the previous one, if another, closes with Replaced first. Returns true when it was not open before (it opened afresh);
        /// false when it was open already (it only moves).</summary>
        public bool Show(Element popup, PopupRequest request)
        {
            if (popup == null) { return false; }
            if (Open != null && Open != popup) { Close(PopupCloseReason.Replaced); }
            bool fresh = Open != popup;
            if (fresh) { Serial++; }
            Open = popup;
            Request = request;
            Anchor = request?.Anchor;
            return fresh;
        }

        /// <summary>Closes the open popup. False: none was open.</summary>
        public bool Close(PopupCloseReason reason)
        {
            if (Open == null) { return false; }
            var p = Open;
            Open = null; Anchor = null; Request = null;
            Closed?.Invoke(p, reason);
            return true;
        }

        /// <summary>A button went down on <c>target</c> (null: not on the window). Outside the popup it closes it: the left press is swallowed (a native menu
        /// does the same; the click does not reach what is under), a right press goes on so a context menu under the pointer can open at once.</summary>
        public PopupPress Press(Element target, bool leftOrMiddle)
        {
            if (Open == null) { return PopupPress.Pass; }
            for (var e = target; e != null; e = e.Parent) { if (e == Open) { return PopupPress.Pass; } }
            Close(PopupCloseReason.Outside);
            return leftOrMiddle ? PopupPress.Swallowed : PopupPress.ClosedPass;
        }

        /// <summary>Esc: closes the popup. True: there was one (the key is used).</summary>
        public bool Escape() => Close(PopupCloseReason.Escape);

        /// <summary>A click was delivered to <c>target</c> and its onclick ran. Closes the popup when it was a choice (PopupRules.IsChoice), if the
        /// <c>was</c> popup is still the open one (the handler may have shown another). serial: Serial as it was before the handler ran (-1: not checked); a popup
        /// the handler closed and opened again is a new one and stays open.</summary>
        public bool Clicked(Element target, Element was, int serial = -1)
        {
            if (Open == null || Open != was || (serial >= 0 && serial != Serial) || !PopupRules.IsChoice(target, Open)) { return false; }
            return Close(PopupCloseReason.Chosen);
        }
    }
}
