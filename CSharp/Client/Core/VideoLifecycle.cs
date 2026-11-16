using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>Where a &lt;video&gt; is in its life: Idle (no decoder, nothing wanted), Waiting (wanted, no decoder slot of the window yet), Decoding (a decoder
    /// is loaded and runs), Ended (the clip ran to its end, the last frame stays), Failed (the file cannot be played, or it shows no frame).</summary>
    internal enum VideoPhase { Idle, Waiting, Decoding, Ended, Failed }

    /// <summary>What the Render side does for a video this frame: Load a decoder (then LoadResult), Unload it, Restart it in place (a loop, or play again after
    /// the end), report Ended (onended) or Error (onerror; the decoder is gone). None: nothing.</summary>
    internal enum VideoStep { None, Load, Unload, Restart, Ended, Error }

    /// <summary>What the Render side sees this frame: Active (the element is painted, its window is open, its file is there), Playing (the decoder says it
    /// runs), HasFrame (the first frame was decoded).</summary>
    internal readonly record struct VideoFacts(bool Active, bool Playing, bool HasFrame);

    /// <summary>The decoders a window may run at once (docs/ui/AUTHORING.md: 2). A video that asks beyond it waits; the owner says so once.
    /// Ids are the owners' keys (one per video element). Pure bookkeeping.</summary>
    internal sealed class VideoSlots
    {
        public const int DefaultMax = 2;
        private readonly HashSet<int> held = new HashSet<int>();

        public VideoSlots(int max = DefaultMax) { Max = max; }

        public int Max { get; }
        public int Count => held.Count;
        public bool Holds(int id) => held.Contains(id);

        /// <summary>True when the id holds a slot now (taken now or before); false when every slot is taken by others.</summary>
        public bool TryTake(int id)
        {
            if (held.Contains(id)) { return true; }
            if (held.Count >= Max) { return false; }
            held.Add(id);
            return true;
        }

        public void Give(int id) => held.Remove(id);
    }

    /// <summary>The state machine of one &lt;video&gt; (pure: the Render side runs the decoder and passes its facts in each frame; a fake player tests it).
    /// Play, Pause (Stop is the same: a resumed clip starts from the beginning, the Video API has no seek), Restart and Reset are the commands.
    /// Update returns the one step the Render side runs. A loop restarts in place when the clip has ended (no onended). Without a loop the clip
    /// ends: onended, Want goes off, the last frame stays until the element is hidden (then it unloads: the poster comes back).
    /// A clip with no frame after StartTimeout seconds fails (onerror). The slot is kept while Decoding and Ended, given back when Idle or Failed.</summary>
    internal sealed class VideoLifecycle
    {
        /// <summary>Seconds from loading to the first frame before the video counts as failed.</summary>
        public const double StartTimeout = 5.0;

        private readonly int id;
        private readonly VideoSlots slots;
        private bool restart;

        public VideoLifecycle(int id, VideoSlots slots, bool autoplay, bool loop)
        {
            this.id = id;
            this.slots = slots;
            Want = autoplay;
            Loop = loop;
        }

        public VideoPhase Phase { get; private set; } = VideoPhase.Idle;
        /// <summary>Play was asked (or autoplay) and not paused since: the clip should run while the element is shown.</summary>
        public bool Want { get; private set; }
        public bool Loop { get; set; }
        /// <summary>The time the decoder was last loaded or restarted (Update's clock), the start-up timeout counts from it.</summary>
        public double LoadedAt { get; private set; }
        /// <summary>A decoder is loaded (Decoding or Ended): the owner keeps its player.</summary>
        public bool Loaded => Phase is VideoPhase.Decoding or VideoPhase.Ended;

        public void Play() { if (Phase == VideoPhase.Failed) { Phase = VideoPhase.Idle; } Want = true; }
        public void Pause() => Want = false;
        public void Restart() { if (Phase == VideoPhase.Failed) { Phase = VideoPhase.Idle; } Want = true; restart = true; }

        /// <summary>The owner dropped its player on its own (the source changed, the element went away, the texture budget took it): the slot is given back.</summary>
        public void Reset()
        {
            slots.Give(id);
            Phase = VideoPhase.Idle;
            restart = false;
        }

        /// <summary>The owner tried a Load or Restart step: ok, or not (then the video is Failed and the slot is given back).</summary>
        public void LoadResult(bool ok, double now)
        {
            if (ok) { LoadedAt = now; return; }
            slots.Give(id);
            Phase = VideoPhase.Failed;
            restart = false;
        }

        /// <summary>One frame of the machine. now is the clock in seconds (any origin, the same one every frame).</summary>
        public VideoStep Update(VideoFacts f, double now)
        {
            switch (Phase)
            {
                case VideoPhase.Failed:
                    return VideoStep.None;

                case VideoPhase.Idle:
                case VideoPhase.Waiting:
                    if (!(Want && f.Active))
                    {
                        slots.Give(id);
                        Phase = VideoPhase.Idle;
                        return VideoStep.None;
                    }
                    Phase = VideoPhase.Waiting;
                    if (!slots.TryTake(id)) { return VideoStep.None; }
                    Phase = VideoPhase.Decoding;
                    restart = false;
                    return VideoStep.Load;

                case VideoPhase.Decoding:
                    if (!(Want && f.Active))
                    {
                        slots.Give(id);
                        Phase = VideoPhase.Idle;
                        return VideoStep.Unload;
                    }
                    if (restart)
                    {
                        restart = false;
                        return VideoStep.Restart;
                    }
                    if (f.HasFrame && !f.Playing)
                    {
                        if (Loop) { return VideoStep.Restart; }
                        Phase = VideoPhase.Ended;
                        Want = false;
                        return VideoStep.Ended;
                    }
                    if (!f.HasFrame && now - LoadedAt > StartTimeout)
                    {
                        slots.Give(id);
                        Phase = VideoPhase.Failed;
                        return VideoStep.Error;
                    }
                    return VideoStep.None;

                case VideoPhase.Ended:
                    if (!f.Active)
                    {
                        slots.Give(id);
                        Phase = VideoPhase.Idle;
                        return VideoStep.Unload;
                    }
                    if (Want || restart)
                    {
                        restart = false;
                        Phase = VideoPhase.Decoding;
                        return VideoStep.Restart;
                    }
                    return VideoStep.None;
            }
            return VideoStep.None;
        }
    }
}
