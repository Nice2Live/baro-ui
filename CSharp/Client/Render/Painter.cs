using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>Draws a laid-out box tree straight into the SpriteBatch (docs/ui/RECON.md, decision 1): background,
    /// borders, rounded corners (9-slice of generated masks), text, overflow clipping by scissor, scroll offsets and
    /// a scroll thumb. The same walk, in reverse, answers hit tests. Styles are read from the DOM at draw time, so a
    /// paint-only change (:hover colour) needs no new layout. Stage 5: controls (input, select, progress, item,
    /// canvas) get their content from UiControls; modal dialogs are skipped here and painted on top by it.</summary>
    internal sealed partial class Painter : IImageSizes
    {
        private readonly UiFonts fonts;
        /// <summary>The window's one texture budget (Core TextureLedger): every texture of the window is in it, the painter's caches and the pictures alike.</summary>
        private readonly TextureLedger ledger;
        /// <summary>Corner masks and corner tiles, in the budget (a texture the budget takes back goes through the graveyard and is made again when asked for).</summary>
        private readonly TextureCache<(int C, int R, int B)> masks;
        private readonly TextureCache<(int Rx, int Ry, int Bwx, int Bwy, int Part)> tiles;
        /// <summary>Masks and tiles whose texture could not be made or filled: the box is drawn with a plain white fill (square corners) from then on, said once in
        /// the log, instead of the whole paint failing every frame.</summary>
        private readonly HashSet<(int, int, int)> maskFailed = new();
        private readonly HashSet<(int, int, int, int, int)> tileFailed = new();
        /// <summary>Baked shadow tiles, gradient ramps, corners and areas (box-shadow, linear-gradient, radial-gradient); in the window's budget.</summary>
        private readonly UiDecorTextures decor;
        /// <summary>Picture files of url() in background-image, border-image-source and &lt;img src&gt;: the window's pictures (UiWindow.Images), so a file used in a
        /// canvas and in CSS is one texture.</summary>
        private readonly UiImages pics;
        /// <summary>The window's &lt;video&gt; elements (UiVideos): their frames are drawn here, their posters through pics.</summary>
        internal UiVideos Videos;
        private readonly BgTile[] bgTiles = new BgTile[1024];
        private readonly NineQuad[] nineQuads = new NineQuad[512];
        private LayoutBox paintRoot;
        private SpriteBatch sb;
        /// <summary>The clip in the coordinates the boxes are drawn in (layout space of the scaled block being painted). Outside scaled blocks the
        /// same as the device scissor; inside, scr is what the device holds (screen pixels) and SetScissorCore converts.</summary>
        private Rectangle clip;
        private bool clipped;
        // scale: a scaled block restarts the sprite batch with a matrix and paints its subtree in unscaled coordinates; m maps them to the screen
        private UiTransform m = UiTransform.Identity;
        private Rectangle scr, scrBase;   // the device scissor now; the one a clip inside the current scaled block may not leave
        /// <summary>The matrix the batch runs with now (null: none), for code that has to restart the batch inside a draw (UiImages), and the
        /// transform the same as a struct (UiFonts picks glyph sizes by it). Painting is single threaded; Paint resets both.</summary>
        internal static Matrix? BatchMatrix;
        internal static UiTransform CurrentXf = UiTransform.Identity;
        public Dictionary<Element, float> Scroll;
        public float UnitPx = 1;
        /// <summary>Paints control content and the overlay (dropdown, modal dialogs); null: plain boxes only.</summary>
        public UiControls Controls;
        /// <summary>Elements painted later on top (open modal dialogs): left out of the normal walk and its hit test.</summary>
        public readonly HashSet<Element> Deferred = new();

        /// <summary>The layout box of an element (the window's), for the sticky step: a sticky box is moved inside its scrolling ancestor (ScreenMap.StickyOffset),
        /// the same in paint and hit test, and the same as ScreenMap.Of gives.</summary>
        public Func<Element, LayoutBox> BoxOf;

        private float StickyDy(Element e, LayoutBox b, float dy)
        {
            if (!b.Sticky || BoxOf == null) { return dy; }
            // inside a rounded target dy is relative to the target's origin; the sticky step reads the screen (the layout's boxes and scroll), so it gets the
            // screen's dy. The offset it returns is a displacement, the same in both
            return dy + ScreenMap.StickyOffset(e, b, passing ? dy + passY : dy, BoxOf, ScrollOfElement, Deferred.Contains);
        }

        private float ScrollOfElement(Element e) => Scroll != null && Scroll.TryGetValue(e, out float v) ? v : 0;

        /// <summary>A painter of one window: its pictures are the window's (files), its textures are in the window's budget (budget).</summary>
        public Painter(UiFonts fonts, UiImages files, TextureLedger budget)
        {
            this.fonts = fonts; pics = files; ledger = budget;
            masks = new TextureCache<(int, int, int)>(budget);
            tiles = new TextureCache<(int, int, int, int, int)>(budget);
            decor = new UiDecorTextures(budget);
            roundedTargets = new TextureCache<Element>(budget);
            roundedTiles = new TextureCache<(int Rx, int Ry, int Corner)>(budget);
        }

        /// <summary>The folder of the page being painted, where a picture path of a stylesheet or of &lt;img src&gt; starts. The window may set it; else it is
        /// found among BaroUi.Windows by the root box (a page read from a file). Null: the page has no folder, its pictures cannot be found.</summary>
        public string PageDir;

        private LayoutBox lookedRoot;
        private double lookedAt;

        internal string PageFolder()
        {
            if (PageDir != null || paintRoot == null) { return PageDir; }
            // not found: do not walk the windows for every box with a picture, look again once a second
            double now = Timing.TotalTime;
            if (ReferenceEquals(lookedRoot, paintRoot) && now - lookedAt < 1.0) { return null; }
            lookedRoot = paintRoot; lookedAt = now;
            var all = BaroUi.Windows;
            for (int i = 0; i < all.Count; i++)
            {
                if (ReferenceEquals(all[i].Root, paintRoot) && all[i].SourcePath != null) { return PageDir = Path.GetDirectoryName(all[i].SourcePath); }
            }
            return null;
        }

        // typewriter text (the typewriter property): the characters left to show in the text being painted, +inf outside one
        private float typeLeft = float.PositiveInfinity;
        private readonly List<Element> typers = new();

        /// <summary>False shows typewriter texts in full at once (bbe_ui anim off).</summary>
        public Func<bool> TypewriterOn = () => true;

        /// <summary>A typewriter text is still coming out (as of the last paint).</summary>
        public bool IsTyping { get; private set; }

        /// <summary>Shows every typewriter text in full at once (the player pressed the confirm key).</summary>
        public void SkipTyping()
        {
            foreach (var e in typers) { e.Typing?.Skip(); }
            typers.Clear();
            IsTyping = false;
        }

        /// <summary>Self test only (stage 7): bytes allocated by each part of the paint while Probe is on —
        /// background, content (controls, item icons), text, scissor switches, overlay.</summary>
        internal static bool Probe;
        internal static readonly long[] ProbeBytes = new long[5];
        internal static readonly string[] ProbeNames = { "background", "content", "text", "scissor", "overlay" };
        private static long P0() => Probe ? GC.GetAllocatedBytesForCurrentThread() : 0;
        private static void P1(int part, long since) { if (Probe) { ProbeBytes[part] += GC.GetAllocatedBytesForCurrentThread() - since; } }

        // ---------------------------------------------------------------- paint

        public void Paint(SpriteBatch spriteBatch, LayoutBox root)
        {
            sb = spriteBatch;
            paintRoot = root;
            ledger.BeginFrame();
            decor.BeginFrame();
            var prev = sb.GraphicsDevice.ScissorRectangle;
            clip = scr = scrBase = prev;
            clipped = false;
            m = UiTransform.Identity;
            CurrentXf = m;
            BatchMatrix = null;
            IsTyping = false;
            typers.Clear();
            typeLeft = float.PositiveInfinity;
            try
            {
                PaintBox(root, 0, 0, 1f);
                long a = P0();
                Controls?.PaintOverlay(this);
                P1(4, a);
            }
            finally
            {
                m = UiTransform.Identity;   // a throw inside a scaled block: back to the plain batch
                CurrentXf = m;
                BatchMatrix = null;
                if (clipped) { SetScissor(prev); }
                sb = null;
                prePassReady = false;       // the targets of a pre-pass are used by one paint only
            }
        }

        private void PaintBox(LayoutBox b, float dx, float dy, float opacity)
        {
            // the root of a typewriter text: how much of it shows now is decided here, the text below draws up to that
            var e = b.Element;
            if (e?.Style is { } es && es.GetNumber("typewriter", 0) > 0 && !(e.Parent?.Style?.GetNumber("typewriter", 0) > 0) && TypewriterOn())
            {
                float saved = typeLeft;
                typeLeft = TypedChars(b, es);
                try { PaintBoxCore(b, dx, dy, opacity); }
                finally { typeLeft = saved; }
                return;
            }
            PaintBoxCore(b, dx, dy, opacity);
        }

        private float TypedChars(LayoutBox root, ComputedStyle s)
        {
            var e = root.Element;
            var state = e.Typing ??= new TypeState();
            ulong sig = TypeState.Seed;
            int total = 0;
            SumText(root, ref sig, ref total);
            int shown = state.Visible(sig, total, Timing.TotalTime, s.GetNumber("typewriter", 0));
            if (!state.Done) { IsTyping = true; typers.Add(e); }
            return shown == int.MaxValue ? float.PositiveInfinity : shown;
        }

        private static void SumText(LayoutBox b, ref ulong sig, ref int total)
        {
            foreach (var f in b.Text) { sig = TypeState.Mix(sig, f.Text); total += f.Text.Length + 1; }
            foreach (var c in b.Children) { SumText(c, ref sig, ref total); }
        }

        private void PaintBoxCore(LayoutBox b, float dx, float dy, float opacity)
        {
            var s = b.Element?.Style ?? b.Style;
            float fw = 0, fh = 0;   // a layout transition: the box is drawn at an animated size (the content is clipped to it)
            if (b.Element != null)
            {
                opacity *= Math.Clamp(s.GetNumber("opacity", 1), 0, 1);
                ScreenMap.Place(b.Element, s, ref dx, ref dy, out fw, out fh);   // the same step ScreenMap.Of walks, so a place on screen is what is painted
                dy = StickyDy(b.Element, b, dy);
            }
            if (opacity <= 0.002f) { return; }
            // scale: the block and everything in it are painted in the same unscaled coordinates under a matrix about the block's corner (after its translate)
            float sc = b.Element != null ? UiTransform.ScaleOf(b.Element, s) : 1;
            if (sc == 1) { PaintBoxAt(b, s, dx, dy, fw, fh, opacity); return; }
            var before = EnterScale(b, dx, dy, sc);
            try { PaintBoxAt(b, s, dx, dy, fw, fh, opacity); }
            finally { LeaveScale(before); }
        }

        private struct ScaleState { public UiTransform M; public Rectangle Clip, Scr, Base; }

        private ScaleState EnterScale(LayoutBox b, float dx, float dy, float sc)
        {
            var was = new ScaleState { M = m, Clip = clip, Scr = scr, Base = scrBase };
            m = m.After(ScreenMap.ScaleAbout(b, dx, dy, sc));
            scrBase = scr;
            // the visible part of the screen in this block's coordinates: boxes outside it are culled the way they are outside any clip
            var q = m.Inverse().MapRect(scr.X, scr.Y, scr.Width, scr.Height, outward: true);
            clip = new Rectangle(q.X, q.Y, q.W, q.H);
            Restart();
            return was;
        }

        private void LeaveScale(in ScaleState was)
        {
            m = was.M;
            clip = was.Clip;
            scr = was.Scr;
            scrBase = was.Base;
            Restart();
        }

        /// <summary>Begins the batch again with the current matrix and the device scissor as it is (the same Begin as SetScissorCore).</summary>
        private void Restart()
        {
            CurrentXf = m;
            BatchMatrix = m.IsIdentity ? null : new Matrix(m.S, 0, 0, 0, 0, m.S, 0, 0, 0, 0, 1, 0, m.Tx, m.Ty, 0, 1);
            sb.End();
            sb.GraphicsDevice.ScissorRectangle = scr;
            sb.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable, transformMatrix: BatchMatrix);
            clipped = true;
        }

        /// <summary>The box, its content and its children (PaintBoxContent), then its outline: the outline lies outside the box's own overflow clip (which is
        /// back to what the ancestors left by now) and over the box's content, and takes no room. Browsers paint it over the whole stacking context; here a
        /// later sibling can still cover it.</summary>
        private void PaintBoxAt(LayoutBox b, ComputedStyle s, float dx, float dy, float fw, float fh, float opacity)
        {
            PaintBoxContent(b, s, dx, dy, fw, fh, opacity);
            if (b.Element == null || !DecorSpec.Of(s).HasOutline) { return; }
            var o = OutlineSpec.Of(s);
            if (o.Visible && s.Get("visibility") == "visible") { PaintOutline(b, s, o, dx, dy, fw, fh, opacity); }
        }

        private void PaintOutline(LayoutBox b, ComputedStyle s, in OutlineSpec o, float dx, float dy, float fw, float fh, float opacity)
        {
            var rect = Snap(b.X + dx, b.Y + dy, Math.Max(0, b.W + fw), Math.Max(0, b.H + fh));
            var ring = OutlineGeometry.Of(rect.X, rect.Y, rect.Width, rect.Height, Radius(s, rect), o.Width, o.Offset);
            if (ring.Empty) { return; }
            var outer = new Rectangle(ring.X, ring.Y, ring.W, ring.H);
            if (!outer.Intersects(clip)) { return; }
            var color = ToColor(o.Color, opacity);
            // only the solid line is drawn (dashed and dotted take the same ring); a ring wider than half the box is a plain fill
            if (ring.Solid) { FillRounded(outer, ring.Radius, color); }
            else { StrokeRounded(outer, ring.Radius, ring.Width, color); }
        }

        private void PaintBoxContent(LayoutBox b, ComputedStyle s, float dx, float dy, float fw, float fh, float opacity)
        {
            var rect = Snap(b.X + dx, b.Y + dy, Math.Max(0, b.W + fw), Math.Max(0, b.H + fh));
            bool visible = s.Get("visibility") == "visible";
            if (visible && b.Element != null && rect.Intersects(clip))
            {
                long a = P0();
                PaintBackground(b, s, rect, opacity);
                if (b.Element.Tag == "img") { PaintImgElement(b, s, rect, opacity); }
                else if (b.Element.Tag == "video") { PaintVideoElement(b, s, rect, opacity); }
                P1(0, a);
                if (Controls != null && UiControls.HasContent(b.Element))
                {
                    a = P0();
                    Controls.PaintContent(this, b, s, rect, opacity);
                    P1(1, a);
                }
            }
            else if (visible && b.Element != null) { PaintShadowNear(s, rect, opacity); }
            var outer = clip;
            bool flipClip = false;
            if (fw != 0 || fh != 0)
            {
                var fpad = Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top,
                    Math.Max(0, b.W + fw - b.Border.Horizontal), Math.Max(0, b.H + fh - b.Border.Vertical));
                if (!PushClip(fpad, out _)) { return; }
                flipClip = true;
            }
            if (b.Text.Count > 0)
            {
                long a = P0();
                foreach (var f in b.Text)
                {
                    // a typewriter text: the fragments are counted in order, the one in the middle is cut
                    string text = f.Text;
                    if (typeLeft != float.PositiveInfinity)
                    {
                        float left = typeLeft;
                        typeLeft -= text.Length + 1;
                        if (left <= 0) { continue; }
                        if (left < text.Length) { text = text.Substring(0, (int)left); }
                    }
                    var fs = f.Owner?.Style ?? f.Style;
                    if (fs.Get("visibility") != "visible") { continue; }
                    if (f.Y + dy > clip.Bottom || f.Y + f.Height + dy < clip.Top) { continue; }
                    var c = fs.GetColor("color");
                    if (c.A == 0) { continue; }
                    var shadow = TextShadow.Of(fs);
                    var spacing = TextSpacing.Of(fs);
                    // no colour written: the shadow has the colour the text has in this frame
                    var shadowColor = shadow.CurrentColor ? c : shadow.Color;
                    // one lookup per fragment says whether any line reaches it (TextDecor.InScope); the walk up the tree is only for those that do
                    bool lines = TextDecor.Seen && TextDecor.InScope(f.Owner);
                    // the shadow is the text again, without the decoration; the decoration lies under the text (underline, overline) or through it (line-through)
                    if (shadow.Active && shadowColor.A > 0) { DrawText(text, f.X + dx + shadow.X, f.Baseline + dy + shadow.Y, ToColor(shadowColor, opacity), fs, spacing); }
                    if (lines) { PaintDecor(f, fs, text, dx, dy, opacity, through: false); }
                    DrawText(text, f.X + dx, f.Baseline + dy, ToColor(c, opacity), fs, spacing);
                    if (lines) { PaintDecor(f, fs, text, dx, dy, opacity, through: true); }
                }
                P1(2, a);
            }
            if (b.Children.Count == 0) { if (flipClip) { PopClip(outer); } return; }

            var saved = clip;
            float cdx = dx, cdy = dy;
            if (NeedsClip(b))
            {
                var pad = Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top,
                    b.W - b.Border.Horizontal, b.H - b.Border.Vertical);
                var next = Rectangle.Intersect(clip, new Rectangle(
                    b.ClipX ? pad.X : clip.X, b.ClipY ? pad.Y : clip.Y,
                    b.ClipX ? pad.Width : clip.Width, b.ClipY ? pad.Height : clip.Height));
                if (next.Width <= 0 || next.Height <= 0) { if (flipClip) { PopClip(outer); } return; }
                SetScissor(next);
                if (b.Element != null && Scroll != null && Scroll.TryGetValue(b.Element, out float sy)) { cdy = ScreenMap.ScrollStep(cdy, sy); }
            }
            // a block with a rounded overflow clip: its children are in the target the pre-pass made (RoundedPass.cs); the scroll bar is drawn over it below
            bool inTarget = b.Element != null && b.ClipX && b.ClipY && TryDrawRounded(b, Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top,
                b.W - b.Border.Horizontal, b.H - b.Border.Vertical));
            if (!inTarget)
            {
                foreach (var c in Order(b))
                {
                    if (Deferred.Count > 0 && c.Element != null && Deferred.Contains(c.Element)) { continue; }
                    PaintBox(c, cdx, cdy, opacity);
                }
            }
            if (b.ClipY && visible) { PaintScrollbar(b, s, dx, dy, opacity); }
            if (clip != saved) { SetScissor(saved); }
            if (flipClip) { PopClip(outer); }
        }

        /// <summary>One text fragment: with letter-spacing or word-spacing character by character (UiFonts.DrawSpaced), else as one string.</summary>
        private void DrawText(string text, float x, float baseline, Color color, ComputedStyle fs, TextSpacing spacing)
        {
            if (spacing.Any) { fonts.DrawSpaced(sb, text, x, baseline, color, fs, spacing.Letter, spacing.Word); }
            else { fonts.Draw(sb, text, x, baseline, color, fs); }
        }

        /// <summary>The text-decoration lines of a fragment: those of its owner and of every ancestor up to an atomic inline or a positioned-out box
        /// (Core/TextDecor.cs), each in its own element's colour and thickness, over the width of the fragment (the spaces inside a merged fragment are
        /// covered, the gap between two fragments of different elements is not). through: only line-through, else underline and overline. The geometry
        /// comes from the fragment's own font (ascent, descent, size).</summary>
        private void PaintDecor(TextFragment f, ComputedStyle fs, string text, float dx, float dy, float opacity, bool through)
        {
            float width = -1;
            for (var e = f.Owner; e != null; e = e.Parent)
            {
                var st = e.Style;
                if (st == null) { break; }
                var d = TextDecor.Of(st);
                if (d.Any && (through ? d.LineThrough : d.Underline || d.Overline))
                {
                    if (width < 0) { width = ReferenceEquals(text, f.Text) ? f.Width : fonts.Width(text, fs) + TextSpacing.Of(fs).Extra(text); }
                    var color = ToColor(d.Color, opacity);
                    float asc = fonts.Ascent(fs), desc = fonts.Descent(fs), size = fs.FontSizePx;
                    if (through) { Line('l'); }
                    else
                    {
                        if (d.Underline) { Line('u'); }
                        if (d.Overline) { Line('o'); }
                    }
                    void Line(char kind)
                    {
                        var (top, h) = TextDecor.Line(kind, asc, desc, size, d.Thickness);
                        FillLine(f.X + dx, width, f.Baseline + dy, top, h, color);
                    }
                }
                if (TextDecor.Stops(st)) { break; }
            }
        }

        /// <summary>A horizontal line of whole screen pixels: x..x+w in layout px, its top row top px below the (rounded) baseline, h px high. In a scaled
        /// block the edges are snapped on the screen, as the text is, and the line is drawn as a stretched white texel.</summary>
        private void FillLine(float x, float w, float baseline, int top, int h, Color c)
        {
            if (w <= 0 || c.A == 0) { return; }
            var xf = CurrentXf;
            if (xf.S == 1) { FillRect(Snap(x, (float)Math.Round(baseline) + top, w, h), c); return; }
            int x0 = (int)Math.Round(xf.X(x)), x1 = (int)Math.Round(xf.X(x + w));
            int y0 = (int)Math.Round(xf.Y(baseline)) + (int)Math.Round(top * xf.S), hh = Math.Max(1, (int)Math.Round(h * xf.S));
            if (x1 <= x0) { return; }
            var back = xf.Inverse();
            var tex = GUI.WhiteTexture;
            sb.Draw(tex, new Vector2(back.X(x0), back.Y(y0)), null, c, 0f, Vector2.Zero,
                new Vector2((x1 - x0) / (xf.S * tex.Width), hh / (xf.S * tex.Height)), SpriteEffects.None, 0f);
        }

        private void PaintBackground(LayoutBox b, ComputedStyle s, Rectangle r, float opacity)
        {
            var bg = s.GetColor("background-color");
            int bt = BorderPx(b.Border.Top), br = BorderPx(b.Border.Right), bb = BorderPx(b.Border.Bottom), bl = BorderPx(b.Border.Left);
            // one cached spec per style (shadow, gradient, corners, picture flag): a box with none of them costs this one lookup
            var decorSpec = DecorSpec.Of(s);
            var cp = decorSpec.HasRadius ? CornerPx.From(decorSpec.Corners.Resolve(r.Width, r.Height), r.Width, r.Height) : default;
            int radius = cp.Single;   // the shadow takes one radius: the largest corner's smaller semi-axis (the gradient follows every corner)
            if (decorSpec.HasShadow) { PaintShadow(s, decorSpec, r, radius, opacity); }
            string line = bt + br + bb + bl > 0 ? s.Get("border-style") : "solid";
            // url() pictures: the background (inside the borders) and the nine-slice border; the text is looked at first so a plain box pays two lookups
            PicSpec pic = null;
            if (decorSpec.HasPicture) { pic = PicSpec.Of(s, PageFolder()); }
            if (cp.Any)
            {
                if (bg.A > 0)
                {
                    if (cp.Circular) { Draw9(Mask(radius, 0), r, ToColor(bg, opacity)); }
                    else { FillCorners(r, cp, ToColor(bg, opacity)); }
                }
                if (decorSpec.Grads != null) { PaintGradients(s, decorSpec.Grads, r, cp, opacity); }
                if (pic != null && pic.Bg != null) { PaintBgPicture(pic, s, r, bt, br, bb, bl, opacity); }
                if (pic == null || pic.Border == null || !PaintNineSlice(pic, s, r, bt, br, bb, bl, opacity)) { RoundedBorder(s, r, cp, bt, br, bb, bl, line, opacity); }
                return;
            }
            if (bg.A > 0) { sb.Draw(GUI.WhiteTexture, r, ToColor(bg, opacity)); }
            if (decorSpec.Grads != null) { PaintGradients(s, decorSpec.Grads, r, default, opacity); }
            if (pic != null)
            {
                if (pic.Bg != null) { PaintBgPicture(pic, s, r, bt, br, bb, bl, opacity); }
                if (pic.Border != null && PaintNineSlice(pic, s, r, bt, br, bb, bl, opacity)) { return; }
            }
            if (line is "dashed" or "dotted")
            {
                DashedSides(s, r, 0, bt, br, bb, bl, line == "dotted", opacity);
                return;
            }
            if (bt > 0) { Fill(r.X, r.Y, r.Width, bt, s.GetColor("border-top-color"), opacity); }
            if (bb > 0) { Fill(r.X, r.Bottom - bb, r.Width, bb, s.GetColor("border-bottom-color"), opacity); }
            if (bl > 0) { Fill(r.X, r.Y + bt, bl, r.Height - bt - bb, s.GetColor("border-left-color"), opacity); }
            if (br > 0) { Fill(r.Right - br, r.Y + bt, br, r.Height - bt - bb, s.GetColor("border-right-color"), opacity); }
        }

        /// <summary>Texture pixels per layout pixel here: the scale of the block being painted (the batch's matrix), 1 outside scaled blocks.</summary>
        private float TexScale => m.IsIdentity ? 1f : m.S;

        /// <summary>A box just outside the clip whose shadow reaches in (the box itself is culled): draws the shadow alone. The bound is cheap (offset, spread
        /// and blur reach in layout px), so a box far away costs a few additions.</summary>
        private void PaintShadowNear(ComputedStyle s, Rectangle r, float opacity)
        {
            var d = DecorSpec.Of(s);
            if (!d.HasShadow) { return; }
            var sh = d.Shadow.Resolve(UnitPx, s.FontSizePx);
            int bound = (int)MathF.Ceiling(Math.Max(MathF.Abs(sh.X), MathF.Abs(sh.Y)) + Math.Max(0, sh.Spread) + 1.5f * sh.Blur + 3);
            if (!new Rectangle(r.X - bound, r.Y - bound, r.Width + 2 * bound, r.Height + 2 * bound).Intersects(clip)) { return; }
            var cp = d.HasRadius ? CornerPx.From(d.Corners.Resolve(r.Width, r.Height), r.Width, r.Height) : default;
            PaintShadow(s, d, r, cp.Single, opacity);
        }

        /// <summary>The outer box-shadow, drawn before the background: a baked white tile (blurred rounded square) cut in 9 and tinted with the shadow colour.
        /// Radius, blur and spread are whole texture pixels at the nearest STEP of the scale (Core DecorScale, 1/8 octave), so a smooth zoom keeps asking for
        /// the same few tiles and the drawn pieces stretch the step's texture by 4.4 % at most. A box too small for the tile gets a shadow baked for its own
        /// size. At most two shadows are baked per frame: a shadow whose bake waits is drawn from the nearest step that has one, or left out for that frame.
        /// Not cut out under the box (a translucent box shows its shadow through).</summary>
        private void PaintShadow(ComputedStyle s, DecorSpec d, Rectangle r, int radius, float opacity)
        {
            var sh = d.Shadow.Resolve(UnitPx, s.FontSizePx);
            var color = d.Shadow.HasColor ? d.Shadow.Color : s.GetColor("color");
            if (color.A == 0) { return; }
            int step = DecorScale.Step(TexScale);
            var plan = ShadowRaster.Plan(radius, sh.Blur, sh.Spread, DecorScale.Scale(step));
            float shapeX = r.X + sh.X - sh.Spread, shapeY = r.Y + sh.Y - sh.Spread, shapeW = r.Width + 2 * sh.Spread, shapeH = r.Height + 2 * sh.Spread;
            if (shapeW <= 0 || shapeH <= 0) { return; }
            float reach = plan.MarginLayout;
            var outer = new Rectangle((int)MathF.Floor(shapeX - reach), (int)MathF.Floor(shapeY - reach), (int)MathF.Ceiling(shapeW + 2 * reach) + 1, (int)MathF.Ceiling(shapeH + 2 * reach) + 1);
            if (!outer.Intersects(clip)) { return; }
            var tint = ToColor(color, opacity);
            if (tint.A == 0) { return; }
            if (plan.CanSlice(shapeW, shapeH))
            {
                var tex = decor.ShadowTile(sb.GraphicsDevice, radius, sh.Blur, sh.Spread, step, shapeW, shapeH, out var used);
                if (tex == null) { return; }
                Span<DecorSlice> slices = stackalloc DecorSlice[9];
                int n = ShadowSlices.Fill(slices, used, shapeX, shapeY, shapeW, shapeH);
                for (int i = 0; i < n; i++)
                {
                    var q = slices[i];
                    sb.Draw(tex, new Rectangle(q.DX, q.DY, q.DW, q.DH), new Rectangle(q.SX, q.SY, q.SW, q.SH), tint);
                }
                return;
            }
            var one = decor.ShadowOneOff(sb.GraphicsDevice, radius, sh.Blur, sh.Spread, step, shapeW, shapeH, out var usedOne);
            if (one == null) { return; }
            float m = usedOne.MarginLayout;
            int x0 = (int)MathF.Round(shapeX - m), y0 = (int)MathF.Round(shapeY - m);
            sb.Draw(one, new Rectangle(x0, y0, (int)MathF.Round(shapeX + shapeW + m) - x0, (int)MathF.Round(shapeY + shapeH + m) - y0), tint);
        }

        /// <summary>The radius of corner i (0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left) of a box whose corners differ.</summary>
        private static void CornerOf(in CornerPx cp, int i, out int rx, out int ry)
        {
            switch (i)
            {
                case 0: rx = cp.TLx; ry = cp.TLy; break;
                case 1: rx = cp.TRx; ry = cp.TRy; break;
                case 2: rx = cp.BRx; ry = cp.BRy; break;
                default: rx = cp.BLx; ry = cp.BLy; break;
            }
        }

        /// <summary>The gradient layers of background-image, over the border box and under the borders: the last listed is painted first, so the first one is on top
        /// (CSS). A layer on an axis is a ramp (PaintGradient); an angle, a corner or a radial layer is an area (PaintGradientArea).</summary>
        private void PaintGradients(ComputedStyle s, Gradient[] layers, Rectangle r, in CornerPx cp, float opacity)
        {
            for (int i = layers.Length - 1; i >= 0; i--)
            {
                if (layers[i].NeedsArea) { PaintGradientArea(s, layers[i], r, cp, opacity); }
                else { PaintGradient(s, layers[i], r, cp, opacity); }
            }
        }

        /// <summary>A gradient drawn per pixel (Core GradientArea): the box as one texture at the nearest step of the scale, with the rounded corners in its alpha,
        /// drawn as one quad. The texture is keyed by its texels and step (a zoom reuses it; the stretch is 4.4 % at most), its corner radii scaled the same way.</summary>
        private void PaintGradientArea(ComputedStyle s, Gradient g, Rectangle r, in CornerPx cp, float opacity)
        {
            if (r.Width <= 0 || r.Height <= 0) { return; }
            var tint = PicTint(opacity);
            if (tint.A == 0) { return; }
            int step = DecorScale.Step(TexScale);
            float scale = DecorScale.Scale(step);
            int tw = AreaSide(r.Width, scale), th = AreaSide(r.Height, scale);
            float kx = tw / (float)r.Width, ky = th / (float)r.Height;
            var rt = new CornerPx(
                (int)MathF.Round(cp.TLx * kx), (int)MathF.Round(cp.TLy * ky), (int)MathF.Round(cp.TRx * kx), (int)MathF.Round(cp.TRy * ky),
                (int)MathF.Round(cp.BRx * kx), (int)MathF.Round(cp.BRy * ky), (int)MathF.Round(cp.BLx * kx), (int)MathF.Round(cp.BLy * ky));
            var tex = decor.Area(sb.GraphicsDevice, g, tw, th, step, rt, UnitPx, s.FontSizePx);
            if (tex != null) { sb.Draw(tex, r, tint); }
        }

        /// <summary>A side of an area texture: the layout side times the scale, on the length ladder, at least 1 and at most GradientArea.MaxSide.</summary>
        private static int AreaSide(float layout, float scale) => Math.Clamp(DecorScale.Length((int)MathF.Round(layout * scale)), 1, GradientArea.MaxSide);

        /// <summary>A linear-gradient in background-image over the border box (after the background colour, before the borders): a ramp texture stretched
        /// along the axis (padded by one texel, so the wrapping sampler never mixes the two ends). With equal circular corners, seven sprites: three ramp
        /// parts and four baked corners (ramp colour times the quarter-circle coverage). With corners that differ, up to eleven: a middle band, a top and a
        /// bottom band, and in each band every corner as its own baked elliptical tile with a ramp strip beside it (Core GradientRounded.FillCorners), so
        /// the gradient follows every corner exactly. Radii above the cap, or corners that reach past each other across the diagonal, draw square corners.
        /// The ramp and corners are keyed at the nearest step of the scale (the pieces stretch it by 4.4 % at most); the corner length is on a ladder too.</summary>
        private void PaintGradient(ComputedStyle s, Gradient g, Rectangle r, in CornerPx cp, float opacity)
        {
            if (r.Width <= 0 || r.Height <= 0) { return; }
            float scale = DecorScale.Snap(TexScale), len = g.Vertical ? r.Height : r.Width;
            var key = g.Key(len, scale);
            var ramp = decor.Ramp(sb.GraphicsDevice, g, key, len, scale, UnitPx, s.FontSizePx);
            if (ramp == null) { return; }
            var tint = new Color((byte)255, (byte)255, (byte)255, (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255));
            if (tint.A == 0) { return; }
            Span<GradientQuad> quads = stackalloc GradientQuad[12];
            int n = -1;
            bool each = cp.Any && !cp.Circular;
            if (each)
            {
                int big = Math.Max(Math.Max(Math.Max(cp.TLx, cp.TRx), Math.Max(cp.BRx, cp.BLx)), Math.Max(Math.Max(cp.TLy, cp.TRy), Math.Max(cp.BRy, cp.BLy)));
                if (MathF.Round(big * scale) <= ShadowRaster.MaxRadius) { n = GradientRounded.FillCorners(quads, r.Width, r.Height, cp, g.Vertical); }
            }
            int rTex = 0;
            if (n < 0)
            {
                each = false;
                int radius = cp.Circular ? cp.TLx : 0;
                rTex = (int)MathF.Round(radius * scale);
                if (radius <= 0 || rTex <= 0 || rTex > ShadowRaster.MaxRadius) { radius = 0; }
                n = GradientRounded.Fill(quads, r.Width, r.Height, radius, g.Vertical);
            }
            for (int i = 0; i < n; i++)
            {
                var q = quads[i];
                int x0 = (int)MathF.Round(r.X + q.X), y0 = (int)MathF.Round(r.Y + q.Y);
                var dest = new Rectangle(x0, y0, (int)MathF.Round(r.X + q.X + q.W) - x0, (int)MathF.Round(r.Y + q.Y + q.H) - y0);
                if (dest.Width <= 0 || dest.Height <= 0) { continue; }
                if (q.Piece == GradientPiece.Ramp)
                {
                    GradientRounded.RampSpan(q.T0, q.T1, key.N, out int at, out int count);
                    sb.Draw(ramp, dest, g.Vertical ? new Rectangle(1, 1 + at, 1, count) : new Rectangle(1 + at, 1, count, 1), tint);
                    continue;
                }
                int rx = rTex, ry = rTex;
                if (each)
                {
                    CornerOf(cp, q.Corner, out int cx, out int cy);
                    rx = Math.Max(1, (int)MathF.Round(cx * scale)); ry = Math.Max(1, (int)MathF.Round(cy * scale));
                }
                var corner = decor.Corner(sb.GraphicsDevice, g, rx, ry, len, scale, q.Piece == GradientPiece.CornerEnd, UnitPx, s.FontSizePx);
                if (corner == null) { continue; }
                var fx = q.FlipCross ? (g.Vertical ? SpriteEffects.FlipHorizontally : SpriteEffects.FlipVertically) : SpriteEffects.None;
                sb.Draw(corner, dest, new Rectangle(1, 1, rx, ry), tint, 0f, Vector2.Zero, fx, 0f);
            }
        }

        // ---------------------------------------------------------------- pictures (url(), <img>, border-image)

        /// <summary>The picture of a source: a file of the package (the texture and the whole of it) or a game sprite (its sheet and the part of it). False when it is
        /// not there (yet): a file that is missing or no picture (the log says so once), a sprite the game has no prefab for (looked for again every second).</summary>
        private bool Pic(PicSource p, out Texture2D tex, out Rectangle src)
        {
            tex = null; src = default;
            if (p == null) { return false; }
            if (p.Full != null)
            {
                tex = pics.GetFull(p.Full);
                if (tex == null || tex.IsDisposed) { return false; }
                src = new Rectangle(0, 0, tex.Width, tex.Height);
                return true;
            }
            double now = Timing.TotalTime;
            if (p.Sprite == null && now >= p.Retry)
            {
                p.Retry = now + 1;
                p.Sprite = UiSprites.Find(p.SpriteKey);
                if (p.Sprite == null) { PicSpec.Tell($"sprite({p.SpriteKey}): no such icon"); }
            }
            tex = p.Sprite?.Texture;   // reading Texture loads a lazy sprite (its source rectangle is empty before)
            if (tex == null || tex.IsDisposed)
            {
                p.Sprite = null;   // the content was reloaded under it: look the sprite up again (at most once a second, p.Retry)
                return false;
            }
            src = p.Sprite.SourceRect;
            return src.Width > 0 && src.Height > 0;
        }

        /// <summary>A piece of a picture on the screen: the destination edges are rounded (not the size), so neighbours share their pixel edge.</summary>
        private void DrawPiece(Texture2D tex, float x, float y, float w, float h, Rectangle src, Color tint)
        {
            int x0 = (int)MathF.Round(x), y0 = (int)MathF.Round(y), x1 = (int)MathF.Round(x + w), y1 = (int)MathF.Round(y + h);
            if (x1 <= x0 || y1 <= y0) { return; }
            var dest = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            if (dest.Intersects(clip)) { sb.Draw(tex, dest, src, tint); }
        }

        private static Color PicTint(float opacity) => new Color((byte)255, (byte)255, (byte)255, (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255));

        /// <summary>background-image: url(...) or sprite(...) with background-size, -position and -repeat, in the padding box (inside the borders), over the
        /// colour and under the content. No rounded clip: with a border-radius the picture keeps square corners.</summary>
        private void PaintBgPicture(PicSpec pic, ComputedStyle s, Rectangle r, int bt, int br, int bb, int bl, float opacity)
        {
            var area = new Rectangle(r.X + bl, r.Y + bt, r.Width - bl - br, r.Height - bt - bb);
            if (area.Width <= 0 || area.Height <= 0 || !area.Intersects(clip) || !Pic(pic.Bg, out var tex, out var sr)) { return; }
            int n = ImageCss.Tiles(bgTiles, area.X, area.Y, area.Width, area.Height, sr.Width, sr.Height, pic.Size, pic.Pos, pic.RepX, pic.RepY, UnitPx, s.FontSizePx, out bool cut);
            if (cut) { PicSpec.Tell("background-image: the picture is repeated more than " + bgTiles.Length + " times in one box, the rest is not drawn (use a larger picture or background-size)"); }
            var tint = PicTint(opacity);
            if (tint.A == 0) { return; }
            for (int i = 0; i < n; i++)
            {
                ref readonly var t = ref bgTiles[i];
                DrawPiece(tex, t.X, t.Y, t.W, t.H, new Rectangle(sr.X + t.SrcX, sr.Y + t.SrcY, t.SrcW, t.SrcH), tint);
            }
        }

        /// <summary>border-image: the nine pieces of a picture over the border box instead of the border (the box's border-width is the size of the corners
        /// unless border-image-width says otherwise). False: the picture is not there or has nothing to draw; the border is then drawn as usual.</summary>
        private bool PaintNineSlice(PicSpec pic, ComputedStyle s, Rectangle r, int bt, int br, int bb, int bl, float opacity)
        {
            if (r.Width <= 0 || r.Height <= 0 || !Pic(pic.Border, out var tex, out var sr)) { return false; }
            var slice = NineSlice.ResolveSlice(pic.Slice, sr.Width, sr.Height);
            var width = NineSlice.ResolveWidth(pic.Width, slice, new SliceEdges(bt, br, bb, bl), r.Width, r.Height, UnitPx, s.FontSizePx);
            int n = NineSlice.Plan(nineQuads, sr.Width, sr.Height, slice, width, pic.Slice.Fill, pic.NineX, pic.NineY, r.X, r.Y, r.Width, r.Height, out bool cut);
            if (cut) { PicSpec.Tell("border-image: the edge is repeated more than " + nineQuads.Length + " times, the rest is not drawn (use a larger slice or border-image-width)"); }
            if (n == 0) { return false; }
            var tint = PicTint(opacity);
            if (tint.A == 0) { return true; }
            if (!r.Intersects(clip)) { return true; }
            for (int i = 0; i < n; i++)
            {
                ref readonly var q = ref nineQuads[i];
                DrawPiece(tex, q.X, q.Y, q.W, q.H, new Rectangle(sr.X + q.SrcX, sr.Y + q.SrcY, q.SrcW, q.SrcH), tint);
            }
            return true;
        }

        /// <summary>&lt;img src alt&gt;: the picture in the content box with object-fit (fill by default, as in a browser: the box keeps the size CSS gives it).
        /// Not found: the alt text is drawn instead, if there is one.</summary>
        /// <summary>LayoutEngine.Images: the natural size of an img's picture, the pixels of its file (of its region, for a sprite). The file is read once, through
        /// the same cache as the paint. False when the img has no picture: it is laid out as 0 x 0 then.</summary>
        public bool TryGetSize(Element e, out int width, out int height)
        {
            width = height = 0;
            if (e.Tag == "video")
            {
                // the video's own size (its WebM header); without a playable file, the poster's; else 16:9 (the placeholder keeps its shape, as a browser's 300 x 150 box)
                if (UiVideos.NaturalSize(e, PageFolder(), out width, out height)) { return true; }
                string poster = e.GetAttribute("poster");
                if (!string.IsNullOrEmpty(poster) && Pic(PicSpec.ImgSource(e, poster, PageFolder()), out _, out var psr) && psr.Width > 0 && psr.Height > 0)
                {
                    width = psr.Width; height = psr.Height;
                    return true;
                }
                width = 320; height = 180;
                return true;
            }
            string srcText = e.GetAttribute("src");
            if (string.IsNullOrEmpty(srcText)) { return false; }
            var source = PicSpec.ImgSource(e, srcText, PageFolder());
            if (!Pic(source, out _, out var sr) || sr.Width <= 0 || sr.Height <= 0) { return false; }
            width = sr.Width; height = sr.Height;
            return true;
        }

        private void PaintImgElement(LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            string srcText = e.GetAttribute("src");
            if (string.IsNullOrEmpty(srcText)) { return; }
            int cx = (int)MathF.Round(b.Border.Left + b.Padding.Left), cy = (int)MathF.Round(b.Border.Top + b.Padding.Top);
            var content = new Rectangle(rect.X + cx, rect.Y + cy, rect.Width - cx - (int)MathF.Round(b.Border.Right + b.Padding.Right), rect.Height - cy - (int)MathF.Round(b.Border.Bottom + b.Padding.Bottom));
            if (content.Width <= 0 || content.Height <= 0 || !content.Intersects(clip)) { return; }
            var source = PicSpec.ImgSource(e, srcText, PageFolder());
            if (Pic(source, out var tex, out var sr))
            {
                var fit = PicSpec.Of(s, PageFolder()).Fit;
                var pl = ImageLayout.Place(sr.X, sr.Y, sr.Width, sr.Height, content.X, content.Y, content.Width, content.Height, fit);
                if (pl.IsEmpty) { return; }
                var tint = PicTint(opacity);
                if (tint.A > 0) { DrawPiece(tex, pl.X, pl.Y, pl.W, pl.H, new Rectangle(pl.SrcX, pl.SrcY, pl.SrcW, pl.SrcH), tint); }
                return;
            }
            string alt = e.GetAttribute("alt");
            if (string.IsNullOrEmpty(alt) || !PushClip(content, out var saved)) { return; }
            try { DrawText(alt, content.X, content.Y + s.FontSizePx, ToColor(s.GetColor("color"), opacity), s, TextSpacing.Of(s)); }
            finally { PopClip(saved); }
        }

        /// <summary>&lt;video&gt;: the decoder's frame in the content box with object-fit (as an img). Until the first frame and when the clip cannot play, the poster
        /// (poster="file") is drawn; without one, a failed clip gets a dark placeholder (the problems list says why). Marks the video as on screen (UiVideos.Paint).</summary>
        private void PaintVideoElement(LayoutBox b, ComputedStyle s, Rectangle rect, float opacity)
        {
            var e = b.Element;
            int cx = (int)MathF.Round(b.Border.Left + b.Padding.Left), cy = (int)MathF.Round(b.Border.Top + b.Padding.Top);
            var content = new Rectangle(rect.X + cx, rect.Y + cy, rect.Width - cx - (int)MathF.Round(b.Border.Right + b.Padding.Right), rect.Height - cy - (int)MathF.Round(b.Border.Bottom + b.Padding.Bottom));
            if (content.Width <= 0 || content.Height <= 0 || !content.Intersects(clip)) { return; }
            string folder = PageFolder();
            bool failed = false;
            Texture2D frame = Videos != null ? Videos.Paint(e, folder, out failed) : null;
            var tint = PicTint(opacity);
            if (tint.A == 0) { return; }
            var fit = PicSpec.Of(s, folder).Fit;
            if (frame != null && !frame.IsDisposed)
            {
                var pl = ImageLayout.Place(0, 0, frame.Width, frame.Height, content.X, content.Y, content.Width, content.Height, fit);
                if (!pl.IsEmpty) { DrawPiece(frame, pl.X, pl.Y, pl.W, pl.H, new Rectangle(pl.SrcX, pl.SrcY, pl.SrcW, pl.SrcH), tint); }
                return;
            }
            string poster = e.GetAttribute("poster");
            if (!string.IsNullOrEmpty(poster) && Pic(PicSpec.ImgSource(e, poster, folder), out var tex, out var sr))
            {
                var pl = ImageLayout.Place(sr.X, sr.Y, sr.Width, sr.Height, content.X, content.Y, content.Width, content.Height, fit);
                if (!pl.IsEmpty) { DrawPiece(tex, pl.X, pl.Y, pl.W, pl.H, new Rectangle(pl.SrcX, pl.SrcY, pl.SrcW, pl.SrcH), tint); }
                return;
            }
            if (failed) { DrawPiece(GUI.WhiteTexture, content.X, content.Y, content.Width, content.Height, new Rectangle(0, 0, 1, 1), new Color((byte)24, (byte)28, (byte)32, tint.A)); }
        }

        private static bool SameColor(Rgba a, Rgba b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

        /// <summary>The border of a box with rounded corners. Four equal sides of one colour on equal circular corners: the ring mask (solid), or
        /// its four corners plus dashes along the straight sides (as before per-corner radii). Everything else (unequal or elliptical corners, sides
        /// of other widths or colours, a rounded button with only a left bar): four corner tiles and four straight strips, no scissor and no batch
        /// restart. A corner tile is split along the line from the outer corner to the inner corner of the border (where browsers join two border
        /// colours): the half of the horizontal side in its colour, the half of the vertical side in its colour, with a 1 px soft edge from the
        /// analytic coverage (Core/Coverage.cs); two sides of one colour use one whole tile. A side without width or with a transparent colour draws nothing.</summary>
        private void RoundedBorder(ComputedStyle s, Rectangle r, in CornerPx cp, int bt, int br, int bb, int bl, string line, float opacity)
        {
            int bw = Math.Max(Math.Max(bt, br), Math.Max(bb, bl));
            if (bw <= 0) { return; }
            var ct = s.GetColor("border-top-color"); var cr = s.GetColor("border-right-color");
            var cb = s.GetColor("border-bottom-color"); var cl = s.GetColor("border-left-color");
            bool dashed = line is "dashed" or "dotted";
            bool same = bt == br && br == bb && bb == bl && SameColor(ct, cr) && SameColor(cr, cb) && SameColor(cb, cl);
            if (same && cp.Circular)
            {
                int radius = cp.TLx;
                if (ct.A == 0) { return; }
                if (!dashed) { Draw9(Mask(radius, bw), r, ToColor(ct, opacity)); return; }
                Draw9(Mask(radius, bw), r, ToColor(ct, opacity), edges: false);
                DashedSides(s, r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2), bt, br, bb, bl, line == "dotted", opacity);
                return;
            }
            // corner tile sizes: the radius, but not less than the border it holds
            int tlw = Math.Max(cp.TLx, bl), tlh = Math.Max(cp.TLy, bt), trw = Math.Max(cp.TRx, br), trh = Math.Max(cp.TRy, bt);
            int brw = Math.Max(cp.BRx, br), brh = Math.Max(cp.BRy, bb), blw = Math.Max(cp.BLx, bl), blh = Math.Max(cp.BLy, bb);
            RingCorner(cp.TLx, cp.TLy, bl, bt, ct, cl, new Rectangle(r.X, r.Y, tlw, tlh), SpriteEffects.None, opacity);
            RingCorner(cp.TRx, cp.TRy, br, bt, ct, cr, new Rectangle(r.Right - trw, r.Y, trw, trh), SpriteEffects.FlipHorizontally, opacity);
            RingCorner(cp.BRx, cp.BRy, br, bb, cb, cr, new Rectangle(r.Right - brw, r.Bottom - brh, brw, brh), SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically, opacity);
            RingCorner(cp.BLx, cp.BLy, bl, bb, cb, cl, new Rectangle(r.X, r.Bottom - blh, blw, blh), SpriteEffects.FlipVertically, opacity);
            if (dashed)
            {
                DashedEdges(s, r, tlw, trw, blw, brw, tlh, blh, trh, brh, bt, br, bb, bl, line == "dotted", opacity);
                return;
            }
            Strip(new Rectangle(r.X + tlw, r.Y, r.Width - tlw - trw, bt), ct, opacity);
            Strip(new Rectangle(r.X + blw, r.Bottom - bb, r.Width - blw - brw, bb), cb, opacity);
            Strip(new Rectangle(r.X, r.Y + tlh, bl, r.Height - tlh - blh), cl, opacity);
            Strip(new Rectangle(r.Right - br, r.Y + trh, br, r.Height - trh - brh), cr, opacity);
        }

        private void Strip(Rectangle strip, Rgba c, float opacity)
        {
            if (strip.Width > 0 && strip.Height > 0 && c.A > 0) { sb.Draw(GUI.WhiteTexture, strip, ToColor(c, opacity)); }
        }

        /// <summary>One corner of the ring (drawn as a top-left corner, mirrored by fx): bwx is the width of its vertical side, bwy of its horizontal
        /// one, ch and cv their colours.</summary>
        private void RingCorner(int rx, int ry, int bwx, int bwy, Rgba ch, Rgba cv, Rectangle dest, SpriteEffects fx, float opacity)
        {
            if (dest.Width <= 0 || dest.Height <= 0 || bwx <= 0 && bwy <= 0) { return; }
            if (bwy <= 0) { ch = Rgba.Transparent; }
            if (bwx <= 0) { cv = Rgba.Transparent; }
            if (ch.A == 0 && cv.A == 0) { return; }
            if (SameColor(ch, cv) || bwx <= 0 || bwy <= 0)
            {
                var c = bwy <= 0 ? cv : ch;
                if (c.A > 0) { sb.Draw(CornerTile(rx, ry, bwx, bwy, 0), dest, null, ToColor(c, opacity), 0f, Vector2.Zero, fx, 0f); }
                return;
            }
            if (ch.A > 0) { sb.Draw(CornerTile(rx, ry, bwx, bwy, 1), dest, null, ToColor(ch, opacity), 0f, Vector2.Zero, fx, 0f); }
            if (cv.A > 0) { sb.Draw(CornerTile(rx, ry, bwx, bwy, 2), dest, null, ToColor(cv, opacity), 0f, Vector2.Zero, fx, 0f); }
        }

        /// <summary>The filled box with its own radius at every corner: rows between the places where a corner starts or ends. In each row the
        /// middle is a plain rectangle and a corner is a slice of the quarter-ellipse tile, mirrored for the three other corners (one texture for
        /// equal corners). Nothing is drawn twice, so a translucent colour stays even. (When two corners across the diagonal reach past each other,
        /// e.g. 15 px at top-left and bottom-right of a 20 px square, their tiles overlap a little in the rows they share.)</summary>
        private void FillCorners(Rectangle r, in CornerPx cp, Color color)
        {
            if (r.Width <= 0 || r.Height <= 0 || color.A == 0) { return; }
            Span<int> ys = stackalloc int[6];
            ys[0] = r.Y; ys[1] = r.Y + cp.TLy; ys[2] = r.Y + cp.TRy; ys[3] = r.Bottom - cp.BLy; ys[4] = r.Bottom - cp.BRy; ys[5] = r.Bottom;
            for (int i = 1; i < 6; i++)
            {
                int v = ys[i], j = i - 1;
                while (j >= 0 && ys[j] > v) { ys[j + 1] = ys[j]; j--; }
                ys[j + 1] = v;
            }
            for (int i = 0; i < 5; i++)
            {
                int y0 = Math.Max(ys[i], r.Y), y1 = Math.Min(ys[i + 1], r.Bottom), hh = y1 - y0;
                if (hh <= 0) { continue; }
                int lw = 0, rw = 0;
                if (cp.TLy > 0 && y0 < r.Y + cp.TLy)
                {
                    lw = cp.TLx;
                    sb.Draw(CornerTile(cp.TLx, cp.TLy, 0, 0, 0), new Rectangle(r.X, y0, lw, hh), new Rectangle(0, y0 - r.Y, lw, hh), color);
                }
                else if (cp.BLy > 0 && y0 >= r.Bottom - cp.BLy)
                {
                    lw = cp.BLx;
                    sb.Draw(CornerTile(cp.BLx, cp.BLy, 0, 0, 0), new Rectangle(r.X, y0, lw, hh), new Rectangle(0, r.Bottom - y1, lw, hh), color, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
                }
                if (cp.TRy > 0 && y0 < r.Y + cp.TRy)
                {
                    rw = cp.TRx;
                    sb.Draw(CornerTile(cp.TRx, cp.TRy, 0, 0, 0), new Rectangle(r.Right - rw, y0, rw, hh), new Rectangle(0, y0 - r.Y, rw, hh), color, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 0f);
                }
                else if (cp.BRy > 0 && y0 >= r.Bottom - cp.BRy)
                {
                    rw = cp.BRx;
                    sb.Draw(CornerTile(cp.BRx, cp.BRy, 0, 0, 0), new Rectangle(r.Right - rw, y0, rw, hh), new Rectangle(0, r.Bottom - y1, rw, hh), color, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically, 0f);
                }
                if (r.Width - lw - rw > 0) { sb.Draw(GUI.WhiteTexture, new Rectangle(r.X + lw, y0, r.Width - lw - rw, hh), color); }
            }
        }

        /// <summary>A corner tile (Core/Corners.cs CornerRaster), white with the coverage in alpha like Mask: key (rx, ry, bwx, bwy, part) in
        /// texture px, so the tile is shared by every box and corner with the same numbers. A ring 1-2 px thick lifts its partly covered pixels
        /// as Mask does (Coverage.ThinLift; the thinner of the two widths decides).</summary>
        private Texture2D CornerTile(int rx, int ry, int bwx, int bwy, int part)
        {
            var key = (rx, ry, bwx, bwy, part);
            if (tiles.TryGet(key, out var tex)) { return tex; }
            if (tileFailed.Count > 0 && tileFailed.Contains(key)) { return GUI.WhiteTexture; }
            var hits = CornerRaster.Hits(rx, ry, bwx, bwy, part, out int tw, out int th);
            int thin = bwx > 0 && bwy > 0 ? Math.Min(bwx, bwy) : Math.Max(bwx, bwy);
            var data = new Color[Math.Max(1, tw * th)];
            for (int i = 0; i < hits.Length; i++)
            {
                float cov = Coverage.ThinLift(hits[i] / 255f, thin);
                data[i] = new Color((byte)255, (byte)255, (byte)255, Coverage.ToByte(cov));
            }
            try
            {
                tex = new Texture2D(sb.GraphicsDevice, Math.Max(1, tw), Math.Max(1, th));
                tex.SetData(data);
            }
            catch (Exception ex) { return Failed(tex, tileFailed, key, $"corner tile {tw}x{th}: {ex.Message}"); }
            tiles.Put(key, tex, (long)tex.Width * tex.Height * 4);
            return tex;
        }

        /// <summary>dashed / dotted borders: dashes of 3 widths with a gap of 2 (dots of 1 width with a gap of 1), fitted so a dash sits at both ends of
        /// a side. inset is the corner radius of a rounded box (the corners are drawn by the ring mask), 0 for a plain one (top and bottom take the corners).</summary>
        private void DashedSides(ComputedStyle s, Rectangle r, int inset, int bt, int br, int bb, int bl, bool dotted, float opacity)
        {
            int side = inset > 0 ? inset : 0, top = inset > 0 ? inset : bt, bottom = inset > 0 ? inset : bb;
            DashedEdges(s, r, side, side, side, side, top, bottom, top, bottom, bt, br, bb, bl, dotted, opacity);
        }

        /// <summary>The dashes with each end of each side given: top side from tl to tr short of the corners, bottom bl, br, left from lt (top) to lb,
        /// right from rt to rb (the room the corner tiles take).</summary>
        private void DashedEdges(ComputedStyle s, Rectangle r, int tl, int tr, int bl_, int br_, int lt, int lb, int rt, int rb,
            int bt, int br, int bb, int bl, bool dotted, float opacity)
        {
            void Run(int w, string colorProp, int x, int y, int len, bool horizontal)
            {
                var c = s.GetColor(colorProp);
                if (w <= 0 || len <= 0 || c.A == 0) { return; }
                var col = ToColor(c, opacity);
                int dash = dotted ? w : w * 3, gap = dotted ? w : Math.Max(2, w * 2);
                int n = Math.Max(1, (int)Math.Round((len + gap) / (float)(dash + gap)));
                float pitch = n > 1 ? (len - dash) / (float)(n - 1) : 0;
                for (int i = 0; i < n; i++)
                {
                    int off = (int)Math.Round(i * pitch);
                    int piece = n == 1 ? len : Math.Min(dash, len - off);
                    sb.Draw(GUI.WhiteTexture, horizontal ? new Rectangle(x + off, y, piece, w) : new Rectangle(x, y + off, w, piece), col);
                }
            }
            Run(bt, "border-top-color", r.X + tl, r.Y, r.Width - tl - tr, true);
            Run(bb, "border-bottom-color", r.X + bl_, r.Bottom - bb, r.Width - bl_ - br_, true);
            Run(bl, "border-left-color", r.X, r.Y + lt, r.Height - lt - lb, false);
            Run(br, "border-right-color", r.Right - br, r.Y + rt, r.Height - rt - rb, false);
        }

        /// <summary>The element whose bar the mouse holds or is over: drawn brighter.</summary>
        internal Element HotBar;

        private void PaintScrollbar(LayoutBox b, ComputedStyle s, float dx, float dy, float opacity)
        {
            float pos = b.Element != null && Scroll != null && Scroll.TryGetValue(b.Element, out float v) ? v : 0;
            if (!BarRects(b, dx, dy, pos, out var track, out var thumb, out bool gutter)) { return; }
            var color = s.GetColor("color");
            if (s.TryGetFirstColor("scrollbar-color", out var thumbColor)) { color = thumbColor; }
            else { color = new Rgba(color.R, color.G, color.B, (byte)(color.A * 0.35f)); }
            if (gutter) { Fill(track.X, track.Y, track.Width, track.Height, new Rgba(color.R, color.G, color.B, (byte)(color.A * 0.18f)), opacity); }
            if (HotBar != null && HotBar == b.Element) { color = new Rgba(color.R, color.G, color.B, (byte)Math.Min(255, color.A * 2 + 40)); }
            Fill(thumb.X, thumb.Y, thumb.Width, thumb.Height, color, opacity);
        }

        /// <summary>Where a scroll area's bar is on screen (dx, dy: the scroll of the areas around it; pos: its own).
        /// overflow-y: scroll with a right padding puts the bar in that padding, its own column the content never
        /// reaches (gutter); otherwise a thin bar lies over the content's right edge. False: no bar (nothing to scroll).</summary>
        internal bool BarRects(LayoutBox b, float dx, float dy, float pos, out Rectangle track, out Rectangle thumb, out bool gutter)
        {
            track = thumb = default;
            gutter = false;
            var s = b.Style;
            if (b.Element == null || !b.ClipY || s == null || s.Get("overflow-y") is not ("auto" or "scroll") || s.Get("scrollbar-width") == "none") { return false; }
            float view = b.H - b.Border.Vertical, max = b.ScrollHeight - view;
            if (max <= 0.5f) { return false; }
            float w = (s.Get("scrollbar-width") == "thin" ? 3 : 5) * UnitPx, x = b.X + dx + b.W - b.Border.Right - w;
            if (s.Get("overflow-y") == "scroll" && b.Padding.Right >= w)
            {
                gutter = true;
                float inset = Math.Max(1, (b.Padding.Right - w) / 2);
                w = b.Padding.Right - 2 * inset;
                x = b.X + dx + b.W - b.Border.Right - b.Padding.Right + inset;
            }
            float top = b.Y + dy + b.Border.Top, size = Math.Max(view * view / b.ScrollHeight, 16 * UnitPx);
            track = Snap(x, top, Math.Max(2, w), view);
            thumb = Snap(x, top + Math.Clamp(pos, 0, max) / max * (view - size), Math.Max(2, w), size);
            return true;
        }

        // ---------------------------------------------------------------- for UiControls (inside Paint only)

        internal SpriteBatch Batch => sb;
        internal Rectangle Clip => clip;

        /// <summary>A painted subtree on top of everything (a modal dialog): absolute position, no ancestor scroll.</summary>
        internal void PaintSubtree(LayoutBox b) => PaintBox(b, 0, 0, 1f);

        /// <summary>Narrows the scissor to r (within the current clip); false when nothing is left. PopClip(saved) after.</summary>
        internal bool PushClip(Rectangle r, out Rectangle saved)
        {
            saved = clip;
            var next = Rectangle.Intersect(clip, r);
            if (next.Width <= 0 || next.Height <= 0) { return false; }
            if (next != clip) { SetScissor(next); }
            return true;
        }

        internal void PopClip(Rectangle saved)
        {
            if (clip != saved) { SetScissor(saved); }
        }

        internal void FillRect(Rectangle r, Color c)
        {
            if (r.Width > 0 && r.Height > 0 && c.A > 0) { sb.Draw(GUI.WhiteTexture, r, c); }
        }

        /// <summary>A filled rounded rectangle (radius 0: plain).</summary>
        internal void FillRounded(Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0 || c.A == 0) { return; }
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            if (radius <= 0) { sb.Draw(GUI.WhiteTexture, r, c); return; }
            Draw9(Mask(radius, 0), r, c);
        }

        /// <summary>A rounded border ring of width bw (radius 0: four edges).</summary>
        internal void StrokeRounded(Rectangle r, int radius, int bw, Color c)
        {
            if (bw <= 0 || c.A == 0) { return; }
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            if (radius > 0) { Draw9(Mask(radius, bw), r, c); return; }
            sb.Draw(GUI.WhiteTexture, new Rectangle(r.X, r.Y, r.Width, bw), c);
            sb.Draw(GUI.WhiteTexture, new Rectangle(r.X, r.Bottom - bw, r.Width, bw), c);
            sb.Draw(GUI.WhiteTexture, new Rectangle(r.X, r.Y + bw, bw, r.Height - 2 * bw), c);
            sb.Draw(GUI.WhiteTexture, new Rectangle(r.Right - bw, r.Y + bw, bw, r.Height - 2 * bw), c);
        }

        internal static int RadiusOf(ComputedStyle s, Rectangle r) => Radius(s, r);
        internal static Color Tint(Rgba c, float opacity) => ToColor(c, opacity);
        internal static Rectangle SnapRect(float x, float y, float w, float h) => Snap(x, y, w, h);

        private void Fill(int x, int y, int w, int h, Rgba c, float opacity)
        {
            if (w <= 0 || h <= 0 || c.A == 0) { return; }
            sb.Draw(GUI.WhiteTexture, new Rectangle(x, y, w, h), ToColor(c, opacity));
        }

        private void SetScissor(Rectangle r)
        {
            long a = P0();
            SetScissorCore(r);
            P1(3, a);
        }

        private void SetScissorCore(Rectangle r)
        {
            // the device scissor is in screen pixels and ignores the batch's matrix: r is in the block's own coordinates, so inside a scaled block it is
            // mapped out (and kept inside what the scaled block started with, the rounding of the two directions must not widen the clip)
            var dev = r;
            if (!m.IsIdentity)
            {
                var q = m.MapRect(r.X, r.Y, r.Width, r.Height, outward: false);
                dev = Rectangle.Intersect(scrBase, new Rectangle(q.X, q.Y, q.W, q.H));
            }
            sb.End();
            sb.GraphicsDevice.ScissorRectangle = dev;
            sb.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable, transformMatrix: BatchMatrix);
            clip = r;
            scr = dev;
            clipped = true;
        }

        /// <summary>A rectangle of the block being painted, on screen (inside a scaled block; otherwise as it is). For what the game or the mouse holds
        /// in screen pixels: the open select's box, the text input rectangle of the IME.</summary>
        internal Rectangle ToScreen(Rectangle r)
        {
            if (m.IsIdentity) { return r; }
            var q = m.MapRect(r.X, r.Y, r.Width, r.Height, outward: false);
            return new Rectangle(q.X, q.Y, q.W, q.H);
        }

        /// <summary>The transform of the box being painted (identity outside scaled blocks).</summary>
        internal UiTransform Xf => m;

        /// <summary>A scissor switch ends the sprite batch; skip it when nothing sticks out (ellipsis rows, lists
        /// that fit). Out-of-flow children are not in the extent, so they keep the clip.</summary>
        private static bool NeedsClip(LayoutBox b)
        {
            if (!b.ClipX && !b.ClipY) { return false; }
            if ((b.ClipX && b.ScrollWidth > b.W - b.Border.Horizontal + 0.5f) || (b.ClipY && b.ScrollHeight > b.H - b.Border.Vertical + 0.5f)) { return true; }
            foreach (var c in b.Children) { if (c.OutOfFlow) { return true; } }
            return false;
        }

        // ---------------------------------------------------------------- hit test

        /// <summary>The element under the point and the on-screen rectangle it was hit in, or null where nothing is
        /// painted (the click then goes to the game). Topmost first: reverse paint order, children before parents.</summary>
        public Element HitTest(LayoutBox root, Point p, out Rectangle hitRect, out LayoutBox hitBox)
        {
            hitRect = Rectangle.Empty;
            hitBox = null;
            return Hit(root, 0, 0, p, new Rectangle(int.MinValue / 2, int.MinValue / 2, int.MaxValue, int.MaxValue), ref hitRect, ref hitBox);
        }

        private Element Hit(LayoutBox b, float dx, float dy, Point p, Rectangle area, ref Rectangle hitRect, ref LayoutBox hitBox)
        {
            var s = b.Element?.Style ?? b.Style;
            if (b.Element != null && s.GetNumber("opacity", 1) <= 0.002f) { return null; }
            float fw = 0, fh = 0;
            if (b.Element != null)
            {
                ScreenMap.Place(b.Element, s, ref dx, ref dy, out fw, out fh);   // the same step ScreenMap.Of walks, so a place on screen is what is painted
                dy = StickyDy(b.Element, b, dy);
            }
            // scale: the block's own coordinates are the point and the clip area taken back through the scale about its corner, so everything below
            // works unchanged; what is found comes out in the parent's coordinates (Up), the topmost call returns screen ones
            var up = UiTransform.Identity;
            if (b.Element != null && UiTransform.ScaleOf(b.Element, s) is var sc && sc != 1)
            {
                up = ScreenMap.ScaleAbout(b, dx, dy, sc);
                var back = up.Inverse();
                p = new Point((int)Math.Floor(back.X(p.X + 0.5f)), (int)Math.Floor(back.Y(p.Y + 0.5f)));   // the pixel's middle
                var q = back.MapRect(area.X, area.Y, area.Width, area.Height, outward: true);
                area = new Rectangle(q.X, q.Y, q.W, q.H);
            }
            var rect = Snap(b.X + dx, b.Y + dy, Math.Max(0, b.W + fw), Math.Max(0, b.H + fh));
            if (b.Children.Count > 0)
            {
                var inner = area;
                float cdy = dy;
                if (b.ClipX || b.ClipY)
                {
                    var pad = Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top, b.W - b.Border.Horizontal, b.H - b.Border.Vertical);
                    inner = Rectangle.Intersect(area, new Rectangle(b.ClipX ? pad.X : area.X, b.ClipY ? pad.Y : area.Y,
                        b.ClipX ? pad.Width : area.Width, b.ClipY ? pad.Height : area.Height));
                    if (b.Element != null && Scroll != null && Scroll.TryGetValue(b.Element, out float sy)) { cdy = ScreenMap.ScrollStep(cdy, sy); }
                }
                if (fw != 0 || fh != 0)
                {
                    inner = Rectangle.Intersect(inner, Snap(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top,
                        Math.Max(0, b.W + fw - b.Border.Horizontal), Math.Max(0, b.H + fh - b.Border.Vertical)));
                }
                if (inner.Contains(p))
                {
                    var order = Order(b);
                    for (int i = order.Length - 1; i >= 0; i--)
                    {
                        if (Deferred.Count > 0 && order[i].Element != null && Deferred.Contains(order[i].Element)) { continue; }
                        var e = Hit(order[i], dx, cdy, p, inner, ref hitRect, ref hitBox);
                        if (e != null) { hitRect = Up(up, hitRect); return e.PseudoHost ?? e; }   // a ::before / ::after box is hit as its element
                    }
                }
            }
            if (!area.Contains(p) || s.Get("pointer-events") == "none" || s.Get("visibility") != "visible") { return null; }
            foreach (var f in b.Text)
            {
                var fr = Snap(f.X + dx, f.Y + dy, f.Width, f.Height);
                if (fr.Contains(p) && f.Owner != null && (f.Owner.Style ?? s).Get("pointer-events") != "none")
                {
                    hitRect = Up(up, Rectangle.Intersect(fr, area));
                    hitBox = b;
                    return f.Owner.PseudoHost ?? f.Owner;
                }
            }
            if (b.Element != null && rect.Contains(p) && Solid(b, s))
            {
                hitRect = Up(up, Rectangle.Intersect(rect, area));
                hitBox = b;
                return b.Element.PseudoHost ?? b.Element;
            }
            return null;
        }

        /// <summary>A rectangle in a scaled block's own coordinates as the parent sees it (the whole pixels that hold it); identity: as it is.</summary>
        private static Rectangle Up(in UiTransform t, Rectangle r)
        {
            if (t.IsIdentity) { return r; }
            var q = t.MapRect(r.X, r.Y, r.Width, r.Height, outward: true);
            return new Rectangle(q.X, q.Y, q.W, q.H);
        }

        /// <summary>Boxes that take the mouse: painted ones, interactive tags and scroll areas. A transparent wrapper
        /// lets the click through, so a page can cover the screen and still leave the game playable.</summary>
        private static bool Solid(LayoutBox b, ComputedStyle s) =>
            s.GetColor("background-color").A > 0 || b.Border.Horizontal > 0 || b.Border.Vertical > 0
            || b.Element.GetAttribute("onclick") != null || s.Get("cursor") == "pointer"
            || b.Element.Tag is "button" or "a" or "input" or "select" or "dialog" or "item" or "canvas" or "progress" or "slot" or "textarea" or "curve" or "chart"
            || b.Element.GetAttribute("title") != null
            || (b.ClipY && b.ScrollHeight > b.H - b.Border.Vertical + 0.5f);

        // ---------------------------------------------------------------- helpers

        private static LayoutBox[] Order(LayoutBox b)
        {
            if (b.PaintOrder != null && SameChildren(b)) { return b.PaintOrder; }
            var flow = new List<LayoutBox>();
            var positioned = new List<(LayoutBox Box, int Z, int I)>();
            for (int i = 0; i < b.Children.Count; i++)
            {
                var c = b.Children[i];
                var cs = c.Element?.Style ?? c.Style;
                if (c.Element != null && cs.Get("position") != "static")
                {
                    positioned.Add((c, int.TryParse(cs.Get("z-index"), out int z) ? z : 0, i));
                }
                else { flow.Add(c); }
            }
            positioned.Sort((x, y) => x.Z != y.Z ? x.Z.CompareTo(y.Z) : x.I.CompareTo(y.I));
            var order = new LayoutBox[b.Children.Count];
            int k = 0;
            foreach (var (box, z, _) in positioned) { if (z < 0) { order[k++] = box; } }
            foreach (var c in flow) { order[k++] = c; }
            foreach (var (box, z, _) in positioned) { if (z >= 0) { order[k++] = box; } }
            b.PaintOrderOf = b.Children.ToArray();
            return b.PaintOrder = order;
        }

        private static bool SameChildren(LayoutBox b)
        {
            var was = b.PaintOrderOf;
            if (was == null || was.Length != b.Children.Count) { return false; }
            for (int i = 0; i < was.Length; i++)
            {
                if (!ReferenceEquals(was[i], b.Children[i])) { return false; }
            }
            return true;
        }

        /// <summary>Edges are rounded, not the size, so neighbours share their pixel edge with no gap.</summary>
        private static Rectangle Snap(float x, float y, float w, float h)
        {
            int x0 = (int)Math.Round(x), y0 = (int)Math.Round(y);
            return new Rectangle(x0, y0, (int)Math.Round(x + w) - x0, (int)Math.Round(y + h) - y0);
        }

        /// <summary>Like Chrome: a border of at least 1 px is floored to whole pixels, a thinner one becomes 1 px.</summary>
        private static int BorderPx(float w) => w <= 0 ? 0 : Math.Max(1, (int)Math.Floor(w + 0.001f));

        /// <summary>One radius for what takes one (outline, progress bars): CornerPx.Single, the box's own radius when all four corners are one circle.</summary>
        private static int Radius(ComputedStyle s, Rectangle r) => CornerPx.Of(s, r.Width, r.Height).Single;

        /// <summary>The colour as the SpriteBatch takes it: straight (not premultiplied) RGB and the alpha, because the game's MonoGame fork blends
        /// NonPremultiplied by default. Scaling RGB by the alpha as well applied the alpha twice: translucent CSS colours came out too dark.</summary>
        private static Color ToColor(Rgba c, float opacity) => new Color(c.R, c.G, c.B, (byte)Math.Clamp((int)Math.Round(c.A * opacity), 0, 255));

        /// <summary>A white rounded-rectangle mask (bw = 0: filled; otherwise the border ring) of size 2c + 3 where
        /// c = max(r, bw): corners c×c, a 3 px straight middle that the 9-slice stretches. The edges are analytic coverage (Core/Coverage.cs, a 1 px
        /// ramp), not point samples.
        /// The texels are white with the coverage in alpha: this game's SpriteBatch blends NonPremultiplied (MonoGame fork, Begin default), and a
        /// premultiplied texel (a, a, a, a) squared the coverage and left a dark halo around every arc.
        /// A thin ring (1-2 px) also gets its partly covered pixels lifted a little (Coverage.ThinLift: ^0.8 for 1 px, ^0.9 for 2 px): a light line on a
        /// dark panel blended in sRGB carries less light at half coverage than on the straight sides. The lift is mild, so the edge does not harden.</summary>
        private Texture2D Mask(int r, int bw)
        {
            int c = Math.Max(r, bw);
            if (masks.TryGet((c, r, bw), out var tex)) { return tex; }
            if (maskFailed.Count > 0 && maskFailed.Contains((c, r, bw))) { return GUI.WhiteTexture; }
            int t = 2 * c + 3;
            var data = new Color[t * t];
            for (int y = 0; y < t; y++)
            {
                for (int x = 0; x < t; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float cov = Coverage.RoundedSquare(px, py, 0, t, r);
                    if (bw > 0) { cov = Math.Clamp(cov - Coverage.RoundedSquare(px, py, bw, t - bw, Math.Max(0, r - bw)), 0f, 1f); }
                    cov = Coverage.ThinLift(cov, bw);
                    data[y * t + x] = new Color((byte)255, (byte)255, (byte)255, Coverage.ToByte(cov));
                }
            }
            try
            {
                tex = new Texture2D(sb.GraphicsDevice, t, t);
                tex.SetData(data);
            }
            catch (Exception ex) { return Failed(tex, maskFailed, (c, r, bw), $"corner mask {t}x{t}: {ex.Message}"); }
            masks.Put((c, r, bw), tex, (long)t * t * 4);
            return tex;
        }

        private void Draw9(Texture2D tex, Rectangle d, Color color, bool edges = true)
        {
            if (ReferenceEquals(tex, GUI.WhiteTexture))
            {
                // the mask could not be made: a square fill (nothing for a ring that draws only its corners)
                if (edges) { sb.Draw(tex, d, color); }
                return;
            }
            int t = tex.Width, c = (t - 3) / 2, m = c + 1;
            int cw = Math.Min(c, d.Width / 2), ch = Math.Min(c, d.Height / 2);
            Span<int> dx = stackalloc int[] { d.X, d.X + cw, d.Right - cw, d.Right };
            Span<int> dy = stackalloc int[] { d.Y, d.Y + ch, d.Bottom - ch, d.Bottom };
            for (int j = 0; j < 3; j++)
            {
                int h = dy[j + 1] - dy[j];
                if (h <= 0) { continue; }
                int sy0 = j == 0 ? 0 : j == 1 ? m : c + 3, sh = j == 1 ? 1 : c;
                for (int i = 0; i < 3; i++)
                {
                    int w = dx[i + 1] - dx[i];
                    if (w <= 0 || !edges && (i == 1 || j == 1)) { continue; }
                    int sx0 = i == 0 ? 0 : i == 1 ? m : c + 3, sw = i == 1 ? 1 : c;
                    sb.Draw(tex, new Rectangle(dx[i], dy[j], w, h), new Rectangle(sx0, sy0, sw, sh), color);
                }
            }
        }

        /// <summary>A texture that could not be made or filled: the half-made one is freed, the key is remembered (no new try every frame), the log says so
        /// once, and the plain white texture stands in (the callers draw it stretched: a square corner).</summary>
        private static Texture2D Failed<TKey>(Texture2D half, HashSet<TKey> failed, TKey key, string what)
        {
            try { half?.Dispose(); } catch (Exception) { }
            failed.Add(key);
            DecorSpec.Tell(what);
            return GUI.WhiteTexture;
        }

        /// <summary>The window closed: masks, corner tiles and decoration textures go through the graveyard (a batch of the frame just ended may still hold
        /// one). The pictures are the window's, not the painter's: the window disposes them.</summary>
        public void Dispose()
        {
            masks.Clear();
            tiles.Clear();
            maskFailed.Clear();
            tileFailed.Clear();
            decor.Dispose();
            DisposeRounded();
        }
    }

    /// <summary>Where a picture comes from: a file of the package (the full path, resolved once) or a game sprite looked up by its key and kept.</summary>
    internal sealed class PicSource
    {
        public string Full, SpriteKey;
        public Sprite Sprite;
        public double Retry;

        /// <summary>url("file") or sprite(kind:id); null for anything else (none, a gradient) or a path that cannot be used (said once in the log).</summary>
        public static PicSource From(string value, string pageDir)
        {
            if (ImageCss.TryCall(value, "sprite", out string key)) { return new PicSource { SpriteKey = key }; }
            if (!ImageCss.TryUrl(value, out string url)) { return null; }
            if (!ImageCss.ResolvePath(pageDir, url, File.Exists, out string full, out string problem))
            {
                PicSpec.Tell("picture " + problem);
                return null;
            }
            return new PicSource { Full = full };
        }
    }

    /// <summary>The picture properties of a style (background-image and its size, position and repeat; border-image-*; object-fit), parsed once per style and
    /// folder; styles are shared, so the painter asks every frame without allocating. A value that cannot be used is told once in the log.</summary>
    internal sealed class PicSpec
    {
        public PicSource Bg, Border;
        public BgSize Size = BgSize.Auto;
        public BgPosition Pos = BgPosition.Origin;
        public BgRepeat RepX = BgRepeat.Repeat, RepY = BgRepeat.Repeat;
        public SliceSpec Slice = SliceSpec.Default;
        public WidthSpec Width = WidthSpec.Default;
        public EdgeRepeat NineX, NineY;
        public ImageFit Fit = ImageFit.Stretch;
        private string[] texts;
        private string dir;

        private static readonly string[] Props =
        {
            "background-image", "background-size", "background-position", "background-repeat", "border-image-source", "border-image-slice", "border-image-width",
            "border-image-repeat", "object-fit",
        };
        private static readonly ConditionalWeakTable<ComputedStyle, PicSpec> cache = new();
        private static readonly ConditionalWeakTable<Element, ImgEntry> images = new();
        private static readonly HashSet<string> told = new(StringComparer.Ordinal);

        private sealed class ImgEntry { public string Src, Dir; public PicSource Source; }

        /// <summary>A value that may be a picture: starts with url( or sprite(. Cheap, for every box.</summary>
        public static bool IsPicture(string v) => v.Length > 5 && (v[0] is 'u' or 'U' or 's' or 'S')
            && (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || v.StartsWith("sprite(", StringComparison.OrdinalIgnoreCase));

        public static void Tell(string text)
        {
            lock (told)
            {
                if (told.Count > 256) { told.Clear(); }   // a page that keeps making new texts cannot grow the set for ever
                if (!told.Add(text)) { return; }
            }
            UiPlugin.Log(text);
        }

        public static PicSpec Of(ComputedStyle s, string pageDir)
        {
            if (cache.TryGetValue(s, out var d) && d.Same(s, pageDir)) { return d; }
            d = Build(s, pageDir);
            lock (cache) { cache.Remove(s); cache.Add(s, d); }
            return d;
        }

        private bool Same(ComputedStyle s, string pageDir)
        {
            if (!ReferenceEquals(dir, pageDir)) { return false; }
            for (int i = 0; i < Props.Length; i++) { if (!ReferenceEquals(texts[i], s.Get(Props[i]))) { return false; } }
            return true;
        }

        private static PicSpec Build(ComputedStyle s, string pageDir)
        {
            var d = new PicSpec { dir = pageDir, texts = new string[Props.Length] };
            for (int i = 0; i < Props.Length; i++) { d.texts[i] = s.Get(Props[i]); }
            string[] t = d.texts;
            string first = ImageCss.FirstLayer(t[0], out _);
            d.Bg = PicSource.From(first, pageDir);
            if (ImageCss.TryParseSize(ImageCss.FirstLayer(t[1], out _), out var size)) { d.Size = size; }
            if (ImageCss.TryParsePosition(ImageCss.FirstLayer(t[2], out _), out var pos)) { d.Pos = pos; }
            if (ImageCss.TryParseRepeat(ImageCss.FirstLayer(t[3], out _), out var rx, out var ry)) { d.RepX = rx; d.RepY = ry; }
            d.Border = PicSource.From(ImageCss.FirstLayer(t[4], out _), pageDir);
            if (NineSlice.TryParseSlice(t[5], out var slice)) { d.Slice = slice; }
            if (NineSlice.TryParseWidth(t[6], out var width)) { d.Width = width; }
            if (NineSlice.TryParseRepeat(t[7], out var nx, out var ny)) { d.NineX = nx; d.NineY = ny; }
            if (ImageCss.TryParseFit(t[8], out var fit)) { d.Fit = fit; }
            return d;
        }

        /// <summary>The source of an &lt;img src&gt;: a package path (relative to the page) or sprite(kind:id), kept per element while src stays the same text.</summary>
        public static PicSource ImgSource(Element e, string src, string pageDir)
        {
            if (images.TryGetValue(e, out var en) && ReferenceEquals(en.Src, src) && ReferenceEquals(en.Dir, pageDir)) { return en.Source; }
            string value = src.StartsWith("sprite(", StringComparison.OrdinalIgnoreCase) ? src : "url(\"" + src + "\")";
            en = new ImgEntry { Src = src, Dir = pageDir, Source = PicSource.From(value, pageDir) };
            lock (images) { images.Remove(e); images.Add(e, en); }
            return en.Source;
        }
    }
}
