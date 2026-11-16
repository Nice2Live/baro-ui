using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI
{
    /// <summary>Stage 0 recon (docs/ui/ENGINE_PLAN.md), active only with bbe_ui_probe.flag in the game folder.
    /// 15 s into a round it loads the monospace font and runs the benches one after another: N game GUIComponents
    /// (frame + text) vs N boxes drawn directly in one GUICustomComponent. Update and draw are timed separately
    /// (UpdateManually/DrawManually), so a frame cap does not hide the cost. Results: "[BBE UI] probe" lines.</summary>
    internal sealed class Probe : IEventUpdate, IEventRoundStarted
    {
        private const string Flag = "bbe_ui_probe.flag";
        private const int Frames = 180;
        private const string FontPath = "Content/Fonts/SourceCodePro-Semibold.ttf";
        private static Probe instance;

        private double roundStart = double.MaxValue;
        private readonly Queue<Bench> queue = new();
        private Bench current;
        private GUICustomComponent host;
        private ScalableFont mono;
        private bool fontChecked;

        public static void Init()
        {
            if (!File.Exists(Flag)) { return; }
            instance = new Probe();
            LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(instance);
            LuaCsSetup.Instance.EventService.Subscribe<IEventRoundStarted>(instance);
            Log("armed");
        }

        public static void Dispose()
        {
            if (instance == null) { return; }
            LuaCsSetup.Instance.EventService.Unsubscribe<IEventUpdate>(instance);
            LuaCsSetup.Instance.EventService.Unsubscribe<IEventRoundStarted>(instance);
            instance.mono?.Dispose();
            instance = null;
        }

        public void OnRoundStart()
        {
            roundStart = Timing.TotalTime;
            queue.Clear();
            current = null;
        }

        public void OnUpdate(double deltaTime)
        {
            if (!(GameMain.GameSession?.IsRunning ?? false) || Timing.TotalTime < roundStart + 15) { return; }
            try
            {
                Step((float)deltaTime);
            }
            catch (Exception e)
            {
                Log($"ERROR {e}");
                queue.Clear();
                current = null;
                roundStart = double.MaxValue;
            }
        }

        private void Step(float deltaTime)
        {
            if (!fontChecked)
            {
                fontChecked = true;
                CheckFont();
                foreach (int n in new[] { 500, 2000 })
                {
                    queue.Enqueue(new ComponentBench(n, textUpdates: 0));
                    queue.Enqueue(new ImmediateBench(n, mono));
                }
                queue.Enqueue(new ComponentBench(2000, textUpdates: 200));
                queue.Enqueue(new MeasureBench(mono));
            }
            if (current == null)
            {
                if (queue.Count == 0) { roundStart = double.MaxValue; Log("done"); return; }
                current = queue.Dequeue();
                current.RunSetup();
            }
            // OnUpdate runs after GUI.Update and before Draw: the host is drawn this frame (no input, none needed)
            host ??= new GUICustomComponent(new RectTransform(new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight)),
                (sb, _) => current?.RunDraw(sb));
            host.AddToGUIUpdateList(order: 1);
            current.RunUpdate(deltaTime);
            if (current.DrawnFrames >= Frames || current.DrawFree)
            {
                Log(current.Report());
                current.Teardown();
                current = null;
            }
        }

        private void CheckFont()
        {
            long alloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            mono = new ScalableFont(FontPath, 16, GameMain.Instance.GraphicsDevice, dynamicLoading: true);
            double loadMs = sw.Elapsed.TotalMilliseconds;
            const string probe = "АЯаяЁё№×₂✕—…→●▲▼░█";
            var missing = probe.Where(c => mono.MeasureString(c.ToString()).X <= 0).ToArray();
            var gameMissing = probe.Where(c => GUIStyle.Font.MeasureChar(c).X <= 0).ToArray();
            Log($"font {FontPath}: load {loadMs:0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - alloc) / 1024} KB; "
                + $"missing in mono: [{new string(missing)}], missing in game Font: [{new string(gameMissing)}]; "
                + $"GUI.Scale {GUI.Scale:0.00}, screen {GameMain.GraphicsWidth}x{GameMain.GraphicsHeight}");
        }

        private static void Log(string text) => UiPlugin.Log($"probe {text}");

        private abstract class Bench
        {
            private readonly List<double> update = new(), draw = new(), alloc = new();
            private double setupMs;
            private long setupAlloc;
            public int DrawnFrames => draw.Count;
            /// <summary>True for benches with no per-frame part.</summary>
            public virtual bool DrawFree => false;
            protected abstract string Name { get; }

            public void RunSetup()
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                Setup();
                setupMs = sw.Elapsed.TotalMilliseconds;
                setupAlloc = GC.GetAllocatedBytesForCurrentThread() - a;
            }

            public void RunUpdate(float dt)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                Update(dt);
                update.Add(sw.Elapsed.TotalMilliseconds);
                alloc.Add(GC.GetAllocatedBytesForCurrentThread() - a);
            }

            public void RunDraw(SpriteBatch sb)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                Draw(sb);
                draw.Add(sw.Elapsed.TotalMilliseconds);
                if (alloc.Count > 0) { alloc[^1] += GC.GetAllocatedBytesForCurrentThread() - a; }
            }

            public string Report()
            {
                static string Stat(List<double> v) => v.Count == 0 ? "-"
                    : $"avg {v.Average():0.000} p95 {v.OrderBy(x => x).ElementAt((int)(v.Count * 0.95)):0.000}";
                // skip the first frames: glyph loading and JIT
                var steady = alloc.Skip(10).ToList();
                return $"{Name}: setup {setupMs:0.0} ms / {setupAlloc / 1024} KB; update ms {Stat(update.Skip(10).ToList())}; "
                       + $"draw ms {Stat(draw.Skip(10).ToList())}; alloc/frame {(steady.Count == 0 ? 0 : steady.Average()):0} B";
            }

            protected abstract void Setup();
            protected virtual void Update(float dt) { }
            protected virtual void Draw(SpriteBatch sb) { }
            public virtual void Teardown() { }

            protected static Rectangle Cell(int i, int n)
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(n * 16.0 / 9.0));
                int w = GameMain.GraphicsWidth / cols, h = Math.Max(8, GameMain.GraphicsHeight / (int)Math.Ceiling(n / (double)cols));
                return new Rectangle(i % cols * w, i / cols * h, w - 2, h - 2);
            }

            protected static string[] Texts(int n, string prefix) =>
                Enumerable.Range(0, n).Select(i => $"{prefix} {i} Топливо {i * 7 % 100} %").ToArray();
        }

        /// <summary>N GUIFrame + GUITextBlock, the way TerminalWindow builds its UI today.</summary>
        private sealed class ComponentBench : Bench
        {
            private readonly int n, textUpdates;
            private GUIFrame root;
            private readonly List<GUITextBlock> blocks = new();
            private string[] textsA, textsB;
            private int frame;

            public ComponentBench(int n, int textUpdates) { this.n = n; this.textUpdates = textUpdates; }
            protected override string Name => $"components N={n}" + (textUpdates > 0 ? $" +{textUpdates} text sets/frame" : "");

            protected override void Setup()
            {
                textsA = Texts(n, "A");
                textsB = Texts(n, "B");
                root = new GUIFrame(new RectTransform(new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight)), style: null);
                for (int i = 0; i < n; i++)
                {
                    var r = Cell(i, n);
                    var f = new GUIFrame(new RectTransform(r.Size, root.RectTransform) { AbsoluteOffset = r.Location }, "InnerFrame");
                    blocks.Add(new GUITextBlock(new RectTransform(Vector2.One, f.RectTransform), textsA[i], font: GUIStyle.SmallFont));
                }
            }

            protected override void Update(float dt)
            {
                frame++;
                for (int i = 0; i < textUpdates; i++)
                {
                    int k = (frame * textUpdates + i) % n;
                    blocks[k].Text = frame % 2 == 0 ? textsA[k] : textsB[k];
                }
                root.UpdateManually(dt, alsoChildren: true);
            }

            protected override void Draw(SpriteBatch sb) => root.DrawManually(sb, alsoChildren: true);

            public override void Teardown()
            {
                root.ClearChildren();
                blocks.Clear();
            }
        }

        /// <summary>The same N boxes drawn directly: fill, border, text. No GUIComponent per box.</summary>
        private sealed class ImmediateBench : Bench
        {
            private readonly int n;
            private readonly ScalableFont font;
            private Rectangle[] cells;
            private string[] texts;
            private static readonly Color Fill = new(16, 24, 28), Border = new(64, 200, 190), Text = new(220, 230, 230);

            public ImmediateBench(int n, ScalableFont font) { this.n = n; this.font = font; }
            protected override string Name => $"immediate N={n}";

            protected override void Setup()
            {
                cells = Enumerable.Range(0, n).Select(i => Cell(i, n)).ToArray();
                texts = Texts(n, "A");
            }

            protected override void Draw(SpriteBatch sb)
            {
                for (int i = 0; i < n; i++)
                {
                    var r = cells[i];
                    GUI.DrawRectangle(sb, r, Fill, isFilled: true);
                    GUI.DrawRectangle(sb, r, Border, isFilled: false, thickness: 1);
                    font.DrawString(sb, texts[i], new Vector2(r.X + 3, r.Y + 2), Text);
                }
            }
        }

        /// <summary>Text measuring, the hot path of a layout pass.</summary>
        private sealed class MeasureBench : Bench
        {
            private readonly ScalableFont font;
            private float sink;
            public MeasureBench(ScalableFont font) { this.font = font; }
            public override bool DrawFree => true;
            protected override string Name => $"measure 10000 strings (sink {sink:0})";

            protected override void Setup()
            {
                var texts = Texts(1000, "M");
                for (int k = 0; k < 10; k++)
                {
                    foreach (var t in texts) { sink += font.MeasureString(t).X; }
                }
            }
        }
    }
}
