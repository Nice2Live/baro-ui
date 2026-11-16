using System;
using System.Diagnostics;
using Barotrauma;

namespace BaroUI.Render
{
    /// <summary>Passive statistics of a window while it is on screen: one console-log line per 10 s with the frame rate
    /// (the time between two paints of the window, which is the whole game frame), what the page costs in it (data, layout,
    /// paint) and what its owner adds (Host). A laggy session leaves the numbers in the log without a test run.</summary>
    internal sealed class FrameStats
    {
        private const double Period = 10.0;

        private long lastPaint;
        private double since;
        private int frames, ensures, hosts;
        private double frameSum, frameMax, ensureSum, ensureMax, paintSum, paintMax, hostSum, hostMax, gameSum, gameMax;
        private int gc0, gc1, gc2;

        /// <summary>One call of the window's data and layout step (EnsureLayout).</summary>
        public void Ensure(double ms) { ensures++; ensureSum += ms; ensureMax = Math.Max(ensureMax, ms); }

        /// <summary>The owner's own per-frame work for this window (the terminal's refresh, the game's slots...).</summary>
        public void Host(double ms) { hosts++; hostSum += ms; hostMax = Math.Max(hostMax, ms); }

        /// <summary>The game's own update of the item the window stands for (its inventories, the slots' dragging).</summary>
        public void Game(double ms) { gameSum += ms; gameMax = Math.Max(gameMax, ms); }

        /// <summary>The window was drawn in this frame; <paramref name="ms"/> is what its paint took.</summary>
        public void Paint(string name, double ms)
        {
            long now = Stopwatch.GetTimestamp();
            double total = Timing.TotalTime;
            if (since == 0 || total - lastAt > 1.0) { Restart(total); }
            else
            {
                double gap = (now - lastPaint) * 1000.0 / Stopwatch.Frequency;
                frames++;
                frameSum += gap;
                frameMax = Math.Max(frameMax, gap);
            }
            lastPaint = now;
            lastAt = total;
            paintSum += ms;
            paintMax = Math.Max(paintMax, ms);
            if (total - since < Period || frames == 0) { return; }
            double secs = total - since;
            UiPlugin.Log($"{name}: frames {secs:0} s: {frames / secs:0.0} fps, frame avg {frameSum / frames:0.0} / max {frameMax:0.0} ms; " +
                         $"data+layout avg {ensureSum / frames:0.00} / max {ensureMax:0.0} ms; paint avg {paintSum / frames:0.00} / max {paintMax:0.0} ms; " +
                         $"owner avg {hostSum / frames:0.00} / max {hostMax:0.0} ms; game item update avg {gameSum / frames:0.00} / max {gameMax:0.0} ms; GC {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
            Restart(total);
        }

        private double lastAt;

        private void Restart(double total)
        {
            since = total;
            frames = ensures = hosts = 0;
            frameSum = frameMax = ensureSum = ensureMax = paintSum = paintMax = hostSum = hostMax = gameSum = gameMax = 0;
            gc0 = GC.CollectionCount(0);
            gc1 = GC.CollectionCount(1);
            gc2 = GC.CollectionCount(2);
        }
    }
}
