using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace BaroUI.Core
{
    /// <summary>The data side of the charts without the game (docs/ui/DESIGN_CHARTS.md 2.5): the model values read into reused buffers, so a steady frame
    /// allocates nothing. Each reader says whether the content changed since its last read (the change is the revision the chart works from), and writes
    /// a changed curve back in the shape it was read in. The tests run these; the window only calls them.</summary>
    internal sealed class NumberSeries
    {
        private double[] values = Array.Empty<double>();

        /// <summary>The numbers of the last read (the first Count entries of <see cref="Values"/>).</summary>
        public int Count { get; private set; }
        public double[] Values => values;

        /// <summary>Reads the numbers of a model value: a list or an array of numbers (other items are skipped; a string or null gives none). Returns true
        /// when the numbers differ from the last read. Lists of double and arrays of double are read without boxing; an IList of boxed numbers without
        /// a copy; any other IEnumerable through one enumerator.</summary>
        public bool Read(object model)
        {
            int n = 0;
            bool changed = false;
            switch (model)
            {
                case null:
                case string:
                    break;
                case double[] arr:
                    for (int i = 0; i < arr.Length; i++) { changed |= Put(ref n, arr[i]); }
                    break;
                case List<double> list:
                    for (int i = 0; i < list.Count; i++) { changed |= Put(ref n, list[i]); }
                    break;
                case IList list:
                    for (int i = 0; i < list.Count; i++)
                    {
                        object item = list[i];
                        if (Expr.IsNumber(item)) { changed |= Put(ref n, Expr.ToNumber(item)); }
                    }
                    break;
                case IEnumerable en:
                    foreach (var item in en)
                    {
                        if (Expr.IsNumber(item)) { changed |= Put(ref n, Expr.ToNumber(item)); }
                    }
                    break;
            }
            if (n != Count) { changed = true; }
            Count = n;
            return changed;
        }

        private bool Put(ref int n, double v)
        {
            if (n >= values.Length) { Array.Resize(ref values, Math.Max(8, values.Length * 2)); }
            bool same = n < Count && (values[n] == v || (double.IsNaN(v) && double.IsNaN(values[n])));
            values[n++] = v;
            return !same;
        }
    }

    /// <summary>The points of a curve read from a model value: a list or an array whose items are points: a DPt, a (double, double) tuple, a pair (an
    /// array or a list of two numbers), a dictionary with x and y keys, or an object with X and Y members. Reads into reused buffers; Write puts edited
    /// points back in the same shape.</summary>
    internal sealed class CurveSource
    {
        private double[] xs = Array.Empty<double>(), ys = Array.Empty<double>();

        /// <summary>The points of the last read.</summary>
        public int Count { get; private set; }
        /// <summary>False when the last read met an item that is not a point (the curve then shows nothing).</summary>
        public bool Valid { get; private set; }
        public double X(int i) => xs[i];
        public double Y(int i) => ys[i];
        /// <summary>The buffers of the last read: the first Count entries are the points (the editor reads them in place).</summary>
        public double[] Xs => xs;
        public double[] Ys => ys;

        /// <summary>Reads the points; true when they differ from the last read (or the validity changed). A value that is not a list gives no points.</summary>
        public bool Read(object model)
        {
            int n = 0;
            bool ok = model is IList || model is Array, changed = false;
            // typed lists and arrays of the value types are read without boxing each item
            if (model is DPt[] pts)
            {
                for (int i = 0; i < pts.Length && ok; i++) { ok = PutFinite(ref n, pts[i].X, pts[i].Y, ref changed); }
            }
            else if (model is List<DPt> dpl)
            {
                for (int i = 0; i < dpl.Count && ok; i++) { ok = PutFinite(ref n, dpl[i].X, dpl[i].Y, ref changed); }
            }
            else if (model is List<ValueTuple<double, double>> tl)
            {
                for (int i = 0; i < tl.Count && ok; i++) { ok = PutFinite(ref n, tl[i].Item1, tl[i].Item2, ref changed); }
            }
            else if (model is IList list)
            {
                for (int i = 0; i < list.Count && ok; i++) { ok = Put(ref n, list[i], ref changed); }
            }
            if (ok && n != Count) { changed = true; }
            if (!ok) { n = 0; }
            if (ok != Valid) { changed = true; }
            Count = n;
            Valid = ok;
            return changed;
        }

        private bool Put(ref int n, object item, ref bool changed)
        {
            if (!TryReadPoint(item, out double x, out double y)) { return false; }
            Put(ref n, x, y, ref changed);
            return true;
        }

        private bool PutFinite(ref int n, double x, double y, ref bool changed)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) { return false; }
            Put(ref n, x, y, ref changed);
            return true;
        }

        private void Put(ref int n, double x, double y, ref bool changed)
        {
            if (n >= xs.Length)
            {
                Array.Resize(ref xs, Math.Max(8, xs.Length * 2));
                Array.Resize(ref ys, xs.Length);
            }
            if (n >= Count || xs[n] != x || ys[n] != y) { changed = true; }
            xs[n] = x; ys[n] = y;
            n++;
        }

        /// <summary>One point of a model item: a DPt, a (double, double) tuple, a pair, a dictionary with x and y, or an object with X and Y numbers.
        /// Finite numbers only. The shapes first in the list are read without boxing; an object with members goes through reflection.</summary>
        public static bool TryReadPoint(object item, out double x, out double y)
        {
            x = y = 0;
            switch (item)
            {
                case DPt p:
                    x = p.X; y = p.Y;
                    break;
                case ValueTuple<double, double> t:
                    x = t.Item1; y = t.Item2;
                    break;
                case object[] pair when pair.Length >= 2:
                    if (!Expr.IsNumber(pair[0]) || !Expr.IsNumber(pair[1])) { return false; }
                    x = Expr.ToNumber(pair[0]); y = Expr.ToNumber(pair[1]);
                    break;
                case IList pl when pl.Count >= 2:
                    if (!Expr.IsNumber(pl[0]) || !Expr.IsNumber(pl[1])) { return false; }
                    x = Expr.ToNumber(pl[0]); y = Expr.ToNumber(pl[1]);
                    break;
                case IDictionary<string, object> d:
                    if (!Number(d, "x", "X", out x) || !Number(d, "y", "Y", out y)) { return false; }
                    break;
                case null:
                    return false;
                default:
                {
                    var type = item.GetType();
                    if (!Member(item, type, "X", out x) || !Member(item, type, "Y", out y)) { return false; }
                    break;
                }
            }
            return double.IsFinite(x) && double.IsFinite(y);
        }

        private static bool Number(IDictionary<string, object> d, string lower, string upper, out double v)
        {
            v = 0;
            object raw = d.TryGetValue(lower, out var a) ? a : d.TryGetValue(upper, out var b) ? b : null;
            if (!Expr.IsNumber(raw)) { return false; }
            v = Expr.ToNumber(raw);
            return true;
        }

        private static bool Member(object item, Type type, string name, out double v)
        {
            v = 0;
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            object raw = p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(item) : type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            if (!Expr.IsNumber(raw)) { return false; }
            v = Expr.ToNumber(raw);
            return true;
        }

        /// <summary>The model value with the points written back in its own shape. A List or an array of points keeps its type: edited in place (an array
        /// of another length gives a new array of the same element type; a list grows and shrinks). Returns the value to store (the same object when it
        /// was edited in place), or null when the shape cannot take the points (a fixed-size list of another length, an unknown element type).</summary>
        public static object Write(object model, IReadOnlyList<DPt> pts)
        {
            if (model is Array arr)
            {
                if (arr.Length == pts.Count)
                {
                    for (int i = 0; i < pts.Count; i++)
                    {
                        object next = WithPoint(arr.GetValue(i), pts[i]);
                        if (next == null) { return null; }
                        arr.SetValue(next, i);
                    }
                    return arr;
                }
                var elem = arr.GetType().GetElementType();
                var grown = Array.CreateInstance(elem, pts.Count);
                for (int i = 0; i < pts.Count; i++)
                {
                    object next = i < arr.Length ? WithPoint(arr.GetValue(i), pts[i]) : NewPoint(elem, pts[i]);
                    if (next == null) { return null; }
                    grown.SetValue(next, i);
                }
                return grown;
            }
            if (model is IList list)
            {
                if (list.IsFixedSize && list.Count != pts.Count) { return null; }
                Type elemType = ElementType(list);
                while (list.Count > pts.Count) { list.RemoveAt(list.Count - 1); }
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i < list.Count)
                    {
                        object next = WithPoint(list[i], pts[i]);
                        if (next == null) { return null; }
                        list[i] = next;
                    }
                    else
                    {
                        object next = NewPoint(elemType, pts[i]);
                        if (next == null) { return null; }
                        list.Add(next);
                    }
                }
                return list;
            }
            return null;
        }

        private static Type ElementType(IList list)
        {
            var t = list.GetType();
            while (t != null)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { return t.GetGenericArguments()[0]; }
                t = t.BaseType;
            }
            return typeof(object);
        }

        /// <summary>The item with its point replaced, in its own shape (a value type is returned as a new box; a dictionary or an object is edited).</summary>
        private static object WithPoint(object item, DPt p)
        {
            switch (item)
            {
                case DPt: return p;
                case ValueTuple<double, double>: return (p.X, p.Y);
                case object[] pair when pair.Length >= 2: pair[0] = p.X; pair[1] = p.Y; return pair;
                case IList pl when pl.Count >= 2 && !pl.IsReadOnly: pl[0] = p.X; pl[1] = p.Y; return pl;
                case IDictionary<string, object> d:
                    Put(d, "x", "X", p.X);
                    Put(d, "y", "Y", p.Y);
                    return d;
                case null: return null;
                default:
                {
                    var type = item.GetType();
                    object box = item;
                    return SetMember(ref box, type, "X", p.X) && SetMember(ref box, type, "Y", p.Y) ? box : null;
                }
            }
        }

        private static void Put(IDictionary<string, object> d, string lower, string upper, double v)
        {
            if (d.ContainsKey(upper) && !d.ContainsKey(lower)) { d[upper] = v; } else { d[lower] = v; }
        }

        private static bool SetMember(ref object box, Type type, string name, double v)
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0)
            {
                p.SetValue(box, Convert.ChangeType(v, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) { return false; }
            f.SetValue(box, Convert.ChangeType(v, f.FieldType, System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>A new item for an element type of a list or array, when a point is added.</summary>
        private static object NewPoint(Type elem, DPt p)
        {
            if (elem == typeof(DPt)) { return p; }
            if (elem == typeof(ValueTuple<double, double>)) { return (p.X, p.Y); }
            if (elem == typeof(object)) { return new object[] { p.X, p.Y }; }
            return null;
        }
    }

    /// <summary>The last value of a key (a change check): Update says whether the pair differs from the last one and keeps it. The charts key their
    /// derived text and geometry on it (the hovered index with the data revision, the tick scale, the zones), so an unchanged frame rebuilds nothing.</summary>
    internal struct ChangeKey
    {
        private long a, b;
        private bool set;

        public bool Update(long x, long y)
        {
            if (set && a == x && b == y) { return false; }
            a = x; b = y; set = true;
            return true;
        }

        public void Reset() => set = false;
    }

    /// <summary>The labels of a set of axis ticks, rebuilt only when the ticks or the format change (a steady scale allocates no strings).</summary>
    internal sealed class TickLabels
    {
        private readonly List<double> key = new();
        private string format;
        private bool made;
        public readonly List<string> Text = new();

        /// <summary>Makes the labels for the ticks by the format; true when they were made again.</summary>
        public bool Ensure(IReadOnlyList<double> ticks, string fmt)
        {
            if (made && format == fmt && key.Count == ticks.Count)
            {
                bool same = true;
                for (int i = 0; i < ticks.Count && same; i++) { same = key[i] == ticks[i]; }
                if (same) { return false; }
            }
            made = true;
            format = fmt;
            key.Clear();
            for (int i = 0; i < ticks.Count; i++) { key.Add(ticks[i]); }
            Text.Clear();
            for (int i = 0; i < ticks.Count; i++) { Text.Add(ChartScale.Label(ticks[i], fmt)); }
            return true;
        }
    }

    /// <summary>One value's label (the gauge's reading): made again only when the value (to the last bit) or the format changes.</summary>
    internal sealed class ValueLabel
    {
        private double value;
        private string format, text;

        public string Text => text;

        /// <summary>Returns the label of v; a new string only when v or the format differ from the last one.</summary>
        public string Of(double v, string fmt)
        {
            if (text != null && value == v && format == fmt) { return text; }
            value = v; format = fmt;
            text = ChartScale.Label(v, fmt);
            return text;
        }
    }

    /// <summary>The category labels of a chart's x axis (x-labels="model.path"): the items' texts, rebuilt only when an item changes. Reads into reused
    /// lists; the texts of a steady frame cost no allocation.</summary>
    internal sealed class CategoryLabels
    {
        private object[] snap = Array.Empty<object>();
        private int count;
        public readonly List<string> Text = new();

        public int Count => count;

        /// <summary>Reads the labels of a model value (a list; a string or null gives none). True when they changed.</summary>
        public bool Read(object model)
        {
            var list = model as IList;
            int n = list == null || model is string ? 0 : list.Count;
            bool changed = n != count;
            for (int i = 0; i < n && !changed; i++)
            {
                if (!Equals(list[i], snap[i])) { changed = true; }
            }
            if (!changed) { return false; }
            if (snap.Length < n) { snap = new object[n]; }
            Text.Clear();
            for (int i = 0; i < n; i++)
            {
                object item = list[i];
                snap[i] = item;
                Text.Add(Expr.ToText(item));
            }
            count = n;
            return true;
        }
    }
}
