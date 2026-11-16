using System;
using System.Collections.Generic;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>A path the kit strokes: the caller fills <see cref="Raw"/> (its points, in the canvas's pixels) and reuses the list; the kit makes the rounded
    /// corners and the segments only when the points, the width or the corner radius changed (the geometry cache, Core <see cref="GeometryCache"/>), so a
    /// steady frame does no geometry work and allocates nothing.</summary>
    public sealed class UiPath
    {
        /// <summary>The points of the path, to be filled by the caller (cleared by the caller, never replaced).</summary>
        public readonly List<DPt> Raw = new();
        internal readonly List<DPt> Curve = new();
        internal readonly List<DSeg> Segs = new();
        internal readonly List<DSeg> Dashes = new();
        internal readonly GeometryCache Cache = new();

        /// <summary>How many times the geometry was made again (a changed path, width or corner).</summary>
        public int Builds => Cache.Builds;
    }

    /// <summary>The drawing kit of the canvases (docs/ui/DESIGN_CHARTS.md 2.1): strokes with round joins and quarter-ring corners, dashes with a running phase,
    /// dots, rings and discs, arcs and sectors, the solid area under a curve, text in the engine's fonts. The window hands it its texture budget (the
    /// textures of the kit are kept in it, and go through the graveyard when it takes one back); the map of the mod manager runs its own kit on its own budget.
    /// Authors reach it from a canvas callback: <c>window.Drawing</c>. Colours are straight (the game's SpriteBatch blends NonPremultiplied).</summary>
    public sealed class UiDraw : IDisposable
    {
        private readonly TextureCache<int> lineTex;
        private readonly TextureCache<(int outer, int inner)> discTex;
        private readonly TextureCache<(int radius, int inner)> cornerTex;
        private readonly TextureLedger budget;
        private readonly List<DSpan> spans = new();
        private readonly List<DPt> polygon = new();
        private readonly List<DPt> ring = new();

        public UiDraw(TextureLedger textureBudget)
        {
            budget = textureBudget;
            lineTex = new TextureCache<int>(budget);
            discTex = new TextureCache<(int, int)>(budget);
            cornerTex = new TextureCache<(int, int)>(budget);
        }

        /// <summary>The phase of the dash pattern at the end of the last dashed piece: a path that goes on in the next piece continues the pattern. Reset to 0
        /// to start a pattern over.</summary>
        public double DashPhase { get; set; }

        /// <summary>A new frame of the owner: the textures used from now on count as used in this frame (the budget is shared, so the owner calls it once).</summary>
        public void BeginFrame() => budget.BeginFrame();

        // ------------------------------------------------------------------ paths

        /// <summary>Strokes the path: the segments and round joins (a disc at each vertex, so no notch shows where two pieces meet), with the right-angle corners
        /// rounded into quarter rings of corner px (0: sharp corners, the joins still round). dashOn and dashOff in px (both 0: solid), the pattern runs on
        /// from DashPhase when continuePhase is true, else it starts at 0. Caps: the ends of the path are flat.</summary>
        public void Stroke(SpriteBatch sb, UiPath path, float width, Color col, double corner = 0, double dashOn = 0, double dashOff = 0, bool continuePhase = false)
        {
            Prepare(path, width, corner);
            if (width <= 0 || path.Curve.Count < 2) { return; }
            if (dashOn > 0 && dashOff >= 0)
            {
                if (!continuePhase) { DashPhase = 0; }
                DashPhase = DrawGeometry.WalkDashes(path.Curve, dashOn, dashOff, DashPhase, path.Dashes);
                foreach (var d in path.Dashes) { Line(sb, Pt(d.A), Pt(d.B), width, col, false); }
                return;
            }
            for (int i = 0; i < path.Segs.Count; i++)
            {
                var s = path.Segs[i];
                Vector2 a = Pt(s.A), b = Pt(s.B);
                // each piece but the last runs a little past its end: the flat ends of two turned pieces left a wedge between them on the outer side of
                // a gentle turn, and now and then a pixel centre fell into it (a one-pixel notch); the overlap is too thin to brighten the line
                if (i + 1 < path.Segs.Count)
                {
                    var d = b - a;
                    float len = d.Length();
                    if (len > 1e-3f) { b += d * (Math.Min(JoinOverlap, len * 0.5f) / len); }
                }
                Line(sb, a, b, width, col, false);
            }
            // the joins: a disc at a vertex where the path turns (round joins, no notch). A sampled smooth curve has a vertex every few pixels and turns
            // by a degree or two at each: a disc there (an integer radius at a whole pixel) stuck out of the line every few pixels, a saw along the curve.
            // Below 15 degrees the gap on the outer side is under a quarter of a pixel for a line of 2 px, so no disc; where one is drawn it is placed exactly.
            int r = Math.Max(1, (int)Math.Round(width / 2.0));
            for (int i = 1; i + 1 < path.Curve.Count; i++)
            {
                DPt a = path.Curve[i - 1], b = path.Curve[i], c = path.Curve[i + 1];
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - b.X, vy = c.Y - b.Y;
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu > 1e-9 && lv > 1e-9 && (ux * vx + uy * vy) / (lu * lv) > JoinSkipCos) { continue; }
                StampExact(sb, Pt(b), DiscTex(r, 0), r, col);
            }
        }

        /// <summary>The geometry of the path for the width and corner, made again only when the points or those changed.</summary>
        private static void Prepare(UiPath path, float width, double corner)
        {
            ulong key = DrawGeometry.Mix(DrawGeometry.Mix(DrawGeometry.Hash(path.Raw), width), corner);
            if (!path.Cache.Need(key, path.Raw.Count, 0)) { return; }
            DrawGeometry.Fillet(path.Raw, corner, path.Curve);
            DrawGeometry.Segments(path.Curve, path.Segs);
        }

        // ------------------------------------------------------------------ dots and rings

        /// <summary>A dot at each point of the path, of radius px (the markers of a curve).</summary>
        public void Dots(SpriteBatch sb, UiPath path, float radius, Color col)
        {
            int r = Math.Max(1, (int)Math.Round(radius));
            for (int i = 0; i < path.Raw.Count; i++) { Disc(sb, Pt(path.Raw[i]), r, col); }
        }

        /// <summary>A filled disc: the soft-edged picture of its size around the middle (1:1 on the pixels).</summary>
        public void Disc(SpriteBatch sb, Vector2 c, int rad, Color col) => Stamp(sb, c, DiscTex(rad, 0), rad, col);

        /// <summary>A ring of the outer and inner radius.</summary>
        public void Ring(SpriteBatch sb, Vector2 c, int outer, int inner, Color col) => Stamp(sb, c, DiscTex(outer, inner), outer, col);

        private static void Stamp(SpriteBatch sb, Vector2 c, Texture2D tex, int outer, Color col)
            => sb.Draw(tex, new Rectangle((int)Math.Round(c.X) - outer - 1, (int)Math.Round(c.Y) - outer - 1, tex.Width, tex.Height), col);

        /// <summary>How far (px) a piece of a stroke runs past its end into the next one.</summary>
        private const float JoinOverlap = 0.5f;

        /// <summary>cos 15°: a join that turns less than this needs no round disc.</summary>
        private const double JoinSkipCos = 0.9659;

        /// <summary>A stamp at its exact sub-pixel middle (the linear sampler blends it), for joins that must sit on a soft line's centre.</summary>
        private static void StampExact(SpriteBatch sb, Vector2 c, Texture2D tex, int outer, Color col)
            => sb.Draw(tex, new Vector2(c.X - outer - 1.5f, c.Y - outer - 1.5f), col);

        // ------------------------------------------------------------------ arcs, sectors, areas

        /// <summary>A solid sector of the circle about (cx, cy): angles in radians, 0 at the top, clockwise. Drawn as pixel rows (the spans of the polygon).</summary>
        public void Sector(SpriteBatch sb, float cx, float cy, float r, double a0, double a1, Color col)
        {
            DrawGeometry.Sector(cx, cy, r, a0, a1, DrawGeometry.StepsFor(r, a1 - a0), polygon);
            Fill(sb, polygon, col);
        }

        /// <summary>An arc of the given width (round ends are not drawn: flat caps), angles as in <see cref="Sector"/>.</summary>
        public void Arc(SpriteBatch sb, UiPath path, float cx, float cy, float r, double a0, double a1, float width, Color col)
        {
            DrawGeometry.Arc(cx, cy, r, a0, a1, DrawGeometry.StepsFor(r, a1 - a0), path.Raw);
            Stroke(sb, path, width, col);
        }

        /// <summary>The solid area under a curve: its points and the base line at baseY (the pixel row the area ends on), drawn as pixel rows.</summary>
        public void Area(SpriteBatch sb, List<DPt> curve, double baseY, Color col)
        {
            DrawGeometry.AreaOutline(curve, baseY, polygon);
            Fill(sb, polygon, col);
        }

        /// <summary>A filled polygon by its spans: each pixel row is drawn as rectangles of the runs inside it.</summary>
        public void Fill(SpriteBatch sb, List<DPt> outline, Color col)
        {
            DrawGeometry.Spans(outline, spans);
            foreach (var s in spans)
            {
                int x0 = (int)Math.Round(s.X0), x1 = (int)Math.Round(s.X1);
                if (x1 > x0) { sb.Draw(GUI.WhiteTexture, new Rectangle(x0, s.Y, x1 - x0, 1), col); }
            }
        }

        // ------------------------------------------------------------------ text

        /// <summary>Text in the engine's font of the style, aligned: 'l' starts at x, 'c' centres on x, 'r' ends at x; baseline is the pixel row of the base.</summary>
        public void Text(SpriteBatch sb, UiFonts fonts, ComputedStyle style, string text, float x, float baseline, Color col, char align = 'l')
        {
            if (string.IsNullOrEmpty(text) || fonts == null) { return; }
            float w = fonts.Width(text, style);
            float left = align == 'c' ? x - w / 2 : align == 'r' ? x - w : x;
            fonts.Draw(sb, text, (float)Math.Round(left), baseline, col, style);
        }

        // ------------------------------------------------------------------ the pieces the map is made of

        /// <summary>A rectangle outline of t px, optionally dashed (dashOn, dashOff in px).</summary>
        public void Outline(SpriteBatch sb, Rectangle b, int t, Color c, double dashOn, double dashOff)
        {
            if (dashOn <= 0)
            {
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X, b.Y, b.Width, t), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X, b.Bottom - t, b.Width, t), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X, b.Y + t, t, Math.Max(0, b.Height - 2 * t)), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.Right - t, b.Y + t, t, Math.Max(0, b.Height - 2 * t)), c);
                return;
            }
            DashRun(sb, b.X, b.Y, b.Width, t, true, c, dashOn, dashOff);
            DashRun(sb, b.X, b.Bottom - t, b.Width, t, true, c, dashOn, dashOff);
            DashRun(sb, b.X, b.Y + t, Math.Max(0, b.Height - 2 * t), t, false, c, dashOn, dashOff);
            DashRun(sb, b.Right - t, b.Y + t, Math.Max(0, b.Height - 2 * t), t, false, c, dashOn, dashOff);
        }

        /// <summary>A rectangle with round corners, filled: the cross of three plain rectangles and a quarter of a disc at each corner.</summary>
        public void FillRound(SpriteBatch sb, Rectangle b, int rr, Color c)
        {
            sb.Draw(GUI.WhiteTexture, new Rectangle(b.X + rr, b.Y, b.Width - 2 * rr, b.Height), c);
            sb.Draw(GUI.WhiteTexture, new Rectangle(b.X, b.Y + rr, rr, b.Height - 2 * rr), c);
            sb.Draw(GUI.WhiteTexture, new Rectangle(b.Right - rr, b.Y + rr, rr, b.Height - 2 * rr), c);
            Corners(sb, b, rr, CornerTex(rr, 0), c);
        }

        /// <summary>The edge of a rectangle with round corners: the four sides between the corners (plain or dashed) and a quarter ring at each corner.</summary>
        public void RingRound(SpriteBatch sb, Rectangle b, int rr, int t, Color c, double dashOn, double dashOff)
        {
            t = Math.Max(1, Math.Min(t, rr));
            int w = b.Width - 2 * rr, h = b.Height - 2 * rr;
            if (dashOn <= 0)
            {
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X + rr, b.Y, w, t), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X + rr, b.Bottom - t, w, t), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.X, b.Y + rr, t, h), c);
                sb.Draw(GUI.WhiteTexture, new Rectangle(b.Right - t, b.Y + rr, t, h), c);
            }
            else
            {
                DashRun(sb, b.X + rr, b.Y, w, t, true, c, dashOn, dashOff);
                DashRun(sb, b.X + rr, b.Bottom - t, w, t, true, c, dashOn, dashOff);
                DashRun(sb, b.X, b.Y + rr, h, t, false, c, dashOn, dashOff);
                DashRun(sb, b.Right - t, b.Y + rr, h, t, false, c, dashOn, dashOff);
            }
            Corners(sb, b, rr, CornerTex(rr, rr - t), c);
        }

        private static void Corners(SpriteBatch sb, Rectangle b, int rr, Texture2D tex, Color c)
        {
            sb.Draw(tex, new Rectangle(b.X, b.Y, rr, rr), null, c, 0f, Vector2.Zero, SpriteEffects.None, 0f);
            sb.Draw(tex, new Rectangle(b.Right - rr, b.Y, rr, rr), null, c, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 0f);
            sb.Draw(tex, new Rectangle(b.X, b.Bottom - rr, rr, rr), null, c, 0f, Vector2.Zero, SpriteEffects.FlipVertically, 0f);
            sb.Draw(tex, new Rectangle(b.Right - rr, b.Bottom - rr, rr, rr), null, c, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically, 0f);
        }

        /// <summary>Dashes along a straight side on the pixel grid, the pattern fitted so a dash sits at both ends.</summary>
        private static void DashRun(SpriteBatch sb, int x, int y, int len, int thick, bool horizontal, Color c, double dashOn, double dashOff)
        {
            if (len <= 0) { return; }
            int dash = Math.Max(1, (int)Math.Round(dashOn)), gap = Math.Max(1, (int)Math.Round(dashOff));
            int n = Math.Max(1, (int)Math.Round((len + gap) / (double)(dash + gap)));
            double pitch = n > 1 ? (len - dash) / (double)(n - 1) : 0;
            for (int i = 0; i < n; i++)
            {
                int off = (int)Math.Round(i * pitch), piece = n == 1 ? len : Math.Min(dash, len - off);
                sb.Draw(GUI.WhiteTexture, horizontal ? new Rectangle(x + off, y, piece, thick) : new Rectangle(x, y + off, thick, piece), c);
            }
        }

        /// <summary>A piece of a path from a to b: a solid line (cap: the ends are extended by half the width, to meet a corner), or dashes walked along it;
        /// restart puts the dash pattern back to 0 (the first piece of a path).</summary>
        public void Segment(SpriteBatch sb, Vector2 a, Vector2 b, float width, Color col, double dashOn, double dashOff, bool restart, bool cap = true)
        {
            if (restart) { DashPhase = 0; }
            var d = b - a;
            float len = d.Length();
            if (len < 0.5f) { return; }
            if (dashOn <= 0 || dashOff < 0) { Line(sb, a, b, width, col, cap); return; }
            double total = dashOn + dashOff, at = 0;
            while (at < len)
            {
                double phase = DashPhase % total;
                bool on = phase < dashOn;
                double piece = Math.Min(len - at, on ? dashOn - phase : total - phase);
                // a step lost to rounding (the phase a hair under the end of a dash) would never move on: at least a thousandth of a pixel
                if (piece < 1e-3) { piece = Math.Min(len - at, 1e-3); if (piece <= 0) { break; } }
                if (on) { Line(sb, a + d * (float)(at / len), a + d * (float)((at + piece) / len), width, col, false); }
                at += piece;
                DashPhase += piece;
            }
        }

        /// <summary>A straight piece. Horizontal or vertical: a rectangle on whole pixels (centred on the wire, the ends squared off so a corner has no notch),
        /// sharp. Any other angle: the soft line profile stretched along it and turned, which the linear sampler smooths into an even line.</summary>
        public void Line(SpriteBatch sb, Vector2 a, Vector2 b, float width, Color col, bool cap)
        {
            var d = b - a;
            int wi = Math.Max(1, (int)Math.Round(width));
            int ext = cap ? wi / 2 : 0;
            if (Math.Abs(d.Y) < 0.01f)
            {
                int x0 = (int)Math.Round(Math.Min(a.X, b.X)) - ext, x1 = (int)Math.Round(Math.Max(a.X, b.X)) + ext, y = (int)Math.Floor(a.Y - wi / 2f + 0.5f);
                sb.Draw(GUI.WhiteTexture, new Rectangle(x0, y, Math.Max(1, x1 - x0), wi), col);
                return;
            }
            if (Math.Abs(d.X) < 0.01f)
            {
                int y0 = (int)Math.Round(Math.Min(a.Y, b.Y)) - ext, y1 = (int)Math.Round(Math.Max(a.Y, b.Y)) + ext, x = (int)Math.Floor(a.X - wi / 2f + 0.5f);
                sb.Draw(GUI.WhiteTexture, new Rectangle(x, y0, wi, Math.Max(1, y1 - y0)), col);
                return;
            }
            var tex = LineTex(Math.Max(4, (int)Math.Round(width * 4)));
            float len = d.Length(), ang = (float)Math.Atan2(d.Y, d.X);
            sb.Draw(tex, a, null, col, ang, new Vector2(0, tex.Height / 2f), new Vector2(len, 1f), SpriteEffects.None, 0f);
        }

        /// <summary>A solid laid wire whose legs all run across or down the screen: each leg a rectangle on whole pixels, each turn a quarter of a ring of the
        /// corner radius (cornerPx, at most half a leg, as the map has it), put so its two ends meet the bands of the legs pixel for pixel. The way is the
        /// corners in pixels (already cleaned: no repeats, no point in the middle of a run). False, drawing nothing: fewer than two corners, a slanted leg or a
        /// turn back.</summary>
        public bool Bent(SpriteBatch sb, IReadOnlyList<Vector2> way, float width, Color col, double cornerPx)
        {
            if (way == null || way.Count < 2) { return false; }
            int n = way.Count;
            for (int i = 1; i < n; i++)
            {
                if (Axis(way[i - 1], way[i]) < 0) { return false; }
                if (i > 1 && Axis(way[i - 2], way[i - 1]) == Axis(way[i - 1], way[i])) { return false; } // a turn back
            }
            int wi = Math.Max(1, (int)Math.Round(width)), ext = wi / 2;
            var from = legFrom; var to = legTo;
            from.Clear(); to.Clear();
            for (int i = 1; i < n; i++)
            {
                bool hor = Axis(way[i - 1], way[i]) == 0;
                float a = hor ? way[i - 1].X : way[i - 1].Y, b = hor ? way[i].X : way[i].Y;
                int sgn = b >= a ? 1 : -1;
                from.Add((int)Math.Round(a) - sgn * ext);
                to.Add((int)Math.Round(b) + sgn * ext);
            }
            for (int i = 1; i < n - 1; i++)
            {
                // the corner way[i] between leg i-1 (into it) and leg i (out of it); hp and vp: the far ends of its horizontal and its vertical leg
                bool inHor = Axis(way[i - 1], way[i]) == 0;
                var hp = inHor ? way[i - 1] : way[i + 1];
                var vp = inHor ? way[i + 1] : way[i - 1];
                double q = Math.Min(cornerPx, Math.Min(Hyp(way[i], way[i - 1]) / 2, Hyp(way[i], way[i + 1]) / 2));
                int hy = (int)Math.Floor(way[i].Y - wi / 2f + 0.5f), vx = (int)Math.Floor(way[i].X - wi / 2f + 0.5f);
                int ro = Math.Max(wi, (int)Math.Round(q + wi / 2.0)), ri = ro - wi;
                // the vertical leg goes down: the ring is on the top side of the turn; the horizontal leg comes from the left: on its right side
                bool top = vp.Y > way[i].Y, right = hp.X < way[i].X;
                int ox = right ? vx + wi : vx, oy = top ? hy : hy + wi;
                int cx = right ? ox - ro : ox + ro, cy = top ? oy + ro : oy - ro;
                var box = new Rectangle(right ? ox - ro : ox, top ? oy : oy - ro, ro, ro);
                var fx = (right ? SpriteEffects.FlipHorizontally : SpriteEffects.None) | (top ? SpriteEffects.None : SpriteEffects.FlipVertically);
                sb.Draw(CornerTex(ro, ri), box, null, col, 0f, Vector2.Zero, fx, 0f);
                // the legs stop where the ring begins
                if (inHor) { to[i - 1] = cx; from[i] = cy; } else { to[i - 1] = cy; from[i] = cx; }
            }
            for (int i = 1; i < n; i++)
            {
                int lo = Math.Min(from[i - 1], to[i - 1]), hi = Math.Max(from[i - 1], to[i - 1]);
                if (hi <= lo) { continue; }
                if (Axis(way[i - 1], way[i]) == 0) { sb.Draw(GUI.WhiteTexture, new Rectangle(lo, (int)Math.Floor(way[i].Y - wi / 2f + 0.5f), hi - lo, wi), col); }
                else { sb.Draw(GUI.WhiteTexture, new Rectangle((int)Math.Floor(way[i].X - wi / 2f + 0.5f), lo, wi, hi - lo), col); }
            }
            return true;
        }

        private readonly List<int> legFrom = new(), legTo = new();

        /// <summary>0: the piece runs across, 1: down, -1: slanted (or no length).</summary>
        public static int Axis(Vector2 a, Vector2 b)
        {
            bool sx = Math.Abs(a.X - b.X) < 0.01f, sy = Math.Abs(a.Y - b.Y) < 0.01f;
            return sy && !sx ? 0 : sx && !sy ? 1 : -1;
        }

        private static double Hyp(Vector2 a, Vector2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static Vector2 Pt(DPt p) => new Vector2((float)p.X, (float)p.Y);

        // ------------------------------------------------------------------ textures (in the budget; freed through the graveyard)

        /// <summary>The cross-section of a line of the width (in quarter pixels): a column of texels with the exact coverage of the line in alpha and an empty
        /// texel at each side (the sampler wraps).</summary>
        private Texture2D LineTex(int quarters)
        {
            if (lineTex.TryGet(quarters, out var tex)) { return tex; }
            float w = quarters / 4f;
            int h = (int)Math.Ceiling(w) + 2;
            float mid = h / 2f;
            var data = new Color[h];
            for (int y = 0; y < h; y++)
            {
                float cover = Math.Max(0f, Math.Min(y + 1, mid + w / 2) - Math.Max(y, mid - w / 2));
                data[y] = new Color((byte)255, (byte)255, (byte)255, (byte)Math.Round(255f * cover));
            }
            tex = new Texture2D(GameMain.Instance.GraphicsDevice, 1, h);
            tex.SetData(data);
            lineTex.Put(quarters, tex, (long)h * 4);
            return tex;
        }

        /// <summary>White texels with the coverage in alpha (straight), analytic (Core/Coverage.cs): a disc of radius outer + 0.5 around the middle texel,
        /// less the hole of radius inner + 0.5.</summary>
        private Texture2D DiscTex(int outer, int inner)
        {
            if (discTex.TryGet((outer, inner), out var tex)) { return tex; }
            int size = 2 * outer + 3;
            float mid = outer + 1.5f, big = outer + 0.5f, hole = inner > 0 ? inner + 0.5f : 0f;
            var data = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - mid, dy = y + 0.5f - mid;
                    float cov = Coverage.Ring(MathF.Sqrt(dx * dx + dy * dy), big, hole);
                    data[y * size + x] = new Color((byte)255, (byte)255, (byte)255, Coverage.ToByte(cov));
                }
            }
            tex = new Texture2D(GameMain.Instance.GraphicsDevice, size, size);
            tex.SetData(data);
            discTex.Put((outer, inner), tex, (long)size * size * 4);
            return tex;
        }

        /// <summary>A quarter of a disc (radius rr, the upper left corner of a box), less the hole of radius inner for a ring; analytic coverage (Core/Coverage.cs).</summary>
        private Texture2D CornerTex(int rr, int inner)
        {
            if (cornerTex.TryGet((rr, inner), out var tex)) { return tex; }
            var data = new Color[rr * rr];
            for (int y = 0; y < rr; y++)
            {
                for (int x = 0; x < rr; x++)
                {
                    float dx = x + 0.5f - rr, dy = y + 0.5f - rr;
                    float cov = Coverage.Ring(MathF.Sqrt(dx * dx + dy * dy), rr, inner);
                    data[y * rr + x] = new Color((byte)255, (byte)255, (byte)255, Coverage.ToByte(cov));
                }
            }
            tex = new Texture2D(GameMain.Instance.GraphicsDevice, rr, rr);
            tex.SetData(data);
            cornerTex.Put((rr, inner), tex, (long)rr * rr * 4);
            return tex;
        }

        /// <summary>Lets the kit's textures go (the owner closes): through the graveyard, like every texture of the budget.</summary>
        public void Dispose()
        {
            lineTex.Clear();
            discTex.Clear();
            cornerTex.Clear();
        }
    }
}
