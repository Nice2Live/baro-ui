using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>The editing model of a text field (docs/ui/ENGINE_PLAN.md, stage 5), free of the game so the tests
    /// run it: text, caret, selection (Anchor..Caret), word jumps, a character filter, a length limit and undo.
    /// Every method that changes the text returns true when it did.</summary>
    internal sealed class TextEdit
    {
        private readonly List<(string Text, int Caret)> undo = new();
        public string Text { get; private set; } = "";
        public int Caret { get; private set; }
        public int Anchor { get; private set; }
        public int MaxLength = int.MaxValue;
        /// <summary>Characters that may be typed (null: any printable). Pasted text is filtered the same way.</summary>
        public Func<char, bool> Accept;
        /// <summary>Bumped on every change of text, caret or selection (the painter caches positions by it).</summary>
        public int Version { get; private set; }

        public bool HasSelection => Caret != Anchor;
        public int SelStart => Math.Min(Caret, Anchor);
        public int SelEnd => Math.Max(Caret, Anchor);
        public string Selected => Text.Substring(SelStart, SelEnd - SelStart);

        /// <summary>Replaces the text from outside (the model), caret at the end, no undo step.</summary>
        public void SetText(string text)
        {
            text ??= "";
            if (text.Length > MaxLength) { text = text.Substring(0, MaxLength); }
            if (text == Text) { return; }
            Text = text;
            Caret = Anchor = text.Length;
            Version++;
        }

        public bool Insert(string s)
        {
            if (s == null) { return false; }
            var filtered = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\r' || c == '\n' || c == '\t') { continue; }
                if (char.IsControl(c)) { continue; }
                if (Accept != null && !Accept(c)) { continue; }
                filtered.Append(c);
            }
            int room = MaxLength - (Text.Length - (SelEnd - SelStart));
            string add = filtered.Length > room ? filtered.ToString(0, Math.Max(0, room)) : filtered.ToString();
            if (add.Length == 0 && !HasSelection) { return false; }
            Push();
            int at = SelStart;
            Text = Text.Remove(at, SelEnd - at).Insert(at, add);
            Caret = Anchor = at + add.Length;
            Version++;
            return true;
        }

        /// <summary>Backspace: the selection, else one character (or a word with ctrl) before the caret.</summary>
        public bool Backspace(bool word = false)
        {
            if (HasSelection) { return DeleteSelection(); }
            if (Caret == 0) { return false; }
            int from = word ? WordLeft(Text, Caret) : Caret - 1;
            Push();
            Text = Text.Remove(from, Caret - from);
            Caret = Anchor = from;
            Version++;
            return true;
        }

        /// <summary>Delete: the selection, else one character (or a word with ctrl) after the caret.</summary>
        public bool Delete(bool word = false)
        {
            if (HasSelection) { return DeleteSelection(); }
            if (Caret >= Text.Length) { return false; }
            int to = word ? WordRight(Text, Caret) : Caret + 1;
            Push();
            Text = Text.Remove(Caret, to - Caret);
            Anchor = Caret;
            Version++;
            return true;
        }

        public bool DeleteSelection()
        {
            if (!HasSelection) { return false; }
            Push();
            int at = SelStart;
            Text = Text.Remove(at, SelEnd - at);
            Caret = Anchor = at;
            Version++;
            return true;
        }

        /// <summary>Moves the caret; extend keeps the anchor (shift), otherwise the selection collapses.</summary>
        public void MoveTo(int index, bool extend)
        {
            index = Math.Clamp(index, 0, Text.Length);
            if (index == Caret && (extend || Anchor == Caret)) { return; }
            Caret = index;
            if (!extend) { Anchor = index; }
            Version++;
        }

        public void Left(bool word, bool extend)
        {
            // without shift, a selection collapses to its start, as in browsers
            if (!extend && HasSelection && !word) { MoveTo(SelStart, false); return; }
            MoveTo(word ? WordLeft(Text, Caret) : Caret - 1, extend);
        }

        public void Right(bool word, bool extend)
        {
            if (!extend && HasSelection && !word) { MoveTo(SelEnd, false); return; }
            MoveTo(word ? WordRight(Text, Caret) : Caret + 1, extend);
        }

        public void Home(bool extend) => MoveTo(0, extend);
        public void End(bool extend) => MoveTo(Text.Length, extend);

        public void SelectAll()
        {
            Anchor = 0;
            Caret = Text.Length;
            Version++;
        }

        public bool Undo()
        {
            if (undo.Count == 0) { return false; }
            var (text, caret) = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            Text = text;
            Caret = Anchor = Math.Min(caret, text.Length);
            Version++;
            return true;
        }

        private void Push()
        {
            if (undo.Count > 0 && undo[^1].Text == Text) { return; }
            if (undo.Count >= 64) { undo.RemoveAt(0); }
            undo.Add((Text, Caret));
        }

        /// <summary>Start of the word before i (skips spaces first), like ctrl+left.</summary>
        public static int WordLeft(string s, int i)
        {
            i = Math.Clamp(i, 0, s.Length);
            while (i > 0 && !IsWord(s[i - 1])) { i--; }
            while (i > 0 && IsWord(s[i - 1])) { i--; }
            return i;
        }

        /// <summary>End of the word after i (then the spaces after it), like ctrl+right.</summary>
        public static int WordRight(string s, int i)
        {
            i = Math.Clamp(i, 0, s.Length);
            while (i < s.Length && IsWord(s[i])) { i++; }
            while (i < s.Length && !IsWord(s[i])) { i++; }
            return i;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>The caret index nearest to x, given the width of each prefix (prefixWidth(k) = width of the first k characters).</summary>
        public static int IndexAt(string s, float x, Func<int, float> prefixWidth)
        {
            if (x <= 0 || s.Length == 0) { return 0; }
            float prev = 0;
            for (int k = 1; k <= s.Length; k++)
            {
                float w = prefixWidth(k);
                if (x < w) { return x - prev < w - x ? k - 1 : k; }
                prev = w;
            }
            return s.Length;
        }
    }

    /// <summary>input type=number: parse with '.' or ',', clamp to min/max, snap to step from min.</summary>
    internal static class NumberField
    {
        public static bool Accept(char c) => char.IsDigit(c) || c is '-' or '.' or ',' or '+' or 'e' or 'E';

        public static bool TryParse(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        public static double Clamp(double v, double? min, double? max, double? step)
        {
            if (step is > 0)
            {
                double basis = min ?? 0;
                v = basis + Math.Round((v - basis) / step.Value) * step.Value;
                v = Math.Round(v, 10);
            }
            if (min.HasValue && v < min.Value) { v = min.Value; }
            if (max.HasValue && v > max.Value) { v = max.Value; }
            return v;
        }

        public static string Format(double v) => v.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>One step of a number field (an arrow key or a wheel notch): the value of text (min, or 0, when it is not a number) plus dir steps of
        /// step (1 without one), snapped onto the grid from min and clamped to min..max. Returns the text of the new value.</summary>
        public static string Stepped(string text, int dir, double? min, double? max, double? step)
        {
            double s = step ?? 1;
            double v = TryParse(text, out double cur) ? cur : min ?? 0;
            return Format(Clamp(v + dir * s, min, max, s));
        }

        public static double? Attr(Element e, string name) =>
            e.GetAttribute(name) is { } s && TryParse(s, out double v) ? v : null;
    }

    /// <summary>input type=range: min (0), max (100), step (1, or "any" for no grid), value (the middle when absent). The thumb's place on the track
    /// is a fraction 0..1 of the way between min and max; the value read from a fraction snaps to the step grid that starts at min.</summary>
    internal static class RangeField
    {
        public static double Min(Element e) => NumberField.Attr(e, "min") ?? 0;

        public static double Max(Element e)
        {
            double min = Min(e);
            double max = NumberField.Attr(e, "max") ?? 100;
            return max < min ? min : max;
        }

        /// <summary>The grid step; null: any value (step="any").</summary>
        public static double? Step(Element e)
        {
            string s = e.GetAttribute("step");
            if (s == null) { return 1; }
            if (s.Trim().Equals("any", StringComparison.OrdinalIgnoreCase)) { return null; }
            return NumberField.TryParse(s, out double v) && v > 0 ? v : 1;
        }

        /// <summary>One arrow key: the step, or a hundredth of the track without a grid.</summary>
        public static double Unit(Element e) => Step(e) ?? (Max(e) - Min(e)) / 100;

        /// <summary>Into the range, then onto the grid that starts at min; a grid point past max (max is not on the grid) steps back, as in HTML.</summary>
        public static double Snap(double v, double min, double max, double? step)
        {
            v = Math.Clamp(v, min, max);
            if (step is not > 0) { return v; }
            v = min + Math.Round((v - min) / step.Value) * step.Value;
            if (v > max) { v -= step.Value; }
            return Math.Round(v, 10);
        }

        public static double Value(Element e)
        {
            double min = Min(e), max = Max(e);
            return Snap(NumberField.Attr(e, "value") ?? min + (max - min) / 2, min, max, Step(e));
        }

        public static double Fraction(double value, double min, double max) =>
            max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;

        public static double FromFraction(double fraction, double min, double max, double? step) =>
            Snap(min + Math.Clamp(fraction, 0, 1) * (max - min), min, max, step);
    }
}
