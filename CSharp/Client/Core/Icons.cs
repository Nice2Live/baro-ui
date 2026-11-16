using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BaroUI.Core
{
    /// <summary>Icons by name: &lt;i data-icon="trash-2"&gt;&lt;/i&gt; gets the glyph of that icon of the Lucide font (Fonts/Lucide.ttf, ISC licence in
    /// Fonts/LICENSE-Lucide.txt) as its only text, and the user agent sheet gives [data-icon] the font (font-family: lucide). The names and their code
    /// points come from Fonts/lucide-codepoints.json, the file that ships with the font (2379 icons, all in the private use area U+E038..U+E856, one
    /// char each). The map is read once, on the first icon; an unknown name gives no glyph and one line through <see cref="Warn"/>.</summary>
    internal static class Icons
    {
        /// <summary>Where lucide-codepoints.json is (BaroUi sets it from the package folder; tests from the repo).</summary>
        public static string MapFile;
        /// <summary>Told once per unknown name (the window logs it).</summary>
        public static Action<string> Warn;

        private static Dictionary<string, string> map;
        private static readonly HashSet<string> warned = new(StringComparer.Ordinal);
        private static readonly object sync = new();

        /// <summary>The glyph (a one-char string) of an icon name; false for an unknown name or when the map cannot be read.</summary>
        public static bool TryGlyph(string name, out string glyph)
        {
            glyph = null;
            if (string.IsNullOrWhiteSpace(name)) { return false; }
            var m = Map();
            if (m != null && m.TryGetValue(name.Trim(), out glyph)) { return true; }
            lock (sync)
            {
                if (warned.Count < 256 && warned.Add(name)) { Warn?.Invoke(m == null ? $"icons: the map {MapFile ?? "(not set)"} could not be read" : $"icons: no icon named '{name}'"); }
            }
            return false;
        }

        /// <summary>Puts the glyph of e's data-icon in as e's only text (the text nodes it had go; element children stay, an icon has none). False when
        /// e has no data-icon. An unknown name leaves e without text.</summary>
        public static bool Apply(Element e)
        {
            string name = e.GetAttribute("data-icon");
            if (name == null) { return false; }
            for (int i = e.Children.Count - 1; i >= 0; i--) { if (e.Children[i] is TextNode t) { e.Remove(t); } }
            if (TryGlyph(name, out string glyph)) { e.Insert(0, new TextNode(glyph)); }
            return true;
        }

        /// <summary>Every element of a subtree with data-icon (after parsing a page).</summary>
        public static void ApplyAll(Element root)
        {
            if (root.GetAttribute("data-icon") != null) { Apply(root); }
            for (int i = 0; i < root.Children.Count; i++) { if (root.Children[i] is Element c) { ApplyAll(c); } }
        }

        /// <summary>Forgets the map (tests, a reloaded package).</summary>
        public static void Reset()
        {
            lock (sync) { map = null; warned.Clear(); }
        }

        private static Dictionary<string, string> Map()
        {
            lock (sync)
            {
                if (map != null) { return map; }
                if (MapFile == null || !File.Exists(MapFile)) { return null; }
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(MapFile));
                    var m = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.TryGetInt32(out int cp) && cp > 0 && cp <= 0xFFFF) { m[p.Name] = ((char)cp).ToString(); }
                    }
                    return map = m;
                }
                catch (Exception) { return null; }
            }
        }
    }
}
