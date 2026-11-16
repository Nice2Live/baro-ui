using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>The attributes of a &lt;video&gt; element (docs/ui/AUTHORING.md, «Видео»): src (a WebM file of the package, a path from the page's folder, as an img's
    /// src), poster (a picture file, shown until the first frame and when the video cannot be played), autoplay, loop, muted and volume (0..1, default 1).
    /// Pure: no game types. Problems are one line each (the element still lays out; the Render side shows the placeholder and says why once).</summary>
    internal sealed class VideoSpec
    {
        public string Src { get; private set; }
        public string Poster { get; private set; }
        public bool Autoplay { get; private set; }
        public bool Loop { get; private set; }
        public bool Muted { get; private set; }
        /// <summary>The volume attribute, 0..1 (a value outside the range is clamped, an unreadable one is 1).</summary>
        public float Volume { get; private set; } = 1f;
        /// <summary>What is wrong with the attributes, one line each (empty when they are fine). The src path is checked by VideoSource.Check.</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>The gain the game's audio gets: 0 when muted, else the volume.</summary>
        public float Gain => Muted ? 0f : Volume;

        public static VideoSpec Of(Element e)
        {
            var v = new VideoSpec();
            v.Src = e.GetAttribute("src");
            v.Poster = e.GetAttribute("poster");
            v.Autoplay = e.GetAttribute("autoplay") != null;
            v.Loop = e.GetAttribute("loop") != null;
            v.Muted = e.GetAttribute("muted") != null;
            string volume = e.GetAttribute("volume");
            if (volume != null) { v.Volume = ParseVolume(volume, v.Problems); }
            return v;
        }

        /// <summary>A volume text: a number 0..1 (the invariant culture: 0.5, not 0,5). Out of range: clamped, said. Not a number: 1, said.</summary>
        public static float ParseVolume(string text, List<string> problems)
        {
            string t = text.Trim();
            if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || float.IsNaN(v))
            {
                problems?.Add($"volume \"{text}\": not a number 0..1, the volume is 1");
                return 1f;
            }
            if (v < 0f || v > 1f)
            {
                problems?.Add($"volume {t}: the range is 0..1, it is clamped");
                return Math.Clamp(v, 0f, 1f);
            }
            return v;
        }
    }

    /// <summary>The file of a &lt;video src&gt;: WebM only (the game's decoder plays WebM), a path inside the package like a picture's
    /// (ImageCss.ResolvePath: relative to the page, no URLs, no absolute paths), and the file must exist.</summary>
    internal static class VideoSource
    {
        public const string Extension = ".webm";

        /// <summary>True: full is the file to play. False: problem says why (no src, not WebM, outside the package, not found).</summary>
        public static bool Check(string pageDir, string src, Func<string, bool> fileExists, out string full, out string problem)
        {
            full = null; problem = null;
            if (string.IsNullOrWhiteSpace(src)) { problem = "no src: a video needs a WebM file"; return false; }
            string path = src;
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) { path = path.Substring(0, cut); }
            if (!path.Trim().EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"'{src.Trim()}': only WebM ({Extension}) is played";
                return false;
            }
            if (!ImageCss.ResolvePath(pageDir, src, fileExists, out string resolved, out string why))
            {
                problem = "video " + why;
                return false;
            }
            if (!fileExists(resolved))
            {
                problem = $"'{src.Trim()}': file not found";
                return false;
            }
            full = resolved;
            return true;
        }
    }
}
