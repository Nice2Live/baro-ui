using System;
using System.Collections.Generic;
using System.Linq;

namespace BaroUI.Core
{
    /// <summary>Translated texts in a page: <c>data-t="id"</c> puts the text of the id into the element (instead of what it holds), <c>data-t-title="id"</c> and <c>data-t-placeholder="id"</c> (any
    /// <c>data-t-&lt;attribute&gt;</c>) put it into that attribute. The ids belong to the text files of the package (Localization/English.xml, Russian.xml: the game's own TextManager), the markup holds no
    /// language of its own. A text may carry {{…}} like any text of the page; it is bound after the substitution. An id with no text is a problem of the page (the banner and the log tell it) and
    /// the element keeps what the markup had. Applied when a page is read (<see cref="UiWindow.FromFile"/>), so a page opened after the language changed is in the new language.</summary>
    public static class Localizer
    {
        /// <summary>The text of an id in the player's language; null when there is none. The host sets it (the game: TextManager), tests set it to read the XML files.</summary>
        public static Func<string, string> Texts;

        /// <summary>Puts the texts in. Returns how many places were filled. Without a source (null) the page stays as it is.</summary>
        public static int Apply(Document doc, Func<string, string> texts = null)
        {
            texts ??= Texts;
            if (texts == null) { return 0; }
            int filled = 0;
            foreach (var e in doc.Root.Descendants().ToList())
            {
                string id = e.GetAttribute("data-t");
                if (id != null)
                {
                    string text = texts(id.Trim());
                    if (text == null) { doc.Diagnostics.Add(new Diagnostic(doc.File, e.Line, $"data-t=\"{id}\": no text with this id")); }
                    else
                    {
                        foreach (var c in e.Children) { c.Parent = null; }
                        e.Children.Clear();
                        e.Append(new TextNode(text) { Line = e.Line });
                        filled++;
                    }
                }
                foreach (var kv in e.Attributes.Where(a => a.Key.StartsWith("data-t-", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    string attr = kv.Key.Substring("data-t-".Length);
                    string text = attr.Length == 0 ? null : texts(kv.Value.Trim());
                    if (text == null) { doc.Diagnostics.Add(new Diagnostic(doc.File, e.Line, $"{kv.Key}=\"{kv.Value}\": no text with this id")); continue; }
                    e.SetAttribute(attr, text);
                    filled++;
                }
            }
            return filled;
        }
    }
}
