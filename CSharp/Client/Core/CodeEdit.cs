using System;
using System.Collections.Generic;
using System.Text;

namespace BaroUI.Core
{
    /// <summary>A place in the text: line and column, both from 0. Compares in reading order.</summary>
    public readonly struct CodePos : IComparable<CodePos>, IEquatable<CodePos>
    {
        public readonly int Line, Col;
        public CodePos(int line, int col) { Line = line; Col = col; }
        public int CompareTo(CodePos o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);
        public bool Equals(CodePos o) => Line == o.Line && Col == o.Col;
        public override bool Equals(object o) => o is CodePos p && Equals(p);
        public override int GetHashCode() => Line * 100003 + Col;
        public override string ToString() => $"{Line + 1}:{Col + 1}";
        public static bool operator ==(CodePos a, CodePos b) => a.Equals(b);
        public static bool operator !=(CodePos a, CodePos b) => !a.Equals(b);
        public static bool operator <(CodePos a, CodePos b) => a.CompareTo(b) < 0;
        public static bool operator >(CodePos a, CodePos b) => a.CompareTo(b) > 0;
        public static bool operator <=(CodePos a, CodePos b) => a.CompareTo(b) <= 0;
        public static bool operator >=(CodePos a, CodePos b) => a.CompareTo(b) >= 0;
    }

    /// <summary>The editing model of the multi-line code editor (&lt;textarea&gt;, docs/ui/AUTHORING.md), free of the game so the tests run it. The text
    /// is a list of lines (an edit touches the lines it covers, so thousands of lines cost nothing); the file's line ending is remembered and written
    /// back by Text. A caret and an anchor make the selection; every edit is one Replace, undone and redone from a bounded history; typing and
    /// deleting by single characters are grouped. Columns count UTF-16 units; the tab stop is TabSize columns. Methods that change the text return
    /// true when they did (false: nothing changed, or ReadOnly).</summary>
    public sealed class CodeEdit
    {
        private enum Kind { Other, Type, Backspace, Delete }

        private sealed class Record
        {
            public CodePos From;
            public string Removed, Inserted;
            public CodePos CaretBefore, AnchorBefore, CaretAfter, AnchorAfter;
            public Kind Kind;
        }

        private const int MaxHistory = 1000, MaxHistoryChars = 4_000_000;

        private readonly List<string> lines = new() { "" };
        private readonly List<Record> undo = new(), redo = new();
        private int historyChars;
        private Kind lastKind;
        private string cachedText = "";
        private int cachedVersion = -1;
        private int goalCol = -1;

        /// <summary>The line ending Text writes: the one the loaded text mostly had ("\r\n" or "\n").</summary>
        public string NewLine { get; set; } = "\n";
        public int TabSize { get; set; } = 4;
        /// <summary>The Tab key writes spaces up to the next tab stop (true) or a tab character.</summary>
        public bool SpacesForTab { get; set; } = true;
        /// <summary>Enter copies the indent of the line (and opens a level after an opening bracket).</summary>
        public bool AutoIndent { get; set; } = true;
        public bool ReadOnly { get; set; }

        public CodePos Caret { get; private set; }
        public CodePos Anchor { get; private set; }
        /// <summary>Bumped by every change of text, caret or selection.</summary>
        public int Version { get; private set; }
        /// <summary>Bumped by every change of the text (and by undo and redo).</summary>
        public int TextVersion { get; private set; }

        /// <summary>The lines changed: first line, how many lines of the old text it covered, how many lines of the new text replace them. The highlighter
        /// keeps its caches by it.</summary>
        public event Action<int, int, int> LinesChanged;

        public int LineCount => lines.Count;
        public string Line(int i) => lines[i];
        public bool HasSelection => Caret != Anchor;
        public CodePos SelStart => Caret < Anchor ? Caret : Anchor;
        public CodePos SelEnd => Caret < Anchor ? Anchor : Caret;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public CodePos End => new(lines.Count - 1, lines[^1].Length);

        /// <summary>The whole text with the file's line ending (cached until the next change).</summary>
        public string Text
        {
            get
            {
                if (cachedVersion != TextVersion) { cachedText = string.Join(NewLine, lines); cachedVersion = TextVersion; }
                return cachedText;
            }
        }

        public string SelectedText => HasSelection ? GetText(SelStart, SelEnd).Replace("\n", NewLine) : "";

        // ---------------------------------------------------------------- loading

        /// <summary>Replaces the text from outside (a model refresh, a loaded file): no undo step, the history is dropped, the caret is clamped (the start
        /// with keepCaret false). The line ending is taken from the text.</summary>
        public void SetText(string text, bool keepCaret = true)
        {
            text ??= "";
            if (text == Text && lines.Count > 0 && cachedVersion == TextVersion) { return; }
            NewLine = DetectNewLine(text, NewLine);
            var parts = Split(text);
            int old = lines.Count;
            lines.Clear();
            lines.AddRange(parts);
            undo.Clear();
            redo.Clear();
            historyChars = 0;
            lastKind = Kind.Other;
            TextVersion++;
            Version++;
            if (keepCaret) { Caret = Clamp(Caret); Anchor = Clamp(Anchor); } else { Caret = Anchor = default; }
            goalCol = -1;
            LinesChanged?.Invoke(0, old, lines.Count);
        }

        public static string DetectNewLine(string text, string fallback)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') { continue; }
                if (i > 0 && text[i - 1] == '\r') { crlf++; } else { lf++; }
            }
            if (crlf == 0 && lf == 0) { return fallback; }
            return crlf > lf ? "\r\n" : "\n";
        }

        /// <summary>Splits at "\r\n", "\n" and a lone "\r"; a text that ends with a line break ends with an empty line.</summary>
        public static List<string> Split(string text)
        {
            var result = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') { continue; }
                result.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
                start = i + 1;
            }
            result.Add(text.Substring(start));
            return result;
        }

        // ---------------------------------------------------------------- positions

        public CodePos Clamp(CodePos p)
        {
            int line = Math.Clamp(p.Line, 0, lines.Count - 1);
            return new CodePos(line, Math.Clamp(p.Col, 0, lines[line].Length));
        }

        /// <summary>The text between two places (any order), lines joined with "\n".</summary>
        public string GetText(CodePos a, CodePos b)
        {
            if (b < a) { (a, b) = (b, a); }
            a = Clamp(a);
            b = Clamp(b);
            if (a.Line == b.Line) { return lines[a.Line].Substring(a.Col, b.Col - a.Col); }
            var sb = new StringBuilder();
            sb.Append(lines[a.Line], a.Col, lines[a.Line].Length - a.Col);
            for (int i = a.Line + 1; i < b.Line; i++) { sb.Append('\n').Append(lines[i]); }
            sb.Append('\n').Append(lines[b.Line], 0, b.Col);
            return sb.ToString();
        }

        /// <summary>Where text that starts at from ends (text has "\n" breaks).</summary>
        public static CodePos Advance(CodePos from, string text)
        {
            int last = text.LastIndexOf('\n');
            if (last < 0) { return new CodePos(from.Line, from.Col + text.Length); }
            int n = 0;
            for (int i = 0; i < text.Length; i++) { if (text[i] == '\n') { n++; } }
            return new CodePos(from.Line + n, text.Length - last - 1);
        }

        /// <summary>The column on screen of a text column: a tab runs to the next multiple of tabSize.</summary>
        public static int VisualCol(string line, int col, int tabSize)
        {
            int v = 0;
            col = Math.Min(col, line.Length);
            for (int i = 0; i < col; i++) { v = line[i] == '\t' ? (v / tabSize + 1) * tabSize : v + 1; }
            return v;
        }

        /// <summary>The text column nearest to a column on screen (a click in the middle of a tab picks the nearer edge).</summary>
        public static int ColFromVisual(string line, int visual, int tabSize)
        {
            if (visual <= 0) { return 0; }
            int v = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int next = line[i] == '\t' ? (v / tabSize + 1) * tabSize : v + 1;
                if (visual < next) { return visual - v < next - visual ? i : i + 1; }
                v = next;
            }
            return line.Length;
        }

        /// <summary>The offset in Text of a place (counts the file's line ending).</summary>
        public int OffsetOf(CodePos p)
        {
            p = Clamp(p);
            int off = 0;
            for (int i = 0; i < p.Line; i++) { off += lines[i].Length + NewLine.Length; }
            return off + p.Col;
        }

        // ---------------------------------------------------------------- the one edit

        /// <summary>Replaces the text between two places (any order) with text (any line breaks), the caret after it. One undo step. This is what the other
        /// edits are made of; a host uses it to change the text from code.</summary>
        public bool Replace(CodePos from, CodePos to, string text)
        {
            text = Normalize(text ?? "");
            if (to < from) { (from, to) = (to, from); }
            from = Clamp(from);
            to = Clamp(to);
            var end = Advance(from, text);
            return Apply(from, to, text, Kind.Other, end, end);
        }

        private static string Normalize(string s) =>
            s.IndexOf('\r') < 0 ? s : s.Replace("\r\n", "\n").Replace('\r', '\n');

        private bool Apply(CodePos from, CodePos to, string inserted, Kind kind, CodePos caretAfter, CodePos anchorAfter)
        {
            if (ReadOnly) { return false; }
            string removed = GetText(from, to);
            if (removed.Length == 0 && inserted.Length == 0) { return false; }
            var rec = new Record { From = from, Removed = removed, Inserted = inserted, CaretBefore = Caret, AnchorBefore = Anchor, CaretAfter = caretAfter, AnchorAfter = anchorAfter, Kind = kind };
            RawReplace(from, to, inserted);
            Push(rec);
            redo.Clear();
            Caret = caretAfter;
            Anchor = anchorAfter;
            goalCol = -1;
            Version++;
            return true;
        }

        /// <summary>The text of the lines changes: no history, no caret.</summary>
        private void RawReplace(CodePos from, CodePos to, string text)
        {
            string head = lines[from.Line].Substring(0, from.Col), tail = lines[to.Line].Substring(to.Col);
            int oldCount = to.Line - from.Line + 1;
            var parts = text.IndexOf('\n') < 0 ? null : text.Split('\n');
            int newCount;
            if (parts == null)
            {
                newCount = 1;
                lines[from.Line] = head + text + tail;
                if (oldCount > 1) { lines.RemoveRange(from.Line + 1, oldCount - 1); }
            }
            else
            {
                newCount = parts.Length;
                parts[0] = head + parts[0];
                parts[^1] += tail;
                lines.RemoveRange(from.Line, oldCount);
                lines.InsertRange(from.Line, parts);
            }
            TextVersion++;
            LinesChanged?.Invoke(from.Line, oldCount, newCount);
        }

        private void Push(Record rec)
        {
            // typing and deleting one character at a time are one step while they continue where the last one ended
            if (undo.Count > 0 && rec.Kind != Kind.Other && rec.Kind == lastKind)
            {
                var last = undo[^1];
                if (rec.Kind == Kind.Type && last.Removed.Length == 0 && rec.Removed.Length == 0 && rec.From == Advance(last.From, last.Inserted)
                    && last.Inserted.IndexOf('\n') < 0 && rec.Inserted.IndexOf('\n') < 0 && IsWordChar(rec.Inserted[0]) == IsWordChar(last.Inserted[^1]))
                {
                    last.Inserted += rec.Inserted;
                    last.CaretAfter = rec.CaretAfter;
                    last.AnchorAfter = rec.AnchorAfter;
                    historyChars += rec.Inserted.Length;
                    return;
                }
                if (rec.Kind == Kind.Backspace && last.Inserted.Length == 0 && rec.Inserted.Length == 0 && Advance(rec.From, rec.Removed) == last.From
                    && last.Removed.IndexOf('\n') < 0 && rec.Removed.IndexOf('\n') < 0)
                {
                    last.From = rec.From;
                    last.Removed = rec.Removed + last.Removed;
                    last.CaretAfter = rec.CaretAfter;
                    last.AnchorAfter = rec.AnchorAfter;
                    historyChars += rec.Removed.Length;
                    return;
                }
                if (rec.Kind == Kind.Delete && last.Inserted.Length == 0 && rec.Inserted.Length == 0 && rec.From == last.From
                    && last.Removed.IndexOf('\n') < 0 && rec.Removed.IndexOf('\n') < 0)
                {
                    last.Removed += rec.Removed;
                    historyChars += rec.Removed.Length;
                    return;
                }
            }
            undo.Add(rec);
            lastKind = rec.Kind;
            historyChars += rec.Removed.Length + rec.Inserted.Length;
            while (undo.Count > MaxHistory || (historyChars > MaxHistoryChars && undo.Count > 1))
            {
                historyChars -= undo[0].Removed.Length + undo[0].Inserted.Length;
                undo.RemoveAt(0);
            }
        }

        public bool Undo()
        {
            if (undo.Count == 0 || ReadOnly) { return false; }
            var rec = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            historyChars -= rec.Removed.Length + rec.Inserted.Length;
            RawReplace(rec.From, Advance(rec.From, rec.Inserted), rec.Removed);
            redo.Add(rec);
            lastKind = Kind.Other;
            Caret = Clamp(rec.CaretBefore);
            Anchor = Clamp(rec.AnchorBefore);
            goalCol = -1;
            Version++;
            return true;
        }

        public bool Redo()
        {
            if (redo.Count == 0 || ReadOnly) { return false; }
            var rec = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            RawReplace(rec.From, Advance(rec.From, rec.Removed), rec.Inserted);
            undo.Add(rec);
            historyChars += rec.Removed.Length + rec.Inserted.Length;
            lastKind = Kind.Other;
            Caret = Clamp(rec.CaretAfter);
            Anchor = Clamp(rec.AnchorAfter);
            goalCol = -1;
            Version++;
            return true;
        }

        /// <summary>The next edit starts a new undo step (after a pause, a click, a caret move).</summary>
        public void BreakUndo() => lastKind = Kind.Other;

        // ---------------------------------------------------------------- typing

        /// <summary>Typed or pasted text over the selection; a pasted text may have line breaks.</summary>
        public bool Insert(string text)
        {
            if (text == null || text.Length == 0 && !HasSelection) { return false; }
            text = Normalize(text);
            var end = Advance(SelStart, text);
            return Apply(SelStart, SelEnd, text, Kind.Other, end, end);
        }

        /// <summary>One typed character (grouped with the ones before it in one undo step).</summary>
        public bool Type(char c)
        {
            if (char.IsControl(c)) { return false; }
            var s = c.ToString();
            var end = Advance(SelStart, s);
            // a character over a selection is a step of its own
            return Apply(SelStart, SelEnd, s, HasSelection ? Kind.Other : Kind.Type, end, end);
        }

        /// <summary>Enter: a line break; with AutoIndent the new line starts with the indent of this one, one level deeper after an opening bracket,
        /// and a closing bracket that stood right after the caret goes to a line of its own.</summary>
        public bool Enter()
        {
            var from = SelStart;
            string text = "\n";
            if (AutoIndent)
            {
                string line = lines[from.Line];
                int ws = 0;
                while (ws < line.Length && (line[ws] == ' ' || line[ws] == '\t')) { ws++; }
                string indent = line.Substring(0, Math.Min(ws, from.Col));
                char before = from.Col > 0 ? line[from.Col - 1] : '\0';
                var to = SelEnd;
                char after = to.Col < lines[to.Line].Length ? lines[to.Line][to.Col] : '\0';
                text += indent;
                if (before is '{' or '(' or '[' )
                {
                    string unit = SpacesForTab ? new string(' ', TabSize) : "\t";
                    text += unit;
                    var caret = Advance(from, text);
                    if ((before == '{' && after == '}') || (before == '(' && after == ')') || (before == '[' && after == ']'))
                    {
                        return Apply(from, to, text + "\n" + indent, Kind.Other, caret, caret);
                    }
                    return Apply(from, to, text, Kind.Other, caret, caret);
                }
            }
            var end = Advance(from, text);
            return Apply(from, SelEnd, text, Kind.Other, end, end);
        }

        /// <summary>Backspace: the selection, else one character (a word with ctrl); inside the indent of a line it goes back one tab stop.</summary>
        public bool Backspace(bool word = false)
        {
            if (HasSelection) { return DeleteSelection(); }
            if (Caret.Col == 0)
            {
                if (Caret.Line == 0) { return false; }
                var to = Caret;
                var from = new CodePos(Caret.Line - 1, lines[Caret.Line - 1].Length);
                return Apply(from, to, "", Kind.Other, from, from);
            }
            string line = lines[Caret.Line];
            int col = Caret.Col, back;
            if (word) { back = col - TextEdit.WordLeft(line, col); }
            else if (SpacesForTab && TabSize > 1 && IsSpaces(line, col) && col >= 1 && line[col - 1] == ' ')
            {
                back = col % TabSize == 0 ? TabSize : col % TabSize;
                back = Math.Min(back, col);
            }
            else { back = 1; }
            var f = new CodePos(Caret.Line, col - back);
            return Apply(f, Caret, "", word || back > 1 ? Kind.Other : Kind.Backspace, f, f);
        }

        /// <summary>Delete: the selection, else one character (a word with ctrl) after the caret, or the line break at the end of the line.</summary>
        public bool Delete(bool word = false)
        {
            if (HasSelection) { return DeleteSelection(); }
            string line = lines[Caret.Line];
            if (Caret.Col >= line.Length)
            {
                if (Caret.Line == lines.Count - 1) { return false; }
                return Apply(Caret, new CodePos(Caret.Line + 1, 0), "", Kind.Other, Caret, Caret);
            }
            int to = word ? TextEdit.WordRight(line, Caret.Col) : Caret.Col + 1;
            return Apply(Caret, new CodePos(Caret.Line, to), "", word ? Kind.Other : Kind.Delete, Caret, Caret);
        }

        public bool DeleteSelection()
        {
            if (!HasSelection) { return false; }
            var s = SelStart;
            return Apply(s, SelEnd, "", Kind.Other, s, s);
        }

        /// <summary>Tab: without a selection the next tab stop (spaces or a tab character); with a selection over lines every one of them goes in a level
        /// (shift: out). Shift+Tab without a selection takes a level off the line.</summary>
        public bool Tab(bool shift)
        {
            if (!HasSelection && !shift)
            {
                string s = SpacesForTab ? new string(' ', TabSize - VisualCol(lines[Caret.Line], Caret.Col, TabSize) % TabSize) : "\t";
                var end = Advance(Caret, s);
                return Apply(Caret, Caret, s, Kind.Other, end, end);
            }
            return ShiftLines(shift ? -1 : 1);
        }

        /// <summary>The lines the selection touches change their indent by a level (dir 1 in, -1 out); the selection stays on the same text.</summary>
        public bool ShiftLines(int dir)
        {
            var a = SelStart;
            var b = SelEnd;
            int first = a.Line, last = b.Line;
            if (last > first && b.Col == 0) { last--; }
            string unit = SpacesForTab ? new string(' ', TabSize) : "\t";
            var sb = new StringBuilder();
            var deltas = new int[last - first + 1];
            for (int i = first; i <= last; i++)
            {
                string line = lines[i];
                string next;
                if (dir > 0) { next = line.Length == 0 ? line : unit + line; }
                else
                {
                    int cut = 0;
                    if (line.Length > 0 && line[0] == '\t') { cut = 1; }
                    else { while (cut < TabSize && cut < line.Length && line[cut] == ' ') { cut++; } }
                    next = line.Substring(cut);
                }
                deltas[i - first] = next.Length - line.Length;
                if (i > first) { sb.Append('\n'); }
                sb.Append(next);
            }
            var from = new CodePos(first, 0);
            var to = new CodePos(last, lines[last].Length);
            string text = sb.ToString();
            if (text == GetText(from, to)) { return false; }
            CodePos Shift(CodePos p) => p.Line >= first && p.Line <= last ? new CodePos(p.Line, Math.Max(0, p.Col + deltas[p.Line - first])) : p;
            var caret = Shift(Caret);
            var anchor = Shift(Anchor);
            return Apply(from, to, text, Kind.Other, caret, anchor);
        }

        private static bool IsSpaces(string line, int col)
        {
            for (int i = 0; i < col; i++) { if (line[i] != ' ') { return false; } }
            return true;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // ---------------------------------------------------------------- moving

        /// <summary>Moves the caret; extend keeps the anchor (shift), otherwise the selection collapses. keepGoal: a vertical move that remembers its column.</summary>
        public void MoveTo(CodePos p, bool extend, bool keepGoal = false)
        {
            p = Clamp(p);
            if (p == Caret && (extend || Anchor == Caret)) { return; }
            Caret = p;
            if (!extend) { Anchor = p; }
            if (!keepGoal) { goalCol = -1; }
            lastKind = Kind.Other;
            Version++;
        }

        public void GoTo(int line, int col = 0, bool extend = false) => MoveTo(new CodePos(line, col), extend);

        public void Left(bool word, bool extend)
        {
            if (!extend && HasSelection && !word) { MoveTo(SelStart, false); return; }
            if (Caret.Col == 0)
            {
                if (Caret.Line > 0) { MoveTo(new CodePos(Caret.Line - 1, lines[Caret.Line - 1].Length), extend); }
                else { MoveTo(Caret, extend); }
                return;
            }
            MoveTo(new CodePos(Caret.Line, word ? TextEdit.WordLeft(lines[Caret.Line], Caret.Col) : Caret.Col - 1), extend);
        }

        public void Right(bool word, bool extend)
        {
            if (!extend && HasSelection && !word) { MoveTo(SelEnd, false); return; }
            string line = lines[Caret.Line];
            if (Caret.Col >= line.Length)
            {
                if (Caret.Line < lines.Count - 1) { MoveTo(new CodePos(Caret.Line + 1, 0), extend); }
                else { MoveTo(Caret, extend); }
                return;
            }
            MoveTo(new CodePos(Caret.Line, word ? TextEdit.WordRight(line, Caret.Col) : Caret.Col + 1), extend);
        }

        public void Up(bool extend) => Vertical(-1, extend);
        public void Down(bool extend) => Vertical(1, extend);

        /// <summary>PageUp/PageDown: lines up or down (the view's height in lines).</summary>
        public void Page(int lineCount, bool extend) => Vertical(lineCount, extend);

        private void Vertical(int d, bool extend)
        {
            int visual = goalCol >= 0 ? goalCol : VisualCol(lines[Caret.Line], Caret.Col, TabSize);
            int line = Caret.Line + d;
            if (line < 0) { MoveTo(new CodePos(0, 0), extend); goalCol = visual; return; }
            if (line >= lines.Count) { MoveTo(new CodePos(lines.Count - 1, lines[^1].Length), extend); goalCol = visual; return; }
            MoveTo(new CodePos(line, ColFromVisual(lines[line], visual, TabSize)), extend, keepGoal: true);
            goalCol = visual;
        }

        /// <summary>Home: to the first character that is not blank; again: to the start of the line.</summary>
        public void Home(bool extend)
        {
            string line = lines[Caret.Line];
            int first = 0;
            while (first < line.Length && (line[first] == ' ' || line[first] == '\t')) { first++; }
            MoveTo(new CodePos(Caret.Line, Caret.Col == first ? 0 : first), extend);
        }

        public void LineEnd(bool extend) => MoveTo(new CodePos(Caret.Line, lines[Caret.Line].Length), extend);
        public void DocStart(bool extend) => MoveTo(default, extend);
        public void DocEnd(bool extend) => MoveTo(End, extend);

        public void SelectAll()
        {
            Anchor = default;
            Caret = End;
            goalCol = -1;
            lastKind = Kind.Other;
            Version++;
        }

        /// <summary>The word (a run of letters, digits and underscores; or of blanks; or of other characters) around the character at a place, selected.</summary>
        public void SelectWordAt(CodePos p)
        {
            WordAt(p, out var a, out var b);
            Select(a, b);
        }

        /// <summary>The bounds of the word around the character at a place (the last character of a line for a place past its end).</summary>
        public void WordAt(CodePos p, out CodePos start, out CodePos end)
        {
            p = Clamp(p);
            string line = lines[p.Line];
            if (line.Length == 0) { start = end = p; return; }
            int i = Math.Min(p.Col, line.Length - 1);   // the character at the place: the view passes the column under the mouse, not the nearest caret place
            int cls = Class(line[i]), a = i, b = i + 1;
            while (a > 0 && Class(line[a - 1]) == cls) { a--; }
            while (b < line.Length && Class(line[b]) == cls) { b++; }
            start = new CodePos(p.Line, a);
            end = new CodePos(p.Line, b);
        }

        /// <summary>Selects from anchor to caret (either may be the earlier one).</summary>
        public void Select(CodePos anchor, CodePos caret)
        {
            anchor = Clamp(anchor);
            caret = Clamp(caret);
            if (anchor == Anchor && caret == Caret) { return; }
            Anchor = anchor;
            Caret = caret;
            goalCol = -1;
            lastKind = Kind.Other;
            Version++;
        }

        /// <summary>A line with its break (the last line without one): where it starts and ends.</summary>
        public void LineRange(int line, out CodePos start, out CodePos end)
        {
            line = Math.Clamp(line, 0, lines.Count - 1);
            start = new CodePos(line, 0);
            end = line + 1 < lines.Count ? new CodePos(line + 1, 0) : new CodePos(line, lines[line].Length);
        }

        private static int Class(char c) => IsWordChar(c) ? 1 : c is ' ' or '\t' ? 2 : 3;

        /// <summary>A whole line (with its break when there is a next line), selected.</summary>
        public void SelectLineAt(int line)
        {
            LineRange(line, out var a, out var b);
            Select(a, b);
        }
    }
}
