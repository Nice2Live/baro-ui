using System;
using System.Collections.Generic;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;

namespace BaroUI.Render
{
    /// <summary>The canvases of a window's mouse (UiWindow.SetCanvasMouse, docs/ui/AUTHORING.md): keeps the id registry and where each canvas was last
    /// painted, reads the game's mouse once a frame and hands it to the window's PointerRouter, which runs the state machine (Core/PointerRouter.cs
    /// and Core/CanvasMouse.cs, which the tests run). This is the part that needs the game: PlayerInput and the painted rectangle. The registry and its
    /// lifetime (the keys of the charts and curves, the pruning of the entries of elements that left the document) are Core/CanvasRegistry.cs.</summary>
    internal sealed class UiCanvasInput
    {
        private sealed class Entry
        {
            public readonly string Id;
            /// <summary>A chart or a curve (keyed by its element, not by an id): dropped when its element leaves the document.</summary>
            public readonly bool Anonymous;
            public Func<CanvasMouseEvent, bool> Handler;
            /// <summary>The element and its content box as the canvas was last painted (PaintCanvas tells).</summary>
            public Element Element;
            public Rectangle Rect;
            /// <summary>The transform the canvas was painted under (a scaled block around it): the pointer is taken through its inverse, so Rect and the
            /// positions the handler gets are in the canvas's own pixels, the ones its draw callback works in.</summary>
            public UiTransform Xf = UiTransform.Identity;
            /// <summary>The element the router holds the handler on (it lags behind Element until the next Sync: a canvas re-created by data-if or data-for).</summary>
            public Element Routed;
            public Entry(string id) { Id = id; Anonymous = id.Length > 0 && id[0] == '#'; }
        }

        private readonly UiWindow w;
        private readonly CanvasRegistry<Element, Entry> canvases = new();
        /// <summary>The entries as an array the frame walks: a handler may register or remove canvases while it runs.</summary>
        private Entry[] list = Array.Empty<Entry>();
        private readonly List<Entry> dropped = new();
        private bool middle;

        internal UiCanvasInput(UiWindow window) { w = window; }

        /// <summary>Some canvas or data-mouse element holds the mouse (a drag is going): the window takes the whole screen's mouse, so the game under
        /// the pointer gets neither the move nor the release.</summary>
        public bool Captured => w.Router.HoldsPointer;

        public bool Has(string id) => canvases.Contains(id);

        /// <summary>Gives a canvas a mouse handler (null takes it away: a drag in progress is cancelled).</summary>
        public void Set(string id, Func<CanvasMouseEvent, bool> handler)
        {
            if (id == null) { return; }
            if (handler == null)
            {
                if (canvases.Remove(id, out var gone)) { Rebuild(); Unroute(gone); }
                return;
            }
            if (!canvases.TryGet(id, out var en))
            {
                // before the new entry exists: a new key is the moment the stale ones (a chart re-created by data-for) are let go
                PruneStale();
                en = new Entry(id);
                canvases.Put(id, en);
                Rebuild();
            }
            en.Handler = handler;
            if (en.Routed != null) { w.Router.SetCanvas(en.Routed, handler); }
        }

        /// <summary>The router dropped a canvas whose handler threw: forget its id too.</summary>
        internal void Dropped(string id)
        {
            if (id != null && canvases.Remove(id, out _)) { Rebuild(); }
        }

        private void Rebuild()
        {
            var next = new Entry[canvases.Count];
            canvases.CopyTo(next);
            list = next;
        }

        private void Unroute(Entry en)
        {
            var el = en.Routed;
            en.Routed = null;
            if (el != null) { w.Router.SetCanvas(el, null); }
        }

        /// <summary>Drops the anonymous canvases whose element is no longer in the window (a chart or a curve that data-for or data-if removed): the
        /// registry lets go of the element and of the handler, which holds it, and the router forgets the element.</summary>
        private void PruneStale()
        {
            if (canvases.Count == 0) { return; }
            dropped.Clear();
            canvases.Prune(en => en.Anonymous && (en.Element == null || !w.Attached(en.Element)), dropped);
            if (dropped.Count == 0) { return; }
            Rebuild();
            foreach (var en in dropped) { Unroute(en); }
        }

        /// <summary>The key a canvas-like element is registered under: its id, or (a chart or a curve has none) a key of its own, kept for the element
        /// (Core/CanvasRegistry.cs).</summary>
        internal string KeyOf(Element e) => canvases.KeyOf(e, e.Id);

        /// <summary>PaintCanvas: where the canvas is on screen now (its content box, the draw callback's rectangle).</summary>
        internal void Painted(Element e, Rectangle content, UiTransform xf)
        {
            if (canvases.Count == 0 || !canvases.TryGet(KeyOf(e), out var en)) { return; }
            en.Element = e;
            en.Rect = content;
            en.Xf = xf;
        }

        /// <summary>The painted record of a canvas with a mouse handler (the router's geometry for it); false for any other element.</summary>
        internal bool TryPainted(Element e, out Rectangle content, out UiTransform xf)
        {
            content = default; xf = UiTransform.Identity;
            if (canvases.Count == 0 || !canvases.TryGet(KeyOf(e), out var en) || en.Element != e) { return false; }
            content = en.Rect; xf = en.Xf;
            return true;
        }

        /// <summary>Once a frame from the window's update. target: the element under the pointer (null when the pointer is not on the window).
        /// blockPress: the primary press of this frame went to a scroll bar. True: a handler used the wheel.</summary>
        internal bool Frame(bool mouseOn, Element target, bool blockPress)
        {
            // canvases count even before the router has them: they are handed over by Sync, which runs in Step (a window with only SetCanvasMouse)
            if (canvases.Count == 0 && !w.Router.Any) { return false; }
            var f = Read();
            if (blockPress) { f.Pressed &= ~MouseButtons.Left; }
            return Step(f, mouseOn, target);
        }

        /// <summary>The frame's work for a pointer state that was not read from the game (the self test).</summary>
        internal bool Step(PointerFrame f, bool mouseOn, Element target)
        {
            var router = w.Router;
            if (canvases.Count == 0 && !router.Any) { return false; }
            Sync();
            f.Over = mouseOn;
            bool wheel = router.Update(f, target, w.PointerGeo, w.Drag.Source);
            if (router.Active) { Character.DisableControls = true; }
            return wheel;
        }

        /// <summary>Moves each handler to the element its canvas was last painted as: a data-if or data-for builds a new element with the same id.</summary>
        private void Sync()
        {
            var entries = list;
            for (int i = 0; i < entries.Length; i++)
            {
                var en = entries[i];
                if (en.Element == en.Routed) { continue; }
                var old = en.Routed;
                en.Routed = en.Element;
                if (old != null) { w.Router.SetCanvas(old, null); }
                // the Cancel to the old element may have thrown and removed this id (Dropped): then the new element is not given the dead handler
                if (!canvases.TryGet(en.Id, out var still) || still != en) { continue; }
                if (en.Element != null && en.Handler != null) { w.Router.SetCanvas(en.Element, en.Handler); }
            }
        }

        private PointerFrame Read()
        {
            var mp = PlayerInput.MousePosition;
            bool mid = PlayerInput.MidButtonHeld();
            var f = new PointerFrame { X = mp.X, Y = mp.Y, Time = Timing.TotalTime, Wheel = PlayerInput.ScrollWheelSpeed / 120f };
            if (PlayerInput.PrimaryMouseButtonHeld()) { f.Held |= MouseButtons.Left; }
            if (PlayerInput.SecondaryMouseButtonHeld()) { f.Held |= MouseButtons.Right; }
            if (mid) { f.Held |= MouseButtons.Middle; }
            if (PlayerInput.PrimaryMouseButtonDown()) { f.Pressed |= MouseButtons.Left; }
            if (PlayerInput.SecondaryMouseButtonDown()) { f.Pressed |= MouseButtons.Right; }
            if (mid && !middle) { f.Pressed |= MouseButtons.Middle; }
            middle = mid;
            if (PlayerInput.IsCtrlDown()) { f.Mods |= KeyMods.Ctrl; }
            if (PlayerInput.IsShiftDown()) { f.Mods |= KeyMods.Shift; }
            if (PlayerInput.IsAltDown()) { f.Mods |= KeyMods.Alt; }
            return f;
        }

        /// <summary>The window closed or was replaced: the canvases leave the router, so every canvas drag ends with a Cancel (the window cancels the rest).</summary>
        internal void Dispose()
        {
            var entries = list;
            canvases.Clear();
            list = Array.Empty<Entry>();
            foreach (var en in entries) { Unroute(en); }
        }
    }
}
