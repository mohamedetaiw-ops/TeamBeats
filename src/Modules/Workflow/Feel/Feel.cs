using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace TeamBeats.Modules.Workflow.Feel;

/// <summary>A FEEL duration: years and months, or days and time.</summary>
public readonly record struct FeelDuration(int Months, TimeSpan Time)
{
    public override string ToString() => Months != 0
        ? $"P{(Months < 0 ? "-" : "")}{Math.Abs(Months) / 12}Y{Math.Abs(Months) % 12}M"
        : XmlConvert.ToString(Time);
}

/// <summary>A FEEL range such as <c>[1..10)</c>.</summary>
public sealed record FeelRange(object? Low, bool LowClosed, object? High, bool HighClosed);

/// <summary>An evaluation that went over its limits, called a function wrongly, or met values it cannot combine.</summary>
public sealed class FeelEvaluationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The FEEL subset of spec 024 (FR-005), evaluated in a sandbox: values are numbers, strings, booleans, dates, date-times,
/// durations, lists, contexts and ranges; names read only the variables given; functions are the fixed list in
/// <see cref="FeelParser.Functions"/>. There is no reflection, no host object, no I/O. Each evaluation has a step budget and a
/// time limit, and a result over the size limit fails. Comparisons with null give null, which tests treat as false.
/// </summary>
public static class FeelEngine
{
    public const int MaxSteps = 20_000;
    public static readonly TimeSpan MaxTime = TimeSpan.FromMilliseconds(50);
    public const int MaxCollection = 10_000;
    public const int MaxString = 256 * 1024;

    /// <summary>Checks that an expression parses; returns the problem code or null.</summary>
    public static string? CheckExpression(string text) => Check(() => FeelParser.ParseExpression(text));

    /// <summary>Checks that the unary tests of a decision table cell parse; returns the problem code or null.</summary>
    public static string? CheckUnaryTests(string text) => Check(() => FeelParser.ParseUnaryTests(text));

    private static string? Check(Func<Node> parse)
    {
        try
        {
            parse();
            return null;
        }
        catch (FeelSyntaxException ex)
        {
            return ex.Message;
        }
    }

    public static object? Evaluate(string expression, IReadOnlyDictionary<string, object?> variables, DateTimeOffset now) =>
        new Evaluator(variables, now).Run(FeelParser.ParseExpression(expression));

    /// <summary>Whether <paramref name="input"/> passes the unary tests (a DMN input cell).</summary>
    public static bool Test(string unaryTests, object? input, IReadOnlyDictionary<string, object?> variables, DateTimeOffset now) =>
        new Evaluator(variables, now, input).Run(FeelParser.ParseUnaryTests(unaryTests)) is true;

    /// <summary>Evaluates a condition: only <c>true</c> passes; null or false do not.</summary>
    public static bool Condition(string expression, IReadOnlyDictionary<string, object?> variables, DateTimeOffset now) =>
        Evaluate(expression, variables, now) is true;

    /// <summary>Converts JSON variables to FEEL values. Numbers become decimals; strings stay strings.</summary>
    public static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(p => p.Key, p => FromJson(p.Value), StringComparer.Ordinal),
        JsonArray a => a.Select(FromJson).ToList(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<decimal>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => null,
    };

    public static Dictionary<string, object?> Variables(JsonObject? json) =>
        json is null ? new(StringComparer.Ordinal) : (Dictionary<string, object?>)FromJson(json)!;

    /// <summary>Converts a FEEL value to JSON for instance variables: dates and durations become ISO 8601 strings.</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        decimal d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTimeOffset t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
        FeelDuration d => JsonValue.Create(d.ToString()),
        IList<object?> l => new JsonArray(l.Select(ToJson).ToArray()),
        IDictionary<string, object?> c => new JsonObject(c.Select(p => KeyValuePair.Create(p.Key, ToJson(p.Value)))),
        FeelRange r => JsonValue.Create($"{(r.LowClosed ? "[" : "(")}{ToJson(r.Low)}..{ToJson(r.High)}{(r.HighClosed ? "]" : ")")}"),
        _ => throw new FeelEvaluationException("unsupported_value"),
    };

    private sealed class Evaluator(IReadOnlyDictionary<string, object?> variables, DateTimeOffset now, object? input = null)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int steps;

        public object? Run(Node node)
        {
            var result = Eval(node);
            if (Size(result, 0) > MaxString)
            {
                throw new FeelEvaluationException("result_too_large");
            }
            return result;
        }

        private static int Size(object? value, int depth) => depth > 32 ? MaxString + 1 : value switch
        {
            string s => s.Length,
            IList<object?> l => l.Sum(v => Size(v, depth + 1) + 1),
            IDictionary<string, object?> c => c.Sum(p => p.Key.Length + Size(p.Value, depth + 1) + 1),
            _ => 8,
        };

        private void Tick()
        {
            if (++steps > MaxSteps || (steps % 256 == 0 && clock.Elapsed > MaxTime))
            {
                throw new FeelEvaluationException("limit");
            }
        }

        private object? Eval(Node node)
        {
            Tick();
            switch (node)
            {
                case Literal l:
                    return l.Value;
                case NameRef n:
                    return n.Name == "?" ? input : variables.TryGetValue(n.Name, out var v) ? v : null;
                case Path p:
                    return Member(Eval(p.Target), p.Name);
                case Index ix:
                    return IndexOf(Eval(ix.Target), ix.Selector);
                case ListNode list:
                    return Limit(list.Items.Select(Eval).ToList());
                case ContextNode ctx:
                {
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (key, value) in ctx.Entries)
                    {
                        map[key] = Eval(value);
                    }
                    return map;
                }
                case RangeNode r:
                    return new FeelRange(r.Low is null ? null : Eval(r.Low), r.LowClosed, r.High is null ? null : Eval(r.High), r.HighClosed);
                case IfNode i:
                    return Eval(i.Condition) is true ? Eval(i.Then) : Eval(i.Else);
                case Unary u:
                    return Negate(Eval(u.Operand));
                case Between b:
                {
                    var value = Eval(b.Value);
                    return And(Compare(">=", value, Eval(b.Low)), Compare("<=", value, Eval(b.High)));
                }
                case InTests t:
                {
                    var value = Eval(t.Value);
                    object? result = false;
                    foreach (var test in t.Tests)
                    {
                        result = Or(result, ApplyTest(test, value));
                        if (result is true)
                        {
                            return true;
                        }
                    }
                    return result;
                }
                case NotTests nt:
                {
                    object? result = false;
                    foreach (var test in nt.Tests)
                    {
                        result = Or(result, ApplyTest(test, input));
                    }
                    return result is bool b2 ? !b2 : null;
                }
                case TestNode test:
                    return ApplyTest(test, input);
                case Binary b:
                    return b.Op switch
                    {
                        "and" => And(Eval(b.Left), Eval(b.Right)),
                        "or" => Or(Eval(b.Left), Eval(b.Right)),
                        "=" or "!=" or "<" or "<=" or ">" or ">=" => Compare(b.Op, Eval(b.Left), Eval(b.Right)),
                        _ => Arithmetic(b.Op, Eval(b.Left), Eval(b.Right)),
                    };
                case Call c:
                    return Invoke(c.Function, c.Arguments.Select(Eval).ToList());
                default:
                    throw new FeelEvaluationException("unsupported");
            }
        }

        private static List<object?> Limit(List<object?> list) =>
            list.Count > MaxCollection ? throw new FeelEvaluationException("result_too_large") : list;

        private object? ApplyTest(Node test, object? value)
        {
            if (test is not TestNode t)
            {
                return Equal(value, Eval(test));
            }
            switch (t.Op)
            {
                case "any":
                    return true;
                case "range":
                    return Eval(t.Operand!) is FeelRange range ? InRange(value, range) : null;
                case "=":
                {
                    var operand = Eval(t.Operand!);
                    return operand switch
                    {
                        FeelRange r => InRange(value, r),
                        IList<object?> list when value is not IList<object?> => list.Any(item => Equal(value, item) is true),
                        bool b when value is not bool => b,
                        _ => Equal(value, operand),
                    };
                }
                default:
                    return Compare(t.Op, value, Eval(t.Operand!));
            }
        }

        private static object? InRange(object? value, FeelRange r)
        {
            var low = r.Low is null ? (object?)true : Compare(r.LowClosed ? ">=" : ">", value, r.Low);
            var high = r.High is null ? (object?)true : Compare(r.HighClosed ? "<=" : "<", value, r.High);
            return And(low, high);
        }

        private static object? And(object? a, object? b) => (a, b) switch
        {
            (false, _) or (_, false) => false,
            (true, true) => true,
            _ => null,
        };

        private static object? Or(object? a, object? b) => (a, b) switch
        {
            (true, _) or (_, true) => true,
            (false, false) => false,
            _ => null,
        };

        private static object? Member(object? target, string name) => target switch
        {
            IDictionary<string, object?> c => c.TryGetValue(name, out var v) ? v : null,
            DateOnly d => name switch { "year" => (decimal)d.Year, "month" => (decimal)d.Month, "day" => (decimal)d.Day, "weekday" => (decimal)((int)d.DayOfWeek == 0 ? 7 : (int)d.DayOfWeek), _ => null },
            DateTimeOffset t => name switch { "year" => (decimal)t.Year, "month" => (decimal)t.Month, "day" => (decimal)t.Day, "hour" => (decimal)t.Hour, "minute" => (decimal)t.Minute, _ => null },
            FeelDuration d => name switch { "years" => (decimal)(d.Months / 12), "months" => (decimal)(d.Months % 12), "days" => (decimal)d.Time.Days, "hours" => (decimal)d.Time.Hours, _ => null },
            IList<object?> list => Limit(list.Select(item => Member(item, name)).ToList()),
            _ => null,
        };

        private object? IndexOf(object? target, Node selector)
        {
            if (target is not IList<object?> list)
            {
                return null;
            }
            // A number picks an item (1 based, negative from the end); anything else filters with "item".
            if (selector is Literal { Value: decimal } || selector is Unary)
            {
                if (Eval(selector) is not decimal index || index == 0)
                {
                    return null;
                }
                var i = index > 0 ? (int)index - 1 : list.Count + (int)index;
                return i >= 0 && i < list.Count ? list[i] : null;
            }
            var kept = new List<object?>();
            foreach (var item in list)
            {
                var scope = new Dictionary<string, object?>(variables.Count + 1, StringComparer.Ordinal) { ["item"] = item };
                if (item is IDictionary<string, object?> c)
                {
                    foreach (var p in c)
                    {
                        scope.TryAdd(p.Key, p.Value);
                    }
                }
                foreach (var p in variables)
                {
                    scope.TryAdd(p.Key, p.Value);
                }
                var inner = new Evaluator(scope, now, input) { steps = steps };
                if (inner.Eval(selector) is true)
                {
                    kept.Add(item);
                }
                steps = inner.steps;
                Tick();
            }
            return kept;
        }

        private static object? Negate(object? value) => value switch
        {
            decimal d => -d,
            FeelDuration d => new FeelDuration(-d.Months, -d.Time),
            _ => null,
        };

        private static object? Equal(object? a, object? b) => (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (decimal x, decimal y) => x == y,
            (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
            (bool x, bool y) => x == y,
            (DateOnly x, DateOnly y) => x == y,
            (DateTimeOffset x, DateTimeOffset y) => x == y,
            (FeelDuration x, FeelDuration y) => x == y,
            (IList<object?> x, IList<object?> y) => x.Count == y.Count && x.Zip(y).All(p => Equal(p.First, p.Second) is true),
            (IDictionary<string, object?> x, IDictionary<string, object?> y) => x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var v) && Equal(p.Value, v) is true),
            _ => null,
        };

        private static object? Compare(string op, object? a, object? b)
        {
            if (op is "=" or "!=")
            {
                var equal = Equal(a, b);
                return op == "=" ? equal : equal is bool e ? !e : null;
            }
            int? order = (a, b) switch
            {
                (decimal x, decimal y) => x.CompareTo(y),
                (string x, string y) => string.CompareOrdinal(x, y),
                (DateOnly x, DateOnly y) => x.CompareTo(y),
                (DateTimeOffset x, DateTimeOffset y) => x.CompareTo(y),
                (DateOnly x, DateTimeOffset y) => new DateTimeOffset(x.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).CompareTo(y),
                (DateTimeOffset x, DateOnly y) => x.CompareTo(new DateTimeOffset(y.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
                (FeelDuration x, FeelDuration y) when x.Months == 0 && y.Months == 0 => x.Time.CompareTo(y.Time),
                (FeelDuration x, FeelDuration y) when x.Time == TimeSpan.Zero && y.Time == TimeSpan.Zero => x.Months.CompareTo(y.Months),
                _ => null,
            };
            if (order is not { } o)
            {
                return null;
            }
            return op switch { "<" => o < 0, "<=" => o <= 0, ">" => o > 0, _ => o >= 0 };
        }

        private static object? Arithmetic(string op, object? a, object? b)
        {
            try
            {
                return (op, a, b) switch
                {
                    ("+", decimal x, decimal y) => x + y,
                    ("-", decimal x, decimal y) => x - y,
                    ("*", decimal x, decimal y) => x * y,
                    ("/", decimal _, decimal y) when y == 0 => null,
                    ("/", decimal x, decimal y) => x / y,
                    ("**", decimal x, decimal y) when y == Math.Floor(y) && Math.Abs(y) <= 64 => Pow(x, (int)y),
                    ("+", string x, string y) => x.Length + y.Length > MaxString ? throw new FeelEvaluationException("result_too_large") : x + y,
                    ("+", DateOnly x, FeelDuration d) => AddToDate(x, d),
                    ("-", DateOnly x, FeelDuration d) => AddToDate(x, new FeelDuration(-d.Months, -d.Time)),
                    ("+", DateTimeOffset x, FeelDuration d) => x.AddMonths(d.Months).Add(d.Time),
                    ("-", DateTimeOffset x, FeelDuration d) => x.AddMonths(-d.Months).Subtract(d.Time),
                    ("-", DateTimeOffset x, DateTimeOffset y) => new FeelDuration(0, x - y),
                    ("-", DateOnly x, DateOnly y) => new FeelDuration(0, TimeSpan.FromDays(x.DayNumber - y.DayNumber)),
                    ("+", FeelDuration x, FeelDuration y) => new FeelDuration(x.Months + y.Months, x.Time + y.Time),
                    ("-", FeelDuration x, FeelDuration y) => new FeelDuration(x.Months - y.Months, x.Time - y.Time),
                    ("*", FeelDuration x, decimal n) => new FeelDuration((int)(x.Months * n), TimeSpan.FromTicks((long)(x.Time.Ticks * n))),
                    _ => null,
                };
            }
            catch (OverflowException)
            {
                return null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static decimal? Pow(decimal x, int n)
        {
            decimal result = 1;
            for (var i = 0; i < Math.Abs(n); i++)
            {
                result *= x;
            }
            return n < 0 ? (result == 0 ? null : 1 / result) : result;
        }

        private static object AddToDate(DateOnly date, FeelDuration d) =>
            d.Time.Ticks % TimeSpan.TicksPerDay == 0 ? date.AddMonths(d.Months).AddDays((int)d.Time.TotalDays)
                : new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMonths(d.Months).Add(d.Time);

        private object? Invoke(string name, List<object?> args)
        {
            object? Arg(int i) => i < args.Count ? args[i] : null;
            void Arity(int min, int max)
            {
                if (args.Count < min || args.Count > max)
                {
                    throw new FeelEvaluationException("arguments:" + name);
                }
            }
            IEnumerable<object?> Items() => args.Count == 1 && args[0] is IList<object?> list ? list : args;

            switch (name)
            {
                case "not":
                    Arity(1, 1);
                    return Arg(0) is bool b ? !b : null;
                case "is null":
                    Arity(1, 1);
                    return Arg(0) is null;
                case "contains":
                    Arity(2, 2);
                    return Arg(0) is string s1 && Arg(1) is string s2 ? s1.Contains(s2, StringComparison.Ordinal) : null;
                case "starts with":
                    Arity(2, 2);
                    return Arg(0) is string s3 && Arg(1) is string s4 ? s3.StartsWith(s4, StringComparison.Ordinal) : null;
                case "ends with":
                    Arity(2, 2);
                    return Arg(0) is string s5 && Arg(1) is string s6 ? s5.EndsWith(s6, StringComparison.Ordinal) : null;
                case "upper case":
                    Arity(1, 1);
                    return (Arg(0) as string)?.ToUpperInvariant();
                case "lower case":
                    Arity(1, 1);
                    return (Arg(0) as string)?.ToLowerInvariant();
                case "string length":
                    Arity(1, 1);
                    return Arg(0) is string s7 ? (decimal)s7.Length : null;
                case "substring":
                {
                    Arity(2, 3);
                    if (Arg(0) is not string text || Arg(1) is not decimal start)
                    {
                        return null;
                    }
                    var from = start > 0 ? (int)start - 1 : text.Length + (int)start;
                    if (from < 0 || from > text.Length)
                    {
                        return null;
                    }
                    var length = Arg(2) is decimal l ? (int)Math.Min(l, text.Length - from) : text.Length - from;
                    return length < 0 ? null : text.Substring(from, length);
                }
                case "string":
                    Arity(1, 1);
                    return Arg(0) switch
                    {
                        null => null,
                        string s => s,
                        decimal d => d.ToString(CultureInfo.InvariantCulture),
                        bool b2 => b2 ? "true" : "false",
                        var other => ToJson(other)?.ToJsonString().Trim('"'),
                    };
                case "number":
                    Arity(1, 1);
                    return Arg(0) is string n && decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                case "date":
                    Arity(1, 3);
                    if (args.Count == 3)
                    {
                        return Arg(0) is decimal y && Arg(1) is decimal m && Arg(2) is decimal d2 && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d2 >= 1
                            && d2 <= DateTime.DaysInMonth((int)y, (int)m) ? new DateOnly((int)y, (int)m, (int)d2) : null;
                    }
                    return Arg(0) switch
                    {
                        string s => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                        DateTimeOffset t => DateOnly.FromDateTime(t.UtcDateTime),
                        DateOnly d3 => d3,
                        _ => null,
                    };
                case "date and time":
                    Arity(1, 1);
                    return Arg(0) switch
                    {
                        string s when s.Length >= 16 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) => t,
                        DateOnly d4 => new DateTimeOffset(d4.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                        DateTimeOffset t2 => t2,
                        _ => null,
                    };
                case "time":
                    Arity(1, 1);
                    return Arg(0) is string ts && TimeOnly.TryParse(ts, CultureInfo.InvariantCulture, out var time) ? time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null;
                case "duration":
                    Arity(1, 1);
                    return Arg(0) is string ds ? ParseDuration(ds) : null;
                case "years":
                    Arity(1, 1);
                    return Arg(0) is decimal ny ? new FeelDuration((int)ny * 12, TimeSpan.Zero) : null;
                case "days":
                    Arity(1, 1);
                    return Arg(0) is decimal nd ? new FeelDuration(0, TimeSpan.FromDays((double)nd)) : null;
                case "today":
                    Arity(0, 0);
                    return DateOnly.FromDateTime(now.UtcDateTime);
                case "now":
                    Arity(0, 0);
                    return now;
                case "abs":
                    Arity(1, 1);
                    return Arg(0) is decimal a ? Math.Abs(a) : null;
                case "floor":
                    Arity(1, 1);
                    return Arg(0) is decimal f ? Math.Floor(f) : null;
                case "ceiling":
                    Arity(1, 1);
                    return Arg(0) is decimal c ? Math.Ceiling(c) : null;
                case "round":
                    Arity(1, 2);
                    return Arg(0) is decimal r ? Math.Round(r, Arg(1) is decimal scale ? (int)Math.Clamp(scale, 0, 28) : 0, MidpointRounding.AwayFromZero) : null;
                case "min":
                case "max":
                {
                    var items = Items().ToList();
                    if (items.Count == 0)
                    {
                        return null;
                    }
                    var best = items[0];
                    foreach (var item in items.Skip(1))
                    {
                        Tick();
                        var better = Compare(name == "min" ? "<" : ">", item, best);
                        if (better is null)
                        {
                            return null;
                        }
                        if (better is true)
                        {
                            best = item;
                        }
                    }
                    return best;
                }
                case "sum":
                {
                    decimal total = 0;
                    foreach (var item in Items())
                    {
                        Tick();
                        if (item is not decimal d5)
                        {
                            return null;
                        }
                        total += d5;
                    }
                    return total;
                }
                case "count":
                    Arity(1, 1);
                    return Arg(0) is IList<object?> list ? (decimal)list.Count : null;
                case "list contains":
                    Arity(2, 2);
                    return Arg(0) is IList<object?> l2 ? l2.Any(item => Equal(item, Arg(1)) is true) : null;
                default:
                    throw new FeelEvaluationException("unknown_function");
            }
        }
    }

    /// <summary>ISO 8601 durations: <c>P1Y2M</c> (years and months) or <c>P3DT4H</c> (days and time).</summary>
    public static FeelDuration? ParseDuration(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        if (body.Length < 3 || body[0] != 'P')
        {
            return null;
        }
        if (!body.Contains('T') && (body.EndsWith('Y') || body.EndsWith('M')) && !body.Contains('D') && !body.Contains('W'))
        {
            var years = 0;
            var months = 0;
            var rest = body[1..];
            var y = rest.IndexOf('Y');
            if (y > 0)
            {
                if (!int.TryParse(rest[..y], NumberStyles.None, CultureInfo.InvariantCulture, out years))
                {
                    return null;
                }
                rest = rest[(y + 1)..];
            }
            if (rest.Length > 0)
            {
                if (!rest.EndsWith('M') || !int.TryParse(rest[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out months))
                {
                    return null;
                }
            }
            var total = years * 12 + months;
            return new FeelDuration(negative ? -total : total, TimeSpan.Zero);
        }
        try
        {
            var span = XmlConvert.ToTimeSpan(body);
            return new FeelDuration(0, negative ? -span : span);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
