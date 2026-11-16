using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>Russian typography for the line breaker: which pair of neighbouring words must not be split by a line break. Pure logic on two
    /// words. The layout turns a glued pair into one unbreakable item (the text itself is not rewritten, copy and select see what the page wrote).
    /// Rules: a one-letter or short (up to 3 letters, e.g. без, для, что, или) preposition/conjunction stays with the next word (в доме, не знаю,
    /// без цели); the particles же бы ли ль ж б stay with the
    /// previous word; a dash stays with the previous word (never starts a line); a number stays with its unit (10 км, 5 %, 1990 г.) and
    /// № with its number; an initial stays with the next name (А. С. Пушкин). Switch: lang (ru or none = on, any other = off) and -bbe-ru-glue.</summary>
    internal static class RuText
    {
        private static readonly HashSet<string> Short = new(StringComparer.Ordinal)
        {
            "в", "к", "с", "у", "о", "и", "а", "я", "но", "на", "за", "по", "от", "до", "из", "не", "ни", "во", "ко", "со", "об", "да",
            "без", "для", "под", "над", "при", "про", "что", "как", "или", "либо", "ибо", "чем",
        };

        private static readonly HashSet<string> Particles = new(StringComparer.Ordinal) { "же", "бы", "ли", "ль", "ж", "б" };

        private static readonly HashSet<string> Units = new(StringComparer.Ordinal)
        {
            "км", "м", "см", "мм", "кг", "г", "мг", "т", "л", "мл", "с", "сек", "мин", "ч", "час", "часов", "шт", "%", "‰", "руб", "коп", "₽", "$", "€", "°C", "°F", "°",
            "г.", "гг.", "тыс", "тыс.", "млн", "млрд", "кВт", "Вт", "МБ", "ГБ", "КБ", "Гц", "кГц", "дн", "лет", "год", "года",
        };

        /// <summary>Whether a page of this lang (the nearest lang attribute, null when there is none) takes the rules: ru and ru-RU do, no lang does
        /// (the rules only match Russian words, other text is unaffected), another language does not.</summary>
        public static bool LangOn(string lang)
        {
            if (string.IsNullOrEmpty(lang)) { return true; }
            return lang.Length >= 2 && (lang[0] == 'r' || lang[0] == 'R') && (lang[1] == 'u' || lang[1] == 'U') && (lang.Length == 2 || lang[2] == '-' || lang[2] == '_');
        }

        public static bool HasCyrillic(string s)
        {
            foreach (char c in s) { if (c >= 'Ѐ' && c <= 'ӿ') { return true; } }
            return false;
        }

        /// <summary>True when a line break must not stand between word a and the word b that follows it.</summary>
        public static bool Glue(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) { return false; }
            if (b == "—" || b == "–") { return true; }
            if (b.Length <= 3 && Particles.Contains(Lower(Bare(b))) && !EndsPunct(a)) { return true; }
            string core = Bare(a);
            if (core.Length > 0 && IsLetters(core) && core.Length == a.Length - LeadLen(a) && Short.Contains(Lower(core))) { return true; }
            if (a == "№") { return true; }
            if (IsNumber(a) && Units.Contains(TrimTail(b))) { return true; }
            if (a.Length == 2 && a[1] == '.' && char.IsUpper(a[0]) && char.IsUpper(FirstLetter(b))) { return true; }
            return false;
        }

        private static string Lower(string s) => s.ToLowerInvariant();

        private static bool EndsPunct(string a) { char c = a[a.Length - 1]; return c == ',' || c == '.' || c == ';' || c == ':' || c == '!' || c == '?' || c == '—'; }

        private static int LeadLen(string a)
        {
            int i = 0;
            while (i < a.Length && (a[i] == '«' || a[i] == '"' || a[i] == '(' || a[i] == '“' || a[i] == '\'')) { i++; }
            return i;
        }

        /// <summary>The word without leading quotes/brackets and trailing punctuation.</summary>
        private static string Bare(string a)
        {
            int s = LeadLen(a), e = a.Length;
            while (e > s && (a[e - 1] == ',' || a[e - 1] == '.' || a[e - 1] == ';' || a[e - 1] == ':' || a[e - 1] == '!' || a[e - 1] == '?' || a[e - 1] == ')' || a[e - 1] == '»' || a[e - 1] == '"' || a[e - 1] == '”')) { e--; }
            return s == 0 && e == a.Length ? a : a.Substring(s, e - s);
        }

        /// <summary>A unit without trailing punctuation that is not part of it ("км," "км)"; "г." and "тыс." keep their dot).</summary>
        private static string TrimTail(string b)
        {
            int e = b.Length;
            while (e > 0 && (b[e - 1] == ',' || b[e - 1] == ';' || b[e - 1] == ':' || b[e - 1] == '!' || b[e - 1] == '?' || b[e - 1] == ')' || b[e - 1] == '»' || b[e - 1] == '"')) { e--; }
            if (e > 0 && b[e - 1] == '.' && !Units.Contains(e == b.Length ? b : b.Substring(0, e))) { e--; }
            return e == b.Length ? b : b.Substring(0, e);
        }

        private static bool IsLetters(string s)
        {
            if (s.Length == 0) { return false; }
            foreach (char c in s) { if (!char.IsLetter(c)) { return false; } }
            return true;
        }

        private static bool IsNumber(string a)
        {
            if (a.Length == 0 || !char.IsDigit(a[a.Length - 1])) { return false; }
            foreach (char c in a) { if (!char.IsDigit(c) && c != ',' && c != '.' && c != '-' && c != '−') { return false; } }
            return true;
        }

        private static char FirstLetter(string b)
        {
            foreach (char c in b) { if (char.IsLetter(c)) { return c; } if (c != '«' && c != '"' && c != '(' && c != '“') { break; } }
            return ' ';
        }
    }
}
