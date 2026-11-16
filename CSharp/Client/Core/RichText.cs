using System.Text.RegularExpressions;

namespace BaroUI.Core
{
    /// <summary>The game's rich-text markers (‖color:…‖ text ‖end‖, ‖metadata:…‖) cut out of a string. A page draws plain text, and mod item names carry
    /// colours (TSM and its addons), so every value that goes into a page loses the markers and shows only the words.</summary>
    internal static class RichText
    {
        private static readonly Regex Marker = new Regex("‖[^‖]*‖", RegexOptions.Compiled);

        public static string Plain(string s) => s == null || s.IndexOf('‖') < 0 ? s : Marker.Replace(s, "");
    }
}
