using System;
using System.Collections.Generic;
using System.IO;

namespace BaroUI.Core
{
    /// <summary>The metrics of a font file that a page or a mod brings: ascent and descent in em from the file's own tables (hhea, else OS/2 win), so the
    /// line box is the file's, as the built-in families' numbers are (FontFamilies.cs). Reads the table directory and a few bytes of head, hhea and
    /// OS/2; no library. TrueType and OpenType with CFF are read; collections (TTC), WOFF and WOFF2 are refused with a reason.</summary>
    internal static class FontFile
    {
        private const uint TagHead = 0x68656164, TagHhea = 0x68686561, TagOs2 = 0x4F532F32;

        /// <summary>The metrics of a file on disk. A file that cannot be opened or read gives a problem text, never an exception.</summary>
        public static bool TryReadMetrics(string path, out double ascent, out double descent, out string problem)
        {
            ascent = descent = 0;
            try
            {
                using var fs = File.OpenRead(path);
                return TryReadMetrics(fs, out ascent, out descent, out problem);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problem = $"not readable ({e.Message})";
                return false;
            }
        }

        /// <summary>The metrics of a font in a seekable stream. False with the problem: not a font, a table cut short, or numbers that are not plausible.</summary>
        public static bool TryReadMetrics(Stream s, out double ascent, out double descent, out string problem)
        {
            ascent = descent = 0;
            problem = null;
            if (s == null || !s.CanSeek) { problem = "the file cannot be read"; return false; }
            byte[] head = Read(s, 0, 12);
            if (head == null) { problem = "not a font file (too short)"; return false; }
            uint magic = U32(head, 0);
            if (magic == 0x74746366) { problem = "a font collection (TTC) is not supported, a single TTF or OTF is"; return false; }
            if (magic == 0x774F4646 || magic == 0x774F4632) { problem = "WOFF is not supported, a TTF or OTF is"; return false; }
            if (magic != 0x00010000 && magic != 0x74727565 && magic != 0x4F54544F) { problem = "not a TrueType or OpenType font"; return false; }
            int count = U16(head, 4);
            if (count < 1 || count > 1000) { problem = "the table count is not plausible"; return false; }
            byte[] dir = Read(s, 12, 16L * count);
            if (dir == null) { problem = "the table directory is cut short"; return false; }
            long headAt = -1, hheaAt = -1, os2At = -1;
            long os2Len = 0;
            for (int i = 0; i < count; i++)
            {
                int at = 16 * i;
                uint tag = U32(dir, at);
                long offset = U32(dir, at + 8), length = U32(dir, at + 12);
                if (tag == TagHead) { headAt = offset; }
                else if (tag == TagHhea) { hheaAt = offset; }
                else if (tag == TagOs2) { os2At = offset; os2Len = length; }
            }
            if (headAt < 0 || hheaAt < 0) { problem = "no head or hhea table"; return false; }
            byte[] h = Read(s, headAt, 20);
            if (h == null) { problem = "the head table is cut short"; return false; }
            int upm = U16(h, 18);
            if (upm < 16 || upm > 16384) { problem = $"unitsPerEm {upm} is not plausible"; return false; }
            byte[] hh = Read(s, hheaAt, 8);
            if (hh == null) { problem = "the hhea table is cut short"; return false; }
            double asc = S16(hh, 4), desc = S16(hh, 6);
            if (asc == 0 && desc == 0 && os2At >= 0 && os2Len >= 78)
            {
                byte[] o = Read(s, os2At, 78);
                if (o != null) { asc = U16(o, 74); desc = -U16(o, 76); }
            }
            double a = asc / upm, d = -desc / upm;
            if (!(a > 0) || a > 5 || d < 0 || d > 5) { problem = "its ascent and descent are not plausible"; return false; }
            ascent = a;
            descent = d;
            return true;
        }

        private static byte[] Read(Stream s, long at, long n)
        {
            if (at < 0 || n < 0 || at + n > s.Length) { return null; }
            var b = new byte[n];
            s.Seek(at, SeekOrigin.Begin);
            int got = 0;
            while (got < n)
            {
                int k = s.Read(b, got, (int)(n - got));
                if (k <= 0) { return null; }
                got += k;
            }
            return b;
        }

        private static ushort U16(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
        private static short S16(byte[] b, int i) => (short)U16(b, i);
        private static uint U32(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
    }

    /// <summary>The font families one window sees besides the table: the @font-face rules of its sheets (StyleEngine reads them with their files)
    /// and the faces BaroUi.RegisterFont registered before the window opened. A family is in the scope only with a readable face (FontFile), so a family
    /// whose files are missing is not there and the font-family list goes on to the next name, then the default. A scope belongs to one StyleEngine:
    /// its faces are released with the window (UiFonts.Release). A style computed with a scope is never shared between engines (StyleEngine.Adopt).</summary>
    internal sealed class FontScope
    {
        /// <summary>One BaroUi.RegisterFont call: the family as written, the absolute file, the weight and the style.</summary>
        public sealed class Registered
        {
            public string Family;
            public string File;
            public int Weight;
            public bool Italic;
        }

        private static readonly List<Registered> registered = new();
        private static readonly object registerLock = new();

        private sealed class Group
        {
            public string Key;
            public string Name;
            public double Ascent, Descent;
            public readonly List<FontFace> Faces = new();
        }

        private readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FontFamily> byName = new(StringComparer.Ordinal);
        private readonly List<FontFace> faces = new();

        /// <summary>The families by their lower case name, as FontFamilies.FindIn looks them up.</summary>
        public IReadOnlyDictionary<string, FontFamily> ByName => byName;
        /// <summary>Every face this scope holds (their files are read): released with the window.</summary>
        public IReadOnlyList<FontFace> Faces => faces;

        /// <summary>The key of a family name (the name as written, quotes already gone): lower case, inner blanks collapsed, the same key FontFamilies.SplitList
        /// gives a quoted name. Null for an empty name.</summary>
        public static string KeyOf(string family)
        {
            string k = string.Join(" ", (family ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            return k.Length > 0 ? k : null;
        }

        /// <summary>Adds a face of a family: the file is read now. Null when it is in the scope, else why it cannot be used (the caller says so once).</summary>
        public string Add(string family, string fullPath, int weight, bool italic)
        {
            string key = KeyOf(family);
            if (key == null) { return $"font-family '{family}' must be one name"; }
            if (!FontFile.TryReadMetrics(fullPath, out double asc, out double desc, out string problem)) { return problem; }
            var face = new FontFace(Math.Clamp(weight, 1, 1000), italic, fullPath) { Local = true, Ascent = asc, Descent = desc };
            if (!groups.TryGetValue(key, out var g))
            {
                g = new Group { Key = key, Name = family.Trim(), Ascent = asc, Descent = desc };
                groups[key] = g;
            }
            g.Faces.Add(face);
            faces.Add(face);
            Seal();
            return null;
        }

        private void Seal()
        {
            byName.Clear();
            foreach (var g in groups.Values)
            {
                byName[g.Key] = new FontFamily(g.Name, g.Ascent, g.Descent, new[] { g.Key }, g.Faces.ToArray());
            }
        }

        /// <summary>BaroUi.RegisterFont: for the windows opened afterwards. Throws on a family that is not one name, or a file that is not an absolute
        /// path (the file itself is read when a window opens: a missing one is one problem there). A call with the same family, weight and style
        /// replaces the earlier one.</summary>
        public static void Register(string family, string file, int weight = 400, bool italic = false)
        {
            string key = KeyOf(family);
            if (key == null || family.Contains(',')) { throw new ArgumentException($"font family '{family}' must be one name (no commas)", nameof(family)); }
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
            {
                throw new ArgumentException("the font file must be an absolute path (a file of the mod's own package)", nameof(file));
            }
            int w = Math.Clamp(weight, 1, 1000);
            lock (registerLock)
            {
                registered.RemoveAll(r => KeyOf(r.Family) == key && r.Weight == w && r.Italic == italic);
                registered.Add(new Registered { Family = family.Trim(), File = file, Weight = w, Italic = italic });
            }
        }

        /// <summary>The registered faces now (a snapshot: a window takes it when it opens).</summary>
        public static IReadOnlyList<Registered> RegisteredFonts()
        {
            lock (registerLock) { return registered.ToArray(); }
        }

        /// <summary>Tests only: forgets every registration.</summary>
        internal static void ClearRegistered()
        {
            lock (registerLock) { registered.Clear(); }
        }
    }
}
