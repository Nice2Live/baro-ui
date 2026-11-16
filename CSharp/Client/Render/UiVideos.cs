using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI.Render
{
    /// <summary>What a command does to a video (Play, Pause, Stop is Pause, Restart), from the markup (play(id)) or the C# handle (BaroUiVideo).</summary>
    internal enum VideoCmd { Play, Pause, Restart }

    /// <summary>The &lt;video&gt; elements of a window (docs/ui/AUTHORING.md, «Видео»). Plays WebM with the game's own decoder (Barotrauma.Media.Video, the one the
    /// splash screens and BBE's menu videos use; the fork's file is not changed). Per frame (Frame, from the window's Update, before the draw): the
    /// lifecycle (Core/VideoLifecycle.cs) says load, unload, restart, ended or error; the decoder's frame is kept as the element's texture, counted in the
    /// window's texture budget (TextureLedger) and let go through the graveyard (a texture the batch may still hold is freed a moment later).
    /// Painted (Paint, from the painter) marks an element as on screen: a video that was not painted in the last frame is not decoded. Unloaded decoders
    /// are not kept: a video that comes back to the screen plays from the beginning. Threading: Load, GetTexture and Dispose on the game's main thread
    /// (here); the decoder's own thread only copies frames into the video under its lock.</summary>
    internal sealed class UiVideos : ITextureOwner, IDisposable
    {
        /// <summary>Decoders a window runs at once (Core VideoSlots). A video beyond them waits, and the problems list says so once.</summary>
        public const int MaxDecoding = VideoSlots.DefaultMax;
        /// <summary>A let-go decoder is disposed this long after (its texture may still be in a batch that is ending).</summary>
        private const double GraveDelay = 0.75;

        private sealed class Entry
        {
            public Element El;
            public int Id;
            public string Src, PageDir, Full, Problem, VolText, Poster;
            public bool MutedNow, LoopNow;
            public VideoSpec Spec;
            public VideoLifecycle Life;
            public Barotrauma.Media.Video Player;
            /// <summary>The frame on screen (null: the poster, or the placeholder, or nothing yet).</summary>
            public Texture2D Shown;
            public int Token = -1;
            public long Painted = -1000;
            public float GainSet = -1f;
            public bool WaitReported;
        }

        /// <summary>Decoders let go, disposed when GraveDelay has passed (static: the graveyard of UiImages works the same way).</summary>
        private static readonly List<(Barotrauma.Media.Video V, double At)> Graveyard = new();
        /// <summary>Header reads, by file: the size and duration of a WebM, read once while the file is the same.</summary>
        private static readonly Dictionary<string, (DateTime Stamp, long Length, WebmHeader.Info Info)> Headers = new(StringComparer.OrdinalIgnoreCase);

        private readonly UiWindow window;
        private readonly TextureLedger budget;
        private readonly Func<string> pageDir;
        private readonly VideoSlots slots = new(MaxDecoding);
        private readonly List<Entry> list = new();
        private readonly Dictionary<Element, Entry> byElement = new();
        private readonly Dictionary<int, Entry> byToken = new();
        private readonly List<Diagnostic> problems = new();
        private readonly HashSet<string> seen = new();
        private int nextId = 1;
        private long frame;

        public UiVideos(UiWindow w, TextureLedger textureBudget, Func<string> pageFolder)
        {
            window = w;
            budget = textureBudget;
            pageDir = pageFolder;
        }

        /// <summary>The problems of the videos (a missing file, a format that is not WebM, a decoder that gives no frame, the decoder limit), one line each,
        /// for the window's problems list (bbe_ui problems).</summary>
        internal List<Diagnostic> Problems => problems;

        /// <summary>The number of video elements the window keeps (decoding or not).</summary>
        public int Count => list.Count;

        /// <summary>Called by the painter for every video it paints: marks it on screen this frame and returns its frame texture (null: show the poster or
        /// the placeholder). failed: the file cannot be played (the placeholder).</summary>
        internal Texture2D Paint(Element e, string folder, out bool failed)
        {
            Entry en = Ensure(e, folder);
            en.Painted = frame;
            failed = en.Problem != null;
            return en.Shown;
        }

        /// <summary>The natural size of a video: the pixels of its file's video track (WebM header, no decoding). False: no playable file.</summary>
        internal static bool NaturalSize(Element e, string folder, out int width, out int height)
        {
            width = height = 0;
            if (!VideoSource.Check(folder, e.GetAttribute("src"), File.Exists, out string full, out _)) { return false; }
            var info = HeaderOf(full);
            if (info.Problem != null) { return false; }
            width = info.Width;
            height = info.Height;
            return true;
        }

        /// <summary>The header of a WebM file (cached while the file is the same). Problem is set when it cannot be read or is not a WebM.</summary>
        internal static WebmHeader.Info HeaderOf(string full)
        {
            try
            {
                var fi = new FileInfo(full);
                if (Headers.TryGetValue(full, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Length == fi.Length) { return c.Info; }
                byte[] buffer = new byte[WebmHeader.ReadBytes];
                int n = 0;
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    while (n < buffer.Length)
                    {
                        int got = stream.Read(buffer, n, buffer.Length - n);
                        if (got <= 0) { break; }
                        n += got;
                    }
                }
                var info = WebmHeader.Read(buffer, n);
                Headers[full] = (fi.LastWriteTimeUtc, fi.Length, info);
                return info;
            }
            catch (Exception ex)
            {
                return new WebmHeader.Info(0, 0, null, false, false, "cannot read the file: " + ex.Message);
            }
        }

        /// <summary>Play, Pause (Stop too), Restart of the video of an element. Creates its entry when the painter has not seen it yet.</summary>
        internal void Command(Element e, VideoCmd cmd)
        {
            if (e == null || e.Tag != "video") { return; }
            Entry en = Ensure(e, pageDir());
            switch (cmd)
            {
                case VideoCmd.Play: en.Life.Play(); break;
                case VideoCmd.Pause: en.Life.Pause(); break;
                case VideoCmd.Restart: en.Life.Restart(); break;
            }
        }

        /// <summary>True while the video's decoder runs (not paused, not ended, the frame is moving).</summary>
        internal bool IsPlaying(Element e)
        {
            if (e == null || !byElement.TryGetValue(e, out var en)) { return false; }
            return en.Life.Phase == VideoPhase.Decoding && en.Player != null && en.Player.IsPlaying;
        }

        /// <summary>The length of the video from its header (no decoding); null when the file has none or cannot be read.</summary>
        internal TimeSpan? DurationOf(Element e)
        {
            if (e == null || !VideoSource.Check(pageDir(), e.GetAttribute("src"), File.Exists, out string full, out _)) { return null; }
            return HeaderOf(full).Duration;
        }

        /// <summary>Once per window frame, from the window's Update (before the input and the draw): the lifecycles of the videos step, the decoders are
        /// loaded, restarted or let go, the frames are taken into the textures, the events run. now is the clock in seconds.</summary>
        internal void Frame(double now)
        {
            frame++;
            Sweep(now);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Entry en = list[i];
                if (!window.Attached(en.El))
                {
                    Remove(i);
                    continue;
                }
                string folder = pageDir();
                if (!string.Equals(en.Src, en.El.GetAttribute("src"), StringComparison.Ordinal) || !string.Equals(en.PageDir, folder, StringComparison.Ordinal))
                {
                    Resolve(en, folder);   // the source changed (a template or a script): the decoder goes, the new file is looked up
                }
                else if (!SameAttributes(en))
                {
                    Respec(en);
                }
                bool active = frame - en.Painted <= 1 && en.Full != null && en.Problem == null;
                bool playing = en.Player != null && en.Player.IsPlaying;
                bool hasFrame = en.Player != null && en.Player.FirstFrameTime != null;
                VideoStep step = en.Life.Update(new VideoFacts(active, playing, hasFrame), now);
                switch (step)
                {
                    case VideoStep.Load:
                        Open(en, now);
                        break;
                    case VideoStep.Restart:
                        CloseDecoder(en);
                        Open(en, now);
                        break;
                    case VideoStep.Unload:
                        CloseDecoder(en);
                        break;
                    case VideoStep.Ended:
                        window.Fire(en.El, "onended");
                        break;
                    case VideoStep.Error:
                        CloseDecoder(en);
                        Fail(en, "the clip gives no frame (the file is damaged or the decoder cannot play it)");
                        window.Fire(en.El, "onerror");
                        break;
                }
                if (en.Life.Phase == VideoPhase.Waiting && !en.WaitReported)
                {
                    en.WaitReported = true;
                    Problem(en, $"at most {MaxDecoding} videos decode at once in a window, this one waits for a free decoder");
                }
                else if (en.Life.Phase != VideoPhase.Waiting) { en.WaitReported = false; }
                if (en.Life.Loaded && en.Player != null)
                {
                    Texture2D tex = en.Player.GetTexture();   // the main thread: the decoder's frame is copied to the texture here
                    if (hasFrame && tex != null) { ShowFrame(en, tex); }
                    else { HideFrame(en); }
                    float gain = en.Spec.Gain;
                    if (gain != en.GainSet) { en.Player.AudioGain = gain; en.GainSet = gain; }
                }
                else { HideFrame(en); }
                if (en.Token >= 0) { budget.Touch(en.Token); }
            }
        }

        /// <summary>The window's Close: every decoder is let go, nothing decodes after it.</summary>
        public void Dispose()
        {
            for (int i = list.Count - 1; i >= 0; i--) { Remove(i); }
            list.Clear();
            byElement.Clear();
            byToken.Clear();
        }

        /// <summary>The budget takes an idle video's texture back (TextureLedger.Trim): its decoder is let go; the video starts again from the beginning
        /// when it is shown again. The ledger has forgotten the token already, so there is no Remove for it.</summary>
        void ITextureOwner.Release(int token)
        {
            if (!byToken.Remove(token, out var en)) { return; }
            en.Token = -1;
            en.Life.Reset();
            CloseDecoder(en);
        }

        // ---------------------------------------------------------------- entries

        private Entry Ensure(Element e, string folder)
        {
            if (byElement.TryGetValue(e, out var en)) { return en; }
            en = new Entry { El = e, Id = nextId++ };
            Resolve(en, folder);
            byElement[e] = en;
            list.Add(en);
            return en;
        }

        /// <summary>The file, the attributes and the lifecycle of an entry, from the element now. A new lifecycle: the old one's slot is given back first.</summary>
        private void Resolve(Entry en, string folder)
        {
            en.Life?.Reset();
            CloseDecoder(en);
            en.Src = en.El.GetAttribute("src");
            en.PageDir = folder;
            en.Poster = en.El.GetAttribute("poster");
            en.Spec = VideoSpec.Of(en.El);
            en.VolText = en.El.GetAttribute("volume");
            en.MutedNow = en.El.GetAttribute("muted") != null;
            en.LoopNow = en.El.GetAttribute("loop") != null;
            foreach (string p in en.Spec.Problems) { Problem(en, p); }
            if (!VideoSource.Check(folder, en.Src, File.Exists, out en.Full, out string why))
            {
                en.Full = null;
                Fail(en, why);
            }
            else { en.Problem = null; }
            en.Life = new VideoLifecycle(en.Id, slots, en.Spec.Autoplay, en.Spec.Loop);
        }

        /// <summary>The volume, muted or loop attribute changed (a template): the spec is read again, the decoder keeps running.</summary>
        private void Respec(Entry en)
        {
            en.Spec = VideoSpec.Of(en.El);
            en.VolText = en.El.GetAttribute("volume");
            en.MutedNow = en.El.GetAttribute("muted") != null;
            en.LoopNow = en.El.GetAttribute("loop") != null;
            en.Life.Loop = en.Spec.Loop;
            foreach (string p in en.Spec.Problems) { Problem(en, p); }
        }

        private bool SameAttributes(Entry en) =>
            string.Equals(en.VolText, en.El.GetAttribute("volume"), StringComparison.Ordinal)
            && en.MutedNow == (en.El.GetAttribute("muted") != null) && en.LoopNow == (en.El.GetAttribute("loop") != null);

        private void Remove(int index)
        {
            Entry en = list[index];
            list.RemoveAt(index);
            byElement.Remove(en.El);
            en.Life?.Reset();
            CloseDecoder(en);
        }

        // ---------------------------------------------------------------- decoder and texture

        private void Open(Entry en, double now)
        {
            Barotrauma.Media.Video player = null;
            string error = null;
            try
            {
                player = Barotrauma.Media.Video.Load(GameMain.Instance.GraphicsDevice, GameMain.SoundManager, en.Full);
            }
            catch (Exception ex) { error = ex.Message; }
            if (player == null)
            {
                en.Life.LoadResult(false, now);
                Fail(en, error != null ? "cannot play the file: " + error : "the game's WebM decoder cannot open the file (damaged, or not VP8/VP9 with Vorbis/Opus)");
                window.Fire(en.El, "onerror");
                return;
            }
            en.Player = player;
            en.GainSet = en.Spec.Gain;
            player.AudioGain = en.GainSet;
            en.Life.LoadResult(true, now);
        }

        /// <summary>Lets the decoder go (its texture through the graveyard, its budget entry gone). The lifecycle is not told: the caller says what happened.</summary>
        private void CloseDecoder(Entry en)
        {
            if (en.Token >= 0)
            {
                budget.Remove(en.Token);
                byToken.Remove(en.Token);
                en.Token = -1;
            }
            if (en.Player != null)
            {
                Graveyard.Add((en.Player, Timing.TotalTime));
                en.Player = null;
            }
            en.Shown = null;
            en.GainSet = -1f;
        }

        /// <summary>The frame is on screen: the element's texture is the decoder's, counted in the window's budget (the size is the video's, fixed).</summary>
        private void ShowFrame(Entry en, Texture2D tex)
        {
            en.Shown = tex;
            if (en.Token < 0 && en.Player != null)
            {
                en.Token = budget.Add(this, (long)en.Player.Width * en.Player.Height * 4);
                byToken[en.Token] = en;
                budget.Trim();
            }
        }

        private void HideFrame(Entry en)
        {
            en.Shown = null;
            if (en.Token >= 0)
            {
                budget.Remove(en.Token);
                byToken.Remove(en.Token);
                en.Token = -1;
            }
        }

        /// <summary>Disposes the decoders that were let go a moment ago (their textures are no longer in any batch).</summary>
        private static void Sweep(double now)
        {
            for (int i = Graveyard.Count - 1; i >= 0; i--)
            {
                if (now - Graveyard[i].At < GraveDelay) { continue; }
                try { Graveyard[i].V.Dispose(); } catch (Exception) { }
                Graveyard.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- problems

        private void Fail(Entry en, string text)
        {
            en.Problem = text;
            Problem(en, text);
        }

        /// <summary>One line in the window's problems: each text of each video is added once (the window's list is read by bbe_ui problems).</summary>
        private void Problem(Entry en, string text)
        {
            string line = $"<video src=\"{en.Src}\">: {text}";
            if (seen.Count > 512 || !seen.Add(line)) { return; }
            problems.Add(new Diagnostic(window.Document.File, en.El.Line, line));
        }
    }
}
