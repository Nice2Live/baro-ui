using System;

namespace BaroUI.Core
{
    /// <summary>The header of a WebM file (EBML), read without decoding: the size of the video track (the layout's natural size of a &lt;video&gt;), the
    /// Duration of the Segment Info and whether there is a video and an audio track. Reads the first ReadBytes of the file (the header comes before the
    /// first Cluster). Pure: no game types, no exceptions; a file that is not WebM gives Problem.</summary>
    internal static class WebmHeader
    {
        /// <summary>How much of the file the header is read from.</summary>
        public const int ReadBytes = 64 * 1024;

        public readonly record struct Info(int Width, int Height, TimeSpan? Duration, bool HasVideo, bool HasAudio, string Problem);

        private const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdInfo = 0x1549A966, IdTimecodeScale = 0x2AD7B1, IdDuration = 0x4489,
            IdTracks = 0x1654AE6B, IdTrackEntry = 0xAE, IdTrackType = 0x83, IdVideo = 0xE0, IdPixelWidth = 0xB0, IdPixelHeight = 0xBA, IdCluster = 0x1F43B675;

        private sealed class State
        {
            public double Duration = -1;
            public ulong TimecodeScale = 1000000;   // ns per tick, the default
            public int Width, Height;
            public bool HasVideo, HasAudio, Done;
        }

        /// <summary>Reads the header from data[0..length). Problem is set when there is no EBML header or no video track was found.</summary>
        public static Info Read(byte[] data, int length)
        {
            if (data == null || length < 4 || data[0] != 0x1A || data[1] != 0x45 || data[2] != 0xDF || data[3] != 0xA3)
            {
                return new Info(0, 0, null, false, false, "not a WebM file (no EBML header)");
            }
            var st = new State();
            Walk(data, 0, length, st);
            TimeSpan? duration = null;
            if (st.Duration > 0 && st.TimecodeScale > 0)
            {
                double seconds = st.Duration * st.TimecodeScale / 1e9;
                if (seconds > 0 && seconds < 360000) { duration = TimeSpan.FromSeconds(seconds); }
            }
            string problem = st.HasVideo && st.Width > 0 && st.Height > 0 ? null : "no video track in the header";
            return new Info(st.Width, st.Height, duration, st.HasVideo, st.HasAudio, problem);
        }

        /// <summary>Walks the elements of [pos, end): the containers that hold what is wanted are entered, the others skipped by their size; the first
        /// Cluster ends the header.</summary>
        private static void Walk(byte[] d, int pos, int end, State st)
        {
            while (pos < end && !st.Done)
            {
                if (!ReadId(d, pos, end, out uint id, out int idLen)) { return; }
                pos += idLen;
                if (!ReadSize(d, pos, end, out ulong size, out int sizeLen, out bool unknown)) { return; }
                pos += sizeLen;
                int bodyEnd = unknown || size > (ulong)(end - pos) ? end : pos + (int)size;
                switch (id)
                {
                    case IdCluster:
                        st.Done = true;
                        return;
                    case IdSegment:
                    case IdInfo:
                    case IdTracks:
                    case IdTrackEntry:
                        Walk(d, pos, bodyEnd, st);
                        break;
                    case IdVideo:
                        st.HasVideo = true;   // the Video master of a track: its PixelWidth and PixelHeight follow
                        Walk(d, pos, bodyEnd, st);
                        break;
                    case IdTimecodeScale:
                        if (!unknown && bodyEnd - pos == (int)size && UInt(d, pos, bodyEnd, out ulong scale) && scale > 0) { st.TimecodeScale = scale; }
                        break;
                    case IdDuration:
                        if (!unknown && bodyEnd - pos == (int)size && Float(d, pos, bodyEnd, out double value)) { st.Duration = value; }
                        break;
                    case IdTrackType:
                        if (UInt(d, pos, bodyEnd, out ulong type)) { if (type == 1) { st.HasVideo = true; } else if (type == 2) { st.HasAudio = true; } }
                        break;
                    case IdPixelWidth:
                        if (UInt(d, pos, bodyEnd, out ulong w) && st.Width == 0) { st.Width = (int)Math.Min(w, int.MaxValue); }
                        break;
                    case IdPixelHeight:
                        if (UInt(d, pos, bodyEnd, out ulong h) && st.Height == 0) { st.Height = (int)Math.Min(h, int.MaxValue); }
                        break;
                }
                pos = bodyEnd;
            }
        }

        /// <summary>An element ID: a vint of 1 to 4 bytes, the length marker kept.</summary>
        private static bool ReadId(byte[] d, int pos, int end, out uint id, out int len)
        {
            id = 0; len = 0;
            if (pos >= end) { return false; }
            int first = d[pos];
            if (first == 0) { return false; }
            len = 1;
            while ((first & (0x80 >> (len - 1))) == 0) { len++; }
            if (len > 4 || pos + len > end) { return false; }
            uint v = 0;
            for (int i = 0; i < len; i++) { v = (v << 8) | d[pos + i]; }
            id = v;
            return true;
        }

        /// <summary>An element size: a vint of 1 to 8 bytes, the marker removed; all bits set is an unknown size.</summary>
        private static bool ReadSize(byte[] d, int pos, int end, out ulong size, out int len, out bool unknown)
        {
            size = 0; len = 0; unknown = false;
            if (pos >= end) { return false; }
            int first = d[pos];
            if (first == 0) { return false; }
            len = 1;
            while ((first & (0x80 >> (len - 1))) == 0) { len++; }
            if (pos + len > end) { return false; }
            ulong v = (ulong)(first & (0xFF >> len));
            for (int i = 1; i < len; i++) { v = (v << 8) | d[pos + i]; }
            size = v;
            unknown = v == (1UL << (7 * len)) - 1;
            return true;
        }

        private static bool UInt(byte[] d, int pos, int end, out ulong value)
        {
            value = 0;
            int n = end - pos;
            if (n < 1 || n > 8) { return false; }
            for (int i = 0; i < n; i++) { value = (value << 8) | d[pos + i]; }
            return true;
        }

        /// <summary>A big-endian IEEE float of 4 or 8 bytes.</summary>
        private static bool Float(byte[] d, int pos, int end, out double value)
        {
            value = 0;
            int n = end - pos;
            if (n == 4)
            {
                uint bits = 0;
                for (int i = 0; i < 4; i++) { bits = (bits << 8) | d[pos + i]; }
                value = BitConverter.Int32BitsToSingle((int)bits);
            }
            else if (n == 8)
            {
                ulong bits = 0;
                for (int i = 0; i < 8; i++) { bits = (bits << 8) | d[pos + i]; }
                value = BitConverter.Int64BitsToDouble((long)bits);
            }
            else { return false; }
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
