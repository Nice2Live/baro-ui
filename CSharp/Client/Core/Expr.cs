using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace BaroUI.Core
{
    /// <summary>Template expressions (docs/ui/ENGINE_PLAN.md, stage 4): paths a.b[0].c, numbers, 'strings',
    /// true/false/null, this, $index, + - * / %, comparisons, &amp;&amp; || !, parentheses. No calls, no assignment:
    /// a template reads data, handlers (C#) change it. Numbers are doubles; comparisons of other values use Equals.</summary>
    public abstract class Expr
    {
        public abstract object Eval(Context ctx);

        /// <summary>Where names come from: loop variables up the element tree, then the data object.</summary>
        public sealed class Context
        {
            public object Data;
            public Element Element;
            public Action<string> Warn;
            /// <summary>The argument of the event being dispatched, read as $arg (onpick: the picked index). Null outside an event.</summary>
            public object Arg;
        }

        /// <summary>A data-for copy's variables: item, $index; chained to the enclosing loop.</summary>
        public sealed class LoopScope
        {
            public string Name;
            public object Value;
            public int Index;
            public LoopScope Outer;
        }

        // ---------------------------------------------------------------- values

        public static bool Truthy(object v) => v switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ when IsNumber(v) => ToNumber(v) != 0,
            _ => true,
        };

        public static bool IsNumber(object v) => v is double or float or int or long or short or byte or sbyte or uint or ulong or ushort or decimal;

        public static double ToNumber(object v) => v switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN,
            _ when IsNumber(v) => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            _ => double.NaN,
        };

        private static readonly object True = true, False = false;
        /// <summary>Booleans without a new box per evaluation.</summary>
        internal static object Box(bool b) => b ? True : False;

        /// <summary>ToText into a builder: numbers are formatted in place, no string per value (stage 7).</summary>
        public static void AppendText(System.Text.StringBuilder sb, object v, string format = null)
        {
            switch (v)
            {
                case null: return;
                case string s: sb.Append(s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case ISpanFormattable f:
                {
                    Span<char> buf = stackalloc char[64];
                    string fmt = format ?? (v is double or float ? "0.######" : null);
                    try
                    {
                        if (f.TryFormat(buf, out int n, fmt, CultureInfo.InvariantCulture)) { sb.Append(buf.Slice(0, n)); return; }
                    }
                    catch (FormatException) { }
                    break;
                }
            }
            sb.Append(ToText(v, format));
        }

        public static string ToText(object v, string format = null)
        {
            if (v == null) { return ""; }
            if (format != null && v is IFormattable f)
            {
                try { return f.ToString(format, CultureInfo.InvariantCulture); } catch (FormatException) { }
            }
            return v switch
            {
                bool b => b ? "true" : "false",
                double d => d.ToString("0.######", CultureInfo.InvariantCulture),
                float x => x.ToString("0.######", CultureInfo.InvariantCulture),
                IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
                _ => RichText.Plain(v.ToString()),
            };
        }

        private static readonly Dictionary<(Type, string), Func<object, object>> getters = new();

        /// <summary>A property read through a typed delegate: a value-type result is boxed only when it differs
        /// from the caller's last one, so a binding that re-reads an unchanged number makes no garbage (stage 7).</summary>
        private abstract class TypedGetter
        {
            public abstract object Get(object o, ref object last);
        }

        private sealed class TypedGetter<TObj, TVal> : TypedGetter
        {
            private readonly Func<TObj, TVal> f;
            public TypedGetter(System.Reflection.MethodInfo m) { f = (Func<TObj, TVal>)Delegate.CreateDelegate(typeof(Func<TObj, TVal>), m); }
            public TypedGetter(System.Reflection.FieldInfo field)
            {
                var o = System.Linq.Expressions.Expression.Parameter(typeof(TObj), "o");
                f = System.Linq.Expressions.Expression.Lambda<Func<TObj, TVal>>(System.Linq.Expressions.Expression.Field(o, field), o).Compile();
            }
            public override object Get(object o, ref object last)
            {
                TVal v = f((TObj)o);
                if (last is TVal l && EqualityComparer<TVal>.Default.Equals(l, v)) { return last; }
                object boxed = v;
                last = boxed;
                return boxed;
            }
        }

        private static readonly Dictionary<(Type, string), TypedGetter> typed = new();

        /// <summary>As Member, reusing last (the caller's previous result) when an unchanged number or bool is read.</summary>
        public static object Member(object obj, string name, Action<string> warn, ref object last)
        {
            if (obj == null || obj is IDictionary<string, object> || obj is IDictionary) { return Member(obj, name, warn); }
            var type = obj.GetType();
            if (!typed.TryGetValue((type, name), out var tg))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                var p = type.IsValueType ? null : type.GetProperty(name, flags);
                var get = p?.GetGetMethod();
                var field = type.IsValueType || p != null ? null : type.GetField(name, flags);
                tg = get != null && p.GetIndexParameters().Length == 0 && p.PropertyType.IsValueType
                    ? (TypedGetter)Activator.CreateInstance(typeof(TypedGetter<,>).MakeGenericType(type, p.PropertyType), get)
                    : field != null && field.FieldType.IsValueType
                    ? (TypedGetter)Activator.CreateInstance(typeof(TypedGetter<,>).MakeGenericType(type, field.FieldType), field)
                    : null;
                typed[(type, name)] = tg;
            }
            return tg != null ? tg.Get(obj, ref last) : Member(obj, name, warn);
        }

        /// <summary>A number as an object, reusing last when it is the same value.</summary>
        internal static object Num(ref object last, double v)
        {
            if (last is double d && d.Equals(v)) { return last; }
            object boxed = v;
            last = boxed;
            return boxed;
        }

        /// <summary>obj.name: dictionaries by key, then a public property or field (case-insensitive), then
        /// length/count of a collection. Missing: null (and a warning).</summary>
        public static object Member(object obj, string name, Action<string> warn)
        {
            switch (obj)
            {
                case null: return null;
                case IDictionary<string, object> d: return d.TryGetValue(name, out var v) ? v : null;
                case IDictionary nd: return nd.Contains(name) ? nd[name] : null;
            }
            var type = obj.GetType();
            if (!getters.TryGetValue((type, name), out var get))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                var p = type.GetProperty(name, flags);
                var f = p == null ? type.GetField(name, flags) : null;
                if (p != null && p.GetIndexParameters().Length == 0) { get = o => p.GetValue(o); }
                else if (f != null) { get = o => f.GetValue(o); }
                else if (name is "length" or "count" && obj is ICollection) { get = o => ((ICollection)o).Count; }
                getters[(type, name)] = get;
            }
            if (get == null) { warn?.Invoke($"'{name}' is not a member of {type.Name}"); return null; }
            return get(obj);
        }

        public static object Index(object obj, object key)
        {
            switch (obj)
            {
                case IList l when IsNumber(key):
                    int i = (int)ToNumber(key);
                    return i >= 0 && i < l.Count ? l[i] : null;
                case IDictionary<string, object> d when key != null: return d.TryGetValue(ToText(key), out var v) ? v : null;
                case IDictionary nd when key != null: return nd.Contains(key) ? nd[key] : null;
                case string s when IsNumber(key):
                    int k = (int)ToNumber(key);
                    return k >= 0 && k < s.Length ? s[k].ToString() : null;
            }
            return null;
        }

        // ---------------------------------------------------------------- nodes

        internal sealed class Literal : Expr
        {
            public readonly object Value;
            public Literal(object v) { Value = v; }
            public override object Eval(Context ctx) => Value;
        }

        internal sealed class Name : Expr
        {
            public readonly string Id;
            private object last;
            public Name(string id) { Id = id; }

            public override object Eval(Context ctx)
            {
                if (Id == "this") { return ctx.Element; }
                if (Id == "$arg") { return ctx.Arg; }
                for (var e = ctx.Element; e != null; e = e.Parent)
                {
                    for (var s = e.Scope as LoopScope; s != null; s = s.Outer)
                    {
                        if (Id == s.Name) { return s.Value; }
                        if (Id == "$index") { return Num(ref last, s.Index); }
                    }
                    if (e.Scope != null) { break; }   // the nearest scope holds the whole chain
                }
                return Member(ctx.Data, Id, ctx.Warn, ref last);
            }
        }

        internal sealed class Get : Expr
        {
            public readonly Expr Target;
            public readonly string Member_;
            public readonly Expr Key;
            public Get(Expr target, string member, Expr key) { Target = target; Member_ = member; Key = key; }
            private object last;

            public override object Eval(Context ctx)
            {
                var t = Target.Eval(ctx);
                return Key != null ? Index(t, Key.Eval(ctx)) : Member(t, Member_, ctx.Warn, ref last);
            }
        }

        internal sealed class Unary : Expr
        {
            public readonly char Op;
            public readonly Expr A;
            public Unary(char op, Expr a) { Op = op; A = a; }
            private object last;
            public override object Eval(Context ctx) => Op == '!' ? Box(!Truthy(A.Eval(ctx))) : Num(ref last, -ToNumber(A.Eval(ctx)));
        }

        internal sealed class Binary : Expr
        {
            public readonly string Op;
            public readonly Expr A, B;
            public Binary(string op, Expr a, Expr b) { Op = op; A = a; B = b; }
            private object last;

            public override object Eval(Context ctx)
            {
                if (Op == "&&") { var l = A.Eval(ctx); return Truthy(l) ? B.Eval(ctx) : l; }
                if (Op == "||") { var l = A.Eval(ctx); return Truthy(l) ? l : B.Eval(ctx); }
                object x = A.Eval(ctx), y = B.Eval(ctx);
                switch (Op)
                {
                    case "+" when x is string || y is string: return ToText(x) + ToText(y);
                    case "+": return Num(ref last, ToNumber(x) + ToNumber(y));
                    case "-": return Num(ref last, ToNumber(x) - ToNumber(y));
                    case "*": return Num(ref last, ToNumber(x) * ToNumber(y));
                    case "/": return Num(ref last, ToNumber(x) / ToNumber(y));
                    case "%": return Num(ref last, ToNumber(x) % ToNumber(y));
                    case "==": return Box(Same(x, y));
                    case "!=": return Box(!Same(x, y));
                }
                int c = IsNumber(x) || IsNumber(y) ? ToNumber(x).CompareTo(ToNumber(y)) : string.CompareOrdinal(ToText(x), ToText(y));
                return Box(Op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 });
            }

            private static bool Same(object x, object y)
            {
                if (x == null || y == null) { return x == null && y == null; }
                if (IsNumber(x) || IsNumber(y)) { return ToNumber(x) == ToNumber(y); }
                if (x is Enum || y is Enum) { return ToText(x) == ToText(y); }
                return x.Equals(y);
            }
        }

        // ---------------------------------------------------------------- parser

        /// <summary>Parses one expression; null and an error message when it is not one.</summary>
        public static Expr Parse(string text, out string error)
        {
            var p = new Parser(text);
            try
            {
                var e = p.Or();
                p.SkipSpace();
                if (!p.End) { throw new FormatException($"unexpected '{text.Substring(p.Pos)}'"); }
                error = null;
                return e;
            }
            catch (FormatException ex)
            {
                error = $"bad expression '{text}': {ex.Message}";
                return null;
            }
        }

        /// <summary>A handler call: name or name(arg, ...). Args are expressions.</summary>
        public static bool ParseCall(string text, out string name, out List<Expr> args, out string error)
        {
            var p = new Parser(text);
            args = new List<Expr>();
            name = null;
            try
            {
                p.SkipSpace();
                name = p.Ident() ?? throw new FormatException("a handler name expected");
                p.SkipSpace();
                if (p.Eat("("))
                {
                    p.SkipSpace();
                    if (!p.Eat(")"))
                    {
                        do { args.Add(p.Or()); p.SkipSpace(); } while (p.Eat(","));
                        if (!p.Eat(")")) { throw new FormatException("')' expected"); }
                    }
                }
                p.SkipSpace();
                if (!p.End) { throw new FormatException($"unexpected '{text.Substring(p.Pos)}'"); }
                error = null;
                return true;
            }
            catch (FormatException ex)
            {
                error = $"bad handler call '{text}': {ex.Message}";
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string s;
            public int Pos;
            public Parser(string text) { s = text ?? ""; }
            public bool End => Pos >= s.Length;

            public void SkipSpace() { while (Pos < s.Length && char.IsWhiteSpace(s[Pos])) { Pos++; } }

            public bool Eat(string token)
            {
                SkipSpace();
                if (string.CompareOrdinal(s, Pos, token, 0, token.Length) != 0) { return false; }
                Pos += token.Length;
                return true;
            }

            public string Ident()
            {
                SkipSpace();
                int start = Pos;
                while (Pos < s.Length && (char.IsLetterOrDigit(s[Pos]) || s[Pos] is '_' or '$') && (Pos > start || !char.IsDigit(s[Pos]))) { Pos++; }
                return Pos > start ? s.Substring(start, Pos - start) : null;
            }

            public Expr Or()
            {
                var a = And();
                while (Eat("||")) { a = new Binary("||", a, And()); }
                return a;
            }

            private Expr And()
            {
                var a = Compare();
                while (Eat("&&")) { a = new Binary("&&", a, Compare()); }
                return a;
            }

            private Expr Compare()
            {
                var a = Add();
                foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
                {
                    if (Eat(op)) { return new Binary(op, a, Add()); }
                }
                return a;
            }

            private Expr Add()
            {
                var a = Mul();
                while (true)
                {
                    if (Eat("+")) { a = new Binary("+", a, Mul()); }
                    else if (Eat("-")) { a = new Binary("-", a, Mul()); }
                    else { return a; }
                }
            }

            private Expr Mul()
            {
                var a = Unary_();
                while (true)
                {
                    if (Eat("*")) { a = new Binary("*", a, Unary_()); }
                    else if (Eat("/")) { a = new Binary("/", a, Unary_()); }
                    else if (Eat("%")) { a = new Binary("%", a, Unary_()); }
                    else { return a; }
                }
            }

            private Expr Unary_()
            {
                if (Eat("!")) { return new Unary('!', Unary_()); }
                if (Eat("-")) { return new Unary('-', Unary_()); }
                return Postfix(Primary());
            }

            private Expr Postfix(Expr e)
            {
                while (true)
                {
                    if (Eat("."))
                    {
                        e = new Get(e, Ident() ?? throw new FormatException("a member name expected after '.'"), null);
                    }
                    else if (Eat("["))
                    {
                        var key = Or();
                        if (!Eat("]")) { throw new FormatException("']' expected"); }
                        e = new Get(e, null, key);
                    }
                    else { return e; }
                }
            }

            private Expr Primary()
            {
                SkipSpace();
                if (End) { throw new FormatException("unexpected end"); }
                char c = s[Pos];
                if (c == '(')
                {
                    Pos++;
                    var e = Or();
                    if (!Eat(")")) { throw new FormatException("')' expected"); }
                    return e;
                }
                if (c is '\'' or '"')
                {
                    int end = s.IndexOf(c, Pos + 1);
                    if (end < 0) { throw new FormatException("unclosed string"); }
                    var lit = new Literal(s.Substring(Pos + 1, end - Pos - 1));
                    Pos = end + 1;
                    return lit;
                }
                if (char.IsDigit(c) || (c == '.' && Pos + 1 < s.Length && char.IsDigit(s[Pos + 1])))
                {
                    int start = Pos;
                    while (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] == '.')) { Pos++; }
                    if (!double.TryParse(s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        throw new FormatException("bad number");
                    }
                    return new Literal(d);
                }
                string id = Ident() ?? throw new FormatException($"unexpected '{c}'");
                return id switch
                {
                    "true" => new Literal(true),
                    "false" => new Literal(false),
                    "null" => new Literal(null),
                    _ => new Name(id),
                };
            }
        }
    }
}
