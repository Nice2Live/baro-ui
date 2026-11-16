using System;
using System.Collections.Generic;
using BaroUI.Core;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>The rounded overflow clip (Core/RoundedClip.cs, docs/ui/PERF.md, «Скругление по overflow»). A block with overflow hidden or auto or scroll on
    /// both axes and a border-radius whose subtree reaches a corner outside the arc keeps its children in a texture: the pre-pass (PrePass, once per frame,
    /// before anything of the frame is drawn to the back buffer, see BaroUi.PrePass) paints the children of the padding box into a RenderTarget2D of the
    /// padding box's size, multiplies the target by the inside of the arc in the four corners, and the paint then draws the target as one sprite where
    /// the children were. No render target is switched during the paint. The mask is baked into the target, so the paint needs no shader and no alpha
    /// blend on the back buffer. Hits are not changed: they are computed from the layout (Hit), not from the texture.
    /// The walk is the paint's own placement (ScreenMap.Place, sticky, scroll step), so the target lies where the paint draws the block; a block inside a
    /// scaled block is not made (it keeps the rectangular clip, as before), and its subtree counts as one rectangle for the blocks around it.</summary>
    internal sealed partial class Painter
    {
        /// <summary>A block that takes the rounded path this frame: its padding box, the children's offset (the paint's cdx, cdy: the scroll is in it), the
        /// opacity the children are painted with, and the padding box's radii.</summary>
        private struct RoundedBlock
        {
            public LayoutBox Box;
            public Element Element;
            public Rectangle Pad;
            public float Cdx, Cdy, Opacity;
            public CornerPx Corners;
        }

        /// <summary>Blocks in post-order (a block after the blocks inside it), the order the pre-pass renders them in: an inner block's target is ready before
        /// the outer block's children are painted into the outer target.</summary>
        private readonly List<RoundedBlock> rounded = new();
        /// <summary>The absolute rectangles (drawn coordinates) of every block the walk visited, in walk order. A block's subtree is the range after its own
        /// entry up to the end when its children are done.</summary>
        private readonly List<Rectangle> walked = new();
        /// <summary>The pre-pass that made the textures the paint uses: an element is in it when its target was drawn by pass passId.</summary>
        private readonly Dictionary<Element, int> roundedPass = new();
        private readonly List<Element> roundedStale = new();
        private TextureCache<Element> roundedTargets;
        private TextureCache<(int Rx, int Ry, int Corner)> roundedTiles;
        private int passId;
        /// <summary>True from the end of PrePass to the start of the next paint: the paint may draw the targets.</summary>
        private bool prePassReady;
        /// <summary>Inside a target: the children are drawn in the target's coordinates, the canvas records are moved to the screen by (passX, passY).</summary>
        private bool passing;
        private int passX, passY;
        private int overflowDiagnostics;
        /// <summary>The corner tiles of one mask (kept: no array per block); and the render target slots the pass reads (no array per frame).</summary>
        private readonly (Texture2D Tex, Rectangle At)[] maskTiles = new (Texture2D, Rectangle)[4];
        private readonly RenderTargetBinding[] renderTargetSlots = new RenderTargetBinding[4];

        private static readonly BlendState InsideMask = new()
        {
            ColorSourceBlend = Blend.Zero, ColorDestinationBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.SourceAlpha,
        };

        /// <summary>The composition of a target onto the screen: its colour is already times its alpha (the batch's convention, see TryDrawRounded).</summary>
        private static readonly BlendState TargetOver = new()
        {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };

        /// <summary>BAROUI_ROUNDED=0 turns the pre-pass off (every rounded block keeps the rectangular clip): the comparison of a before and an after picture.</summary>
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("BAROUI_ROUNDED") != "0";

        /// <summary>True when the paint may draw the rounded targets (the pre-pass of this frame ran for this window).</summary>
        internal bool PrePassReady => prePassReady;

        /// <summary>A canvas record of the paint: in a target it is moved to the screen (the target's origin in the drawn coordinates), else as it is.</summary>
        internal Rectangle Abs(Rectangle r) => passing ? new Rectangle(r.X + passX, r.Y + passY, r.Width, r.Height) : r;

        /// <summary>The start of a frame: walks the window and renders the children of every rounded block that needs the path, at most
        /// <see cref="RoundedClip.MaxPerFrame"/> of them. Runs with the window laid out (after its update) and before any drawing of the frame; restores the
        /// device's render target, viewport and scissor. Any exception leaves prePassReady false (the paint then clips as before).</summary>
        internal void PrePass(SpriteBatch batch, LayoutBox root)
        {
            prePassReady = false;
            if (!Enabled) { return; }
            passId++;
            rounded.Clear();
            walked.Clear();
            if (root == null) { DropStale(); return; }
            var gd = batch.GraphicsDevice;
            // a target already set by someone else: the pass would have to restore it, so it does not run this frame
            gd.GetRenderTargets(renderTargetSlots);
            var bindings = renderTargetSlots;
            if (bindings[0].RenderTarget != null) { DecorSpec.Tell("rounded overflow: a render target is set before the pre-pass; the rounded clip is off this frame"); return; }
            Walk(root, 0, 0, 1f);
            int done = 0, beyond = 0;
            for (int i = 0; i < rounded.Count; i++)
            {
                if (done >= RoundedClip.MaxPerFrame) { beyond++; continue; }
                if (Render(batch, rounded[i])) { roundedPass[rounded[i].Element] = passId; done++; }
            }
            if (beyond > 0 && overflowDiagnostics++ < 20)
            {
                DecorSpec.Tell($"rounded overflow: {beyond} more blocks than {RoundedClip.MaxPerFrame} in one frame keep the rectangular clip");
            }
            DropStale();
            prePassReady = true;
        }

        /// <summary>The walk of the paint: the same placement as PaintBoxCore and PaintBoxContent, nothing drawn. A block that needs the path is added after its
        /// children (post-order); the rectangles of the subtree decide it (RoundedClip.Reaches, the scroll step applied when the paint applies it).</summary>
        private void Walk(LayoutBox b, float dx, float dy, float opacity)
        {
            var e = b.Element;
            var s = e?.Style ?? b.Style;
            float fw = 0, fh = 0;
            if (e != null)
            {
                opacity *= Math.Clamp(s.GetNumber("opacity", 1), 0, 1);
                ScreenMap.Place(e, s, ref dx, ref dy, out fw, out fh);
                dy = StickyDy(e, b, dy);
            }
            if (opacity <= 0.002f) { return; }
            var rect = Snap(b.X + dx, b.Y + dy, Math.Max(0, b.W + fw), Math.Max(0, b.H + fh));
            walked.Add(rect);
            // a block in a scaled block is not walked: its whole rectangle counts for the blocks around it (the paint draws it inside their targets)
            if (e != null && UiTransform.ScaleOf(e, s) != 1) { return; }
            int start = walked.Count;
            float cdx = dx, cdy = dy;
            bool clips = NeedsClip(b);
            if (e != null && clips && Scroll != null && Scroll.TryGetValue(e, out float sy)) { cdy = ScreenMap.ScrollStep(cdy, sy); }
            if (b.Children.Count > 0)
            {
                foreach (var c in Order(b))
                {
                    if (Deferred.Count > 0 && c.Element != null && Deferred.Contains(c.Element)) { continue; }
                    Walk(c, cdx, cdy, opacity);
                }
            }
            if (e == null || !b.ClipX || !b.ClipY || walked.Count == start) { return; }
            var st = DecorSpec.Of(s);
            if (!st.HasRadius) { return; }
            var outer = CornerPx.From(st.Corners.Resolve(rect.Width, rect.Height), rect.Width, rect.Height);
            if (!outer.Any) { return; }
            var pad = Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top, b.W - b.Border.Horizontal, b.H - b.Border.Vertical);
            if (pad.Width <= 0 || pad.Height <= 0) { return; }
            var corners = RoundedClip.Inset(outer, BorderPx(b.Border.Left), BorderPx(b.Border.Top), BorderPx(b.Border.Right), BorderPx(b.Border.Bottom));
            bool reach = false;
            for (int i = start; i < walked.Count && !reach; i++)
            {
                var r = walked[i];
                reach = RoundedClip.Reaches(pad.Width, pad.Height, corners, r.X - pad.X, r.Y - pad.Y, r.Width, r.Height);
            }
            if (!reach) { return; }
            rounded.Add(new RoundedBlock { Box = b, Element = e, Pad = pad, Cdx = cdx, Cdy = cdy, Opacity = opacity, Corners = corners });
        }

        /// <summary>One block's target: drawn by the children in the target's coordinates, then the corners masked. False when the target or a corner tile
        /// could not be made (the block keeps the rectangular clip this frame).</summary>
        private bool Render(SpriteBatch batch, in RoundedBlock blk)
        {
            var gd = batch.GraphicsDevice;
            int w = blk.Pad.Width, h = blk.Pad.Height;
            if (!roundedTargets.TryGet(blk.Element, out var tex) || tex.Width != w || tex.Height != h)
            {
                tex = MakeTarget(gd, w, h);
                if (tex == null) { return false; }
                roundedTargets.Put(blk.Element, tex, (long)w * h * 4);
            }
            if (tex is not RenderTarget2D target) { return false; }
            var viewport = gd.Viewport;
            var scissor = gd.ScissorRectangle;
            bool ok = false;
            try
            {
                gd.SetRenderTarget(target);
                gd.Viewport = new Viewport(0, 0, w, h);
                gd.ScissorRectangle = new Rectangle(0, 0, w, h);
                gd.Clear(Color.Transparent);
                sb = batch;
                clip = scr = scrBase = new Rectangle(0, 0, w, h);
                clipped = false;
                m = UiTransform.Identity;
                CurrentXf = m;
                BatchMatrix = null;
                passing = true;
                passX = blk.Pad.X; passY = blk.Pad.Y;
                batch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);
                try
                {
                    foreach (var c in Order(blk.Box))
                    {
                        if (Deferred.Count > 0 && c.Element != null && Deferred.Contains(c.Element)) { continue; }
                        PaintBox(c, blk.Cdx - blk.Pad.X, blk.Cdy - blk.Pad.Y, blk.Opacity);
                    }
                }
                finally { batch.End(); }
                ok = MaskCorners(batch, gd, blk.Corners, w, h);
            }
            finally
            {
                passing = false;
                gd.SetRenderTarget(null);
                gd.Viewport = viewport;
                gd.ScissorRectangle = scissor;
                sb = null;
            }
            return ok;
        }

        /// <summary>Multiplies the target by the inside of the arc in each corner square: the tile's alpha is the inside coverage, the blend keeps the target's
        /// colour and alpha times the tile's alpha (Zero, SourceAlpha), so the edge is anti-aliased and the rest of the target is untouched.</summary>
        private bool MaskCorners(SpriteBatch batch, GraphicsDevice gd, in CornerPx c, int w, int h)
        {
            var tiles = maskTiles;
            int n = 0;
            if (c.TLx > 0 && c.TLy > 0) { if (!Tile(gd, 0, c.TLx, c.TLy, out var t)) { return false; } tiles[n++] = (t, new Rectangle(0, 0, c.TLx, c.TLy)); }
            if (c.TRx > 0 && c.TRy > 0) { if (!Tile(gd, 1, c.TRx, c.TRy, out var t)) { return false; } tiles[n++] = (t, new Rectangle(w - c.TRx, 0, c.TRx, c.TRy)); }
            if (c.BRx > 0 && c.BRy > 0) { if (!Tile(gd, 2, c.BRx, c.BRy, out var t)) { return false; } tiles[n++] = (t, new Rectangle(w - c.BRx, h - c.BRy, c.BRx, c.BRy)); }
            if (c.BLx > 0 && c.BLy > 0) { if (!Tile(gd, 3, c.BLx, c.BLy, out var t)) { return false; } tiles[n++] = (t, new Rectangle(0, h - c.BLy, c.BLx, c.BLy)); }
            if (n == 0) { return true; }
            batch.Begin(SpriteSortMode.Deferred, InsideMask, SamplerState.PointClamp, null, null);
            try
            {
                for (int i = 0; i < n; i++) { batch.Draw(tiles[i].Tex, tiles[i].At, Color.White); }
            }
            finally { batch.End(); }
            return true;
        }

        private bool Tile(GraphicsDevice gd, int corner, int rx, int ry, out Texture2D tex)
        {
            if (roundedTiles.TryGet((rx, ry, corner), out tex)) { return true; }
            try
            {
                var data = new Color[rx * ry];
                for (int y = 0; y < ry; y++)
                {
                    for (int x = 0; x < rx; x++) { data[y * rx + x] = new Color((byte)255, (byte)255, (byte)255, RoundedClip.TileAlpha(corner, rx, ry, x, y)); }
                }
                tex = new Texture2D(gd, rx, ry);
                tex.SetData(data);
            }
            catch (Exception ex)
            {
                DecorSpec.Tell($"rounded overflow: corner tile {rx}x{ry} failed: {ex.Message}");
                tex = null;
                return false;
            }
            roundedTiles.Put((rx, ry, corner), tex, (long)rx * ry * 4);
            return true;
        }

        private static RenderTarget2D MakeTarget(GraphicsDevice gd, int w, int h)
        {
            try { return new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None); }
            catch (Exception ex)
            {
                DecorSpec.Tell($"rounded overflow: target {w}x{h} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>The paint's draw of a block's target at its padding box, in place of the children; false when there is no target of this pass for it (the
        /// children are painted as before).</summary>
        private bool TryDrawRounded(LayoutBox b, Rectangle pad)
        {
            if (!prePassReady || b.Element == null || roundedPass.Count == 0) { return false; }
            if (!roundedPass.TryGetValue(b.Element, out int id) || id != passId) { return false; }
            if (!roundedTargets.TryGet(b.Element, out var tex) || tex.Width != pad.Width || tex.Height != pad.Height) { return false; }
            // the target holds what the batch's own blend left in it (colour times alpha, the batch's convention), so it is composed with the matching
            // premultiplied over; the batch's blend would multiply the colour by the alpha a second time
            sb.End();
            sb.Begin(SpriteSortMode.Deferred, TargetOver, GUI.SamplerState, null, GameMain.ScissorTestEnable, transformMatrix: BatchMatrix);
            sb.Draw(tex, pad, Color.White);
            sb.End();
            sb.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable, transformMatrix: BatchMatrix);
            return true;
        }

        /// <summary>Entries of blocks the last pass did not make: their targets go through the graveyard (the budget may keep them until then).</summary>
        private void DropStale()
        {
            roundedStale.Clear();
            foreach (var kv in roundedPass)
            {
                if (kv.Value != passId) { roundedStale.Add(kv.Key); }
            }
            foreach (var e in roundedStale)
            {
                roundedPass.Remove(e);
                roundedTargets.Drop(e);
            }
        }

        /// <summary>The window closed: targets and tiles go through the graveyard.</summary>
        private void DisposeRounded()
        {
            roundedTargets?.Clear();
            roundedTiles?.Clear();
            roundedPass.Clear();
            rounded.Clear();
            walked.Clear();
        }
    }
}
