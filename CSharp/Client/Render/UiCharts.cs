using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Barotrauma;
using BBE.Curves;
using BaroUI.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace BaroUI.Render
{
    /// <summary>The charts of a window (docs/ui/DESIGN_CHARTS.md 2.2 to 2.5): &lt;curve&gt; (an editable curve: a text value or a list of points; drag, double
    /// click, right click or Delete, arrows nudge), &lt;chart&gt; (series by type with round axes, category or numeric x labels, a hover line and a tooltip,
    /// onpick with the index as $arg, eased changes), &lt;gauge&gt; (a dial with zones, a needle or a filled sector, the needle eased). The geometry and the
    /// rules are Core (DrawGeometry, ChartScale, CurveEditor, GaugeMath, ChartModel, CanvasRegistry: tested without the game); this part reads the model
    /// (<see cref="Template.ReadModel"/>), paints with the drawing kit and takes the mouse through the canvas registry. A steady frame allocates nothing: the
    /// model values are read into reused buffers, the labels and the tooltip are made again only when their scale, index or data change, and the hosts
    /// live in weak tables keyed by the element (a removed element takes its host with it). Colours and sizes come from custom properties: --series-1..n,
    /// --grid-color, --stroke-width, --point-size, --point-color, --zone-0..n, --track-color, --track-width, --needle, --tick-color.</summary>
    internal sealed class UiCharts
    {
        private readonly UiWindow w;
        private readonly ConditionalWeakTable<Element, CurveHost> curves = new();
        private readonly ConditionalWeakTable<Element, ChartHost> charts = new();
        private readonly ConditionalWeakTable<Element, GaugeHost> gauges = new();
        private readonly List<double> ticks = new();
        private readonly List<double> xTicks = new();

        private static readonly Color[] SeriesPalette =
        {
            new(0x3f, 0xb6, 0xa8), new(0xe0, 0xa8, 0x4c), new(0xd8, 0x6a, 0x6a),
            new(0x7a, 0x9c, 0xd6), new(0xb4, 0x8a, 0xd6), new(0x9a, 0xc8, 0x5a),
        };
        private static readonly Color[] ZonePalette = { new(0x5f, 0xbf, 0x8a), new(0xe0, 0xb0, 0x50), new(0xd8, 0x6a, 0x6a), new(0x9a, 0xa8, 0xb0) };
        private static readonly Color GridDefault = new(0x22, 0x3a, 0x40), TrackDefault = new(0x2a, 0x3a, 0x40), NeedleDefault = new(0xe8, 0xe8, 0xe8);
        /// <summary>The custom property names of the series and the zones, made once (a name built per frame would be garbage).</summary>
        private static readonly string[] SeriesNames = { "--series-1", "--series-2", "--series-3", "--series-4", "--series-5", "--series-6", "--series-7", "--series-8" };
        private static readonly string[] ZoneNames = { "--zone-0", "--zone-1", "--zone-2", "--zone-3", "--zone-4", "--zone-5", "--zone-6", "--zone-7" };

        internal UiCharts(UiWindow window) { w = window; }

        private UiDraw Draw => w.Drawing;
        private float opacity = 1f;

        /// <summary>Paints the chart of the element in its content box cr (the element's tag says which).</summary>
        internal void Paint(Painter p, Element e, ComputedStyle s, Rectangle cr, float alpha)
        {
            opacity = alpha;
            try
            {
                switch (e.Tag)
                {
                    case "curve": PaintCurve(p, e, s, cr); break;
                    case "chart": PaintChart(p, e, s, cr); break;
                    case "gauge": PaintGauge(p, e, s, cr); break;
                }
            }
            catch (Exception ex)
            {
                UiPlugin.Log($"{w.Name}: {e.Tag} failed and was not drawn: {ex}");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static THost HostOf<THost>(ConditionalWeakTable<Element, THost> table, Element e) where THost : class, new()
        {
            if (!table.TryGetValue(e, out var h)) { h = new THost(); table.Add(e, h); }
            return h;
        }

        private static double Num(Element e, string name, double fallback) =>
            double.TryParse(e.GetAttribute(name)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;

        /// <summary>A custom property as a number ("2", "2px"); the fallback when absent or not a number.</summary>
        private static float VarNum(ComputedStyle s, string name, float fallback)
        {
            string raw = s.Var(name);
            if (raw == null) { return fallback; }
            ReadOnlySpan<char> span = raw.AsSpan().Trim();
            if (span.EndsWith("px".AsSpan(), StringComparison.OrdinalIgnoreCase)) { span = span.Slice(0, span.Length - 2); }
            return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;
        }

        /// <summary>A custom property as a colour, times the opacity; the fallback when absent or not a colour.</summary>
        private static Color VarColor(ComputedStyle s, string name, Color fallback, float opacity)
        {
            if (s.Var(name) is { } raw && Rgba.TryParse(raw.Trim(), out var c)) { return Painter.Tint(c, opacity); }
            return fallback * opacity;
        }

        private static string SeriesVar(int i) => i < SeriesNames.Length ? SeriesNames[i] : "--series-" + (i + 1).ToString(CultureInfo.InvariantCulture);
        private static string ZoneVar(int z) => z < ZoneNames.Length ? ZoneNames[z] : "--zone-" + z.ToString(CultureInfo.InvariantCulture);

        private static double Scalar(object v, double fallback)
        {
            if (v == null) { return fallback; }
            if (Expr.IsNumber(v)) { return Expr.ToNumber(v); }
            return double.TryParse(Expr.ToText(v), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : fallback;
        }

        // ------------------------------------------------------------------ curve (E3)

        private sealed class CurveHost
        {
            public CurveEditor Ed;
            public string Text, Error;
            public string SnapAttr, ModeAttr;
            public double SnapX, SnapY;
            public CurveMode Mode;
            public PointCurve Pc;
            public int PcRev = -1;
            /// <summary>The model is a list of points (edited in its own shape) rather than a text.</summary>
            public bool ListBound;
            /// <summary>The editor holds the points of the current model value (false: a new editor, or a model that changed shape).</summary>
            public bool Loaded;
            public readonly CurveSource Src = new();
            public readonly List<DPt> Pts = new();
            public int Drag = -1, Sel = -1, Hover = -1;
            public readonly ClickCounter Clicks = new();
            public readonly UiPath Line = new();
            public bool Registered;
            public string Key;
            public Rectangle Cr, Plot;
        }

        private void PaintCurve(Painter p, Element e, ComputedStyle s, Rectangle cr)
        {
            var h = HostOf(curves, e);
            double x0 = Num(e, "x-min", 0), x1 = Num(e, "x-max", 1), y0 = Num(e, "y-min", 0), y1 = Num(e, "y-max", 1);
            if (!(x1 > x0) || !(y1 > y0)) { return; }
            if (h.Ed == null || h.Ed.XMin != x0 || h.Ed.XMax != x1 || h.Ed.YMin != y0 || h.Ed.YMax != y1)
            {
                h.Ed = new CurveEditor(x0, x1, y0, y1);
                h.Text = null;
                h.Loaded = false;
            }
            var ed = h.Ed;
            ed.MaxPoints = Math.Max(2, (int)Num(e, "max-points", 16));
            string mono = e.GetAttribute("monotone");
            ed.Monotone = mono != null && mono != "false";
            string snap = e.GetAttribute("snap") ?? "";
            if (snap != h.SnapAttr)
            {
                h.SnapAttr = snap;
                string[] parts = snap.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                h.SnapX = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sx) && sx > 0 ? sx : 0;
                h.SnapY = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sy) && sy > 0 ? sy : 0;
            }
            ed.SnapX = h.SnapX;
            ed.SnapY = h.SnapY;
            string modeAttr = e.GetAttribute("mode") ?? "linear";
            if (modeAttr != h.ModeAttr)
            {
                h.ModeAttr = modeAttr;
                if (!PointCurve.TryParseMode(modeAttr, out h.Mode)) { h.Mode = CurveMode.Linear; }
            }

            // the value: a list of points (the model's list, read when it changed), or the model's text, or the value attribute without a model
            string spec = e.GetAttribute("data-model");
            object model = spec != null ? w.Template.ReadModel(spec, e) : null;
            if (model != null && model is not string)
            {
                bool changed = h.Src.Read(model);
                if (changed || !h.Loaded || !h.ListBound)
                {
                    h.ListBound = true;
                    h.Text = null;
                    if (!h.Src.Valid) { h.Error = "not a list of points"; }
                    else if (!ed.LoadPoints(h.Src.Xs, h.Src.Ys, h.Src.Count, out h.Error)) { h.Error ??= "bad curve"; }
                    else { h.Error = null; }
                    h.Loaded = true;
                }
            }
            else
            {
                string text = spec == null ? e.GetAttribute("value") : model as string;
                if (text != h.Text || h.ListBound)
                {
                    h.ListBound = false;
                    h.Text = text;
                    if (text != null && !ed.Load(text, out h.Error)) { h.Error ??= "bad curve"; }
                    else { h.Error = null; }
                }
            }
            if (ed.Count == 0) { return; }

            // the plot: the content box less a margin
            const int pad = 6;
            var plot = new Rectangle(cr.X + pad, cr.Y + pad, cr.Width - 2 * pad, cr.Height - 2 * pad);
            if (plot.Width < 10 || plot.Height < 10) { return; }
            h.Cr = cr; h.Plot = plot;
            var sb = p.Batch;
            float op = opacity;
            float plotW = plot.Width, plotH = plot.Height;

            // grid: the ticks of both axes
            var gridCol = VarColor(s, "--grid-color", GridDefault, op);
            CurveGrid(sb, plot, x0, x1, y0, y1, gridCol);

            // the curve: linear, step, or smooth (the monotone curve the game reads); the smooth one is made again only when the points change
            var raw = h.Line.Raw;
            raw.Clear();
            if (h.Mode == CurveMode.Smooth && ed.Count >= 2)
            {
                if (h.PcRev != ed.Revision)
                {
                    var xs = new double[ed.Count];
                    var ys = new double[ed.Count];
                    for (int i = 0; i < ed.Count; i++) { xs[i] = ed[i].X; ys[i] = ed[i].Y; }
                    PointCurve.TryCreate(xs, ys, CurveMode.Smooth, out h.Pc, out _);
                    h.PcRev = ed.Revision;
                }
                int steps = Math.Clamp(plot.Width / 3, 8, 400);
                double a = ed[0].X, b = ed[ed.Count - 1].X;
                for (int k = 0; k <= steps; k++)
                {
                    double x = a + (b - a) * k / steps;
                    double y = h.Pc != null ? h.Pc.Eval(x) : ed.LinearAt(x);
                    var q = ed.ToPixel(new DPt(x, y), plotW, plotH);
                    raw.Add(new DPt(plot.X + q.X, plot.Y + q.Y));
                }
            }
            else
            {
                for (int i = 0; i < ed.Count; i++)
                {
                    var q = ed.ToPixel(i, plotW, plotH);
                    if (h.Mode == CurveMode.Step && i > 0)
                    {
                        var prev = ed.ToPixel(i - 1, plotW, plotH);
                        raw.Add(new DPt(plot.X + q.X, plot.Y + prev.Y));
                    }
                    raw.Add(new DPt(plot.X + q.X, plot.Y + q.Y));
                }
            }
            var lineCol = VarColor(s, "--series-1", SeriesPalette[0], op);
            Draw.Stroke(sb, h.Line, VarNum(s, "--stroke-width", 2), lineCol);

            // the points: a dot each, the selected one ringed
            var dotCol = VarColor(s, "--point-color", Color.White, op);
            float size = VarNum(s, "--point-size", 8);
            int rad = Math.Max(2, (int)Math.Round(size / 2));
            for (int i = 0; i < ed.Count; i++)
            {
                var q = ed.ToPixel(i, plotW, plotH);
                var c = new Vector2((float)(plot.X + q.X), (float)(plot.Y + q.Y));
                Draw.Disc(sb, c, rad, dotCol);
                if (i == h.Sel) { Draw.Ring(sb, c, rad + 3, rad + 2, VarColor(s, "--series-2", SeriesPalette[1], op)); }
            }

            // the mouse: registered once per element, the painted box is kept for the handler
            string key = w.CanvasInput.KeyOf(e);
            if (!h.Registered || h.Key != key)
            {
                h.Key = key;
                w.CanvasInput.Set(key, ev => CurveMouse(h, e, ev));
                h.Registered = true;
            }
            w.CanvasInput.Painted(e, cr, p.Xf);
        }

        private void CurveGrid(SpriteBatch sb, Rectangle plot, double x0, double x1, double y0, double y1, Color col)
        {
            if (ChartScale.Ticks(x0, x1, 5, ticks, out _, out _, out _))
            {
                foreach (var t in ticks)
                {
                    int x = plot.X + (int)Math.Round(ChartScale.Map(t, x0, x1, 0, plot.Width));
                    if (x >= plot.X && x <= plot.Right) { sb.Draw(GUI.WhiteTexture, new Rectangle(x, plot.Y, 1, plot.Height), col); }
                }
            }
            if (ChartScale.Ticks(y0, y1, 5, ticks, out _, out _, out _))
            {
                foreach (var t in ticks)
                {
                    int y = plot.Bottom - (int)Math.Round(ChartScale.Map(t, y0, y1, 0, plot.Height));
                    if (y >= plot.Y && y <= plot.Bottom) { sb.Draw(GUI.WhiteTexture, new Rectangle(plot.X, y, plot.Width, 1), col); }
                }
            }
        }

        /// <summary>The curve's mouse: a left press on a point drags it (the move follows the pointer, oninput each step), a double click on empty plot adds a
        /// point, a right press removes the point, a release fires onchange. The handler answers true only when it takes the press.</summary>
        private bool CurveMouse(CurveHost h, Element e, CanvasMouseEvent ev)
        {
            var ed = h.Ed;
            if (ed == null || h.Plot.Width <= 0) { return false; }
            float px = ev.X + h.Cr.X - h.Plot.X, py = ev.Y + h.Cr.Y - h.Plot.Y;
            float pw = h.Plot.Width, ph = h.Plot.Height;
            switch (ev.Kind)
            {
                case CanvasMouseKind.Move:
                    if (h.Drag >= 0)
                    {
                        var v = ed.FromPixel(px, py, pw, ph);
                        ed.Move(h.Drag, v.X, v.Y);
                        Commit(e, h, "oninput");
                        return true;
                    }
                    h.Hover = ed.HitTest(px, py, pw, ph, 9);
                    return false;
                case CanvasMouseKind.Down when ev.Button == MouseButtons.Left:
                {
                    // a press takes the keyboard, as a text field does: Delete and the arrows then reach the curve
                    w.Controls.SetFocus(e);
                    int hit = ed.HitTest(px, py, pw, ph, 9);
                    int n = h.Clicks.Count(in ev);
                    if (hit >= 0)
                    {
                        h.Sel = hit;
                        h.Drag = hit;
                        return true;
                    }
                    if (n >= 2)
                    {
                        var v = ed.FromPixel(px, py, pw, ph);
                        int at = ed.Add(v.X, v.Y);
                        if (at >= 0) { h.Sel = at; Commit(e, h, "onchange"); return true; }
                    }
                    return false;
                }
                case CanvasMouseKind.Down when ev.Button == MouseButtons.Right:
                {
                    int hit = ed.HitTest(px, py, pw, ph, 9);
                    if (hit >= 0 && ed.Remove(hit))
                    {
                        h.Sel = -1;
                        Commit(e, h, "onchange");
                        return true;
                    }
                    return false;
                }
                case CanvasMouseKind.Up when ev.Button == MouseButtons.Left && h.Drag >= 0:
                    h.Drag = -1;
                    Commit(e, h, "onchange");
                    return true;
                case CanvasMouseKind.Cancel:
                case CanvasMouseKind.Leave:
                    h.Drag = -1;
                    h.Hover = -1;
                    return false;
            }
            return false;
        }

        /// <summary>Writes the curve back to its model (as text, or as points in the model's own list or array shape) and fires the event. A model that cannot
        /// take the points is logged and kept as it is.</summary>
        private void Commit(Element e, CurveHost h, string eventAttr)
        {
            string spec = e.GetAttribute("data-model");
            if (h.ListBound && spec != null)
            {
                h.Pts.Clear();
                for (int i = 0; i < h.Ed.Count; i++) { h.Pts.Add(h.Ed[i]); }
                object model = w.Template.ReadModel(spec, e);
                object stored = CurveSource.Write(model, h.Pts);
                if (stored == null) { UiPlugin.Log($"{w.Name}: curve {spec} cannot take the points (not a list of points of its shape)"); }
                else if (!ReferenceEquals(stored, model)) { if (w.Template.WriteModel(e, stored)) { w.Refresh(); } }
                else { w.Refresh(); }
            }
            else
            {
                string text = h.Ed.Format();
                h.Text = text;
                if (spec != null) { if (w.Template.WriteModel(e, text)) { w.Refresh(); } }
                else { e.SetAttribute("value", text); }
            }
            w.Fire(e, eventAttr);
        }

        /// <summary>The keys of a focused curve: the arrows nudge the selected point by a snap step, Delete (or Backspace) removes it. Tab is not here (the page
        /// moves the focus).</summary>
        internal void Key(Element e, Keys key)
        {
            if (!curves.TryGetValue(e, out var h) || h.Ed == null || h.Ed.Count == 0) { return; }
            if (h.Sel < 0 || h.Sel >= h.Ed.Count) { h.Sel = h.Ed.Count > 2 ? 1 : 0; }
            int i = h.Sel;
            bool changed = false;
            switch (key)
            {
                case Keys.Left: changed = h.Ed.Nudge(i, -1, 0); break;
                case Keys.Right: changed = h.Ed.Nudge(i, 1, 0); break;
                case Keys.Up: changed = h.Ed.Nudge(i, 0, 1); break;
                case Keys.Down: changed = h.Ed.Nudge(i, 0, -1); break;
                case Keys.Delete:
                case Keys.Back:
                    if (h.Ed.Remove(i)) { h.Sel = Math.Min(i, h.Ed.Count - 1); changed = true; }
                    break;
            }
            if (changed) { Commit(e, h, "onchange"); }
        }

        // ------------------------------------------------------------------ chart (E4)

        private sealed class ChartHost
        {
            public readonly List<UiPath> Paths = new();
            public readonly List<SeriesRef> Refs = new();
            public readonly List<NumberSeries> Series = new();
            public readonly List<double[]> Shown = new();
            public string SpecKey, KindAttr, Format;
            public ChartKind Kind;
            public int Hover = -1;
            public bool Pressed, Registered;
            public string Key;
            public double LastTime = double.NaN;
            public Rectangle Cr, Plot;
            public double TLo, THi;
            public int N, Ns;
            /// <summary>Counts the changes of any series' numbers: the tooltip and the hover are made again when it moves.</summary>
            public long DataRev;
            public ChangeKey TipKey;
            public readonly List<string> Tip = new();
            public readonly TickLabels YLabels = new(), XTickLabels = new();
            public readonly CategoryLabels XLabels = new();
        }

        private void PaintChart(Painter p, Element e, ComputedStyle s, Rectangle cr)
        {
            var h = HostOf(charts, e);
            string spec = e.GetAttribute("data-series");
            if (spec != h.SpecKey)
            {
                h.SpecKey = spec;
                h.Refs.Clear();
                if (!ChartScale.TryParseSeries(spec, h.Refs, out string err)) { UiPlugin.Log($"{w.Name}: chart: {err}"); }
                h.Paths.Clear(); h.Shown.Clear(); h.Series.Clear();
                for (int i = 0; i < h.Refs.Count; i++) { h.Paths.Add(new UiPath()); h.Shown.Add(Array.Empty<double>()); h.Series.Add(new NumberSeries()); }
                h.TipKey.Reset();
            }
            string kindAttr = e.GetAttribute("type") ?? "line";
            if (kindAttr != h.KindAttr)
            {
                h.KindAttr = kindAttr;
                if (!ChartScale.TryParseKind(kindAttr, out ChartKind kind)) { kind = ChartKind.Line; }
                h.Kind = kind;
            }
            ChartKind k0 = h.Kind;
            string format = e.GetAttribute("format") ?? "0.##";
            if (format != h.Format) { h.Format = format; h.TipKey.Reset(); }
            int ns = h.Refs.Count;
            if (ns == 0) { return; }

            // the data: one reused buffer per series (the model's numbers), the revision moves when any of them changed
            int n = 0;
            double dmin = double.PositiveInfinity, dmax = double.NegativeInfinity;
            bool data = false;
            for (int i = 0; i < ns; i++)
            {
                var ser = h.Series[i];
                if (ser.Read(w.Template.ReadModel(h.Refs[i].Path, e))) { data = true; }
                n = Math.Max(n, ser.Count);
                var vals = ser.Values;
                for (int k = 0; k < ser.Count; k++) { dmin = Math.Min(dmin, vals[k]); dmax = Math.Max(dmax, vals[k]); }
            }
            if (data) { h.DataRev++; }
            if (n == 0) { return; }
            if (k0 is ChartKind.Bar or ChartKind.Area) { dmin = Math.Min(dmin, 0); }
            double lo = Num(e, "y-min", dmin), hi = Num(e, "y-max", dmax);
            if (!ChartScale.Ticks(lo, hi, 5, ticks, out double tlo, out double thi, out _)) { return; }
            if (!double.IsNaN(Num(e, "y-min", double.NaN))) { tlo = lo; }
            if (!double.IsNaN(Num(e, "y-max", double.NaN))) { thi = hi; }
            h.TLo = tlo; h.THi = thi;
            h.N = n; h.Ns = ns;
            h.YLabels.Ensure(ticks, h.Format);

            // the x axis: category labels (x-labels="model.path"), or numeric ticks when x-min or x-max is given; the room below the plot is kept for them
            double xlo = Num(e, "x-min", 0), xhi = Num(e, "x-max", Math.Max(1, n - 1));
            string xSpec = e.GetAttribute("x-labels");
            if (xSpec != null) { h.XLabels.Read(w.Template.ReadModel(xSpec, e)); }
            bool catLabels = xSpec != null && h.XLabels.Count > 0;
            bool numX = !catLabels && (e.GetAttribute("x-min") != null || e.GetAttribute("x-max") != null);
            if (numX && ChartScale.Ticks(xlo, xhi, 5, xTicks, out _, out _, out _)) { h.XTickLabels.Ensure(xTicks, h.Format); }
            else if (numX) { xTicks.Clear(); }
            bool xAxis = catLabels || numX;
            float xAxisH = xAxis ? w.Fonts.NormalLineHeight(s) + 4 : 0;

            // the plot: room on the left for the axis labels
            var sb = p.Batch;
            float op = opacity;
            float labelW = 0;
            for (int k = 0; k < ticks.Count; k++) { labelW = Math.Max(labelW, w.Fonts.Width(h.YLabels.Text[k], s)); }
            var plot = new Rectangle(cr.X + (int)Math.Ceiling(labelW) + 10, cr.Y + 6, cr.Width - (int)Math.Ceiling(labelW) - 16, cr.Height - 12 - (int)Math.Ceiling(xAxisH));
            if (plot.Width < 10 || plot.Height < 10) { return; }
            h.Cr = cr; h.Plot = plot;

            // the eased values (a change of data moves the columns and the lines, it does not jump)
            double now = Timing.TotalTime;
            double dt = double.IsNaN(h.LastTime) ? 0 : Math.Clamp(now - h.LastTime, 0, 0.1);
            h.LastTime = now;
            bool moving = false;
            for (int i = 0; i < ns; i++)
            {
                var ser = h.Series[i];
                int cnt = ser.Count;
                var vals = ser.Values;
                if (h.Shown[i].Length != cnt)
                {
                    var fresh = new double[cnt];
                    Array.Copy(vals, fresh, cnt);
                    h.Shown[i] = fresh;
                    continue;
                }
                var shown = h.Shown[i];
                for (int k = 0; k < cnt; k++)
                {
                    shown[k] = GaugeMath.Approach(shown[k], vals[k], dt, 0.12);
                    if (Math.Abs(shown[k] - vals[k]) > 1e-3 * Math.Max(1e-9, thi - tlo)) { moving = true; }
                }
            }
            if (moving) { w.Refresh(); }

            // grid and axis labels
            var gridCol = VarColor(s, "--grid-color", GridDefault, op);
            var labelCol = Painter.Tint(s.GetColor("color"), op);
            for (int k = 0; k < ticks.Count; k++)
            {
                int y = plot.Bottom - (int)Math.Round(ChartScale.Map(ticks[k], tlo, thi, 0, plot.Height));
                sb.Draw(GUI.WhiteTexture, new Rectangle(plot.X, y, plot.Width, 1), gridCol);
                float by = y + w.Fonts.Ascent(s) / 2f;
                Draw.Text(sb, w.Fonts, s, h.YLabels.Text[k], cr.X + labelW, by - w.Fonts.Ascent(s) * 0.5f + 0f, labelCol, 'r');
            }
            float zeroY = Math.Clamp(plot.Bottom - (float)ChartScale.Map(0, tlo, thi, 0, plot.Height), plot.Y, plot.Bottom);

            float stroke = VarNum(s, "--stroke-width", 2);
            // x positions: the points at the ends of the plot for the lines, the middles of the slots for the bars
            double Xat(int k) => k0 == ChartKind.Bar ? plot.X + (k + 0.5) * plot.Width / n : (n > 1 ? plot.X + k * (double)plot.Width / (n - 1) : plot.X + plot.Width / 2.0);
            double Yat(double v) => plot.Bottom - ChartScale.Map(v, tlo, thi, 0, plot.Height);

            if (k0 == ChartKind.Bar)
            {
                for (int k = 0; k < n; k++)
                {
                    ChartScale.BarSlot(k, n, plot.Width, 0.7, out double bx0, out double bx1);
                    double subW = (bx1 - bx0) / ns;
                    for (int i = 0; i < ns; i++)
                    {
                        if (k >= h.Series[i].Count) { continue; }
                        double top = Yat(h.Shown[i][k]);
                        int x = plot.X + (int)Math.Round(bx0 + i * subW + 1), wdt = Math.Max(1, (int)Math.Round(subW - 2));
                        int yTop = (int)Math.Round(Math.Min(top, zeroY)), yBot = (int)Math.Round(Math.Max(top, zeroY));
                        var rect = new Rectangle(x, yTop, wdt, Math.Max(1, yBot - yTop));
                        var col = SeriesColor(s, i, op);
                        int rr = Math.Min(4, Math.Min(rect.Width, rect.Height) / 2);
                        if (rr >= 2) { Draw.FillRound(sb, rect, rr, col); } else { sb.Draw(GUI.WhiteTexture, rect, col); }
                    }
                }
            }
            else
            {
                for (int i = 0; i < ns; i++)
                {
                    var raw = h.Paths[i].Raw;
                    raw.Clear();
                    var vals = h.Shown[i];
                    int cnt = h.Series[i].Count;
                    for (int k = 0; k < cnt; k++)
                    {
                        double x = Xat(k), y = Yat(vals[k]);
                        if (k0 == ChartKind.Step && k > 0) { raw.Add(new DPt(x, Yat(vals[k - 1]))); }
                        raw.Add(new DPt(x, y));
                    }
                    var col = SeriesColor(s, i, op);
                    if (k0 == ChartKind.Area && raw.Count > 0)
                    {
                        Draw.Area(sb, raw, zeroY, col * 0.25f);
                    }
                    if (raw.Count >= 2) { Draw.Stroke(sb, h.Paths[i], stroke, col); }
                }
            }

            // the x axis labels: the categories (every stride-th when they would touch), or the numeric ticks mapped onto the categories
            if (xAxis)
            {
                float baseline = plot.Bottom + 2 + w.Fonts.Ascent(s);
                if (catLabels)
                {
                    int cnt = Math.Min(h.XLabels.Count, n);
                    float maxW = 0;
                    for (int k = 0; k < cnt; k++) { maxW = Math.Max(maxW, w.Fonts.Width(h.XLabels.Text[k], s)); }
                    double pitch = n > 1 ? Xat(1) - Xat(0) : plot.Width;
                    int stride = ChartScale.LabelStride(cnt, pitch, maxW, 8);
                    for (int k = 0; k < cnt; k += stride) { Draw.Text(sb, w.Fonts, s, h.XLabels.Text[k], (float)Xat(k), baseline, labelCol, 'c'); }
                }
                else
                {
                    for (int k = 0; k < xTicks.Count; k++)
                    {
                        if (xTicks[k] < xlo - 1e-9 || xTicks[k] > xhi + 1e-9) { continue; }
                        double x = n > 1 ? ChartScale.Map(xTicks[k], xlo, xhi, Xat(0), Xat(n - 1)) : Xat(0);
                        Draw.Text(sb, w.Fonts, s, h.XTickLabels.Text[k], (float)x, baseline, labelCol, 'c');
                    }
                }
            }

            // hover: the line at the category and a tooltip of the values there; the tooltip text is made again only when the category or the data change
            if (h.Hover >= 0 && h.Hover < n)
            {
                double hx = Xat(h.Hover);
                sb.Draw(GUI.WhiteTexture, new Rectangle((int)Math.Round(hx), plot.Y, 1, plot.Height), labelCol * 0.5f);
                if (h.TipKey.Update(h.Hover, h.DataRev))
                {
                    h.Tip.Clear();
                    for (int i = 0; i < ns; i++)
                    {
                        var ser = h.Series[i];
                        if (h.Hover < ser.Count) { h.Tip.Add(h.Refs[i].Name + ": " + ChartScale.Label(ser.Values[h.Hover], h.Format)); }
                    }
                }
                float tw = 0;
                foreach (var l in h.Tip) { tw = Math.Max(tw, w.Fonts.Width(l, s)); }
                float lh = w.Fonts.NormalLineHeight(s);
                var box = new Rectangle((int)Math.Round(Math.Min(hx + 10, plot.Right - tw - 12)), plot.Y + 4, (int)Math.Ceiling(tw) + 10, (int)Math.Ceiling(lh * h.Tip.Count) + 8);
                sb.Draw(GUI.WhiteTexture, box, new Color(0x0a, 0x14, 0x18) * op * 0.92f);
                float ty = box.Y + 4 + w.Fonts.Ascent(s);
                foreach (var l in h.Tip)
                {
                    Draw.Text(sb, w.Fonts, s, l, box.X + 5, ty, labelCol, 'l');
                    ty += lh;
                }
            }

            string key = w.CanvasInput.KeyOf(e);
            if (!h.Registered || h.Key != key)
            {
                h.Key = key;
                w.CanvasInput.Set(key, ev => ChartMouse(h, e, ev));
                h.Registered = true;
            }
            w.CanvasInput.Painted(e, cr, p.Xf);
        }

        private static Color SeriesColor(ComputedStyle s, int i, float op) => VarColor(s, SeriesVar(i), SeriesPalette[i % SeriesPalette.Length], op);

        private bool ChartMouse(ChartHost h, Element e, CanvasMouseEvent ev)
        {
            int n = h.N;
            ChartKind kind = h.Kind;
            if (h.Plot.Width <= 0 || n == 0) { return false; }
            float mx = ev.X + h.Cr.X, my = ev.Y + h.Cr.Y;
            bool inside = mx >= h.Plot.X && mx <= h.Plot.Right && my >= h.Plot.Y && my <= h.Plot.Bottom;
            int idx = -1;
            if (inside)
            {
                if (kind == ChartKind.Bar) { idx = Math.Clamp((int)Math.Floor((mx - h.Plot.X) / (h.Plot.Width / (double)n)), 0, n - 1); }
                else { idx = ChartScale.NearestIndex(mx, h.Plot.X, n > 1 ? h.Plot.Width / (double)(n - 1) : h.Plot.Width, n); }
            }
            switch (ev.Kind)
            {
                case CanvasMouseKind.Move:
                    h.Hover = idx;
                    return false;
                case CanvasMouseKind.Leave:
                case CanvasMouseKind.Cancel:
                    h.Hover = -1;
                    h.Pressed = false;
                    return false;
                case CanvasMouseKind.Down when ev.Button == MouseButtons.Left:
                    h.Pressed = idx >= 0;
                    return false;
                case CanvasMouseKind.Up when ev.Button == MouseButtons.Left:
                    if (h.Pressed && idx >= 0)
                    {
                        h.Pressed = false;
                        // the index: data-picked (the attribute), and $arg in the onpick handler
                        e.SetAttribute("data-picked", idx.ToString(CultureInfo.InvariantCulture));
                        w.Fire(e, "onpick", idx);
                    }
                    h.Pressed = false;
                    return false;
            }
            return false;
        }

        // ------------------------------------------------------------------ gauge (E5)

        private sealed class GaugeHost
        {
            public double Shown;
            public bool Started;
            public double LastTime = double.NaN;
            public readonly UiPath Track = new(), Needle = new();
            public readonly List<UiPath> Ticks = new();
            public readonly List<UiPath> Zones = new();
            public readonly List<double> Thresholds = new();
            public string ZonesAttr;
            public readonly TickLabels Labels = new();
            public readonly ValueLabel Reading = new();
        }

        private static UiPath ZoneAt(GaugeHost h, int z)
        {
            while (h.Zones.Count <= z) { h.Zones.Add(new UiPath()); }
            return h.Zones[z];
        }

        private void PaintGauge(Painter p, Element e, ComputedStyle s, Rectangle cr)
        {
            var h = HostOf(gauges, e);
            double min = Num(e, "min", 0), max = Num(e, "max", 100);
            if (!(max > min)) { max = min + 1; }
            string spec = e.GetAttribute("data-model");
            double target = Scalar(spec != null ? w.Template.ReadModel(spec, e) : null, Num(e, "value", min));
            target = Math.Clamp(target, min, max);
            if (!h.Started) { h.Shown = target; h.Started = true; }
            double now = Timing.TotalTime;
            double dt = double.IsNaN(h.LastTime) ? 0 : Math.Clamp(now - h.LastTime, 0, 0.1);
            h.LastTime = now;
            h.Shown = GaugeMath.Approach(h.Shown, target, dt, 0.15);
            if (Math.Abs(h.Shown - target) > 1e-3 * (max - min)) { w.Refresh(); }

            // the tick labels stand inside the arc, each pushed in from the end of its tick by its own half size along the radius (without that, wide
            // labels ran into the arc); their texts are made once per scale
            string fmt = e.GetAttribute("format") ?? "0";
            int tickCount = (int)Math.Clamp(Num(e, "ticks", 5), 2, 20);
            bool hasTicks = ChartScale.Ticks(min, max, tickCount, ticks, out _, out _, out _);
            float labelH = w.Fonts.Ascent(s) * 1.3f;
            if (hasTicks) { h.Labels.Ensure(ticks, fmt); }
            const float gap = 3;

            // the dial: the arc spans -135..135 degrees from the top; the room below the arc is not needed
            const float pad = 6;
            float r = Math.Min(cr.Width / 2f - pad, (cr.Height - 2 * pad) / 1.707f);
            if (r < 8) { return; }
            float cx = cr.X + cr.Width / 2f, cy = cr.Y + pad + r;
            float op = opacity;
            float start = (float)GaugeMath.DefaultStart, sweep = (float)GaugeMath.DefaultSweep;
            var sb = p.Batch;
            float trackW = VarNum(s, "--track-width", Math.Max(3, r * 0.1f));

            float Rad(double deg) => (float)(deg * Math.PI / 180);
            Draw.Arc(sb, h.Track, cx, cy, r, Rad(start), Rad(start + sweep), trackW, VarColor(s, "--track-color", TrackDefault, op));

            // zones: the bands between min, the thresholds and max, each in its colour (the thresholds are parsed when the attribute changes)
            string zattr = e.GetAttribute("zones");
            if (zattr != h.ZonesAttr)
            {
                h.ZonesAttr = zattr;
                GaugeMath.TryParseThresholds(zattr, h.Thresholds);
            }
            double prev = min;
            for (int z = 0; z <= h.Thresholds.Count; z++)
            {
                double end = z < h.Thresholds.Count ? Math.Min(h.Thresholds[z], max) : max;
                if (end > prev)
                {
                    var col = VarColor(s, ZoneVar(z), ZonePalette[Math.Min(z, ZonePalette.Length - 1)], op);
                    Draw.Arc(sb, ZoneAt(h, z), cx, cy, r, Rad(GaugeMath.Angle(prev, min, max, start, sweep)), Rad(GaugeMath.Angle(end, min, max, start, sweep)), trackW, col);
                }
                prev = end;
            }
            int zone = GaugeMath.ZoneOf(h.Shown, h.Thresholds);
            var needleCol = VarColor(s, "--needle", NeedleDefault, op);
            var zoneCol = VarColor(s, ZoneVar(zone), ZonePalette[Math.Min(zone, ZonePalette.Length - 1)], op);

            // ticks inside the arc and their labels further in; on a small dial, where neighbouring labels would touch, every 2nd (3rd, ...) tick is labelled
            var tickCol = VarColor(s, "--tick-color", new Color(0x8a, 0x9a, 0xa0), op);
            if (hasTicks)
            {
                float outer = r - trackW / 2 - 2, inner = outer - Math.Max(4, r * 0.1f);
                int stride = 1;
                while (stride < ticks.Count && LabelsTouch(stride)) { stride++; }
                for (int i = 0; i < ticks.Count; i++)
                {
                    double t = ticks[i];
                    if (t < min - 1e-9 || t > max + 1e-9) { continue; }
                    double deg = GaugeMath.Angle(t, min, max, start, sweep);
                    while (h.Ticks.Count <= i) { h.Ticks.Add(new UiPath()); }
                    var tp = h.Ticks[i];
                    tp.Raw.Clear();
                    tp.Raw.Add(GaugeMath.Point(cx, cy, outer, deg));
                    tp.Raw.Add(GaugeMath.Point(cx, cy, inner, deg));
                    Draw.Stroke(sb, tp, Math.Max(1f, r * 0.02f), tickCol);
                    if (i % stride != 0) { continue; }
                    var lp = LabelAt(i, out _);
                    Draw.Text(sb, w.Fonts, s, h.Labels.Text[i], (float)lp.X, (float)lp.Y + w.Fonts.Ascent(s) * 0.4f, tickCol, 'c');
                }

                // the middle of label i and its width
                DPt LabelAt(int i, out float tw)
                {
                    double deg = GaugeMath.Angle(ticks[i], min, max, start, sweep), a = deg * Math.PI / 180;
                    tw = w.Fonts.Width(h.Labels.Text[i], s);
                    float ext = (float)(Math.Abs(Math.Sin(a)) * tw / 2 + Math.Abs(Math.Cos(a)) * labelH / 2);
                    return GaugeMath.Point(cx, cy, inner - gap - ext, deg);
                }

                // whether two labels a stride apart (the ones that would be drawn) overlap, with a little air between them
                bool LabelsTouch(int step)
                {
                    for (int i = 0; i + step < ticks.Count; i += step)
                    {
                        var p1 = LabelAt(i, out float w1);
                        var p2 = LabelAt(i + step, out float w2);
                        if (Math.Abs(p1.X - p2.X) < (w1 + w2) / 2 + 3 && Math.Abs(p1.Y - p2.Y) < labelH) { return true; }
                    }
                    return false;
                }
            }

            // the reading: a filled sector up to the value, or a needle; then the hub and the value below it
            double angle = GaugeMath.Angle(h.Shown, min, max, start, sweep);
            if (e.GetAttribute("fill") == "sector")
            {
                Draw.Sector(sb, cx, cy, r - trackW, Rad(start), Rad(angle), zoneCol * 0.35f);
            }
            var tip = GaugeMath.Point(cx, cy, r * 0.85, angle);
            h.Needle.Raw.Clear();
            h.Needle.Raw.Add(new DPt(cx, cy));
            h.Needle.Raw.Add(new DPt(tip.X, tip.Y));
            Draw.Stroke(sb, h.Needle, Math.Max(2f, r * 0.04f), zone > 0 ? zoneCol : needleCol);
            Draw.Disc(sb, new Vector2(cx, cy), Math.Max(3, (int)Math.Round(r * 0.06)), needleCol);
            Draw.Text(sb, w.Fonts, s, h.Reading.Of(h.Shown, fmt), cx, cy + r * 0.35f + w.Fonts.Ascent(s), needleCol, 'c');
        }
    }
}
