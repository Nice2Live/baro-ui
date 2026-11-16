using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>querySelector for the engine's DOM: any selector the stylesheet parser accepts (#id, .cls, tag,
    /// descendant and child combinators, :hover…). Parsed selectors are cached.</summary>
    public static class Query
    {
        private static readonly Dictionary<string, Selector> cache = new();

        public static Selector Parse(string selector)
        {
            if (cache.TryGetValue(selector, out var s)) { return s; }
            s = CssParser.ParseSelector(selector.Trim(), out _);
            cache[selector] = s;
            return s;
        }

        public static Element First(Element root, string selector)
        {
            var s = Parse(selector);
            if (s == null) { return null; }
            foreach (var e in root.Descendants()) { if (!e.Hidden && s.Matches(e)) { return e; } }
            return null;
        }

        public static List<Element> All(Element root, string selector)
        {
            var list = new List<Element>();
            var s = Parse(selector);
            if (s == null) { return list; }
            foreach (var e in root.Descendants()) { if (s.Matches(e)) { list.Add(e); } }
            return list;
        }
    }
}
