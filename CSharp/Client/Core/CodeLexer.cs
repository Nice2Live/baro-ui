using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    internal enum CodeLang { None, Xml, Lua, CSharp }

    internal enum CodeKind : byte
    {
        Plain, Keyword, Type, Constant, String, Number, Comment, Punct,
        /// <summary>XML element names.</summary>
        Tag,
        /// <summary>XML attribute names.</summary>
        Attribute,
        /// <summary>XML entities (&amp;amp;), C# preprocessor lines, XML declarations and processing instructions.</summary>
        Entity,
        Directive,
    }

    /// <summary>A coloured piece of a line (the pieces that are Plain are left out).</summary>
    internal readonly struct CodeToken
    {
        public readonly int Start, Length;
        public readonly CodeKind Kind;
        public CodeToken(int start, int length, CodeKind kind) { Start = start; Length = length; Kind = kind; }
        public int End => Start + Length;
        public override string ToString() => $"{Kind}[{Start},{Length}]";
    }

    /// <summary>Simple line scanners for XML, Lua and C# (no parsing: only what colouring needs). Each scans one line given the state the previous line
    /// ended in (an open comment, a long string, a multi-line tag...) and returns the state this line ends in, so a text of thousands of lines is
    /// coloured line by line, on demand. Never throws: whatever the text, every character is passed once.</summary>
    internal static class CodeLexer
    {
        public static CodeLang LangOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return CodeLang.None; }
            string n = name.Trim().TrimStart('.').ToLowerInvariant();
            int dot = n.LastIndexOf('.');
            if (dot >= 0) { n = n.Substring(dot + 1); }
            switch (n)
            {
                case "xml": case "html": case "svg": case "xaml": case "csproj": case "props": case "config": return CodeLang.Xml;
                case "lua": return CodeLang.Lua;
                case "cs": case "csharp": case "c#": return CodeLang.CSharp;
                default: return CodeLang.None;
            }
        }

        /// <summary>Scans a line. into (may be null: only the state is wanted) gets the coloured pieces in order. Returns the state at the end of the line.</summary>
        public static int Scan(CodeLang lang, string line, int state, List<CodeToken> into) => lang switch
        {
            CodeLang.Xml => ScanXml(line, state, into),
            CodeLang.Lua => ScanLua(line, state, into),
            CodeLang.CSharp => ScanCs(line, state, into),
            _ => 0,
        };

        /// <summary>Adds a piece; next to a piece of the same kind it joins it.</summary>
        private static void Add(List<CodeToken> into, int start, int end, CodeKind kind)
        {
            if (into == null || end <= start) { return; }
            if (into.Count > 0 && into[^1].End == start && into[^1].Kind == kind)
            {
                into[^1] = new CodeToken(into[^1].Start, end - into[^1].Start, kind);
                return;
            }
            into.Add(new CodeToken(start, end - start, kind));
        }

        private static bool Starts(string s, int i, string t) => string.CompareOrdinal(s, i, t, 0, t.Length) == 0;

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        // ---------------------------------------------------------------- XML

        private const int XText = 0, XComment = 1, XCData = 2, XTag = 3, XDq = 4, XSq = 5, XPi = 6, XDecl = 7;

        private static int ScanXml(string s, int state, List<CodeToken> into)
        {
            int n = s.Length, i = 0;
            while (i < n)
            {
                switch (state)
                {
                    case XComment:
                    {
                        int end = s.IndexOf("-->", i, StringComparison.Ordinal);
                        int stop = end < 0 ? n : end + 3;
                        Add(into, i, stop, CodeKind.Comment);
                        i = stop;
                        if (end >= 0) { state = XText; }
                        break;
                    }
                    case XCData:
                    {
                        int end = s.IndexOf("]]>", i, StringComparison.Ordinal);
                        int stop = end < 0 ? n : end + 3;
                        Add(into, i, stop, CodeKind.String);
                        i = stop;
                        if (end >= 0) { state = XText; }
                        break;
                    }
                    case XPi:
                    {
                        int end = s.IndexOf("?>", i, StringComparison.Ordinal);
                        int stop = end < 0 ? n : end + 2;
                        Add(into, i, stop, CodeKind.Directive);
                        i = stop;
                        if (end >= 0) { state = XText; }
                        break;
                    }
                    case XDecl:
                    {
                        int end = s.IndexOf('>', i);
                        int stop = end < 0 ? n : end + 1;
                        Add(into, i, stop, CodeKind.Directive);
                        i = stop;
                        if (end >= 0) { state = XText; }
                        break;
                    }
                    case XDq:
                    case XSq:
                    {
                        int end = s.IndexOf(state == XDq ? '"' : '\'', i);
                        int stop = end < 0 ? n : end + 1;
                        Add(into, i, stop, CodeKind.String);
                        i = stop;
                        if (end >= 0) { state = XTag; }
                        break;
                    }
                    case XTag:
                    {
                        char c = s[i];
                        if (c == '>') { Add(into, i, i + 1, CodeKind.Punct); i++; state = XText; }
                        else if (c == '/' && i + 1 < n && s[i + 1] == '>') { Add(into, i, i + 2, CodeKind.Punct); i += 2; state = XText; }
                        else if (c == '"' || c == '\'')
                        {
                            int end = s.IndexOf(c, i + 1);
                            int stop = end < 0 ? n : end + 1;
                            Add(into, i, stop, CodeKind.String);
                            i = stop;
                            if (end < 0) { state = c == '"' ? XDq : XSq; }
                        }
                        else if (c == '=' || c == '/' || c == '?') { Add(into, i, i + 1, CodeKind.Punct); i++; }
                        else if (IsXmlName(c))
                        {
                            int j = i + 1;
                            while (j < n && IsXmlName(s[j])) { j++; }
                            Add(into, i, j, CodeKind.Attribute);
                            i = j;
                        }
                        else { i++; }
                        break;
                    }
                    default:
                    {
                        char c = s[i];
                        if (c == '<')
                        {
                            if (Starts(s, i, "<!--")) { state = XComment; Add(into, i, i + 4, CodeKind.Comment); i += 4; }
                            else if (Starts(s, i, "<![CDATA[")) { state = XCData; Add(into, i, i + 9, CodeKind.String); i += 9; }
                            else if (Starts(s, i, "<?")) { state = XPi; Add(into, i, i + 2, CodeKind.Directive); i += 2; }
                            else if (Starts(s, i, "<!")) { state = XDecl; Add(into, i, i + 2, CodeKind.Directive); i += 2; }
                            else if (i + 1 < n && (s[i + 1] == '/' || char.IsLetter(s[i + 1]) || s[i + 1] == '_' || s[i + 1] == ':'))
                            {
                                int j = i + 1;
                                if (j < n && s[j] == '/') { j++; }
                                Add(into, i, j, CodeKind.Punct);
                                int k = j;
                                while (k < n && IsXmlName(s[k])) { k++; }
                                Add(into, j, k, CodeKind.Tag);
                                i = k;
                                state = XTag;
                            }
                            else { i++; }
                        }
                        else if (c == '&')
                        {
                            int semi = s.IndexOf(';', i + 1, Math.Min(12, n - i - 1));
                            if (semi > i + 1) { Add(into, i, semi + 1, CodeKind.Entity); i = semi + 1; } else { i++; }
                        }
                        else
                        {
                            int j = i + 1;
                            while (j < n && s[j] != '<' && s[j] != '&') { j++; }
                            i = j;
                        }
                        break;
                    }
                }
            }
            return state;
        }

        private static bool IsXmlName(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

        // ---------------------------------------------------------------- Lua

        // state: 0 normal; 1 and 2 a string that goes on after a backslash at the end of the line; 0x100 | level a long comment; 0x200 | level a long string
        private const int LDq = 1, LSq = 2, LComment = 0x100, LString = 0x200;

        private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "for", "function", "goto", "if", "in", "local", "not", "or", "repeat", "return", "then", "until", "while",
        };

        private static readonly HashSet<string> LuaConstants = new(StringComparer.Ordinal) { "nil", "true", "false" };

        private static int ScanLua(string s, int state, List<CodeToken> into)
        {
            int n = s.Length, i = 0;
            while (i < n)
            {
                if ((state & 0xF00) == LComment || (state & 0xF00) == LString)
                {
                    bool comment = (state & 0xF00) == LComment;
                    int level = state & 0xFF;
                    int end = FindLuaClose(s, i, level);
                    int stop = end < 0 ? n : end;
                    Add(into, i, stop, comment ? CodeKind.Comment : CodeKind.String);
                    i = stop;
                    if (end >= 0) { state = 0; }
                    continue;
                }
                if (state == LDq || state == LSq)
                {
                    i = LuaQuoted(s, i, state == LDq ? '"' : '\'', into, out bool open, 0);
                    state = open ? state : 0;
                    continue;
                }
                char c = s[i];
                if (c == '-' && i + 1 < n && s[i + 1] == '-')
                {
                    int level = LuaLongOpen(s, i + 2, out int bodyStart);
                    if (level >= 0)
                    {
                        int end = FindLuaClose(s, bodyStart, level);
                        int stop = end < 0 ? n : end;
                        Add(into, i, stop, CodeKind.Comment);
                        i = stop;
                        if (end < 0) { state = LComment | Math.Min(level, 0xFF); }
                    }
                    else { Add(into, i, n, CodeKind.Comment); i = n; }
                }
                else if (c == '[' && LuaLongOpen(s, i, out int lbody) is var lv && lv >= 0)
                {
                    int end = FindLuaClose(s, lbody, lv);
                    int stop = end < 0 ? n : end;
                    Add(into, i, stop, CodeKind.String);
                    i = stop;
                    if (end < 0) { state = LString | Math.Min(lv, 0xFF); }
                }
                else if (c == '"' || c == '\'')
                {
                    i = LuaQuoted(s, i, c, into, out bool open, 1);
                    if (open) { state = c == '"' ? LDq : LSq; }
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    int j = NumberEnd(s, i);
                    Add(into, i, j, CodeKind.Number);
                    i = j;
                }
                else if (IsIdentStart(c))
                {
                    int j = i + 1;
                    while (j < n && IsIdent(s[j])) { j++; }
                    string word = s.Substring(i, j - i);
                    if (LuaConstants.Contains(word)) { Add(into, i, j, CodeKind.Constant); }
                    else if (LuaKeywords.Contains(word)) { Add(into, i, j, CodeKind.Keyword); }
                    i = j;
                }
                else if (c is '+' or '-' or '*' or '/' or '%' or '^' or '#' or '&' or '~' or '|' or '<' or '>' or '=' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ':' or ',' or '.')
                {
                    Add(into, i, i + 1, CodeKind.Punct);
                    i++;
                }
                else { i++; }
            }
            return state;
        }

        /// <summary>At s[i] = '[': "[[" or "[=*[" opens a long bracket; the level is the number of '='. -1: it is not one.</summary>
        private static int LuaLongOpen(string s, int i, out int bodyStart)
        {
            bodyStart = i;
            if (i >= s.Length || s[i] != '[') { return -1; }
            int j = i + 1, level = 0;
            while (j < s.Length && s[j] == '=') { level++; j++; }
            if (j < s.Length && s[j] == '[') { bodyStart = j + 1; return level; }
            return -1;
        }

        /// <summary>The index just past "]=*]" of the level at or after i; -1 when the line does not close it.</summary>
        private static int FindLuaClose(string s, int i, int level)
        {
            for (int j = s.IndexOf(']', i); j >= 0; j = j + 1 < s.Length ? s.IndexOf(']', j + 1) : -1)
            {
                int k = j + 1, eq = 0;
                while (k < s.Length && s[k] == '=') { eq++; k++; }
                if (eq == level && k < s.Length && s[k] == ']') { return k + 1; }
            }
            return -1;
        }

        /// <summary>A quoted string from i (the opening quote is at i when fresh = 1; 0: the line continues one). Returns where it ends; open: a backslash
        /// ends the line, the string goes on.</summary>
        private static int LuaQuoted(string s, int i, char q, List<CodeToken> into, out bool open, int fresh)
        {
            open = false;
            int start = i, j = i + fresh;
            while (j < s.Length)
            {
                char c = s[j];
                if (c == '\\') { if (j + 1 >= s.Length) { open = true; j = s.Length; break; } j += 2; continue; }
                if (c == q) { j++; break; }
                j++;
            }
            if (j > s.Length) { j = s.Length; }
            Add(into, start, j, CodeKind.String);
            return j;
        }

        private static int NumberEnd(string s, int i)
        {
            int n = s.Length, j = i;
            if (s[j] == '0' && j + 1 < n && (s[j + 1] == 'x' || s[j + 1] == 'X'))
            {
                j += 2;
                while (j < n && (Uri.IsHexDigit(s[j]) || s[j] == '_' || s[j] == '.')) { j++; }
                if (j < n && (s[j] == 'p' || s[j] == 'P')) { j++; if (j < n && (s[j] == '+' || s[j] == '-')) { j++; } while (j < n && char.IsDigit(s[j])) { j++; } }
                return j;
            }
            while (j < n && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == '_')) { j++; }
            if (j < n && (s[j] == 'e' || s[j] == 'E')) { j++; if (j < n && (s[j] == '+' || s[j] == '-')) { j++; } while (j < n && char.IsDigit(s[j])) { j++; } }
            return j;
        }

        // ---------------------------------------------------------------- C#

        // state: 0 normal; 1 a block comment; 2 a verbatim string (@"..."); 0x100 | quote count a raw string ("""..."""); 0x200 | quote count a raw
        // string where braces... (interpolation is not followed: a $ string is a string)
        private const int CBlock = 1, CVerbatim = 2, CRaw = 0x100;

        private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else", "enum", "event",
            "explicit", "extern", "finally", "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new",
            "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sealed", "sizeof", "stackalloc", "static",
            "struct", "switch", "this", "throw", "try", "typeof", "unchecked", "unsafe", "using", "virtual", "volatile", "while",
            // contextual ones that read as keywords where they matter
            "async", "await", "get", "set", "init", "add", "remove", "yield", "partial", "where", "when", "nameof", "value", "record", "required", "file",
            "global", "with", "and", "or", "not", "from", "select", "let", "orderby", "group", "join", "into", "on", "by", "equals", "ascending", "descending", "managed", "unmanaged",
        };

        private static readonly HashSet<string> CsTypes = new(StringComparer.Ordinal)
        {
            "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "void", "var", "dynamic", "nint", "nuint",
        };

        private static readonly HashSet<string> CsConstants = new(StringComparer.Ordinal) { "true", "false", "null" };

        private static int ScanCs(string s, int state, List<CodeToken> into)
        {
            int n = s.Length, i = 0;
            bool lineStart = true;
            while (i < n)
            {
                if (state == CBlock)
                {
                    int end = s.IndexOf("*/", i, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + 2;
                    Add(into, i, stop, CodeKind.Comment);
                    i = stop;
                    if (end >= 0) { state = 0; }
                    continue;
                }
                if (state == CVerbatim)
                {
                    int j = i;
                    bool closed = false;
                    while (j < n)
                    {
                        if (s[j] == '"') { if (j + 1 < n && s[j + 1] == '"') { j += 2; continue; } j++; closed = true; break; }
                        j++;
                    }
                    Add(into, i, j, CodeKind.String);
                    i = j;
                    if (closed) { state = 0; }
                    continue;
                }
                if ((state & 0xF00) == CRaw)
                {
                    int quotes = state & 0xFF;
                    string close = new string('"', quotes);
                    int end = s.IndexOf(close, i, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + quotes;
                    Add(into, i, stop, CodeKind.String);
                    i = stop;
                    if (end >= 0) { state = 0; }
                    continue;
                }
                char c = s[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                bool first = lineStart;
                lineStart = false;
                if (c == '#' && first)
                {
                    int cut = s.IndexOf("//", i, StringComparison.Ordinal);
                    Add(into, i, cut < 0 ? n : cut, CodeKind.Directive);
                    i = cut < 0 ? n : cut;
                }
                else if (c == '/' && i + 1 < n && s[i + 1] == '/') { Add(into, i, n, CodeKind.Comment); i = n; }
                else if (c == '/' && i + 1 < n && s[i + 1] == '*') { state = CBlock; Add(into, i, i + 2, CodeKind.Comment); i += 2; }
                else if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$')))
                {
                    // prefixes $ and @ in either order, then the quote (a lone @ or $ before something else is not a string)
                    int j = i;
                    bool verbatim = false;
                    while (j < n && (s[j] == '@' || s[j] == '$')) { if (s[j] == '@') { verbatim = true; } j++; }
                    if (j >= n || s[j] != '"') { Add(into, i, j, CodeKind.Punct); i = j; continue; }
                    int q = 0;
                    while (j + q < n && s[j + q] == '"') { q++; }
                    if (q >= 3 && !verbatim)
                    {
                        // a raw string: ends at the same number of quotes
                        string close = new string('"', q);
                        int end = s.IndexOf(close, j + q, StringComparison.Ordinal);
                        int stop = end < 0 ? n : end + q;
                        Add(into, i, stop, CodeKind.String);
                        i = stop;
                        if (end < 0) { state = CRaw | Math.Min(q, 0xFF); }
                    }
                    else if (verbatim)
                    {
                        int k = j + 1;
                        bool closed = false;
                        while (k < n)
                        {
                            if (s[k] == '"') { if (k + 1 < n && s[k + 1] == '"') { k += 2; continue; } k++; closed = true; break; }
                            k++;
                        }
                        Add(into, i, k, CodeKind.String);
                        i = k;
                        if (!closed) { state = CVerbatim; }
                    }
                    else
                    {
                        int k = j + 1;
                        while (k < n) { if (s[k] == '\\') { k += 2; continue; } if (s[k] == '"') { k++; break; } k++; }
                        if (k > n) { k = n; }
                        Add(into, i, k, CodeKind.String);
                        i = k;
                    }
                }
                else if (c == '\'')
                {
                    int k = i + 1;
                    while (k < n) { if (s[k] == '\\') { k += 2; continue; } if (s[k] == '\'') { k++; break; } k++; }
                    if (k > n) { k = n; }
                    Add(into, i, k, CodeKind.String);
                    i = k;
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    int j = CsNumberEnd(s, i);
                    Add(into, i, j, CodeKind.Number);
                    i = j;
                }
                else if (IsIdentStart(c) || c == '@')
                {
                    int j = i + (c == '@' ? 1 : 0);
                    int from = j;
                    while (j < n && IsIdent(s[j])) { j++; }
                    if (j == from) { i++; continue; }
                    string word = s.Substring(from, j - from);
                    if (c != '@')
                    {
                        if (CsConstants.Contains(word)) { Add(into, i, j, CodeKind.Constant); }
                        else if (CsTypes.Contains(word)) { Add(into, i, j, CodeKind.Type); }
                        else if (CsKeywords.Contains(word)) { Add(into, i, j, CodeKind.Keyword); }
                    }
                    i = j;
                }
                else if (c is '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '!' or '~' or '<' or '>' or '=' or '?' or ':' or ';' or ',' or '.' or '(' or ')' or '[' or ']' or '{' or '}')
                {
                    Add(into, i, i + 1, CodeKind.Punct);
                    i++;
                }
                else { i++; }
            }
            return state;
        }

        private static int CsNumberEnd(string s, int i)
        {
            int n = s.Length, j = i;
            if (s[j] == '0' && j + 1 < n && (s[j + 1] is 'x' or 'X' or 'b' or 'B'))
            {
                j += 2;
                while (j < n && (Uri.IsHexDigit(s[j]) || s[j] == '_')) { j++; }
            }
            else
            {
                while (j < n && (char.IsDigit(s[j]) || s[j] == '_' || (s[j] == '.' && j + 1 < n && char.IsDigit(s[j + 1])))) { j++; }
                if (j < n && (s[j] == 'e' || s[j] == 'E')) { int k = j + 1; if (k < n && (s[k] == '+' || s[k] == '-')) { k++; } if (k < n && char.IsDigit(s[k])) { j = k; while (j < n && char.IsDigit(s[j])) { j++; } } }
            }
            while (j < n && s[j] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U' or 'l' or 'L') { j++; }
            return j;
        }
    }

    /// <summary>The coloured pieces of the lines of a CodeEdit, kept per line and worked out on demand: a line is scanned when it is first asked for, and
    /// again only when it changed or the state before it did. Follows the editor through LinesChanged.</summary>
    internal sealed class CodeHighlighter
    {
        private static readonly CodeToken[] None = Array.Empty<CodeToken>();
        private readonly CodeEdit ed;
        private readonly List<CodeToken[]> tokens = new();
        private readonly List<int> startUsed = new(), endState = new();
        private readonly List<CodeToken> scratch = new();
        /// <summary>Lines before this one have a known state at their end.</summary>
        private int clean;
        private CodeLang lang;

        public CodeHighlighter(CodeEdit editor, CodeLang language)
        {
            ed = editor;
            lang = language;
            Reset();
            ed.LinesChanged += OnLinesChanged;
        }

        public CodeLang Lang
        {
            get => lang;
            set { if (value == lang) { return; } lang = value; Reset(); }
        }

        public void Detach() => ed.LinesChanged -= OnLinesChanged;

        private void Reset()
        {
            tokens.Clear(); startUsed.Clear(); endState.Clear();
            for (int i = 0; i < ed.LineCount; i++) { tokens.Add(null); startUsed.Add(-1); endState.Add(0); }
            clean = 0;
        }

        private void OnLinesChanged(int first, int oldCount, int newCount)
        {
            if (tokens.Count != ed.LineCount - newCount + oldCount) { Reset(); return; }
            tokens.RemoveRange(first, oldCount);
            startUsed.RemoveRange(first, oldCount);
            endState.RemoveRange(first, oldCount);
            for (int i = 0; i < newCount; i++) { tokens.Insert(first + i, null); startUsed.Insert(first + i, -1); endState.Insert(first + i, 0); }
            if (clean > first) { clean = first; }
        }

        /// <summary>The state at the end of a line, brought up to date (the lines before it are scanned for it, without keeping their pieces).</summary>
        private int StateAfter(int line)
        {
            for (int j = clean; j <= line; j++)
            {
                int start = j == 0 ? 0 : endState[j - 1];
                if (tokens[j] != null && startUsed[j] == start) { continue; }
                tokens[j] = null;
                startUsed[j] = start;
                endState[j] = CodeLexer.Scan(lang, ed.Line(j), start, null);
            }
            if (line + 1 > clean) { clean = line + 1; }
            return endState[line];
        }

        /// <summary>The coloured pieces of a line, in order, without overlaps.</summary>
        public CodeToken[] Tokens(int line)
        {
            if (lang == CodeLang.None || line < 0 || line >= tokens.Count) { return None; }
            int start = line == 0 ? 0 : StateAfter(line - 1);
            if (tokens[line] != null && startUsed[line] == start) { return tokens[line]; }
            scratch.Clear();
            endState[line] = CodeLexer.Scan(lang, ed.Line(line), start, scratch);
            startUsed[line] = start;
            tokens[line] = scratch.Count == 0 ? None : scratch.ToArray();
            return tokens[line];
        }

        /// <summary>The state a line starts in (tests).</summary>
        public int StateBefore(int line) => line == 0 ? 0 : StateAfter(line - 1);
    }
}
