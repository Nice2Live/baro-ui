using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Barotrauma;
using BaroUI.Core;
using BaroUI.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI
{
    /// <summary>In-game check of stages 3 to 5, active only with bbe_ui_test.flag in the game folder (tools/test.py runs
    /// sit in the lobby long enough). 10 s after the first frame it opens UI/test.html and checks: hit tests land on
    /// the right elements, a class change that only recolours keeps the layout, scrolling clamps, a text change lays
    /// out again; then times 120 drawn frames (ms, allocations) and saves the drawn window from the back buffer to
    /// bbe_ui_test.png (no desktop capture). Stage 5 then opens UI/controls.html and drives its controls through the
    /// keyboard and click entry points the game calls (typing, Tab, number clamp, checkbox, dropdown, modal dialog,
    /// Enter, Esc, the virtual list) and saves bbe_ui_controls.png with the dialog open. Stage 6: SelfTestTools.cs.
    /// Results: "[BBE UI] selftest" lines and bbe_ui_test.txt.</summary>
    internal static partial class SelfTest
    {
        private const string Flag = "bbe_ui_test.flag", Report = "bbe_ui_test.txt", Shot = "bbe_ui_test.png", Shot5 = "bbe_ui_controls.png";
        private const int TimedFrames = 120;
        private static bool armed;
        private static double start = -1;
        private static UiWindow window;
        private static int frame;
        private static readonly List<string> lines = new();
        private static readonly List<double> drawMs = new();
        private static readonly List<long> drawAlloc = new();
        private static bool failed, shotTaken;
        private static double openMs;
        private static TestView view;
        private static string echoSent;
        private static Stopwatch echoClock;
        private static bool echoDone;
        // stage 5
        private static bool startControls, shot5Taken;
        private static UiWindow cwin;
        private static ControlsView cview;
        private static int cframe, cdraws, cstepDraws = -1;
        private static double clastDraw;

        public static void Init()
        {
            armed = File.Exists(Flag);
            if (armed) { UiPlugin.Log("selftest armed"); }
        }

        public static void Dispose()
        {
            armed = false;
            window = null;
        }

        public sealed class Module
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int Value { get; set; }
        }

        public sealed class TestModel
        {
            public List<Module> Modules { get; } = new();
            public bool Alarm { get; set; }
            public string Status { get; set; } = "Наведите мышь, нажмите кнопку, прокрутите список.";
            public Dictionary<string, object> Net { get; } = new() { ["value"] = "—" };
        }

        /// <summary>The test page's C# side: buttons select, rows pick, the cross closes.</summary>
        public sealed class TestView : BaroUiView
        {
            private static readonly string[] Names =
            {
                "Топливный стержень (обогащённый уран, длинное название)", "Балластный насос", "Гидролокатор", "Двигатель",
                "Кислородный генератор", "Аккумулятор А", "Аккумулятор Б", "Рельсотрон левый", "Рельсотрон правый",
                "Шлюз носовой", "Шлюз кормовой", "Фабрикатор", "Разборщик", "Медицинский фабрикатор", "Навигационный терминал",
                "Распределитель питания", "Супер-конденсатор", "Корпус: секция 1", "Корпус: секция 2", "Корпус: секция 3",
                "Дренаж", "Последняя строка списка",
            };
            public readonly TestModel Model = new();
            public int LastPick = -1;

            protected override void OnOpen()
            {
                for (int i = 0; i < Names.Length; i++) { Model.Modules.Add(new Module { Id = i + 1, Name = Names[i], Value = (i * 37 + 41) % 101 }); }
                Data = Model;
            }

            [UiHandler]
            private void select(Element button)
            {
                foreach (var b in FindAll(".btn")) { ToggleClass(b, "selected", b == button); }
                Model.Status = $"Нажато: {Text(button).Trim()} (#{button.Id})";
                Refresh();
            }

            [UiHandler]
            private void pick(int id)
            {
                LastPick = id;
                Model.Status = $"Строка {id}: {Model.Modules.Find(m => m.Id == id)?.Name}";
                Refresh();
            }

            [UiHandler("close")]
            private void CloseWindow() => Close();
        }

        public sealed class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public sealed class ControlsModel
        {
            public string Name { get; set; } = "";
            public double Qty { get; set; } = 1;
            public bool Armed { get; set; }
            public string Mode { get; set; } = "patrol";
            public int Charge { get; set; } = 40;
            public double Volume { get; set; } = 60;
            public string Status { get; set; } = "Tab — поле, Enter — ОК, Esc — закрыть";
            public string Note { get; set; } = "";
            public List<Row> Rows { get; } = new();
        }

        /// <summary>The controls page's C# side: a canvas, two buttons, the dialog's default button.</summary>
        public sealed class ControlsView : BaroUiView
        {
            public readonly ControlsModel Model = new();
            public int CanvasFrames, Closes;

            protected override void OnOpen()
            {
                for (int i = 1; i <= 2000; i++) { Model.Rows.Add(new Row { Id = i, Name = $"Запись журнала {i}: давление {(i * 13) % 97} бар" }); }
                Data = Model;
                Window.SetCanvas("scope", DrawScope);
            }

            private void DrawScope(SpriteBatch sb, Rectangle r)
            {
                CanvasFrames++;
                double t = Barotrauma.Timing.TotalTime;
                for (int x = 0; x < r.Width; x += 2)
                {
                    int y = (int)(r.Center.Y + Math.Sin(x * 0.05 + t * 3) * r.Height * 0.35);
                    sb.Draw(GUI.WhiteTexture, new Rectangle(r.X + x, y, 2, 2), new Color(64, 200, 190));
                }
            }

            [UiHandler]
            private void charge(int delta)
            {
                Model.Charge = Math.Clamp(Model.Charge + delta, 0, 100);
                Refresh();
            }

            [UiHandler]
            private void confirmNote()
            {
                Model.Status = $"Записано: {Model.Note}";
                Window.CloseDialog("confirm");
                Refresh();
            }

            [UiHandler]
            private void dialogClosed() => Closes++;
        }

        /// <summary>Runs from the update-list hook every frame.</summary>
        public static void Tick()
        {
            if (!armed) { return; }
            if (start < 0) { start = Barotrauma.Timing.TotalTime; }
            if (startPerf || pwin != null) { PerfTick(); return; }
            if (startTools || twin != null) { ToolsTick(); return; }
            if (startInputs || iwin != null) { InputsTick(); return; }
            if (startControls || cwin != null) { ControlsTick(); return; }
            if (window == null)
            {
                if (Barotrauma.Timing.TotalTime < start + 10) { return; }
                var sw = Stopwatch.StartNew();
                window = BaroUi.OpenPage("test");
                view = (TestView)window.View;
                window.AddToGUIUpdateList(); // lays out now, so the open time includes it (the hook adds it again: harmless)
                openMs = sw.Elapsed.TotalMilliseconds;
                Log($"open test: {openMs:0.0} ms total (style {window.LastStyleMs:0.0}, layout {window.LastLayoutMs:0.0}), "
                    + $"{window.Root.All().Count()} boxes, unit {window.UnitPx:0.###} px, screen {GameMain.GraphicsWidth}x{GameMain.GraphicsHeight}");
                return;
            }
            if (!window.IsOpen) { Finish("window closed by an error"); return; }
            frame++;
            if (frame == 3) { Checks(); }
            if (frame == 4) { DataChecks(); }
            if (frame == 5) { SendEcho(); }
            if (echoClock != null && !echoDone)
            {
                if (window.Find("net")?.TextContent.Contains(echoSent) == true)
                {
                    echoDone = true;
                    double ms = echoClock.Elapsed.TotalMilliseconds;
                    Check(ms < 1000, $"server echo reaches the bound node in {ms:0} ms (budget 1000)");
                }
                else if (echoClock.Elapsed.TotalSeconds > 5)
                {
                    echoDone = true;
                    Fail("server echo: no answer in 5 s");
                }
            }
        }

        private static void Checks()
        {
            var w = window;
            foreach (string id in new[] { "btn-a", "btn-c", "close", "list", "tiles", "status", "window" })
            {
                var e = w.Find(id);
                var b = w.BoxOf(e);
                if (b == null) { Fail($"#{id}: no box"); continue; }
                // a point inside the padding box away from children: 3 px in from the top-left corner
                var p = new Point((int)(b.X + b.Border.Left + 3), (int)(b.Y + b.Border.Top + 3));
                var hit = w.ElementAt(p);
                bool ok = hit != null && (hit == e || IsAncestor(e, hit));
                Check(ok, $"hit #{id} at {p}: {hit?.ToString() ?? "nothing"}");
            }
            Check(w.ElementAt(new Point(5, 5)) == null, "screen corner outside the window passes through");

            int layouts = w.LayoutCount;
            var btnD = w.Find("btn-d");
            string before = btnD.Style.Get("color");
            w.ToggleClass(btnD, "selected", true);
            w.AddToGUIUpdateList();
            Check(btnD.Style.Get("color") != before && w.LayoutCount == layouts,
                $"class .selected recolours without layout ({before} -> {btnD.Style.Get("color")}, layouts {layouts} -> {w.LayoutCount})");
            w.ToggleClass(btnD, "selected", false);

            var list = w.Find("list");
            var lb = w.BoxOf(list);
            float max = lb.ScrollHeight - (lb.H - lb.Border.Vertical);
            Check(max > 0, $"#list overflows: content {lb.ScrollHeight:0} > view {lb.H - lb.Border.Vertical:0}");
            w.ScrollBy(list, 40);
            Check(Math.Abs(w.GetScroll(list) - Math.Min(40, max)) < 0.01f, $"scroll by 40 -> {w.GetScroll(list):0.#}");
            w.ScrollBy(list, 100000);
            Check(Math.Abs(w.GetScroll(list) - max) < 0.01f, $"scroll clamps at {max:0.#} -> {w.GetScroll(list):0.#}");
            w.ScrollBy(list, -100000);

            // the first open pays for JIT and glyph rendering; a second one shows the steady cost
            var sw = Stopwatch.StartNew();
            var again = BaroUi.Open(Path.Combine(BaroUi.PackageDir, "UI", "test.html"), new TestView(), "test-reopen");
            again.AddToGUIUpdateList();
            double reopen = sw.Elapsed.TotalMilliseconds;
            again.Close();
            Check(reopen < 50, $"second open {reopen:0.0} ms (style {again.LastStyleMs:0.0}, layout {again.LastLayoutMs:0.0}), budget 50 ms");

            view.Model.Status = "Самопроверка: текст сменён";
            view.Refresh();
            w.AddToGUIUpdateList();
            Check(w.LayoutCount == layouts + 1 && w.Find("status").TextContent.StartsWith("Самопроверка"),
                $"bound text change lays out once more (layouts {w.LayoutCount}): bindings {w.LastUpdateMs:0.00} ms, layout {w.LastLayoutMs:0.0} ms");
        }

        /// <summary>Stage 4: data-for keeps rows by key, data-class and data-if follow the model, handlers get their
        /// arguments, the page has no diagnostics.</summary>
        private static void DataChecks()
        {
            var w = window;
            var m = view.Model;
            List<Element> Rows() => view.FindAll("#list .row");
            Check(w.Document.Diagnostics.Count == 0, $"page diagnostics: {w.Document.Diagnostics.Count} {string.Join(" | ", w.Document.Diagnostics.Take(3))}");
            var rows = Rows();
            Check(rows.Count == m.Modules.Count, $"data-for: {rows.Count} rows for {m.Modules.Count} modules, {w.Template.BindingCount} bindings");
            var row5 = rows.Count > 4 ? rows[4] : null;

            int layouts = w.LayoutCount;
            m.Modules[4].Value = 10;
            view.Refresh();
            w.AddToGUIUpdateList();
            Check(Rows().IndexOf(row5) == 4 && row5.HasClass("low") && row5.TextContent.Contains("10 %") && w.LayoutCount == layouts + 1,
                $"value 10: same row element, .low on, text '{row5?.TextContent.Trim()}', bindings {w.LastUpdateMs:0.00} ms");

            m.Modules.RemoveAt(0);
            m.Modules.Add(new Module { Id = 23, Name = "Новый модуль", Value = 55 });
            view.Refresh();
            w.AddToGUIUpdateList();
            rows = Rows();
            Check(rows.Count == m.Modules.Count && rows.IndexOf(row5) == 3 && rows[^1].TextContent.Contains("Новый модуль"),
                $"remove first + add last: {rows.Count} rows, row 5 kept at index {rows.IndexOf(row5)}");

            var alarm = view.Find("#alarm") ?? Query.All(w.Document.Root, "#alarm").FirstOrDefault();
            m.Alarm = true;
            view.Refresh();
            w.AddToGUIUpdateList();
            bool shown = w.BoxOf(alarm) != null;
            m.Alarm = false;
            view.Refresh();
            w.AddToGUIUpdateList();
            Check(shown && w.BoxOf(alarm) == null && alarm.Hidden, "data-if shows and hides #alarm");

            w.Click(rows[6]);
            Check(view.LastPick == m.Modules[6].Id, $"onclick=\"pick(m.Id)\" on row 7 -> pick({view.LastPick}), expected {m.Modules[6].Id}");
            var btnA = view.Find("#btn-a");
            w.Click(btnA);
            w.AddToGUIUpdateList();
            Check(btnA.HasClass("selected") && !view.Find("#btn-b").HasClass("selected") && w.Find("status").TextContent.Contains("btn-a"),
                $"onclick=\"select(this)\": #btn-a selected, status '{w.Find("status").TextContent}'");
            w.Click(view.Find("#btn-c"));
            w.AddToGUIUpdateList();
            Check(!w.Find("status").TextContent.Contains("btn-c"), "inactive #btn-c: click has no effect");
        }

        private static void SendEcho()
        {
            echoSent = $"эхо {DateTime.Now:HHmmss}";
            echoClock = Stopwatch.StartNew();
            UiEcho.Received = text => { if (window != null && window.IsOpen) { view.Set("net.value", text); } };
            UiEcho.Send(echoSent);
        }

        /// <summary>Called by every window after it drew; times the test window and captures it once.</summary>
        public static void AfterDraw(UiWindow w, SpriteBatch sb)
        {
            if (armed) { ToolsAfterDraw(w, sb); PerfAfterDraw(w); }
            if (armed && w == cwin) { cdraws++; clastDraw = Barotrauma.Timing.TotalTime; }
            if (armed && w == iwin) { InputsAfterDraw(w, sb); }
            if (armed && w == cwin && cframe >= 9 && !shot5Taken)
            {
                shot5Taken = true;
                Capture(w, sb, Shot5);
                return;
            }
            if (!armed || w != window || frame < 5) { return; }
            if (drawMs.Count < TimedFrames || !echoDone) { return; }
            if (!shotTaken)
            {
                shotTaken = true;
                Capture(w, sb, Shot);
                // stage 5 next frame: closing a window inside its own draw would free textures the batch still holds
                startControls = true;
            }
        }

        /// <summary>True while the test window's paints are being timed (frames 5 .. 5 + TimedFrames).</summary>
        public static bool Timing(UiWindow w)
        {
            bool on = (armed && w == window && frame >= 5 && drawMs.Count < TimedFrames) || PerfTiming(w);
            if (on) { timedWin = w; }
            return on;
        }

        private static UiWindow timedWin;

        public static void AddPaint(double ms, long alloc)
        {
            if (timedWin != null && timedWin == pwin) { ppaint.Add(ms); ppaintAlloc.Add(alloc); return; }
            drawMs.Add(ms);
            drawAlloc.Add(alloc);
        }

        private static void Capture(UiWindow w, SpriteBatch sb, string file)
        {
            var b = w.BoxOf(w.Find("window")) ?? w.Root;
            var r = new Rectangle((int)b.X - 8, (int)b.Y - 8, (int)b.W + 16, (int)b.H + 16);
            string error = UiWindow.SavePng(sb, r, file);
            if (error == null) { Log($"capture {r.Width}x{r.Height} -> {file}"); } else { Fail($"capture: {error}"); }
        }

        private static void ControlsTick()
        {
            if (cwin == null)
            {
                startControls = false;
                if (window.IsOpen) { window.Close(); }
                cwin = BaroUi.OpenPage("controls");
                cview = cwin.View as ControlsView;
                if (cview == null) { Finish("controls page opened without ControlsView"); return; }
                cwin.AddToGUIUpdateList();
                clastDraw = Barotrauma.Timing.TotalTime;
                Log($"stage 5: open controls {cwin.LastStyleMs:0.0} + {cwin.LastLayoutMs:0.0} ms, {cwin.Root.All().Count()} boxes, "
                    + $"rows created {cwin.Template.RowsCreated} of {cview.Model.Rows.Count}");
                return;
            }
            if (!cwin.IsOpen && cframe < 13) { Finish("controls window closed by an error"); return; }
            // one step per drawn frame: dropdown, canvas and capture need a paint between the steps
            if (cdraws == cstepDraws)
            {
                if (Barotrauma.Timing.TotalTime - clastDraw > 180) { Finish($"controls window not drawn for 180 s (draws {cdraws}, step {cframe})"); }
                return;
            }
            cstepDraws = cdraws;
            cframe++;
            try
            {
                switch (cframe)
                {
                    case 1: CanvasMouseArm(); break;
                    case 2: CanvasMouseChecks(); break;
                    case 8: CanvasMouseUnderModal(); break;
                    case 3: TypingChecks(); break;
                    case 4: ClickChecks(); break;
                    case 5: VirtualChecks(); break;
                    case 6: DropdownChecks(); break;
                    case 7: ModalOpen(); break;
                    case 11: ModalEnter(); break;
                    case 13: EscapeChecks(); break;
                }
            }
            catch (Exception e)
            {
                Finish($"stage 5 step {cframe}: {e}");
            }
        }

        private static void TypingChecks()
        {
            var c = cwin.Controls;
            var m = cview.Model;
            cwin.FocusId("name");
            Check(c.Focus == cwin.Find("name") && GUI.KeyboardDispatcher.Subscriber == c, "focus #name takes the game keyboard");
            foreach (char ch in "Тест-1") { c.ReceiveTextInput(ch); }
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Home);
            c.ReceiveTextInput('[');
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.End);
            c.ReceiveTextInput(']');
            Check(m.Name == "[Тест-1]", $"typing, Home, End -> model '{m.Name}'");
            c.ReceiveCommandInput('\b');
            string afterBack = m.Name;
            c.ReceiveCommandInput((char)0x1A);
            Check(afterBack == "[Тест-1" && m.Name == "[Тест-1]", $"backspace '{afterBack}', ctrl+z '{m.Name}'");
            c.ReceiveTextInput(new string('x', 40));
            Check(m.Name.Length == 24, $"maxlength 24 -> {m.Name.Length}");
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Tab);
            Check(c.Focus == cwin.Find("qty"), $"Tab -> {c.Focus}");
            c.ReceiveTextInput("17");
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Tab);
            Check(Math.Abs(m.Qty - 10) < 1e-9 && cwin.Find("qty").GetAttribute("value") == "10", $"number 17 clamps to max 10 on leaving: model {m.Qty}");
            Check(c.Focus == cwin.Find("armed"), $"Tab -> checkbox ({c.Focus})");
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Space);
            Check(m.Armed && cwin.Find("armed").Checked, "Space toggles the checkbox, model true");
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Tab);
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Down);
            Check(c.Focus == cwin.Find("mode") && m.Mode == "hunt", $"Tab -> select, Down -> mode '{m.Mode}'");
            c.Blur();
            Check(c.Focus == null && GUI.KeyboardDispatcher.Subscriber != c, "blur gives the keyboard back");
        }

        private static readonly Point Nowhere = new(-100, -100);

        private static void ClickChecks()
        {
            var c = cwin.Controls;
            c.Click(cwin.Find("armed-label"), Nowhere);
            Check(!cview.Model.Armed, "click on the label toggles its checkbox back");
            c.Click(cwin.Find("mode"), Nowhere);
            Check(c.OpenSelect == cwin.Find("mode"), "click on the select opens the dropdown");
        }

        private static void VirtualChecks()
        {
            var list = cwin.Find("rows");
            int created = cwin.Template.RowsCreated;
            cwin.ScrollBy(list, 1e6f);
            cwin.AddToGUIUpdateList();
            cwin.AddToGUIUpdateList();
            var rows = Query.All(list, ".vrow");
            bool last = rows.Any(r => r.TextContent.Contains("2000"));
            Check(created < 80 && rows.Count < 80 && last,
                $"virtual list: {rows.Count} rows alive, {cwin.Template.RowsCreated} created for 2000, last row shown {last}, update {cwin.LastUpdateMs:0.00} ms");
            cwin.ScrollBy(list, -1e6f);
        }

        private static void DropdownChecks()
        {
            var c = cwin.Controls;
            var area = c.DropArea;
            Check(area.Height > 0 && cwin.ElementAt(area.Center) == cwin.Find("mode"), $"dropdown painted at {area}, hit test lands on the select");
            int rowH = area.Height / Math.Min(8, 10);
            c.Click(cwin.Find("mode"), new Point(area.X + 5, area.Y + rowH * 2 + rowH / 2));
            Check(cview.Model.Mode == "tow" && c.OpenSelect == null, $"row 3 chosen -> mode '{cview.Model.Mode}', dropdown closed");
        }

        private static void ModalOpen()
        {
            var c = cwin.Controls;
            cwin.ShowModal("confirm");
            cwin.AddToGUIUpdateList();
            var dlg = cwin.Find("confirm");
            var b = cwin.BoxOf(dlg);
            bool centred = b != null && Math.Abs(b.X + b.W / 2 - GameMain.GraphicsWidth / 2f) < 2 && Math.Abs(b.Y + b.H / 2 - GameMain.GraphicsHeight / 2f) < 2;
            Check(c.Modals.Count == 1 && centred, $"modal open and centred: {b}");
            Check(c.Focus == cwin.Find("note"), $"autofocus -> {c.Focus}");
            Check(cwin.ElementAt(new Point(5, 5)) == dlg, "backdrop takes clicks outside the dialog");
            var under = cwin.BoxOf(cwin.Find("charge-up"));
            Check(under != null && cwin.ElementAt(new Point((int)(under.X + 3), (int)(under.Y + 3))) != cwin.Find("charge-up"), "page under the modal gets no hit");
            foreach (char ch in "журнал") { c.ReceiveTextInput(ch); }
        }

        private static void ModalEnter()
        {
            var c = cwin.Controls;
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Enter);
            cwin.AddToGUIUpdateList();
            Check(c.Modals.Count == 0 && cview.Model.Status == "Записано: журнал" && cview.Closes == 1,
                $"Enter clicks data-default: status '{cview.Model.Status}', onclose {cview.Closes}");
            Check(cview.CanvasFrames > 0, $"canvas drawn {cview.CanvasFrames} times");
            Check(shot5Taken, $"controls captured with the dialog open ({cdraws} draws so far)");
            Check(cwin.Document.Diagnostics.Count == 0, $"controls page diagnostics: {string.Join(" | ", cwin.Document.Diagnostics.Take(3))}");
            Check(BaroUi.EscapeHooked, "Esc hook on GUI.TogglePauseMenu");
        }

        private static void EscapeChecks()
        {
            cwin.ShowModal("confirm");
            cwin.AddToGUIUpdateList();
            bool first = cwin.OnEscape();
            cwin.AddToGUIUpdateList();
            Check(first && cwin.Controls.Modals.Count == 0 && cwin.IsOpen, "Esc closes the modal first, the window stays");
            cwin.OnEscape();
            Check(!cwin.IsOpen && GUI.KeyboardDispatcher.Subscriber != cwin.Controls, "second Esc closes the window, keyboard released");
            startInputs = true;   // stage D (SelfTestInput.cs) runs next and starts the tools stage when it is done
        }

        private static void Finish(string error)
        {
            if (error != null) { Fail(error); }
            if (drawMs.Count > 0)
            {
                var steady = drawMs.Skip(10).OrderBy(x => x).ToList();
                var alloc = drawAlloc.Skip(10).ToList();
                Log($"draw {steady.Count} frames: avg {steady.Average():0.000} ms, p95 {steady[(int)(steady.Count * 0.95)]:0.000} ms, "
                    + $"alloc avg {alloc.Average():0} B/frame");
            }
            Log(failed ? "FAIL" : "PASS");
            try { File.WriteAllLines(Report, lines); } catch (Exception e) { UiPlugin.Log($"selftest report: {e.Message}"); }
            armed = false;
        }

        private static bool IsAncestor(Element ancestor, Element e)
        {
            for (var x = e; x != null; x = x.Parent) { if (x == ancestor) { return true; } }
            return false;
        }

        private static void Check(bool ok, string what)
        {
            if (!ok) { failed = true; }
            Log($"{(ok ? "ok" : "FAIL")} {what}");
        }

        private static void Fail(string what) { failed = true; Log($"FAIL {what}"); }

        private static void Log(string text)
        {
            lines.Add(text);
            UiPlugin.Log($"selftest {text}");
        }
    }
}
