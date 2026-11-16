using System;
using System.Collections.Generic;
using System.Globalization;

namespace BaroUI.Core
{
    /// <summary>The Tab order of a page (docs/ui/DESIGN_EVENTS.md section 5), game-free so the tests run it: as in HTML, elements with a positive tabindex come
    /// first, by that number and then by document order; then the natural ones (tabindex 0, or none on a control that takes the keyboard) in document
    /// order. tabindex -1 can take the focus from a click or from code but Tab skips it.</summary>
    internal static class FocusOrder
    {
        /// <summary>The tabindex attribute as an integer; null when there is none or it is not a number.</summary>
        public static int? TabIndex(Element e) =>
            int.TryParse(e.GetAttribute("tabindex")?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;

        /// <summary>Fills "into" with the Tab stops under root, in Tab order. natural: whether a control without a tabindex is a stop by its kind (text field, button, ...);
        /// an element with a tabindex is a stop when it is 0 or more, whatever its kind.</summary>
        public static void Collect(Element root, Func<Element, bool> natural, List<Element> into)
        {
            into.Clear();
            List<(int Tab, int Order, Element E)> positive = null;
            int order = 0;
            foreach (var e in root.Descendants())
            {
                order++;
                if (TabIndex(e) is { } t)
                {
                    if (t < 0) { continue; }
                    if (t > 0) { (positive ??= new()).Add((t, order, e)); continue; }
                    into.Add(e);
                }
                else if (natural(e)) { into.Add(e); }
            }
            if (positive == null) { return; }
            positive.Sort((a, b) => a.Tab != b.Tab ? a.Tab.CompareTo(b.Tab) : a.Order.CompareTo(b.Order));
            var all = new List<Element>(positive.Count + into.Count);
            foreach (var p in positive) { all.Add(p.E); }
            all.AddRange(into);
            into.Clear();
            into.AddRange(all);
        }

        /// <summary>The next (dir 1) or previous (dir -1) usable stop after current, wrapping round; current itself comes last (the only stop gives itself back).
        /// A current that is not a stop (tabindex -1, gone) starts from the first stop forwards or the last backwards. A stop with a negative tabindex now is skipped
        /// even when the order was collected before it changed. Null: no usable stop.</summary>
        public static Element Step(IReadOnlyList<Element> order, Element current, int dir, Func<Element, bool> usable)
        {
            int n = order.Count, i = current == null ? -1 : IndexOf(order, current);
            for (int k = 1; k <= n; k++)
            {
                int at = i < 0 ? (dir > 0 ? k - 1 : n - k) : ((i + dir * k) % n + n) % n;
                // an order kept from before a tabindex changed may still list a -1 element: Tab never stops on it
                if (TabIndex(order[at]) is < 0) { continue; }
                if (usable(order[at])) { return order[at]; }
            }
            return null;
        }

        private static int IndexOf(IReadOnlyList<Element> order, Element e)
        {
            for (int i = 0; i < order.Count; i++) { if (order[i] == e) { return i; } }
            return -1;
        }
    }
}
