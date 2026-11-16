using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>Forgiving parser for the HTML subset of docs/ui/ENGINE_PLAN.md: tags, attributes (quoted, unquoted,
    /// boolean), comments, entities, void and self-closing tags, &lt;style&gt;. Problems become diagnostics, never
    /// exceptions; a stray close tag closes up to its matching ancestor or is ignored.</summary>
    internal static class HtmlParser
    {
        private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
        {
            "br", "hr", "img", "video", "input", "meta", "link", "item", "slot",
        };

        // textarea: its content is its text (the value), not markup: <textarea data-lang="xml">&lt;a/&gt;</textarea>
        private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase) { "style", "script", "textarea" };

        private static readonly Dictionary<string, string> Entities = new(StringComparer.Ordinal)
        {
            ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
            ["times"] = "×", ["mdash"] = "—", ["ndash"] = "–", ["hellip"] = "…", ["rarr"] = "→", ["larr"] = "←",
            ["uarr"] = "↑", ["darr"] = "↓", ["middot"] = "·", ["deg"] = "°", ["laquo"] = "«", ["raquo"] = "»",
            ["numero"] = "№", ["minus"] = "−", ["plusmn"] = "±",
        };

        public static Document Parse(string html, string file = "html")
        {
            var doc = new Document { File = file };
            new State(html ?? "", doc).Run();
            Icons.ApplyAll(doc.Root);   // <i data-icon="name"> gets its glyph (Core/Icons.cs)
            return doc;
        }

        public static string DecodeEntities(string s, Action<string> warn = null)
        {
            if (s.IndexOf('&') < 0) { return s; }
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '&') { sb.Append(s[i]); continue; }
                int semi = s.IndexOf(';', i + 1);
                if (semi < 0 || semi - i > 12) { sb.Append('&'); continue; }
                string name = s.Substring(i + 1, semi - i - 1);
                string value = null;
                if (name.Length > 1 && name[0] == '#')
                {
                    bool hex = name[1] == 'x' || name[1] == 'X';
                    if (int.TryParse(hex ? name.Substring(2) : name.Substring(1), hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int code) && code > 0 && code <= 0x10FFFF)
                    {
                        value = char.ConvertFromUtf32(code);
                    }
                }
                else
                {
                    Entities.TryGetValue(name, out value);
                }
                if (value == null)
                {
                    warn?.Invoke($"unknown entity &{name};");
                    sb.Append('&');
                    continue;
                }
                sb.Append(value);
                i = semi;
            }
            return sb.ToString();
        }

        private sealed class State
        {
            private readonly string s;
            private readonly Document doc;
            private readonly Stack<Element> open = new();
            private int pos, line = 1;

            public State(string s, Document doc)
            {
                this.s = s;
                this.doc = doc;
                open.Push(doc.Root);
            }

            private void Warn(string message, int at = -1) => doc.Diagnostics.Add(new Diagnostic(doc.File, at < 0 ? line : at, message));

            private void Advance(int to)
            {
                for (int i = pos; i < to && i < s.Length; i++) { if (s[i] == '\n') { line++; } }
                pos = Math.Min(to, s.Length);
            }

            public void Run()
            {
                while (pos < s.Length)
                {
                    int lt = s.IndexOf('<', pos);
                    if (lt < 0) { Text(s.Length); break; }
                    if (lt > pos) { Text(lt); }
                    if (Starts("<!--")) { Comment(); }
                    else if (Starts("<!")) { Skip('>'); }
                    else if (Starts("</")) { CloseTag(); }
                    else if (pos + 1 < s.Length && (char.IsLetter(s[pos + 1]))) { OpenTag(); }
                    else { Text(pos + 1); }
                }
                while (open.Count > 1)
                {
                    var e = open.Pop();
                    Warn($"<{e.Tag}> is not closed", e.Line);
                }
            }

            private bool Starts(string t) => string.CompareOrdinal(s, pos, t, 0, t.Length) == 0;

            private void Skip(char until)
            {
                int end = s.IndexOf(until, pos);
                Advance(end < 0 ? s.Length : end + 1);
            }

            private void Text(int end)
            {
                int at = line;
                string raw = s.Substring(pos, end - pos);
                Advance(end);
                var text = DecodeEntities(raw, m => Warn(m, at));
                var parent = open.Peek();
                // merge with a preceding text node (a stray '<' splits text)
                if (parent.Children.Count > 0 && parent.Children[^1] is TextNode prev) { prev.Text += text; return; }
                parent.Append(new TextNode(text) { Line = at });
            }

            private void Comment()
            {
                int end = s.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                if (end < 0) { Warn("comment is not closed"); Advance(s.Length); return; }
                Advance(end + 3);
            }

            private void CloseTag()
            {
                int at = line;
                int gt = s.IndexOf('>', pos);
                if (gt < 0) { Warn("broken close tag"); Advance(s.Length); return; }
                string name = s.Substring(pos + 2, gt - pos - 2).Trim().ToLowerInvariant();
                Advance(gt + 1);
                if (Void.Contains(name)) { return; }
                foreach (var e in open)
                {
                    if (e == doc.Root) { break; }
                    if (e.Tag != name) { continue; }
                    while (open.Peek() != e)
                    {
                        var unclosed = open.Pop();
                        Warn($"<{unclosed.Tag}> is not closed before </{name}>", unclosed.Line);
                    }
                    open.Pop();
                    return;
                }
                Warn($"</{name}> without an open <{name}>", at);
            }

            private void OpenTag()
            {
                int at = line;
                int i = pos + 1;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' || s[i] == ':')) { i++; }
                var el = new Element(s.Substring(pos + 1, i - pos - 1)) { Line = at };
                Advance(i);
                bool selfClosing = false;
                while (true)
                {
                    SkipSpace();
                    if (pos >= s.Length) { Warn($"<{el.Tag}> is cut off", at); break; }
                    if (s[pos] == '>') { Advance(pos + 1); break; }
                    if (Starts("/>")) { selfClosing = true; Advance(pos + 2); break; }
                    if (!Attribute(el)) { Advance(pos + 1); }
                }
                open.Peek().Append(el);
                if (RawText.Contains(el.Tag))
                {
                    int end = s.IndexOf("</" + el.Tag, pos, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) { end = s.Length; Warn($"<{el.Tag}> is not closed", at); }
                    int contentLine = line;
                    string content = s.Substring(pos, end - pos);
                    Advance(end);
                    Skip('>');
                    if (el.Tag == "style") { doc.Styles.Add((content, contentLine)); }
                    else if (el.Tag == "textarea")
                    {
                        // as in HTML: one line break right after the tag is not part of the text, entities are decoded; a value="" attribute wins
                        if (content.StartsWith("\r\n", StringComparison.Ordinal)) { content = content.Substring(2); }
                        else if (content.StartsWith("\n", StringComparison.Ordinal)) { content = content.Substring(1); }
                        if (el.GetAttribute("value") == null && content.Length > 0) { el.SetAttribute("value", DecodeEntities(content, m => Warn(m, contentLine))); }
                    }
                    else { Warn("<script> is not supported: behaviour is C# (docs/ui/ENGINE_PLAN.md, stage 4)", at); }
                    return;
                }
                if (!selfClosing && !Void.Contains(el.Tag)) { open.Push(el); }
            }

            private void SkipSpace()
            {
                int i = pos;
                while (i < s.Length && char.IsWhiteSpace(s[i])) { i++; }
                Advance(i);
            }

            private bool Attribute(Element el)
            {
                int i = pos;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=' && s[i] != '>' && !(s[i] == '/' && i + 1 < s.Length && s[i + 1] == '>'))
                {
                    i++;
                }
                if (i == pos) { return false; }
                string name = s.Substring(pos, i - pos);
                int at = line;
                Advance(i);
                SkipSpace();
                if (pos >= s.Length || s[pos] != '=') { el.SetAttribute(name, ""); return true; }
                Advance(pos + 1);
                SkipSpace();
                string value;
                if (pos < s.Length && (s[pos] == '"' || s[pos] == '\''))
                {
                    char q = s[pos];
                    int end = s.IndexOf(q, pos + 1);
                    if (end < 0) { Warn($"attribute {name} has no closing quote", at); end = s.Length; }
                    value = s.Substring(pos + 1, end - pos - 1);
                    Advance(end + 1);
                }
                else
                {
                    int j = pos;
                    while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '>') { j++; }
                    value = s.Substring(pos, j - pos);
                    Advance(j);
                }
                if (el.GetAttribute(name) != null) { Warn($"attribute {name} repeats on <{el.Tag}>", at); }
                el.SetAttribute(name, DecodeEntities(value, m => Warn(m, at)));
                return true;
            }
        }
    }
}
