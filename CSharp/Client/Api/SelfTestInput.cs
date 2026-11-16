using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BaroUI.Core;
using BaroUI.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma;

namespace BaroUI
{
    /// <summary>In-game checks of the input primitives (docs/ui/AUTHORING.md): the canvas mouse (D1), the code editor (D2), the canvas picture
    /// (D3) and the dropped file (D4). They drive the same entry points the window calls with a made-up pointer, after the canvas was drawn once
    /// (the window learns where a canvas is from its paint). Run from the stage 5 steps of SelfTest.cs (the controls page).</summary>
    internal static partial class SelfTest
    {
        private static readonly List<CanvasMouseEvent> canvasLog = new();

        /// <summary>Step 1: register the handler; it needs a paint to learn the canvas's box.</summary>
        private static void CanvasMouseArm()
        {
            canvasLog.Clear();
            cwin.SetCanvasMouse("scope", e => { canvasLog.Add(e); return e.Kind != CanvasMouseKind.Wheel || e.Ctrl; });
        }

        private static PointerFrame Pointer(Point p, UiWindow w, Element canvas, MouseButtons held = MouseButtons.None, MouseButtons pressed = MouseButtons.None, float wheel = 0)
        {
            var b = w.BoxOf(canvas);
            return new PointerFrame
            {
                X = p.X, Y = p.Y, Held = held, Pressed = pressed, Wheel = wheel, Time = Barotrauma.Timing.TotalTime,
                Over = false, RectX = (float)b.ContentX, RectY = (float)b.ContentY, RectW = b.ContentWidth, RectH = b.ContentHeight,
            };
        }

        /// <summary>Step 2: a hover, a press, a drag out of the canvas, the release; the wheel; a drag ended by taking the handler away.</summary>
        private static void CanvasMouseChecks()
        {
            var w = cwin;
            var canvas = w.Find("scope");
            var b = w.BoxOf(canvas);
            if (b == null) { Fail("canvas mouse: #scope has no box"); return; }
            var centre = new Point((int)(b.ContentX + b.ContentWidth / 2), (int)(b.ContentY + b.ContentHeight / 2));
            var input = w.CanvasInput;
            Check(input.Has("scope"), "canvas mouse: the handler is registered for #scope");
            Check(w.ElementAt(centre) == canvas, "canvas mouse: the canvas is the element under its own centre (it takes the game's mouse there)");
            Check(w.ElementAt(new Point(5, 5)) == null, "canvas mouse: the screen corner outside the page still passes through to the game");

            bool Step(Point p, MouseButtons held = MouseButtons.None, MouseButtons pressed = MouseButtons.None, float wheel = 0, bool wantOver = true)
            {
                var f = Pointer(p, w, canvas, held, pressed, wheel);
                var target = w.ElementAt(p);
                f.Mods = (wheel != 0 && p == centre) ? KeyMods.Ctrl : KeyMods.None;
                return input.Step(f, wantOver && target != null, target);
            }

            canvasLog.Clear();
            Step(centre);
            bool entered = canvasLog.Count == 1 && canvasLog[0].Kind == CanvasMouseKind.Move && Math.Abs(canvasLog[0].X - b.ContentWidth / 2) <= 1.5f && Math.Abs(canvasLog[0].Y - b.ContentHeight / 2) <= 1.5f
                && Math.Abs(canvasLog[0].Width - b.ContentWidth) <= 1.5f;
            Check(entered, $"canvas mouse: the pointer at the centre is a Move at ({(canvasLog.Count > 0 ? canvasLog[0].X : -1):0},{(canvasLog.Count > 0 ? canvasLog[0].Y : -1):0}) of {b.ContentWidth:0}x{b.ContentHeight:0}");
            Step(centre, MouseButtons.Left, MouseButtons.Left);
            Check(canvasLog[^1].Kind == CanvasMouseKind.Down && canvasLog[^1].Left && input.Captured, $"canvas mouse: the press is a Down, the window holds the mouse ({canvasLog[^1]})");
            Step(new Point(2, 2), MouseButtons.Left);
            Check(canvasLog[^1].Kind == CanvasMouseKind.Move && canvasLog[^1].Captured && !canvasLog[^1].Inside && canvasLog[^1].X < 0, $"canvas mouse: dragged out of the canvas the Move still comes, captured ({canvasLog[^1]})");
            bool usedHeld = Step(centre, MouseButtons.Left, wheel: 1);
            Check(usedHeld && canvasLog[^1].Kind == CanvasMouseKind.Wheel && canvasLog[^1].Ctrl, $"canvas mouse: the wheel during the drag, with Ctrl, is used ({canvasLog[^1]})");
            canvasLog.Clear();
            Step(new Point(2, 2));
            Check(canvasLog.Count >= 1 && canvasLog[0].Kind == CanvasMouseKind.Up && !input.Captured, $"canvas mouse: the release outside is an Up, the capture is gone ({string.Join(" | ", canvasLog)})");
            canvasLog.Clear();
            Step(centre, MouseButtons.Right, MouseButtons.Right);
            Step(centre);
            Check(canvasLog.Any(e => e.Kind == CanvasMouseKind.Down && e.Right) && canvasLog.Any(e => e.Kind == CanvasMouseKind.Up && e.Right), "canvas mouse: the right button gives its own Down and Up");
            bool wheelUsed = Step(centre, wheel: 1);
            bool wheelFree = !Step(new Point(centre.X + 1, centre.Y), wheel: 1);   // the handler takes a wheel only with Ctrl: without it the page may scroll
            Check(wheelUsed && wheelFree, "canvas mouse: the wheel is used when the handler says so and passes to the page when it does not");
            canvasLog.Clear();
            Step(centre, MouseButtons.Left, MouseButtons.Left);
            w.ClearCanvasMouse("scope");
            Check(canvasLog.Count == 1 && canvasLog[0].Kind == CanvasMouseKind.Cancel && !input.Captured && !input.Has("scope"), $"canvas mouse: taking the handler away in a drag ends it with a Cancel ({string.Join(" | ", canvasLog)})");
            // modal: a dialog over the canvas takes the pointer
            canvasLog.Clear();
            w.SetCanvasMouse("scope", e => { canvasLog.Add(e); return true; });
        }

        /// <summary>A canvas under a modal dialog gets no pointer: the hit test lands on the dialog's backdrop (called with the dialog open).</summary>
        private static void CanvasMouseUnderModal()
        {
            var w = cwin;
            var canvas = w.Find("scope");
            var b = w.BoxOf(canvas);
            if (b == null) { return; }
            var centre = new Point((int)(b.ContentX + b.ContentWidth / 2), (int)(b.ContentY + b.ContentHeight / 2));
            var target = w.ElementAt(centre);
            canvasLog.Clear();
            var f = Pointer(centre, w, canvas, MouseButtons.Left, MouseButtons.Left);
            w.CanvasInput.Step(f, true, target);
            Check(target != canvas && canvasLog.Count == 0, $"canvas mouse: under an open modal the canvas gets nothing (target {target})");
            w.ClearCanvasMouse("scope");
        }

        // ---------------------------------------------------------------- the inputs page (D2 to D4), a stage of its own after the controls page

        private static bool startInputs;
        private static UiWindow iwin;
        private static InputsView iview;
        private static int iframe, idraws, istepDraws = -1;
        private static bool ishotTaken;
        private static string picPath;
        private static bool picDrawn;
        private static readonly List<UiFileInput> inputsGot = new();

        public sealed class InputsModel
        {
            public string Code { get; set; } = "";
            public string Status { get; set; } = "Ввод: код, мышь на холсте, картинка, файл";
            public string Lang { get; set; } = "xml";
            public int Lines => string.IsNullOrEmpty(Code) ? 1 : Code.Split('\n').Length;
            public string Dropped { get; set; } = "";
            /// <summary>The number input of the page (data-model="qty"): the mouse wheel and typing move it, the engine clamps it to min and max.</summary>
            public double Qty { get; set; } = 4;
        }

        /// <summary>The inputs page's C# side: the code editor's events and the model.</summary>
        public sealed class InputsView : BaroUiView
        {
            public readonly InputsModel Model = new();
            public int Edits, Saves;

            protected override void OnOpen()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!-- a test text -->\r\n<Items>\r\n");
                for (int i = 1; i <= 400; i++) { sb.Append($"  <Item identifier=\"item{i}\" tags=\"a,b\" maxstacksize=\"{i % 16}\">\r\n    <Price baseprice=\"{i * 7}\" />\r\n  </Item>\r\n"); }
                sb.Append("</Items>\r\n");
                Model.Code = sb.ToString();
                Data = Model;
            }

            [UiHandler]
            private void edited() { Edits++; Model.Status = $"Правок: {Edits}"; Refresh(); }

            [UiHandler]
            private void saved() { Saves++; Model.Status = $"Ctrl+S: {Saves}"; Refresh(); }
        }

        private static void InputsTick()
        {
            if (iwin == null)
            {
                startInputs = false;
                picPath = Path.Combine(Path.GetTempPath(), "bbe_ui_test_pic.png");
                try
                {
                    using var tex = new Texture2D(GameMain.Instance.GraphicsDevice, 8, 4);
                    var px = new Color[32];
                    for (int i = 0; i < px.Length; i++) { px[i] = new Color((i * 8) % 256, 160, 255 - (i * 8) % 256); }
                    tex.SetData(px);
                    using var fs = File.Create(picPath);
                    tex.SaveAsPng(fs, 8, 4);
                }
                catch (Exception e) { Finish($"inputs: cannot write the test picture: {e.Message}"); return; }
                iwin = BaroUi.OpenPage("inputs");
                iview = iwin.View as InputsView;
                if (iview == null) { Finish("inputs page opened without InputsView"); return; }
                iwin.AddToGUIUpdateList();
                iwin.SetCanvasMouse("pad", e => { canvasLog.Add(e); return true; });
                iwin.SetCanvas("pad", (sb, r) => sb.Draw(GUI.WhiteTexture, r, new Color(20, 60, 64)));
                iwin.SetCanvas("pic", (sb, r) => picDrawn = iwin.Images.Draw(sb, picPath, r, ImageFit.Contain, null, pixelPerfect: true));
                iwin.SetFileInput("drop", f => { inputsGot.Add(f); return true; }, ".png,.xml");
                Log($"stage D: open inputs {iwin.LastStyleMs:0.0} + {iwin.LastLayoutMs:0.0} ms, {iwin.Root.All().Count()} boxes");
                return;
            }
            if (!iwin.IsOpen && iframe < 9) { Finish("inputs window closed by an error"); return; }
            // one step per drawn frame: the editor learns its geometry from a paint, typed text reaches the model in the next update
            if (idraws == istepDraws)
            {
                if (iframe > 0 && Barotrauma.Timing.TotalTime - clastDraw > 180) { Finish($"inputs window not drawn for 180 s (step {iframe})"); }
                return;
            }
            istepDraws = idraws;
            iframe++;
            try
            {
                switch (iframe)
                {
                    case 2: CodeLoadChecks(); break;
                    case 3: CodeTypeChecks(); break;
                    case 4: CodeEditChecks(); break;
                    case 5: CodeMouseChecks(); break;
                    case 6: PictureChecks(); break;
                    case 7: FileInputChecks(); break;
                    case 9: InputsFinish(); break;
                }
            }
            catch (Exception e) { Finish($"stage D step {iframe}: {e}"); }
        }

        private static void InputsAfterDraw(UiWindow w, SpriteBatch sb)
        {
            idraws++;
            clastDraw = Barotrauma.Timing.TotalTime;
            if (iframe >= 5 && !ishotTaken) { ishotTaken = true; Capture(w, sb, "bbe_ui_inputs.png"); }
        }

        private static void CodeLoadChecks()
        {
            var w = iwin;
            var code = w.Find("code");
            var ed = w.GetCodeEdit("code");
            Check(ed != null && ed.Text == iview.Model.Code, $"code editor: <textarea> shows the data-model text ({ed?.LineCount} lines)");
            Check(ed != null && ed.NewLine == "\r\n" && ed.LineCount > 1000, $"code editor: the file's CRLF is kept, {ed?.LineCount} lines");
            var probe = w.Controls.Code.Probe(code);
            Check(probe.Text.Width > 100 && probe.LineH >= 12 && probe.Cell >= 4, $"code editor: painted at {probe.Text}, a line {probe.LineH:0.#} px, a column {probe.Cell:0.##} px");
            Check(w.Document.Diagnostics.Count == 0, $"inputs page diagnostics: {string.Join(" | ", w.Document.Diagnostics.Take(3))}");
            w.FocusId("code");
            Check(w.Controls.Focus == code && GUI.KeyboardDispatcher.Subscriber == w.Controls, "code editor: focus takes the game keyboard");
            var c = w.Controls;
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Down);
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Down);
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Down);
            Check(ed.Caret.Line == 3, $"code editor: Down x3 -> line {ed.Caret.Line + 1}");
            c.ReceiveTextInput("<A/>");
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Enter);
            c.ReceiveSpecialInput(Microsoft.Xna.Framework.Input.Keys.Tab);
            c.ReceiveTextInput('x');
        }

        private static void CodeTypeChecks()
        {
            var w = iwin;
            var ed = w.GetCodeEdit("code");
            Check(ed.Line(3) == "<A/>" && ed.Line(4).StartsWith("    x"), $"code editor: typed text, Enter and Tab -> '{ed.Line(3)}' / '{ed.Line(4)}'");
            Check(iview.Model.Code == ed.Text && iview.Edits >= 1, $"code editor: the typing reached the data-model and oninput ({iview.Edits} calls), {ed.Text.Length} chars");
            var c = w.Controls;
            c.ReceiveCommandInput((char)0x1A);
            Check(!ed.Line(4).StartsWith("    x"), "code editor: ctrl+Z undoes the last step");
            c.ReceiveCommandInput((char)0x13);
            Check(iview.Saves == 1, $"code editor: ctrl+S runs onsave ({iview.Saves})");
        }

        private static void CodeEditChecks()
        {
            var w = iwin;
            var ed = w.GetCodeEdit("code");
            var c = w.Controls;
            c.ReceiveCommandInput((char)0x1);
            Check(ed.HasSelection && ed.SelectedText == ed.Text, "code editor: ctrl+A selects everything");
            c.ReceiveCommandInput((char)0x3);
            string clip = Microsoft.Xna.Framework.Clipboard.GetText();
            Check(clip == ed.Text, $"code editor: ctrl+C puts the text on the clipboard ({clip?.Length} chars)");
            ed.Select(new CodePos(2, 0), new CodePos(3, 0));
            string before = ed.Text;
            string line = ed.SelectedText;
            c.ReceiveCommandInput((char)0x18);
            Check(ed.Text.Length < before.Length && Microsoft.Xna.Framework.Clipboard.GetText() == line, "code editor: ctrl+X cuts the selected line");
            c.ReceiveCommandInput((char)0x16);
            Check(ed.Text == before, "code editor: ctrl+V pastes it back");
            ed.GoTo(0, 0);
            iwin.RevealCaret("code");
        }

        private static void CodeMouseChecks()
        {
            var w = iwin;
            var code = w.Find("code");
            var ed = w.GetCodeEdit("code");
            var probe = w.Controls.Code.Probe(code);
            var c = w.Controls;
            // a press at the 3rd character of the 5th line
            var at = new Point(probe.Text.X + (int)(probe.Cell * 3.2f), probe.Text.Y + (int)(probe.LineH * 4.5f));
            bool taken = c.Code.MouseDown(code, at);
            Check(taken && ed.Caret == new CodePos(4, 3), $"code editor: a click at {at} puts the caret at {ed.Caret} (expected 5:4)");
            // a double click selects a word
            var word = new Point(probe.Text.X + (int)(probe.Cell * 5.5f), probe.Text.Y + (int)(probe.LineH * 2.5f));
            c.Code.MouseDown(code, word);
            c.Code.MouseDown(code, word);
            Check(ed.HasSelection && ed.SelectedText.Length > 0 && !ed.SelectedText.Contains('\n'), $"code editor: a double click selects a word '{ed.SelectedText}'");
            // the wheel scrolls
            bool wheel = c.Wheel(new Point(probe.Text.Center.X, probe.Text.Center.Y), -5);
            var after = w.Controls.Code.Probe(code);
            Check(wheel && after.ScrollY > 0, $"code editor: the wheel scrolls it ({after.ScrollY:0} px)");
            w.Controls.Code.Reveal(code);
        }

        private static void PictureChecks()
        {
            var w = iwin;
            Check(picDrawn, "picture: the canvas drew the picture file");
            var tex = w.Images.Peek(picPath);
            Check(tex != null && tex.Width == 8 && tex.Height == 4 && w.Images.Count == 1, $"picture: the file is a {tex?.Width}x{tex?.Height} texture, {w.Images.Count} kept");
            Check(w.Images.Get(picPath) == tex, "picture: the same file gives the same texture");
            Check(w.Images.Get(Path.Combine(Path.GetTempPath(), "bbe_ui_no_such_picture.png")) == null, "picture: a missing file gives null (and a line in the log)");
            var pasted = w.Images.Add("pasted:test", File.ReadAllBytes(picPath));
            Check(pasted != null && pasted.Width == 8, $"picture: bytes of a picture give a texture under a key ({w.Images.Count} kept)");
            w.Images.Release("pasted:test");
            Check(w.Images.Peek("pasted:test") == null, "picture: Release forgets a key");
        }

        private static void FileInputChecks()
        {
            var w = iwin;
            var drop = w.Find("drop");
            var b = w.BoxOf(drop);
            var centre = new Point((int)(b.X + b.W / 2), (int)(b.Y + b.H / 2));
            string xml = Path.Combine(Path.GetTempPath(), "bbe_ui_test_drop.xml");
            File.WriteAllText(xml, "<a/>");
            inputsGot.Clear();
            var r = w.FileTargets.Drop(xml, centre);
            Check(r.Over && r.Handled && inputsGot.Count == 1 && inputsGot[0].Source == FileInputSource.Drop && inputsGot[0].Path == xml && inputsGot[0].Target == drop, $"file input: an .xml dropped on the block reaches the handler ({inputsGot.Count})");
            r = w.FileTargets.Drop(Path.Combine(Path.GetTempPath(), "x.txt"), centre);
            Check(r.Over && !r.Handled && inputsGot.Count == 1, "file input: a .txt is not on the accept list: nothing, but the window covers the point");
            r = w.FileTargets.Drop(xml, new Point(2, 2));
            Check(!r.Over && !r.Handled, "file input: a drop away from the window is not the window's");
            var codeBox = w.BoxOf(w.Find("code"));
            r = w.FileTargets.Drop(xml, new Point((int)(codeBox.X + 20), (int)(codeBox.Y + 20)));
            Check(r.Over && !r.Handled, "file input: a drop on the page outside the target is covered, not taken");
            // paste: a path as text
            Microsoft.Xna.Framework.Clipboard.SetText("\"" + xml + "\"");
            w.FocusId("drop");
            Check(w.Controls.Focus == drop, "file input: the target takes the focus (tabindex)");
            inputsGot.Clear();
            w.Controls.ReceiveCommandInput((char)0x16);
            Check(inputsGot.Count == 1 && inputsGot[0].Source == FileInputSource.PastePath && inputsGot[0].Path == Path.GetFullPath(xml), $"file input: ctrl+V with a quoted path on the clipboard ({(inputsGot.Count > 0 ? inputsGot[0].ToString() : "nothing")})");
            Microsoft.Xna.Framework.Clipboard.SetText("just words");
            inputsGot.Clear();
            w.Controls.ReceiveCommandInput((char)0x16);
            Check(inputsGot.Count == 0, "file input: ctrl+V with other text does nothing");
            w.Controls.Blur();
            try { File.Delete(xml); } catch (Exception) { }
        }

        private static void InputsFinish()
        {
            var w = iwin;
            Check(iview.Edits >= 1, "stage D: the editor's oninput ran");
            w.Close();
            Check(w.Images.Count == 0, "picture: closing the window lets its pictures go");
            Check(GUI.KeyboardDispatcher.Subscriber != w.Controls, "stage D: the keyboard is released");
            try { File.Delete(picPath); } catch (Exception) { }
            startTools = true;
        }
    }
}
