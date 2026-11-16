using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>The CSS cursor keyword to one of the game's seven cursors (GUI.CursorState: 0 Default, 1 Hand, 2 Move, 3 IBeam, 4 Dragging, 5 Waiting,
    /// 6 WaitingBackground). The game has no other shapes, so the resize arrows become Move and what has no counterpart (help, crosshair, zoom, not-allowed...)
    /// the plain cursor. Pure: the window casts the number, tests read the table.</summary>
    internal static class CssCursor
    {
        public const int Default = 0, Hand = 1, Move = 2, IBeam = 3, Dragging = 4, Waiting = 5, WaitingBackground = 6;

        /// <summary>The cursor for one keyword (any case); false for a word that is no CSS cursor (the cursor is then Default).</summary>
        public static bool TryMapKeyword(string word, out int cursor)
        {
            cursor = Default;
            switch (word.ToLowerInvariant())
            {
                case "auto": case "default": case "none": case "context-menu": case "help": case "crosshair": case "cell": case "alias": case "copy":
                case "not-allowed": case "no-drop": case "zoom-in": case "zoom-out":
                    return true;
                case "pointer": case "grab": cursor = Hand; return true;
                case "text": case "vertical-text": cursor = IBeam; return true;
                case "grabbing": cursor = Dragging; return true;
                case "wait": cursor = Waiting; return true;
                case "progress": cursor = WaitingBackground; return true;
                case "move": case "all-scroll": case "col-resize": case "row-resize": case "ew-resize": case "ns-resize": case "nesw-resize": case "nwse-resize":
                case "n-resize": case "e-resize": case "s-resize": case "w-resize": case "ne-resize": case "nw-resize": case "se-resize": case "sw-resize":
                    cursor = Move; return true;
            }
            return false;
        }

        private static readonly Dictionary<string, int> known = new(StringComparer.Ordinal);

        /// <summary>The cursor for a whole property value: a list like "url(a.png) 1 2, pointer" takes its first known keyword (url() images are not
        /// supported); no known keyword is Default. Called every frame under the mouse: a value seen before costs a lookup and no allocation.</summary>
        public static int Of(string value)
        {
            if (string.IsNullOrEmpty(value)) { return Default; }
            lock (known)
            {
                if (known.TryGetValue(value, out int c)) { return c; }
                c = Parse(value, out _);
                known[value] = c;
                return c;
            }
        }

        /// <summary>The cursor of a value and, for the style engine's warning, the first item that is no cursor keyword (null: all fine; url() items are skipped).</summary>
        public static int Parse(string value, out string unknown)
        {
            unknown = null;
            int result = Default;
            bool found = false;
            foreach (var item in CssParser.SplitTop(value ?? "", ','))
            {
                string t = item.Trim();
                if (t.Length == 0 || t.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (TryMapKeyword(t, out int c)) { if (!found) { result = c; found = true; } }
                else { unknown ??= t; }
            }
            return result;
        }
    }
}
