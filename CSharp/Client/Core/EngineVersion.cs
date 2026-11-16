using System;

namespace BaroUI.Core
{
    /// <summary>Version strings of the engine (docs/ui/API.md, versioning): "0.2.0", "0.2" or "0.2.0-bbe" (a suffix after '-' or '+' is not compared).
    /// Pure code, no game types, so the offline tests (tools/ui_test, VersionTests) run the same parse and compare.</summary>
    internal static class EngineVersion
    {
        /// <summary>The version in text form, or null when it is not major.minor[.build[.revision]] with numbers only.</summary>
        public static Version Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            string core = text.Trim();
            int cut = core.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) { core = core.Substring(0, cut); }
            var parts = core.Split('.');
            if (parts.Length < 2 || parts.Length > 4) { return null; }
            var numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 9) { return null; }
                foreach (char c in parts[i]) { if (c < '0' || c > '9') { return null; } }
                numbers[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            }
            // missing build and revision count as 0, so "0.2", "0.2.0" and "0.2.0.0" are the same version
            int Part(int i) => i < numbers.Length ? numbers[i] : 0;
            return new Version(Part(0), Part(1), Part(2), Part(3));
        }

        /// <summary>Missing build and revision count as 0 (System.Version uses -1 for a missing part, which made 0.2.0 older than 0.2.0.0).</summary>
        public static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        /// <summary>True when the engine is at least the minimum. An unreadable minimum is not at least anything: false.</summary>
        public static bool AtLeast(Version engine, string minimum)
        {
            var min = Parse(minimum);
            return engine != null && min != null && Normalize(engine).CompareTo(min) >= 0;
        }
    }
}
