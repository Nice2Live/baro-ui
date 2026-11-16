using System;
using System.Collections.Generic;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace BaroUI.Render
{
    /// <summary>&lt;textarea&gt;: the page's multi-line code editor (docs/ui/AUTHORING.md, "Code editor"). The editing is Core/CodeEdit.cs, the colours
    /// Core/CodeLexer.cs; this is what needs the game: painting the visible lines (the other thousands cost nothing), the mouse (caret, drag selection,
    /// double and triple click, the bars, the wheel), the keyboard the focused field receives from UiControls, and the model (the value attribute,
    /// data-model, oninput, onchange, onsave). One view per element, made on first use and kept while the window lives.</summary>
    internal sealed class UiCodeEdit
    {
        private const int MaxRunCache = 600, LongRun = 4096;
        private static string[] numbers = new string[256];

        private sealed class Run
        {
            public string Line;
            public CodeToken[] Tokens;
            public int Count;
            public string[] Text = new string[8];
            public int[] VCol = new int[8];
            public CodeKind[] Kind = new CodeKind[8];
            public int VEnd;
        }

        private sealed class Theme
        {
            public Rgba Fg, Gutter, Line, Selection;
            public readonly Rgba[] Kinds = new Rgba[12];
        }

        private sealed class View
        {
            public Element E;
            public readonly CodeEdit Ed = new();
            public CodeHighlighter Hl;
            public float ScrollY, ScrollX;
            public bool Reveal;
            public double Blink;
            public string SyncedValue, ValueAtFocus;
            public int SyncedTextVersion;
            public readonly ClickCounter Clicks = new();
            // the mouse
            public bool Dragging;
            public int DragMode;
            public CodePos DragFirst, DragLast;
            public int BarDrag;
            public float BarGrab, BarFrom;
            // from the style
            public ComputedStyle Style;
            public float Cell, LineH, Ascent, Shift;
            public Theme Theme;
            public bool AttrReadOnly;
            // where it was painted
            public Rectangle Content, Text, Gutter, VTrack, VThumb, HTrack, HThumb;
            public bool VBar, HBar;
            public int MaxCols, MaxColsVersion = -1, Digits;
            public readonly Dictionary<int, Run> Runs = new();
        }

        private readonly UiWindow w;
        private readonly UiControls controls;
        private readonly Dictionary<Element, View> views = new();
        /// <summary>The same views as an array the frame walks: oninput may reach for another editor while it runs.</summary>
        private View[] list = Array.Empty<View>();

        internal UiCodeEdit(UiWindow window, UiControls owner) { w = window; controls = owner; }

        public static bool Is(Element e) => e is { Tag: "textarea" };

        // ---------------------------------------------------------------- the view of an element

        /// <summary>The editing model of a textarea (the host changes the text, the caret or the selection through it).</summary>
        public CodeEdit Model(Element e) => e != null && Is(e) ? Get(e).Ed : null;

        /// <summary>Where the editor was painted (the self test aims its mouse by it): the text area, the width of a column, the height of a line, the scroll.</summary>
        internal (Rectangle Text, float Cell, float LineH, float ScrollY, float ScrollX) Probe(Element e) =>
            e != null && views.TryGetValue(e, out var v) ? (v.Text, v.Cell, v.LineH, v.ScrollY, v.ScrollX) : default;

        public void Reveal(Element e) { if (e != null && Is(e)) { Get(e).Reveal = true; } }

        private View Get(Element e)
        {
            if (views.TryGetValue(e, out var v)) { return v; }
            if (views.Count > 64) { Purge(); }
            v = new View { E = e };
            v.Hl = new CodeHighlighter(v.Ed, CodeLang.None);
            v.Ed.SetText(e.Value);
            v.SyncedValue = e.Value;
            v.SyncedTextVersion = v.Ed.TextVersion;
            Options(v);
            views[e] = v;
            Rebuild();
            return v;
        }

        private void Rebuild()
        {
            var next = new View[views.Count];
            views.Values.CopyTo(next, 0);
            list = next;
        }

        private void Purge()
        {
            var gone = new List<Element>();
            foreach (var kv in views) { if (w.BoxOf(kv.Key) == null && controls.Focus != kv.Key) { gone.Add(kv.Key); } }
            foreach (var e in gone) { views[e].Hl.Detach(); views.Remove(e); }
            Rebuild();
        }

        /// <summary>The attributes that set the editor up, read again every frame: data-lang, tabsize, data-tabs, data-autoindent, readonly. An attribute that is not
        /// there leaves what the host set through the model (GetCodeEdit) alone; readonly follows the attribute (and disabled) when that changes.</summary>
        private void Options(View v)
        {
            var e = v.E;
            var lang = CodeLexer.LangOf(e.GetAttribute("data-lang"));
            if (v.Hl.Lang != lang) { v.Hl.Lang = lang; v.Runs.Clear(); }
            if (int.TryParse(e.GetAttribute("tabsize"), out int tab) && tab is >= 1 and <= 16) { v.Ed.TabSize = tab; }
            if (e.GetAttribute("data-tabs") is { } tabs) { v.Ed.SpacesForTab = tabs != "tab"; }
            if (e.GetAttribute("data-autoindent") is { } auto) { v.Ed.AutoIndent = auto != "off"; }
            bool readOnly = e.GetAttribute("readonly") != null || UiWindow.IsDisabled(e);
            if (readOnly != v.AttrReadOnly) { v.AttrReadOnly = readOnly; v.Ed.ReadOnly = readOnly; }
        }

        // ---------------------------------------------------------------- the model

        /// <summary>The value attribute changed under the editor (a model refresh, a script): the editor shows it. Edits not yet written are written first.</summary>
        private void SyncIn(View v)
        {
            var e = v.E;
            if (v.Ed.TextVersion != v.SyncedTextVersion) { Flush(v); return; }
            string val = e.Value;
            if (ReferenceEquals(val, v.SyncedValue)) { return; }
            v.SyncedValue = val;
            if (val != v.Ed.Text)
            {
                v.Ed.SetText(val);
                v.Runs.Clear();
            }
            v.SyncedTextVersion = v.Ed.TextVersion;
        }

        /// <summary>The text of the editor goes to the value attribute and the model, and oninput runs: at most once a frame however many keys were typed.</summary>
        private void Flush(View v)
        {
            if (v.Ed.TextVersion == v.SyncedTextVersion) { return; }
            var e = v.E;
            v.SyncedTextVersion = v.Ed.TextVersion;
            string text = v.Ed.Text;
            e.SetAttribute("value", text);
            v.SyncedValue = e.Value;
            if (e.GetAttribute("data-model") != null && w.Template.WriteModel(e, text)) { w.Refresh(); }
            w.Fire(e, "oninput");
        }

        // ---------------------------------------------------------------- guards
        // These are called from the game's keyboard and from the window's update: a fault in the editor is logged once, never thrown into them.

        private readonly HashSet<string> faults = new();

        private void Fault(Exception ex)
        {
            if (faults.Add(ex.GetType().Name + ex.Message)) { UiPlugin.Log($"{w.Name}: <textarea>: {ex}"); }
        }

        public void OnFocus(Element e) { try { OnFocusCore(e); } catch (Exception ex) { Fault(ex); } }
        public void OnBlur(Element e) { try { OnBlurCore(e); } catch (Exception ex) { Fault(ex); } }
        public void Frame() { try { FrameCore(); } catch (Exception ex) { Fault(ex); } }
        public void TextInput(Element e, char c) { try { TextInputCore(e, c); } catch (Exception ex) { Fault(ex); } }
        public void TextInput(Element e, string text) { try { TextInputCore(e, text); } catch (Exception ex) { Fault(ex); } }
        public void Command(Element e, char command) { try { CommandCore(e, command); } catch (Exception ex) { Fault(ex); } }
        public void Special(Element e, Keys key, bool shift, bool ctrl) { try { SpecialCore(e, key, shift, ctrl); } catch (Exception ex) { Fault(ex); } }
        public bool MouseDown(Element target, Point p) { try { return MouseDownCore(target, p); } catch (Exception ex) { Fault(ex); return false; } }
        public bool Wheel(Point p, float notches) { try { return WheelCore(p, notches); } catch (Exception ex) { Fault(ex); return false; } }

        // ---------------------------------------------------------------- focus and the frame

        private void OnFocusCore(Element e)
        {
            var v = Get(e);
            Options(v);
            SyncIn(v);
            v.ValueAtFocus = e.Value;
            v.Blink = Timing.TotalTime;
            v.Ed.BreakUndo();
        }

        private void OnBlurCore(Element e)
        {
            if (!views.TryGetValue(e, out var v)) { return; }
            v.Dragging = false;
            v.BarDrag = 0;
            Flush(v);
            if (!ReferenceEquals(e.Value, v.ValueAtFocus) && e.Value != v.ValueAtFocus) { v.ValueAtFocus = e.Value; w.Fire(e, "onchange"); }
        }

        /// <summary>Every frame from the window: drags go on, typed text reaches the model.</summary>
        private void FrameCore()
        {
            var all = list;
            for (int i = 0; i < all.Length; i++)
            {
                var v = all[i];
                if (v.Ed.TextVersion != v.SyncedTextVersion) { Flush(v); }
                if (v.Dragging || v.BarDrag != 0) { Drag(v); }
            }
        }

        public void Dispose()
        {
            foreach (var v in list) { v.Hl.Detach(); }
            views.Clear();
            list = Array.Empty<View>();
        }

        // ---------------------------------------------------------------- keyboard

        private void TextInputCore(Element e, char c)
        {
            var v = Get(e);
            if (v.Ed.Type(c)) { Typed(v); }
        }

        private void TextInputCore(Element e, string text)
        {
            var v = Get(e);
            if (v.Ed.Insert(text)) { Typed(v); }
        }

        private void Typed(View v)
        {
            v.Reveal = true;
            v.Blink = Timing.TotalTime;
        }

        /// <summary>Control characters: Backspace, ctrl+A, C, X, V, Z, Y and S (the game delivers a key's character here).</summary>
        private void CommandCore(Element e, char command)
        {
            var v = Get(e);
            var ed = v.Ed;
            bool ctrl = PlayerInput.IsCtrlDown(), shift = PlayerInput.IsShiftDown();
            v.Blink = Timing.TotalTime;
            switch (command)
            {
                case '\b': ed.Backspace(ctrl); break;
                case (char)0x7F: ed.Backspace(true); break;
                case (char)0x1: ed.SelectAll(); break;
                case (char)0x3:
                    if (ed.HasSelection) { Clipboard.SetText(ed.SelectedText); }
                    return;
                case (char)0x18:
                    if (ed.HasSelection && !ed.ReadOnly) { Clipboard.SetText(ed.SelectedText); ed.DeleteSelection(); }
                    else if (ed.HasSelection) { Clipboard.SetText(ed.SelectedText); }
                    break;
                case (char)0x16: ed.Insert(Clipboard.GetText() ?? ""); break;
                case (char)0x1A: if (shift) { ed.Redo(); } else { ed.Undo(); } break;
                case (char)0x19: ed.Redo(); break;
                case (char)0x13:
                    Flush(v);
                    w.Fire(e, "onsave");
                    return;
                default: return;
            }
            v.Reveal = true;
        }

        /// <summary>Keys that are no characters: arrows, Home, End, PageUp, PageDown, Delete, Enter, Tab. (Ctrl+Tab leaves the field: UiControls handles it.)</summary>
        private void SpecialCore(Element e, Keys key, bool shift, bool ctrl)
        {
            var v = Get(e);
            var ed = v.Ed;
            v.Blink = Timing.TotalTime;
            int page = Math.Max(1, (int)(v.Text.Height / Math.Max(1f, v.LineH)) - 1);
            switch (key)
            {
                case Keys.Left: ed.Left(ctrl, shift); break;
                case Keys.Right: ed.Right(ctrl, shift); break;
                case Keys.Up: ed.Up(shift); break;
                case Keys.Down: ed.Down(shift); break;
                case Keys.Home: if (ctrl) { ed.DocStart(shift); } else { ed.Home(shift); } break;
                case Keys.End: if (ctrl) { ed.DocEnd(shift); } else { ed.LineEnd(shift); } break;
                case Keys.PageUp: ed.Page(-page, shift); break;
                case Keys.PageDown: ed.Page(page, shift); break;
                case Keys.Delete: ed.Delete(ctrl); break;
                case Keys.Enter: ed.Enter(); break;
                case Keys.Tab: ed.Tab(shift); break;
                default: return;
            }
            v.Reveal = true;
        }

        // ---------------------------------------------------------------- mouse

        private CodePos PosAt(View v, Point p, bool character)
        {
            var ed = v.Ed;
            float y = p.Y - v.Text.Y + v.ScrollY, x = p.X - v.Text.X + v.ScrollX;
            int line = Math.Clamp((int)Math.Floor(y / Math.Max(1f, v.LineH)), 0, ed.LineCount - 1);
            int vcol = (int)(character ? Math.Floor(x / v.Cell) : Math.Round(x / v.Cell));
            return new CodePos(line, CodeEdit.ColFromVisual(ed.Line(line), Math.Max(0, vcol), ed.TabSize));
        }

        /// <summary>The primary button went down over a textarea. True: handled (focus, caret, selection, bar).</summary>
        private bool MouseDownCore(Element target, Point p)
        {
            if (target == null || !Is(target)) { return false; }
            if (UiWindow.IsDisabled(target)) { return true; }
            var v = Get(target);
            controls.SetFocus(target);
            // the view's rectangles are where it was painted, in the box's own coordinates: inside a scaled block the mouse is taken there
            var lp = w.ToLocal(target, new Vector2(p.X, p.Y));
            p = new Point((int)Math.Floor(lp.X), (int)Math.Floor(lp.Y));
            v.Blink = Timing.TotalTime;
            if (v.Content.Width <= 0) { return true; }
            var ed = v.Ed;
            if (v.VBar && v.VTrack.Contains(p))
            {
                if (!v.VThumb.Contains(p)) { ScrollTo(v, ScrollForThumb(v, p.Y - v.VThumb.Height / 2f, true), v.ScrollX); RecalcBars(v); }
                v.BarDrag = 1; v.BarGrab = p.Y - v.VThumb.Y; v.BarFrom = v.ScrollY;
                return true;
            }
            if (v.HBar && v.HTrack.Contains(p))
            {
                if (!v.HThumb.Contains(p)) { ScrollTo(v, v.ScrollY, ScrollForThumb(v, p.X - v.HThumb.Width / 2f, false)); RecalcBars(v); }
                v.BarDrag = 2; v.BarGrab = p.X - v.HThumb.X; v.BarFrom = v.ScrollX;
                return true;
            }
            int clicks = v.Clicks.Count(MouseButtons.Left, p.X, p.Y, Timing.TotalTime);
            bool shift = PlayerInput.IsShiftDown();
            if (v.Gutter.Contains(p))
            {
                var at = PosAt(v, new Point(v.Text.X, p.Y), false);
                ed.LineRange(at.Line, out var a, out var b);
                ed.Select(a, b);
                v.DragMode = 3; v.DragFirst = a; v.DragLast = b; v.Dragging = true;
                v.Reveal = false;
                return true;
            }
            var pos = PosAt(v, p, false);
            if (clicks == 2)
            {
                ed.WordAt(PosAt(v, p, true), out var a, out var b);
                ed.Select(a, b);
                v.DragMode = 2; v.DragFirst = a; v.DragLast = b;
            }
            else if (clicks == 3)
            {
                ed.LineRange(pos.Line, out var a, out var b);
                ed.Select(a, b);
                v.DragMode = 3; v.DragFirst = a; v.DragLast = b;
            }
            else
            {
                ed.MoveTo(pos, shift);
                ed.BreakUndo();
                v.DragMode = 1;
                v.DragFirst = ed.Anchor;
            }
            v.Dragging = true;
            return true;
        }

        /// <summary>While the button is held after a press: extends the selection to the pointer, scrolls when it is past an edge, drags a bar.</summary>
        private void Drag(View v)
        {
            if (!PlayerInput.PrimaryMouseButtonHeld()) { v.Dragging = false; v.BarDrag = 0; return; }
            if (w.BoxOf(v.E) == null) { v.Dragging = false; v.BarDrag = 0; return; }
            var lm = w.ToLocal(v.E, PlayerInput.MousePosition);   // the view's rectangles are in the box's own coordinates (see MouseDownCore)
            var mp = new Point((int)Math.Floor(lm.X), (int)Math.Floor(lm.Y));
            if (v.BarDrag != 0)
            {
                if (v.BarDrag == 1) { ScrollTo(v, ScrollForThumb(v, mp.Y - v.BarGrab, true), v.ScrollX); }
                else { ScrollTo(v, v.ScrollY, ScrollForThumb(v, mp.X - v.BarGrab, false)); }
                return;
            }
            // beyond the top or bottom edge the text scrolls, faster the farther the pointer is
            float dy = mp.Y < v.Text.Y ? mp.Y - v.Text.Y : mp.Y > v.Text.Bottom ? mp.Y - v.Text.Bottom : 0;
            float dx = mp.X < v.Text.X ? mp.X - v.Text.X : mp.X > v.Text.Right ? mp.X - v.Text.Right : 0;
            if (dy != 0 || dx != 0) { ScrollTo(v, v.ScrollY + dy * 0.3f, v.ScrollX + dx * 0.3f); }
            var pos = PosAt(v, mp, false);
            var ed = v.Ed;
            switch (v.DragMode)
            {
                case 1:
                    ed.MoveTo(pos, true);
                    break;
                case 2:
                {
                    ed.WordAt(PosAt(v, mp, true), out var a, out var b);
                    if (a < v.DragFirst) { ed.Select(v.DragLast, a); } else { ed.Select(v.DragFirst, b); }
                    break;
                }
                case 3:
                {
                    ed.LineRange(pos.Line, out var a, out var b);
                    if (a < v.DragFirst) { ed.Select(v.DragLast, a); } else { ed.Select(v.DragFirst, b); }
                    break;
                }
            }
        }

        /// <summary>The wheel over a textarea scrolls it (shift: sideways). False: not over one, or already at the end.</summary>
        private bool WheelCore(Point p, float notches)
        {
            if (views.Count == 0) { return false; }
            var e = w.ElementAt(p);
            if (e == null || !Is(e) || !views.TryGetValue(e, out var v) || v.Content.Width <= 0) { return false; }
            float oy = v.ScrollY, ox = v.ScrollX;
            if (PlayerInput.IsShiftDown()) { ScrollTo(v, v.ScrollY, v.ScrollX - notches * v.Cell * 8); }
            else { ScrollTo(v, v.ScrollY - notches * v.LineH * 3, v.ScrollX); }
            return v.ScrollY != oy || v.ScrollX != ox;
        }

        private float MaxScrollY(View v) => Math.Max(0, v.Ed.LineCount * v.LineH - v.Text.Height);
        private float MaxScrollX(View v) => Math.Max(0, (v.MaxCols + 2) * v.Cell - v.Text.Width);

        private void ScrollTo(View v, float y, float x)
        {
            v.ScrollY = Math.Clamp(y, 0, MaxScrollY(v));
            v.ScrollX = Math.Clamp(x, 0, MaxScrollX(v));
        }

        /// <summary>The scroll that puts the thumb's top (left) edge at a place on its track.</summary>
        private float ScrollForThumb(View v, float edge, bool vertical)
        {
            var track = vertical ? v.VTrack : v.HTrack;
            var thumb = vertical ? v.VThumb : v.HThumb;
            float span = vertical ? track.Height - thumb.Height : track.Width - thumb.Width;
            if (span <= 0) { return 0; }
            float f = ((edge - (vertical ? track.Y : track.X)) / span);
            return Math.Clamp(f, 0, 1) * (vertical ? MaxScrollY(v) : MaxScrollX(v));
        }

        // ---------------------------------------------------------------- paint

        private static string Number(int n)
        {
            if (n >= numbers.Length) { Array.Resize(ref numbers, Math.Max(n + 1, numbers.Length * 2)); }
            return numbers[n] ??= n.ToString();
        }

        private static Rgba Pick(ComputedStyle s, string name, Rgba fallback) =>
            s.Var(name) is { } text && Rgba.TryParse(text.Trim(), out var c) ? c : fallback;

        private static Rgba With(Rgba c, byte alpha) => new(c.R, c.G, c.B, alpha);

        private Theme MakeTheme(ComputedStyle s)
        {
            var t = new Theme { Fg = s.GetColor("color") };
            var accent = s.GetColor("accent-color");
            if (accent.A == 0) { accent = new Rgba(0x40, 0xc8, 0xbe); }
            t.Gutter = Pick(s, "--code-gutter", With(t.Fg, 110));
            t.Line = Pick(s, "--code-line", new Rgba(255, 255, 255, 12));
            t.Selection = Pick(s, "--code-selection", With(accent, 100));
            var k = t.Kinds;
            k[(int)CodeKind.Plain] = t.Fg;
            k[(int)CodeKind.Keyword] = Pick(s, "--code-keyword", new Rgba(0xc5, 0x86, 0xc0));
            k[(int)CodeKind.Type] = Pick(s, "--code-type", new Rgba(0x4e, 0xc9, 0xb0));
            k[(int)CodeKind.Constant] = Pick(s, "--code-constant", new Rgba(0x56, 0x9c, 0xd6));
            k[(int)CodeKind.String] = Pick(s, "--code-string", new Rgba(0xce, 0x91, 0x78));
            k[(int)CodeKind.Number] = Pick(s, "--code-number", new Rgba(0xb5, 0xce, 0xa8));
            k[(int)CodeKind.Comment] = Pick(s, "--code-comment", new Rgba(0x6a, 0x8f, 0x92));
            k[(int)CodeKind.Punct] = Pick(s, "--code-punct", With(t.Fg, 190));
            k[(int)CodeKind.Tag] = Pick(s, "--code-tag", accent);
            k[(int)CodeKind.Attribute] = Pick(s, "--code-attr", new Rgba(0x9c, 0xdc, 0xfe));
            k[(int)CodeKind.Entity] = Pick(s, "--code-entity", new Rgba(0xd7, 0xba, 0x7d));
            k[(int)CodeKind.Directive] = Pick(s, "--code-directive", new Rgba(0x9b, 0x9b, 0xa8));
            return t;
        }

        private static float LineHeightOf(UiFonts fonts, ComputedStyle s)
        {
            string lh = s.Get("line-height");
            float h;
            if (double.TryParse(lh, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor)) { h = (float)(factor * s.FontSizePx); }
            else if (lh != "normal" && s.GetLength("line-height") is { Unit: BaroUI.Core.Unit.Px } len) { h = len.Value; }
            else { h = fonts.NormalLineHeight(s); }
            return Math.Max(1, (float)Math.Round(h));
        }

        private void Metrics(View v, ComputedStyle s)
        {
            if (ReferenceEquals(v.Style, s) && v.Cell > 0) { return; }
            v.Style = s;
            var fonts = w.Fonts;
            v.Cell = Math.Max(1f, fonts.Width("MMMMMMMMMMMMMMMM", s) / 16f);
            v.LineH = LineHeightOf(fonts, s);
            v.Shift = (v.LineH - fonts.NormalLineHeight(s)) / 2f;
            v.Ascent = fonts.Ascent(s);
            v.Theme = MakeTheme(s);
            v.Runs.Clear();
        }

        private int MaxCols(View v)
        {
            var ed = v.Ed;
            if (v.MaxColsVersion == ed.TextVersion) { return v.MaxCols; }
            int max = 0;
            for (int i = 0; i < ed.LineCount; i++)
            {
                string l = ed.Line(i);
                int len = l.Length;
                if (len > max || l.IndexOf('\t') >= 0)
                {
                    int vc = l.IndexOf('\t') >= 0 ? CodeEdit.VisualCol(l, l.Length, ed.TabSize) : len;
                    if (vc > max) { max = vc; }
                }
            }
            v.MaxColsVersion = ed.TextVersion;
            return v.MaxCols = max;
        }

        /// <summary>The geometry of a painted editor: the gutter, the text area, which bars are there and where.</summary>
        private void Layout(View v, Rectangle cr)
        {
            var ed = v.Ed;
            v.Content = cr;
            bool gutter = v.E.GetAttribute("data-gutter") != "off";
            v.Digits = Math.Max(2, Number(ed.LineCount).Length);
            int gw = gutter ? (int)Math.Ceiling((v.Digits + 2) * v.Cell) : 0;
            int bar = Math.Max(4, (int)Math.Round(w.UnitPx * 6));
            v.MaxCols = MaxCols(v);
            float contentH = ed.LineCount * v.LineH, contentW = (v.MaxCols + 2) * v.Cell;
            bool needV = contentH > cr.Height, needH = contentW > cr.Width - gw - (needV ? bar : 0);
            if (needH && !needV) { needV = contentH > cr.Height - bar; }
            v.VBar = needV;
            v.HBar = needH;
            v.Gutter = new Rectangle(cr.X, cr.Y, gw, cr.Height);
            v.Text = new Rectangle(cr.X + gw, cr.Y, Math.Max(0, cr.Width - gw - (needV ? bar : 0)), Math.Max(0, cr.Height - (needH ? bar : 0)));
            v.VTrack = needV ? new Rectangle(cr.Right - bar, cr.Y, bar, v.Text.Height) : Rectangle.Empty;
            v.HTrack = needH ? new Rectangle(v.Text.X, cr.Bottom - bar, v.Text.Width, bar) : Rectangle.Empty;
            ScrollTo(v, v.ScrollY, v.ScrollX);
            RecalcBars(v);
        }

        private void RecalcBars(View v)
        {
            if (v.VBar)
            {
                float contentH = Math.Max(1f, v.Ed.LineCount * v.LineH);
                int th = Math.Clamp((int)Math.Round(v.VTrack.Height * v.Text.Height / contentH), Math.Min(16, v.VTrack.Height), v.VTrack.Height);
                float max = MaxScrollY(v);
                int ty = v.VTrack.Y + (max > 0 ? (int)Math.Round((v.VTrack.Height - th) * v.ScrollY / max) : 0);
                v.VThumb = new Rectangle(v.VTrack.X, ty, v.VTrack.Width, th);
            }
            else { v.VThumb = Rectangle.Empty; }
            if (v.HBar)
            {
                float contentW = Math.Max(1f, (v.MaxCols + 2) * v.Cell);
                int tw = Math.Clamp((int)Math.Round(v.HTrack.Width * v.Text.Width / contentW), Math.Min(16, v.HTrack.Width), v.HTrack.Width);
                float max = MaxScrollX(v);
                int tx = v.HTrack.X + (max > 0 ? (int)Math.Round((v.HTrack.Width - tw) * v.ScrollX / max) : 0);
                v.HThumb = new Rectangle(tx, v.HTrack.Y, tw, v.HTrack.Height);
            }
            else { v.HThumb = Rectangle.Empty; }
        }

        private Run RunsOf(View v, int line)
        {
            var ed = v.Ed;
            string text = ed.Line(line);
            var tokens = v.Hl.Tokens(line);
            if (v.Runs.TryGetValue(line, out var run) && ReferenceEquals(run.Line, text) && ReferenceEquals(run.Tokens, tokens)) { return run; }
            if (v.Runs.Count > MaxRunCache) { v.Runs.Clear(); }
            run ??= new Run();
            run.Line = text;
            run.Tokens = tokens;
            run.Count = 0;
            int pos = 0, vcol = 0, tab = ed.TabSize;
            for (int t = 0; t <= tokens.Length; t++)
            {
                int start = t < tokens.Length ? tokens[t].Start : text.Length;
                if (start > pos) { Add(run, text, pos, start, CodeKind.Plain, ref vcol, tab); }
                if (t < tokens.Length) { Add(run, text, tokens[t].Start, tokens[t].End, tokens[t].Kind, ref vcol, tab); pos = tokens[t].End; }
            }
            run.VEnd = vcol;
            v.Runs[line] = run;
            return run;
        }

        private static void Add(Run run, string text, int a, int b, CodeKind kind, ref int vcol, int tab)
        {
            if (b <= a) { return; }
            string piece = text.Substring(a, b - a);
            int start = vcol;
            if (piece.IndexOf('\t') >= 0)
            {
                var sb = new System.Text.StringBuilder(piece.Length + 8);
                int vc = vcol;
                foreach (char c in piece)
                {
                    if (c == '\t') { int n = (vc / tab + 1) * tab - vc; sb.Append(' ', n); vc += n; }
                    else { sb.Append(c); vc++; }
                }
                piece = sb.ToString();
                vcol = vc;
            }
            else { vcol += piece.Length; }
            if (run.Count == run.Text.Length)
            {
                Array.Resize(ref run.Text, run.Count * 2);
                Array.Resize(ref run.VCol, run.Count * 2);
                Array.Resize(ref run.Kind, run.Count * 2);
            }
            run.Text[run.Count] = piece;
            run.VCol[run.Count] = start;
            run.Kind[run.Count] = kind;
            run.Count++;
        }

        private static bool Blank(string s)
        {
            for (int i = 0; i < s.Length; i++) { if (s[i] != ' ') { return false; } }
            return true;
        }

        internal void Paint(Painter p, LayoutBox b, ComputedStyle s, Rectangle rect, float opacity, Rectangle cr)
        {
            var e = b.Element;
            var v = Get(e);
            Options(v);
            SyncIn(v);
            Metrics(v, s);
            if (cr.Width <= 4 || cr.Height <= 4) { return; }
            Layout(v, cr);
            var ed = v.Ed;
            bool focused = controls.Focus == e;
            var theme = v.Theme;
            float cell = v.Cell, lh = v.LineH;
            if (v.Reveal)
            {
                v.Reveal = false;
                float cy = ed.Caret.Line * lh;
                float y = v.ScrollY, x = v.ScrollX;
                if (cy < y) { y = cy; }
                else if (cy + lh > y + v.Text.Height) { y = cy + lh - v.Text.Height; }
                float cx = CodeEdit.VisualCol(ed.Line(ed.Caret.Line), ed.Caret.Col, ed.TabSize) * cell;
                if (cx < x) { x = Math.Max(0, cx - cell * 4); }
                else if (cx + cell * 2 > x + v.Text.Width) { x = cx + cell * 2 - v.Text.Width; }
                ScrollTo(v, y, x);
                RecalcBars(v);
            }
            var fonts = w.Fonts;
            var ta = v.Text;
            int first = Math.Clamp((int)Math.Floor(v.ScrollY / lh), 0, ed.LineCount - 1);
            int last = Math.Min(ed.LineCount - 1, (int)Math.Floor((v.ScrollY + ta.Height) / lh));
            var selStart = ed.SelStart;
            var selEnd = ed.SelEnd;
            bool hasSel = focused && ed.HasSelection;
            if (p.PushClip(new Rectangle(cr.X, cr.Y, cr.Width, cr.Height), out var saved))
            {
                // the gutter: numbers right aligned, a line at its edge
                if (v.Gutter.Width > 0)
                {
                    p.FillRect(v.Gutter, Painter.Tint(new Rgba(0, 0, 0, 46), opacity));
                    p.FillRect(new Rectangle(v.Gutter.Right - 1, v.Gutter.Y, 1, v.Gutter.Height), Painter.Tint(With(theme.Gutter, 60), opacity));
                }
                if (p.PushClip(Rectangle.Intersect(ta, p.Clip), out var inner))
                {
                    for (int i = first; i <= last; i++)
                    {
                        int top = ta.Y + (int)Math.Round(i * lh - v.ScrollY);
                        int lineH = (int)lh;
                        if (focused && i == ed.Caret.Line && !ed.HasSelection) { p.FillRect(new Rectangle(ta.X, top, ta.Width, lineH), Painter.Tint(theme.Line, opacity)); }
                        string text = ed.Line(i);
                        if (hasSel && i >= selStart.Line && i <= selEnd.Line)
                        {
                            int a = i == selStart.Line ? CodeEdit.VisualCol(text, selStart.Col, ed.TabSize) : 0;
                            int z = i == selEnd.Line ? CodeEdit.VisualCol(text, selEnd.Col, ed.TabSize) : CodeEdit.VisualCol(text, text.Length, ed.TabSize);
                            float x0 = ta.X - v.ScrollX + a * cell, x1 = ta.X - v.ScrollX + z * cell + (i < selEnd.Line ? cell * 0.5f : 0);
                            if (x1 > x0) { p.FillRect(Painter.SnapRect(x0, top, x1 - x0, lineH), Painter.Tint(theme.Selection, opacity)); }
                        }
                        if (text.Length == 0) { continue; }
                        var run = RunsOf(v, i);
                        float baseline = top + v.Shift + v.Ascent;
                        for (int r = 0; r < run.Count; r++)
                        {
                            float x = ta.X - v.ScrollX + run.VCol[r] * cell;
                            string piece = run.Text[r];
                            float width = piece.Length * cell;
                            if (x >= ta.Right) { break; }
                            if (x + width <= ta.X || Blank(piece)) { continue; }
                            if (piece.Length > LongRun)
                            {
                                // a very long piece: only the part in view
                                int from = Math.Clamp((int)((ta.X - x) / cell) - 2, 0, piece.Length);
                                int count = Math.Min(piece.Length - from, (int)(ta.Width / cell) + 6);
                                piece = piece.Substring(from, count);
                                x += from * cell;
                            }
                            fonts.Draw(p.Batch, piece, x, baseline, Painter.Tint(theme.Kinds[(int)run.Kind[r]], opacity), s);
                        }
                    }
                    if (ed.LineCount == 1 && ed.Line(0).Length == 0 && e.GetAttribute("placeholder") is { Length: > 0 } ph)
                    {
                        fonts.Draw(p.Batch, ph, ta.X + cell * 0.5f, ta.Y + v.Shift + v.Ascent, Painter.Tint(theme.Fg, opacity * 0.45f), s);
                    }
                    if (focused && (Timing.TotalTime - v.Blink) % 1.0 < 0.55 && ed.Caret.Line >= first && ed.Caret.Line <= last)
                    {
                        string text = ed.Line(ed.Caret.Line);
                        float x = ta.X - v.ScrollX + CodeEdit.VisualCol(text, ed.Caret.Col, ed.TabSize) * cell;
                        float y = ta.Y + ed.Caret.Line * lh - v.ScrollY;
                        var cc = s.Get("caret-color") is "auto" or null ? theme.Fg : s.GetColor("caret-color");
                        p.FillRect(Painter.SnapRect(x, y, Math.Max(1, w.UnitPx * 1.5f), lh), Painter.Tint(cc, opacity));
                    }
                    p.PopClip(inner);
                }
                // line numbers (over the gutter, outside the text clip)
                if (v.Gutter.Width > 0 && p.PushClip(Rectangle.Intersect(new Rectangle(v.Gutter.X, ta.Y, v.Gutter.Width, ta.Height), p.Clip), out var gclip))
                {
                    for (int i = first; i <= last; i++)
                    {
                        string num = Number(i + 1);
                        float y = ta.Y + i * lh - v.ScrollY;
                        float x = v.Gutter.X + (v.Digits - num.Length + 1) * cell;
                        bool current = focused && i == ed.Caret.Line;
                        fonts.Draw(p.Batch, num, x, y + v.Shift + v.Ascent, Painter.Tint(current ? theme.Fg : theme.Gutter, opacity), s);
                    }
                    p.PopClip(gclip);
                }
                // the bars: thin, over the edge
                if (v.VBar) { PaintBar(p, v.VTrack, v.VThumb, theme, opacity, v.BarDrag == 1); }
                if (v.HBar) { PaintBar(p, v.HTrack, v.HThumb, theme, opacity, v.BarDrag == 2); }
                p.PopClip(saved);
            }
        }

        private static void PaintBar(Painter p, Rectangle track, Rectangle thumb, Theme theme, float opacity, bool held)
        {
            p.FillRect(track, Painter.Tint(With(theme.Fg, 16), opacity));
            p.FillRect(thumb, Painter.Tint(With(theme.Fg, (byte)(held ? 130 : 70)), opacity));
        }
    }
}
