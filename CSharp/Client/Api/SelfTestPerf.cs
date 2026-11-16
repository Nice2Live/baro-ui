using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Barotrauma;
using BaroUI.Render;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI
{
    /// <summary>Stage 7 of the self test (after stage 6): a terminal-sized page (200 recipe tiles bound by data-for,
    /// a 100-row log) against the budgets of docs/ui/ENGINE_PLAN.md — warm open ≤ 50 ms, a frame that changes 200
    /// bound values, a frame with one change, a refresh with nothing new (0 B), the paint (0 B). One step per drawn
    /// frame; numbers go to the report and docs/ui/PERF.md takes them from there.</summary>
    internal static partial class SelfTest
    {
        private static bool startPerf;
        private static string pdir;
        private static UiWindow pwin;
        private static PerfView pview;
        private static int pdraws, pstep, pstepDraws = -1;
        private static double plastDraw, pcoldMs;
        private static readonly List<double> pms = new(), ppaint = new();
        private static readonly List<long> palloc = new(), ppaintAlloc = new();
        private static bool ptiming;
        private const int PerfFrames = 60;

        public sealed class Tile
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int V { get; set; }
        }

        public sealed class PerfModel
        {
            public List<Tile> Tiles { get; } = new();
            public List<Row> Log { get; } = new();
            public string Status { get; set; } = "стенд этапа 7";
        }

        public sealed class PerfView : BaroUiView
        {
            public readonly PerfModel Model = new();
            protected override void OnOpen()
            {
                for (int i = 0; i < 200; i++) { Model.Tiles.Add(new Tile { Id = i, Name = $"Рецепт номер {i}", V = i * 37 % 101 }); }
                for (int i = 0; i < 100; i++) { Model.Log.Add(new Row { Id = i, Name = $"Запись журнала {i}: давление {i * 13 % 97} бар" }); }
                Data = Model;
            }
        }

        private const string PerfCss =
            "#window{display:flex;flex-direction:column;position:absolute;left:20u;top:20u;width:1560u;height:860u;padding:10u;gap:8u;background-color:#0f1d22;border:2u solid #40c8be;font-size:11u;color:#d8e6e8}"
            + "#head{display:flex;gap:10u;align-items:center}.badge{padding:2u 8u;border-radius:4u;background-color:#1d3a3f}.grow{flex:1}"
            + "#body{display:flex;gap:10u;flex:1;min-height:0}"
            + ".g{display:grid;grid-template-columns:repeat(8,1fr);gap:4u;overflow-y:auto;flex:2}"
            + ".t{display:flex;flex-direction:column;padding:4u;border:1u solid #2a4a50;gap:2u}.t.low{border-color:#e0a040}"
            + ".n{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.v{color:#40c8be}"
            + "item{width:32u;height:32u}#list{flex:1;overflow-y:auto}.r{display:flex;height:18u;padding:0 6u}.r .a{width:44u;color:#8aa}.r .b{flex:1;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}";

        private const string PerfHtml =
            "<html><head><link rel=\"stylesheet\" href=\"p7.css\"></head><body><div id=\"window\">"
            + "<div id=\"head\"><h1>ТЕРМИНАЛ</h1><span class=\"badge\">BBE · этап 7</span><span class=\"grow\"></span><span id=\"st\">{{status}}</span></div>"
            + "<div id=\"body\"><div class=\"g\"><div class=\"t\" data-for=\"t in tiles\" data-key=\"t.id\" data-class=\"low: t.v &lt; 20\">"
            + "<item prefab=\"oxygentank\"></item><span class=\"n\">{{t.name}}</span><span class=\"v\">{{t.v}} %</span></div></div>"
            + "<div id=\"list\"><div class=\"r\" data-for=\"r in log\" data-key=\"r.id\"><span class=\"a\">{{r.id | 0000}}</span><span class=\"b\">{{r.name}}</span></div></div>"
            + "</div></div></body></html>";

        private static void PerfTick()
        {
            if (pwin == null)
            {
                startPerf = false;
                pdir = Path.Combine(Path.GetTempPath(), "bbe_ui_selftest7");
                Directory.CreateDirectory(pdir);
                File.WriteAllText(Path.Combine(pdir, "p7.html"), PerfHtml);
                File.WriteAllText(Path.Combine(pdir, "p7.css"), PerfCss);
                var sw = Stopwatch.StartNew();
                pwin = OpenPerf();
                pcoldMs = sw.Elapsed.TotalMilliseconds;
                // warm open: the same page again (parsers, styles, fonts and glyphs are loaded)
                pwin.Close();
                sw.Restart();
                pwin = OpenPerf();
                double warm = sw.Elapsed.TotalMilliseconds;
                int elements = pwin.Document.Root.Descendants().Count();
                Log($"stage 7: page of {elements} elements, {pwin.Template.BindingCount} bindings; open cold {pcoldMs:0.0} ms, warm {warm:0.0} ms "
                    + $"(style {pwin.LastStyleMs:0.0}, layout {pwin.LastLayoutMs:0.0}, bindings {pwin.LastUpdateMs:0.0})");
                Check(warm <= 50, $"warm open {warm:0.0} ms (budget 50)");
                plastDraw = Barotrauma.Timing.TotalTime;
                return;
            }
            if (!pwin.IsOpen) { Finish("stage 7 window closed by an error"); CleanPerf(); return; }
            if (pdraws == pstepDraws)
            {
                if (Barotrauma.Timing.TotalTime - plastDraw > 180) { Finish($"stage 7 window not drawn for 180 s (step {pstep})"); CleanPerf(); }
                return;
            }
            pstepDraws = pdraws;
            pstep++;
            try { PerfStep(); }
            catch (Exception e) { Finish($"stage 7 step {pstep}: {e}"); CleanPerf(); }
        }

        private static UiWindow OpenPerf()
        {
            pview = new PerfView();
            var w = BaroUi.Open(Path.Combine(pdir, "p7.html"), pview, "p7");
            w.AddToGUIUpdateList();
            return w;
        }

        /// <summary>Frames 1-60: 200 values change; 61-120: one value; 121-180: Refresh with nothing new; then paint.</summary>
        private static void PerfStep()
        {
            var m = pview.Model;
            int phase = (pstep - 1) / PerfFrames;
            if ((pstep - 1) % PerfFrames == 0 && pstep > 1 && phase - 1 <= 2) { PerfReport(phase - 1); }
            switch (phase)
            {
                case 0: foreach (var t in m.Tiles) { t.V = (t.V + 7) % 101; } break;
                case 1: m.Tiles[pstep % 200].V = (m.Tiles[pstep % 200].V + 1) % 101; break;
                case 2: break;
                case 3:
                    if (!ptiming) { ptiming = true; Array.Clear(Painter.ProbeBytes); Painter.Probe = true; return; }   // paints of the next frames are timed (AddPaint)
                    if (ppaint.Count < PerfFrames) { return; }
                    ptiming = false;
                    Painter.Probe = false;
                    PerfFinish();
                    return;
            }
            pview.Refresh();
            long a = GC.GetAllocatedBytesForCurrentThread(), t0 = Stopwatch.GetTimestamp();
            pwin.AddToGUIUpdateList();
            long t1 = Stopwatch.GetTimestamp(), a1 = GC.GetAllocatedBytesForCurrentThread();
            pms.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            palloc.Add(a1 - a);
        }

        private static readonly string[] PhaseNames = { "200 values change per frame", "one value changes per frame", "Refresh with nothing new" };

        private static void PerfReport(int phase)
        {
            var ms = pms.Skip(5).OrderBy(x => x).ToList();
            var al = palloc.Skip(5).ToList();
            string line = $"{PhaseNames[phase]}: median {ms[ms.Count / 2]:0.000} ms, p95 {ms[(int)(ms.Count * 0.95)]:0.000} ms, "
                + $"alloc avg {al.Average() / 1024:0.0} KB/frame (bindings {pwin.LastUpdateMs:0.000} ms, layout {pwin.LastLayoutMs:0.000} ms, full {pwin.LastFullLayout})";
            switch (phase)
            {
                case 0: Check(ms[ms.Count / 2] <= 2.5 && !pwin.LastFullLayout, line + " — budget 2.5 ms, incremental"); break;
                case 1: Check(ms[ms.Count / 2] <= 0.5 && !pwin.LastFullLayout, line + " — budget 0.5 ms, incremental"); break;
                default: Check(al.Average() == 0, line + " — budget 0 B"); break;
            }
            pms.Clear();
            palloc.Clear();
        }

        private static void PerfFinish()
        {
            var ms = ppaint.Skip(5).OrderBy(x => x).ToList();
            var al = ppaintAlloc.Skip(5).ToList();
            Check(al.Average() == 0, $"paint of the page: avg {ms.Average():0.000} ms, p95 {ms[(int)(ms.Count * 0.95)]:0.000} ms, alloc {al.Average():0} B/frame — budget 0 B");
            int frames = Math.Max(1, ppaint.Count);
            Log("paint allocations by part: " + string.Join(", ", Painter.ProbeNames.Select((n, i) => $"{n} {Painter.ProbeBytes[i] / frames} B")));
            var fonts = BaroUi.Fonts;
            Log($"text width cache: {fonts.WidthHits} hits, {fonts.WidthMisses} misses");
            CleanPerf();
            Finish(null);
        }

        /// <summary>Paint timing of the stage 7 window (AddPaint routes here while it runs).</summary>
        private static bool PerfTiming(UiWindow w) => armed && ptiming && w == pwin && ppaint.Count < PerfFrames;

        private static void PerfAfterDraw(UiWindow w)
        {
            if (w != pwin || pwin == null) { return; }
            pdraws++;
            plastDraw = Barotrauma.Timing.TotalTime;
        }

        private static void CleanPerf()
        {
            ptiming = false;
            Painter.Probe = false;
            if (pwin != null && pwin.IsOpen) { pwin.Close(); }
            try { if (pdir != null) { Directory.Delete(pdir, true); } } catch (Exception e) { Log($"temp folder: {e.Message}"); }
        }
    }
}
