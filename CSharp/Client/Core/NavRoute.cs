namespace BaroUI.Core
{
    /// <summary>Where a navigation event (the side buttons, Alt+Left, Alt+Right: the attributes onback and onforward) goes first. The element under the
    /// pointer and its ancestors come first, then the focused element and its ancestors; the first one with the attribute gets it, and the handler
    /// runs there (Template.Dispatch walks up from it, so a handler on a higher element still gets its turn). Null: no element has it, the view's
    /// OnBack/OnForward is asked next. Free of the game (the tests build the tree).</summary>
    internal static class NavRoute
    {
        public const string Back = "onback";
        public const string Forward = "onforward";

        /// <summary>The attribute of the navigation: <see cref="Back"/> or <see cref="Forward"/>.</summary>
        public static Element Target(Element under, Element focus, string attr)
        {
            for (var e = under; e != null; e = e.Parent) { if (e.GetAttribute(attr) != null) { return e; } }
            for (var e = focus; e != null; e = e.Parent) { if (e.GetAttribute(attr) != null) { return e; } }
            return null;
        }
    }
}
