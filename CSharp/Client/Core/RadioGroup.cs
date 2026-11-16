using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>input type=radio: radios with the same name in one page are a group, one checked at a time. Game-free so the tests run it.
    /// A radio without a name is a group of one.</summary>
    internal static class RadioGroup
    {
        public static bool Is(Element e) => e is { Tag: "input" } && e.GetAttribute("type")?.ToLowerInvariant() == "radio";

        /// <summary>What the radio stands for in the model: its value attribute, "on" without one (as in HTML).</summary>
        public static string ValueOf(Element e) => e.GetAttribute("value") ?? "on";

        /// <summary>Fills "into" with the radios of the group of "radio" under root, in document order (the radio itself included).</summary>
        public static void Members(Element root, Element radio, List<Element> into)
        {
            into.Clear();
            string name = radio.GetAttribute("name");
            if (string.IsNullOrEmpty(name)) { into.Add(radio); return; }
            foreach (var e in root.Descendants()) { if (Is(e) && e.GetAttribute("name") == name) { into.Add(e); } }
        }

        /// <summary>Checks the radio and unchecks the rest of its group. "changed" gets every element whose state changed (restyle them); false: it was checked already.</summary>
        public static bool Check(Element root, Element radio, List<Element> changed)
        {
            changed.Clear();
            if (radio.Checked) { return false; }
            var members = new List<Element>();
            Members(root, radio, members);
            foreach (var m in members)
            {
                if (m != radio && m.Checked) { m.RemoveAttribute("checked"); changed.Add(m); }
            }
            radio.SetAttribute("checked", "");
            changed.Add(radio);
            return true;
        }

        /// <summary>The next (dir 1) or previous (dir -1) usable radio of the group, wrapping round; null when there is no other.</summary>
        public static Element Neighbor(Element root, Element radio, int dir, Func<Element, bool> usable)
        {
            var members = new List<Element>();
            Members(root, radio, members);
            int i = members.IndexOf(radio), n = members.Count;
            for (int k = 1; k < n; k++)
            {
                var m = members[((i + dir * k) % n + n) % n];
                if (usable(m)) { return m; }
            }
            return null;
        }

        /// <summary>Whether Tab stops on this radio: in a group only the checked one is a stop (the first usable one while none is checked), so Tab leaves the group in one press.</summary>
        public static bool IsTabStop(Element root, Element radio, Func<Element, bool> usable)
        {
            if (string.IsNullOrEmpty(radio.GetAttribute("name"))) { return true; }
            var members = new List<Element>();
            Members(root, radio, members);
            Element first = null;
            foreach (var m in members)
            {
                if (!usable(m)) { continue; }
                if (m.Checked) { return m == radio; }
                first ??= m;
            }
            return first == radio;
        }
    }
}
