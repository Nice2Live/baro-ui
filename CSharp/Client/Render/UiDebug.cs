using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>Stage 6 tools, drawn after the page in screen pixels with the game's small font:
    /// block outlines and an inspector for the element under the mouse (bbe_ui debug), and the problem banner
    /// at the bottom of the canvas (bbe_ui debug or bbe_ui watch). Off by default: costs nothing then.</summary>
    internal static class UiDebug
    {
        /// <summary>Outlines of every element box and the inspector under the mouse.</summary>
        public static bool Outlines;
        /// <summary>The banner with the window's problems (parse, style, layout, handlers).</summary>
        public static bool Banner;
        internal static bool Active => Outlines || Banner;

        /// <summary>For the self test: what the last draw showed.</summary>
        internal static int LastOutlined, LastBannerLines;
        internal static string LastLabel;
        /// <summary>Inspect this screen point instead of the mouse (self test).</summary>
        internal static Point? Probe;

        private static readonly Color[] DepthColors =
        {
            new(80, 200, 255), new(255, 190, 70), new(150, 255, 120), new(255, 110, 200), new(190, 150, 255),
        };

        internal static void Draw(UiWindow w, SpriteBatch sb)
        {
            if (w.Root == null) { return; }
            if (Outlines)
            {
                LastOutlined = 0;
                var screen = new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
                Walk(w, sb, w.Root, 0, screen, 0);
                Inspect(w, sb);
            }
            DrawBanner(w, sb);
        }

        // ---------------------------------------------------------------- outlines

        private static void Walk(UiWindow w, SpriteBatch sb, LayoutBox b, float dy, Rectangle clip, int depth)
        {
            // where the box is drawn: ScreenRect takes translate, FLIP, scroll and scale of the element and of every block around it (Core/ScreenMap.cs),
            // the same steps the painter takes; an anonymous box (no element) keeps the plain layout place with the scroll above
            var r = b.Element != null ? w.ScreenRect(b.Element) : Painter.SnapRect(b.X, b.Y + dy, b.W, b.H);
            if (b.Element != null && b.Element.Tag != "#root" && r.Width > 0 && r.Height > 0)
            {
                var shown = Rectangle.Intersect(r, clip);
                if (shown.Width > 0 && shown.Height > 0)
                {
                    var c = DepthColors[depth % DepthColors.Length];
                    GUI.DrawRectangle(sb, shown, c * 0.55f, isFilled: false, thickness: 1);
                    LastOutlined++;
                }
            }
            if (b.Element != null) { depth++; }
            if (b.ClipX || b.ClipY)
            {
                clip = Rectangle.Intersect(clip, r);
                if (clip.Width <= 0 || clip.Height <= 0) { return; }
                if (b.Element != null) { dy -= w.GetScroll(b.Element); }
            }
            foreach (var c in b.Children) { Walk(w, sb, c, dy, clip, depth); }
        }

        /// <summary>Margin, padding and content of the element under the mouse, as in a browser's inspector, and a
        /// label: selector, size in u (and px), the main layout values.</summary>
        private static void Inspect(UiWindow w, SpriteBatch sb)
        {
            LastLabel = null;
            var e = w.ElementAt(Probe ?? PlayerInput.MousePosition.ToPoint());
            var b = w.BoxOf(e);
            if (b == null) { return; }
            // the element's offset (translate, FLIP, scroll above: ScreenMap) in the coordinates it is painted in, then ToScreen applies the scale around it
            float dx = 0, dy = -w.GetScrollAbove(e), u = Math.Max(0.01f, w.UnitPx);
            if (w.PlaceOf(e, out var place)) { dx = place.Dx; dy = place.Dy; }
            var border = w.ToScreen(e, Painter.SnapRect(b.X + dx, b.Y + dy, b.W, b.H));
            var margin = w.ToScreen(e, Painter.SnapRect(b.X + dx - b.Margin.Left, b.Y + dy - b.Margin.Top, b.W + b.Margin.Horizontal, b.H + b.Margin.Vertical));
            var padding = w.ToScreen(e, Painter.SnapRect(b.X + dx + b.Border.Left, b.Y + dy + b.Border.Top, b.W - b.Border.Horizontal, b.H - b.Border.Vertical));
            var content = w.ToScreen(e, Painter.SnapRect(b.ContentX + dx, b.ContentY + dy, b.ContentWidth, b.ContentHeight));
            Ring(sb, margin, border, new Color(246, 178, 107) * 0.45f);
            Ring(sb, padding, content, new Color(147, 196, 125) * 0.45f);
            GUI.DrawRectangle(sb, content, new Color(111, 168, 220) * 0.35f, isFilled: true);
            GUI.DrawRectangle(sb, border, Color.White * 0.9f, isFilled: false, thickness: 1);

            string U(float px) => (px / u).ToString("0.#", CultureInfo.InvariantCulture);
            string E(Edges x) => x.Top == x.Bottom && x.Left == x.Right && x.Top == x.Left ? U(x.Top) : $"{U(x.Top)} {U(x.Right)} {U(x.Bottom)} {U(x.Left)}";
            var s = e.Style;
            string display = s?.Get("display") ?? "?";
            if (display == "flex") { display += " " + (s.Get("flex-direction") ?? "row"); }
            string line1 = $"{e}  {U(b.W)}×{U(b.H)}u  ({border.Width}×{border.Height}px)";
            string line2 = $"{display} · padding {E(b.Padding)} · margin {E(b.Margin)} · font {U(s?.FontSizePx ?? 0)}u · стр. {e.Line}";
            LastLabel = line1 + "\n" + line2;

            var font = GUIStyle.SmallFont;
            if (!font.HasValue) { return; }
            var size1 = font.MeasureString(line1);
            var size2 = font.MeasureString(line2);
            int pad = 4, lw = (int)Math.Ceiling(Math.Max(size1.X, size2.X)) + 2 * pad, lh = (int)Math.Ceiling(size1.Y + size2.Y) + 2 * pad;
            int x = Math.Clamp(border.X, 0, Math.Max(0, GameMain.GraphicsWidth - lw));
            int y = border.Y - lh - 2 >= 0 ? border.Y - lh - 2 : Math.Min(border.Bottom + 2, GameMain.GraphicsHeight - lh);
            GUI.DrawRectangle(sb, new Rectangle(x, y, lw, lh), new Color(8, 14, 18) * 0.92f, isFilled: true);
            GUI.DrawRectangle(sb, new Rectangle(x, y, lw, lh), new Color(80, 200, 255), isFilled: false, thickness: 1);
            font.DrawString(sb, line1, new Vector2(x + pad, y + pad), Color.White);
            font.DrawString(sb, line2, new Vector2(x + pad, y + pad + size1.Y), new Color(170, 200, 210));
        }

        /// <summary>Fills the area between an outer and an inner rectangle.</summary>
        private static void Ring(SpriteBatch sb, Rectangle outer, Rectangle inner, Color c)
        {
            if (outer.Width <= 0 || outer.Height <= 0) { return; }
            inner = Rectangle.Intersect(inner, outer);
            Fill(sb, new Rectangle(outer.X, outer.Y, outer.Width, inner.Y - outer.Y), c);
            Fill(sb, new Rectangle(outer.X, inner.Bottom, outer.Width, outer.Bottom - inner.Bottom), c);
            Fill(sb, new Rectangle(outer.X, inner.Y, inner.X - outer.X, inner.Height), c);
            Fill(sb, new Rectangle(inner.Right, inner.Y, outer.Right - inner.Right, inner.Height), c);
        }

        private static void Fill(SpriteBatch sb, Rectangle r, Color c)
        {
            if (r.Width > 0 && r.Height > 0) { sb.Draw(GUI.WhiteTexture, r, c); }
        }

        // ---------------------------------------------------------------- problem banner

        private const int BannerLines = 3;

        private static void DrawBanner(UiWindow w, SpriteBatch sb)
        {
            LastBannerLines = 0;
            var list = w.Problems;
            if (list.Count == 0) { return; }
            var font = GUIStyle.SmallFont;
            if (!font.HasValue) { return; }
            var lines = new List<string> { $"Baro-UI · {w.Name}: замечаний {list.Count} (полный список: baroui problems {w.Name})" };
            for (int i = 0; i < list.Count && i < BannerLines; i++) { lines.Add(list[i].ToString()); }
            if (list.Count > BannerLines) { lines.Add($"… и ещё {list.Count - BannerLines}"); }
            LastBannerLines = lines.Count;

            var canvas = Painter.SnapRect(w.Root.X, w.Root.Y, w.Root.W, w.Root.H);
            float lineH = font.MeasureString("Ag").Y;
            int pad = 6, h = (int)Math.Ceiling(lineH * lines.Count) + 2 * pad;
            var r = new Rectangle(canvas.X, canvas.Bottom - h, canvas.Width, h);
            GUI.DrawRectangle(sb, r, new Color(70, 12, 12) * 0.9f, isFilled: true);
            GUI.DrawRectangle(sb, new Rectangle(r.X, r.Y, r.Width, 2), new Color(255, 90, 80), isFilled: true);
            for (int i = 0; i < lines.Count; i++)
            {
                var color = i == 0 ? new Color(255, 200, 190) : new Color(255, 235, 230);
                font.DrawString(sb, Clip(font, lines[i], r.Width - 2 * pad), new Vector2(r.X + pad, r.Y + pad + i * lineH), color);
            }
        }

        private static string Clip(GUIFont font, string text, float width)
        {
            if (font.MeasureString(text).X <= width) { return text; }
            int n = text.Length;
            while (n > 1 && font.MeasureString(text.Substring(0, n) + "…").X > width) { n = n * 9 / 10; }
            return text.Substring(0, n) + "…";
        }
    }
}
