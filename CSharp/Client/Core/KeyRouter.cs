using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>The keys an element can ask for with data-keys. Names are as in the attribute (case does not matter there).</summary>
    public enum UiKey { Enter, Space, Escape, ArrowUp, ArrowDown, ArrowLeft, ArrowRight, Delete, Backspace, Home, End, PageUp, PageDown, Tab }

    /// <summary>One key press delivered to a data-keys handler.</summary>
    public sealed class UiKeyEvent
    {
        public UiKey Key { get; internal set; }
        public bool Ctrl { get; internal set; }
        public bool Shift { get; internal set; }
        public bool Alt { get; internal set; }
        /// <summary>The element that had the focus.</summary>
        public Element Target { get; internal set; }
        /// <summary>The element whose data-keys is being handled (Target or one of its ancestors).</summary>
        public Element Current { get; internal set; }
    }

    /// <summary>Keys on blocks (docs/ui/DESIGN_EVENTS.md section 5), game-free so the tests run it: an element with data-keys="Enter Delete" and
    /// data-key-name="card" gets those presses to the handler the page set under "card", while it or something inside it has the focus. The press bubbles
    /// from the focused element up through the ancestors that asked for that key; a handler answering true stops it and the default action (Space toggling
    /// a checkbox, Tab moving the focus, Esc closing) does not happen.</summary>
    internal sealed class KeyRouter
    {
        private readonly Dictionary<string, Func<UiKeyEvent, bool>> handlers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> masks = new(StringComparer.Ordinal);

        /// <summary>A handler threw: its name and the exception (the router has already removed it).</summary>
        public Action<string, Exception> OnError;

        public bool Any => handlers.Count > 0;

        /// <summary>Sets the handler of data-key-name="name"; null takes it away.</summary>
        public void Set(string name, Func<UiKeyEvent, bool> handler)
        {
            if (name == null) { return; }
            if (handler == null) { handlers.Remove(name); } else { handlers[name] = handler; }
        }

        /// <summary>The key of a data-keys word (ArrowUp, Up, Esc, ...), null when it is not one.</summary>
        public static UiKey? Parse(string word)
        {
            // Enum.TryParse takes "1" for the key numbered 1: only a name is a key
            if (string.IsNullOrEmpty(word) || !char.IsLetter(word[0])) { return null; }
            switch (word.ToLowerInvariant())
            {
                case "esc": return UiKey.Escape;
                case "up": return UiKey.ArrowUp;
                case "down": return UiKey.ArrowDown;
                case "left": return UiKey.ArrowLeft;
                case "right": return UiKey.ArrowRight;
                case "del": return UiKey.Delete;
                case "return": return UiKey.Enter;
                case "back": return UiKey.Backspace;
            }
            return Enum.TryParse(word, true, out UiKey k) && Enum.IsDefined(typeof(UiKey), k) ? k : null;
        }

        /// <summary>The key for the name the game's key enum gives it (Keys.Up is "Up", Keys.Back is "Back"); null for a key that is not routed.</summary>
        public static UiKey? FromGameKey(string name) => name switch
        {
            "Up" => UiKey.ArrowUp,
            "Down" => UiKey.ArrowDown,
            "Left" => UiKey.ArrowLeft,
            "Right" => UiKey.ArrowRight,
            "Back" => UiKey.Backspace,
            "Enter" or "Space" or "Escape" or "Delete" or "Home" or "End" or "PageUp" or "PageDown" or "Tab" => Enum.Parse<UiKey>(name),
            _ => null,
        };

        /// <summary>Whether a focused text field (codeEditor: a textarea; numberField: type=number) uses this key to edit: Delete, Backspace, Left, Right, Home and End always;
        /// Enter, Up, Down, PageUp and PageDown in a textarea; Up and Down in a number field (they step it). Such a key goes to the field and never to the data-keys of a
        /// block around it; every other key (Esc, Tab, Enter in a one-line field, Space...) still goes to data-keys first.</summary>
        public static bool EditsText(UiKey key, bool codeEditor, bool numberField)
        {
            switch (key)
            {
                case UiKey.Delete: case UiKey.Backspace: case UiKey.ArrowLeft: case UiKey.ArrowRight: case UiKey.Home: case UiKey.End: return true;
                case UiKey.Enter: case UiKey.PageUp: case UiKey.PageDown: return codeEditor;
                case UiKey.ArrowUp: case UiKey.ArrowDown: return codeEditor || numberField;
            }
            return false;
        }

        /// <summary>The keys of a data-keys value as a bit set (1 shifted by the key's number); words that are not keys are ignored.</summary>
        public int Mask(string keys)
        {
            if (string.IsNullOrEmpty(keys)) { return 0; }
            if (masks.TryGetValue(keys, out int m)) { return m; }
            foreach (var word in keys.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Parse(word) is { } k) { m |= 1 << (int)k; }
            }
            if (masks.Count > 256) { masks.Clear(); }
            masks[keys] = m;
            return m;
        }

        /// <summary>Delivers a press to the element that has the focus and its ancestors. True: a handler took it (the default action is off).</summary>
        public bool Dispatch(Element focus, UiKey key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            if (focus == null || handlers.Count == 0) { return false; }
            UiKeyEvent ev = null;
            for (var e = focus; e != null; e = e.Parent)
            {
                if (e.GetAttribute("data-keys") is not { } keys || (Mask(keys) & (1 << (int)key)) == 0) { continue; }
                if (e.GetAttribute("data-key-name") is not { } name || !handlers.TryGetValue(name, out var fn)) { continue; }
                ev ??= new UiKeyEvent { Key = key, Ctrl = ctrl, Shift = shift, Alt = alt, Target = focus };
                ev.Current = e;
                bool taken;
                try { taken = fn(ev); }
                catch (Exception ex) { handlers.Remove(name); OnError?.Invoke(name, ex); continue; }
                if (taken) { return true; }
            }
            return false;
        }
    }
}
