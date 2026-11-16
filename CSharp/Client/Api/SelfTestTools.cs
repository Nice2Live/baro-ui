using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Barotrauma;
using BaroUI.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI
{
    /// <summary>Stage 6 of the self test (after stage 5): a page written to a temp folder with a CSS mistake on a
    /// known line; the problem list and banner, block outlines and the inspector (at a probe point, the mouse is not
    /// ours), then bbe_ui watch: the stylesheet is saved fixed and the window reloads by itself, keeping its view,
    /// model and scroll. Saves bbe_ui_tools.png with outlines, inspector and banner.</summary>
    internal static partial class SelfTest
    {
        private const string Shot6 = "bbe_ui_tools.png";
        private static bool startTools, shot6Taken;
        private static string tdir;
        private static UiWindow twin, reloadedTo;
        private static ToolsView tview;
        private static int tdraws, tstep, tstepDraws = -1;
        private static double twaitSince, tlastDraw;

        public sealed class ToolsModel
        {
            public int N { get; set; } = 5;
            public List<int> Rows { get; } = Enumerable.Range(1, 40).ToList();
        }

        public sealed class ToolsView : BaroUiView
        {
            public readonly ToolsModel Model = new();
            public int Opens, Closes;
            protected override void OnOpen() { Opens++; Data ??= Model; }
            protected override void OnClose() => Closes++;
        }

        private const string ToolsHtml = "<html><head><link rel=\"stylesheet\" href=\"p6.css\"></head><body>\n"
            + "<div id=\"window\">\n  <h2>Инструменты</h2>\n  <button id=\"b\" class=\"btn\">n = <span id=\"n\">{{n}}</span></button>\n"
            + "  <div id=\"list\"><p data-for=\"r in rows\">строка {{r}}</p></div>\n</div>\n</body></html>\n";
        private const string ToolsCssBroken = "#window { position: absolute; left: 100u; top: 100u; width: 500u; height: 300u; padding: 10u; background-color: #10202a; border: 2u solid #40c8be; }\n"
            + ".btn { width: 120u; height: 30u;\n  colr: red; }\n#list { height: 100u; overflow-y: auto; }\n";
        private const string ToolsCssFixed = "#window { position: absolute; left: 100u; top: 100u; width: 500u; height: 300u; padding: 10u; background-color: #10202a; border: 2u solid #40c8be; }\n"
            + ".btn { width: 120u; height: 30u; }\n#b { width: 300u; }\n#list { height: 100u; overflow-y: auto; }\n";

        private static void ToolsTick()
        {
            if (twin == null)
            {
                startTools = false;
                tdir = Path.Combine(Path.GetTempPath(), "bbe_ui_selftest6");
                Directory.CreateDirectory(tdir);
                File.WriteAllText(Path.Combine(tdir, "p6.html"), ToolsHtml);
                File.WriteAllText(Path.Combine(tdir, "p6.css"), ToolsCssBroken);
                tview = new ToolsView();
                twin = BaroUi.Open(Path.Combine(tdir, "p6.html"), tview, "p6");
                twin.AddToGUIUpdateList();
                tlastDraw = Barotrauma.Timing.TotalTime;
                Log($"stage 6: page with a CSS mistake opened from {tdir}");
                return;
            }
            if (reloadedTo == null && tstep >= 2)
            {
                if (Barotrauma.Timing.TotalTime - twaitSince > 10) { Finish("bbe_ui watch: no reload 10 s after the save"); CleanTools(); }
                return;
            }
            if (tdraws == tstepDraws)
            {
                if (Barotrauma.Timing.TotalTime - tlastDraw > 180) { Finish($"stage 6 window not drawn for 180 s (step {tstep})"); CleanTools(); }
                return;
            }
            tstepDraws = tdraws;
            tstep++;
            try
            {
                if (tstep == 1) { ToolsProblems(); }
                else if (tstep == 2) { ToolsOverlay(); }
                else if (tstep == 3) { ToolsReloaded(); }
            }
            catch (Exception e)
            {
                Finish($"stage 6 step {tstep}: {e}");
                CleanTools();
            }
        }

        private static void ToolsProblems()
        {
            var probs = twin.Problems.Select(p => p.ToString()).ToList();
            Check(probs.Contains("p6.css:3: unknown property colr"), $"problem with its file and line: {string.Join(" | ", probs)}");
            Check(tview.Opens == 1 && tview.Window == twin, $"view attached once ({tview.Opens})");
            var b = twin.BoxOf(twin.Find("b"));
            UiDebug.Outlines = true;
            UiDebug.Banner = true;
            UiDebug.Probe = b == null ? null : new Point((int)(b.X + b.W / 2), (int)(b.Y + b.H / 2));
        }

        private static void ToolsOverlay()
        {
            Check(UiDebug.LastBannerLines >= 2, $"banner drawn with {UiDebug.LastBannerLines} lines");
            Check(UiDebug.LastOutlined >= 5, $"outlines drawn: {UiDebug.LastOutlined} boxes");
            Check(UiDebug.LastLabel?.Contains("#b") == true && UiDebug.LastLabel.Contains("u  ("), $"inspector label: {UiDebug.LastLabel?.Replace("\n", " / ")}");
            Check(shot6Taken, "tools overlay captured");
            UiDebug.Outlines = false;
            UiDebug.Probe = null;

            // the model and the scroll change, then the stylesheet is saved fixed: watch must reload the window
            tview.Model.N = 7;
            tview.Refresh();
            twin.AddToGUIUpdateList();
            twin.ScrollBy(twin.Find("list"), 30 * twin.UnitPx);
            BaroUi.Reloaded += OnReloaded;
            BaroUi.Watch = true;
            string css = Path.Combine(tdir, "p6.css");
            File.WriteAllText(css, ToolsCssFixed);
            File.SetLastWriteTimeUtc(css, DateTime.UtcNow.AddSeconds(5));
            twaitSince = Barotrauma.Timing.TotalTime;
        }

        private static void OnReloaded(UiWindow from, UiWindow to)
        {
            if (from == twin) { reloadedTo = to; }
        }

        private static void ToolsReloaded()
        {
            var fresh = reloadedTo;
            double waited = Barotrauma.Timing.TotalTime - twaitSince;
            fresh.AddToGUIUpdateList();
            Check(!twin.IsOpen && fresh.IsOpen && BaroUi.Windows.Contains(fresh) && !BaroUi.Windows.Contains(twin),
                $"watch reloaded the saved page in {waited:0.0} s, one window in the list");
            Check(fresh.Problems.Count == 0, $"fixed stylesheet: no problems ({string.Join(" | ", fresh.Problems)})");
            Check(tview.Window == fresh && tview.Opens == 2 && tview.Closes == 0, $"view moved to the new window: opens {tview.Opens}, closes {tview.Closes}");
            Check(fresh.Find("n")?.TextContent == "7", $"model kept: n = '{fresh.Find("n")?.TextContent}'");
            var b = fresh.BoxOf(fresh.Find("b"));
            Check(b != null && Math.Abs(b.W - 300 * fresh.UnitPx) < 1.5f, $"new CSS applied: #b {b?.W:0.0} px, expected {300 * fresh.UnitPx:0.0}");
            float sy = fresh.GetScroll(fresh.Find("list"));
            Check(Math.Abs(sy - 30 * fresh.UnitPx) < 1.5f, $"scroll kept: {sy:0.0} px");

            // a missing stylesheet is a problem on the banner, not a crash
            File.Delete(Path.Combine(tdir, "p6.css"));
            string error = BaroUi.Reload(fresh);
            var last = BaroUi.Windows.FirstOrDefault(w => w.Name == "p6");
            Check(error == null && last != null && last.Problems.Any(p => p.Message.Contains("'p6.css' not found")),
                $"reload without the stylesheet: {error ?? string.Join(" | ", last?.Problems ?? new List<Core.Diagnostic>())}");
            CleanTools();
            startPerf = true;   // stage 7 next (SelfTestPerf.cs)
        }

        private static void ToolsAfterDraw(UiWindow w, SpriteBatch sb)
        {
            if (w.Name != "p6" || twin == null) { return; }
            tdraws++;
            tlastDraw = Barotrauma.Timing.TotalTime;
            if (tstep == 1 && !shot6Taken && UiDebug.Outlines)
            {
                shot6Taken = true;
                Capture(w, sb, Shot6);
            }
        }

        private static void CleanTools()
        {
            BaroUi.Reloaded -= OnReloaded;
            BaroUi.Watch = false;
            UiDebug.Outlines = UiDebug.Banner = false;
            UiDebug.Probe = null;
            foreach (var w in BaroUi.Windows.Where(w => w.Name == "p6").ToList()) { w.Close(); }
            try { if (tdir != null) { Directory.Delete(tdir, true); } } catch (Exception e) { Log($"temp folder: {e.Message}"); }
        }
    }
}
