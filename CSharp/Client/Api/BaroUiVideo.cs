using System;
using System.Globalization;
using BaroUI.Core;
using BaroUI.Render;

namespace BaroUI
{
    /// <summary>A &lt;video&gt; of a window by its id (UiWindow.Video(id)): Play, Pause, Stop (a pause), Restart, Volume, IsPlaying, Duration (from the file's
    /// header, before it plays). Nothing happens for an id that is not a video (Found is false). The same as the markup's play(id), pause(id), stop(id),
    /// restart(id) and the element's volume attribute. Pause and Stop unload the decoder: a resumed clip starts again from the beginning.
    /// The handle keeps no state: make it where you need it.</summary>
    public sealed class BaroUiVideo
    {
        private readonly UiWindow window;

        internal BaroUiVideo(UiWindow w, string id)
        {
            window = w;
            Id = id;
        }

        public string Id { get; }

        /// <summary>The id is a video element of the window now.</summary>
        public bool Found => window.Find(Id) is { Tag: "video" };

        public void Play() => window.Videos.Command(window.Find(Id), VideoCmd.Play);
        public void Pause() => window.Videos.Command(window.Find(Id), VideoCmd.Pause);
        public void Stop() => window.Videos.Command(window.Find(Id), VideoCmd.Pause);
        public void Restart() => window.Videos.Command(window.Find(Id), VideoCmd.Restart);

        /// <summary>The volume attribute, 0..1 (set: clamped, written as the element's volume attribute, which the audio follows at the next frame).</summary>
        public float Volume
        {
            get
            {
                var e = window.Find(Id);
                if (e == null || e.Tag != "video") { return 0f; }
                string text = e.GetAttribute("volume");
                return text == null ? 1f : VideoSpec.ParseVolume(text, null);
            }
            set
            {
                var e = window.Find(Id);
                if (e == null || e.Tag != "video") { return; }
                float v = float.IsNaN(value) ? 1f : Math.Clamp(value, 0f, 1f);
                e.SetAttribute("volume", v.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>True while the decoder runs (not paused, not ended).</summary>
        public bool IsPlaying => window.Videos.IsPlaying(window.Find(Id));

        /// <summary>The length of the clip from its file's header; null when the file has none or cannot be read.</summary>
        public TimeSpan? Duration => window.Videos.DurationOf(window.Find(Id));
    }
}
