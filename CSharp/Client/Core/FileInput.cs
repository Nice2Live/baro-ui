using System;
using System.Collections.Generic;
using System.IO;

namespace BaroUI.Core
{
    /// <summary>The parts of file input that need no game (UiWindow.SetFileInput): which text from the clipboard names a file, which files a target takes.</summary>
    internal static class FileInput
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga" };

        public static bool IsImage(string path)
        {
            string ext = Extension(path);
            return Array.IndexOf(ImageExtensions, ext) >= 0;
        }

        /// <summary>The extension with its dot in lower case (".png"); empty when there is none.</summary>
        public static string Extension(string path)
        {
            if (string.IsNullOrEmpty(path)) { return ""; }
            int dot = path.LastIndexOf('.');
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return dot > slash && dot >= 0 ? path.Substring(dot).ToLowerInvariant() : "";
        }

        /// <summary>Whether a target with this accept list takes the file: a comma separated list of extensions (".png", "png"), "image/*" for
        /// the picture formats, "*" or nothing for any file. A pasted picture without a file has the path ".png".</summary>
        public static bool Accepts(string accept, string path)
        {
            if (string.IsNullOrWhiteSpace(accept)) { return true; }
            string ext = Extension(path);
            foreach (string raw in accept.Split(','))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0) { continue; }
                if (t == "*" || t == "*/*") { return true; }
                if (t == "image/*") { if (IsImage(path)) { return true; } continue; }
                if (t[0] != '.') { t = "." + t; }
                if (t == ext) { return true; }
            }
            return false;
        }

        /// <summary>The files a text from the clipboard names: one path to a line, with or without quotes (Explorer's "Copy as path" quotes it) or a
        /// file: address. Only paths that exist (exists) are returned, in order.</summary>
        public static List<string> PathsFromText(string text, Func<string, bool> exists)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) { return result; }
            foreach (string raw in text.Split('\n', '\r'))
            {
                string s = raw.Trim().Trim('"', '\'').Trim();
                if (s.Length == 0 || s.Length > 4096) { continue; }
                if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(5).TrimStart('/');
                    try { s = Uri.UnescapeDataString(s); } catch (UriFormatException) { continue; }
                }
                if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.IndexOfAny(new[] { '*', '?', '<', '>', '|' }) >= 0) { continue; }
                string full;
                try { full = Path.GetFullPath(s); } catch (Exception) { continue; }
                if (!Path.IsPathRooted(s) || !exists(full)) { continue; }
                if (!result.Contains(full)) { result.Add(full); }
            }
            return result;
        }

        /// <summary>The file a BMP wrapper needs: the bytes of a clipboard DIB (a BITMAPINFOHEADER and what follows it) as the content of a .bmp file,
        /// which the game's picture loader reads. Null when the DIB is not one this can wrap.</summary>
        public static byte[] DibToBmp(byte[] dib)
        {
            if (dib == null || dib.Length < 40) { return null; }
            int headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length) { return null; }
            int bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int colorsUsed = BitConverter.ToInt32(dib, 32);
            int table = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bitCount) * 4 : colorsUsed * 4;
            if (compression == 3 && headerSize == 40) { table += 12; }   // BI_BITFIELDS: three colour masks follow a plain header
            long offBits = 14L + headerSize + table;
            if (offBits > 14L + dib.Length) { return null; }
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes((int)offBits).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
            return bmp;
        }
    }
}
