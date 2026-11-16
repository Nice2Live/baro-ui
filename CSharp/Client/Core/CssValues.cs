using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>CSS length units. U = min(window width / 1600, window height / 900) × HUD scale (UiWindow sets it; the 1600×900 design canvas).</summary>
    public enum Unit { None, Px, U, Percent, Fr, Em, Auto }

    public readonly struct Length : IEquatable<Length>
    {
        public readonly float Value;
        public readonly Unit Unit;
        public Length(float value, Unit unit) { Value = value; Unit = unit; }
        public static readonly Length Auto = new(0, Unit.Auto);
        public static readonly Length Zero = new(0, Unit.Px);
        public bool IsAuto => Unit == Unit.Auto;

        private static readonly (string Suffix, Unit Unit)[] Units = { ("px", Unit.Px), ("fr", Unit.Fr), ("em", Unit.Em), ("%", Unit.Percent), ("u", Unit.U) };

        /// <summary>No allocations: layout and paint parse lengths often.</summary>
        public static bool TryParse(string s, out Length length)
        {
            length = Zero;
            if (s == null) { return false; }
            var t = s.AsSpan().Trim();
            if (t.Length == 0) { return false; }
            if (t.Equals("auto", StringComparison.OrdinalIgnoreCase)) { length = Auto; return true; }
            foreach (var (suffix, unit) in Units)
            {
                if (!t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!float.TryParse(t.Slice(0, t.Length - suffix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) { return false; }
                length = new Length(v, unit);
                return true;
            }
            if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) { return false; }
            // a bare number is only a length when it is 0 (CSS rule); line-height and flex numbers read it as None
            length = new Length(n, n == 0 ? Unit.Px : Unit.None);
            return true;
        }

        public bool Equals(Length o) => Value == o.Value && Unit == o.Unit;
        public override bool Equals(object o) => o is Length l && Equals(l);
        public override int GetHashCode() => HashCode.Combine(Value, Unit);
        public override string ToString() => Unit switch
        {
            Unit.Auto => "auto", Unit.None => Value.ToString(CultureInfo.InvariantCulture),
            Unit.Percent => Value.ToString(CultureInfo.InvariantCulture) + "%",
            _ => Value.ToString(CultureInfo.InvariantCulture) + Unit.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Straight-alpha colour without any XNA dependency (the core runs outside the game in tests).</summary>
    public readonly struct Rgba : IEquatable<Rgba>
    {
        public readonly byte R, G, B, A;
        public Rgba(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
        public static readonly Rgba Transparent = new(0, 0, 0, 0);

        private static readonly Dictionary<string, Rgba> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["transparent"] = Transparent, ["black"] = new(0, 0, 0), ["white"] = new(255, 255, 255),
            ["red"] = new(255, 0, 0), ["green"] = new(0, 128, 0), ["blue"] = new(0, 0, 255), ["yellow"] = new(255, 255, 0),
            ["orange"] = new(255, 165, 0), ["gray"] = new(128, 128, 128), ["grey"] = new(128, 128, 128),
            ["cyan"] = new(0, 255, 255), ["teal"] = new(0, 128, 128), ["magenta"] = new(255, 0, 255), ["lime"] = new(0, 255, 0),
            ["silver"] = new(192, 192, 192), ["navy"] = new(0, 0, 128), ["maroon"] = new(128, 0, 0), ["purple"] = new(128, 0, 128),
        };

        public static bool TryParse(string s, out Rgba color)
        {
            color = Transparent;
            if (string.IsNullOrWhiteSpace(s)) { return false; }
            s = s.Trim();
            if (Named.TryGetValue(s, out color)) { return true; }
            if (s[0] == '#') { return TryHex(s.Substring(1), out color); }
            int open = s.IndexOf('(');
            if (open < 0 || !s.EndsWith(")", StringComparison.Ordinal)) { return false; }
            string fn = s.Substring(0, open).Trim().ToLowerInvariant();
            if (fn != "rgb" && fn != "rgba") { return false; }
            var parts = s.Substring(open + 1, s.Length - open - 2).Replace("/", ",").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 && parts.Length != 4) { return false; }
            var c = new byte[4];
            c[3] = 255;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                bool pct = p.EndsWith("%", StringComparison.Ordinal);
                if (!float.TryParse(pct ? p.TrimEnd('%') : p, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) { return false; }
                float max = i == 3 ? 1f : 255f;
                if (pct) { v = v / 100f * max; }
                c[i] = (byte)Math.Round(Math.Clamp(v, 0, max) / max * 255f);
            }
            color = new Rgba(c[0], c[1], c[2], c[3]);
            return true;
        }

        private static bool TryHex(string h, out Rgba color)
        {
            color = Transparent;
            if (h.Length == 3 || h.Length == 4) { h = string.Concat(Array.ConvertAll(h.ToCharArray(), ch => new string(ch, 2))); }
            if (h.Length != 6 && h.Length != 8) { return false; }
            if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) { return false; }
            if (h.Length == 6) { v = (v << 8) | 0xFF; }
            color = new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        public bool Equals(Rgba o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object o) => o is Rgba c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public override string ToString() => A == 255 ? $"#{R:x2}{G:x2}{B:x2}" : $"#{R:x2}{G:x2}{B:x2}{A:x2}";
    }
}
