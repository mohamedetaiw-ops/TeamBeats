using System.Globalization;
using System.Text;

namespace TeamBeats.Modules.Workflow.Feel;

/// <summary>A FEEL expression or unary test that does not parse, with the position of the problem.</summary>
public sealed class FeelSyntaxException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

internal enum TokenKind
{
    Number,
    String,
    Name,
    Symbol,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position, decimal Number = 0);

/// <summary>Splits FEEL text into tokens. Names are letters, digits and <c>_</c>; spaces separate them. <c>?</c> is the input of a unary test.</summary>
internal static class FeelLexer
{
    private static readonly string[] Symbols = ["..", "<=", ">=", "!=", "**", "<", ">", "=", "+", "-", "*", "/", "(", ")", "[", "]", "{", "}", ",", ".", ":", "?"];

    public const int MaxLength = 4000;

    public static List<Token> Read(string text)
    {
        if (text.Length > MaxLength)
        {
            throw new FeelSyntaxException("too_long", 0);
        }
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && (tokens.Count == 0 || tokens[^1].Text != ".")))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }
                // A dot is a decimal point only when a digit follows, so "1..5" stays a range.
                if (i + 1 < text.Length && text[i] == '.' && char.IsDigit(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }
                }
                var raw = text[start..i];
                if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                {
                    throw new FeelSyntaxException("number", start);
                }
                tokens.Add(new Token(TokenKind.Number, raw, start, value));
                continue;
            }
            if (c == '"')
            {
                var start = i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new FeelSyntaxException("string_unclosed", start);
                    }
                    var ch = text[i++];
                    if (ch == '"')
                    {
                        break;
                    }
                    if (ch == '\\' && i < text.Length)
                    {
                        var next = text[i++];
                        sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                        continue;
                    }
                    sb.Append(ch);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
                tokens.Add(new Token(TokenKind.Name, text[start..i], start));
                continue;
            }
            var symbol = Symbols.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0)
                ?? throw new FeelSyntaxException("unexpected_character", i);
            tokens.Add(new Token(TokenKind.Symbol, symbol, i));
            i += symbol.Length;
        }
        tokens.Add(new Token(TokenKind.End, "", text.Length));
        return tokens;
    }
}

internal abstract record Node(int Position);

internal sealed record Literal(object? Value, int Position) : Node(Position);

internal sealed record NameRef(string Name, int Position) : Node(Position);

internal sealed record Path(Node Target, string Name, int Position) : Node(Position);

internal sealed record Index(Node Target, Node Selector, int Position) : Node(Position);

internal sealed record Call(string Function, IReadOnlyList<Node> Arguments, int Position) : Node(Position);

internal sealed record Unary(string Op, Node Operand, int Position) : Node(Position);

internal sealed record Binary(string Op, Node Left, Node Right, int Position) : Node(Position);

internal sealed record Between(Node Value, Node Low, Node High, int Position) : Node(Position);

internal sealed record InTests(Node Value, IReadOnlyList<Node> Tests, int Position) : Node(Position);

internal sealed record IfNode(Node Condition, Node Then, Node Else, int Position) : Node(Position);

internal sealed record ListNode(IReadOnlyList<Node> Items, int Position) : Node(Position);

internal sealed record ContextNode(IReadOnlyList<(string Key, Node Value)> Entries, int Position) : Node(Position);

internal sealed record RangeNode(Node? Low, bool LowClosed, Node? High, bool HighClosed, int Position) : Node(Position);

/// <summary>A unary test against the input value (<c>?</c>): a comparison, a range, a value to equal, or any.</summary>
internal sealed record TestNode(string Op, Node? Operand, int Position) : Node(Position);

internal sealed record NotTests(IReadOnlyList<Node> Tests, int Position) : Node(Position);

/// <summary>Parses the FEEL subset of spec 024 section 12. No function definitions, no <c>for</c>, no quantifiers, no paths into host objects.</summary>
internal sealed class FeelParser
{
    /// <summary>Built-in functions by their FEEL name, which may contain spaces.</summary>
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal)
    {
        "not", "contains", "starts with", "ends with", "upper case", "lower case", "string length", "substring", "string", "number",
        "date", "date and time", "time", "duration", "today", "now", "abs", "floor", "ceiling", "round", "min", "max", "sum", "count",
        "list contains", "is null", "years", "days",
    };

    private const int MaxDepth = 64;
    private readonly List<Token> tokens;
    private int at;
    private int depth;

    private FeelParser(string text) => tokens = FeelLexer.Read(text);

    public static Node ParseExpression(string text)
    {
        var parser = new FeelParser(text);
        var node = parser.Expression();
        parser.ExpectEnd();
        return node;
    }

    /// <summary>Parses the unary tests of a DMN input cell: <c>-</c>, a list of tests, or <c>not(...)</c>.</summary>
    public static Node ParseUnaryTests(string text)
    {
        var parser = new FeelParser(text);
        Node node;
        if (parser.Peek.Kind == TokenKind.Symbol && parser.Peek.Text == "-" && parser.tokens[parser.at + 1].Kind == TokenKind.End)
        {
            node = new TestNode("any", null, 0);
            parser.at++;
        }
        else if (parser.Peek.Kind == TokenKind.Name && parser.Peek.Text == "not" && parser.tokens[parser.at + 1].Text == "(")
        {
            var position = parser.Peek.Position;
            parser.at += 2;
            var tests = parser.Tests(")");
            parser.Expect(")");
            node = new NotTests(tests, position);
        }
        else
        {
            node = new InTests(new NameRef("?", 0), parser.Tests(null), 0);
        }
        parser.ExpectEnd();
        return node;
    }

    private Token Peek => tokens[at];

    private bool IsSymbol(string s) => Peek.Kind == TokenKind.Symbol && Peek.Text == s;

    private bool IsName(string s) => Peek.Kind == TokenKind.Name && Peek.Text == s;

    private Token Expect(string symbol)
    {
        if (!IsSymbol(symbol))
        {
            throw new FeelSyntaxException("expected:" + symbol, Peek.Position);
        }
        return tokens[at++];
    }

    private void ExpectEnd()
    {
        if (Peek.Kind != TokenKind.End)
        {
            throw new FeelSyntaxException("unexpected:" + Peek.Text, Peek.Position);
        }
    }

    private void Enter()
    {
        if (++depth > MaxDepth)
        {
            throw new FeelSyntaxException("too_deep", Peek.Position);
        }
    }

    private Node Expression()
    {
        Enter();
        try
        {
            if (IsName("if"))
            {
                var position = tokens[at++].Position;
                var condition = Expression();
                ExpectName("then");
                var then = Expression();
                ExpectName("else");
                return new IfNode(condition, then, Expression(), position);
            }
            return Disjunction();
        }
        finally
        {
            depth--;
        }
    }

    private void ExpectName(string name)
    {
        if (!IsName(name))
        {
            throw new FeelSyntaxException("expected:" + name, Peek.Position);
        }
        at++;
    }

    private Node Disjunction()
    {
        var left = Conjunction();
        while (IsName("or"))
        {
            var position = tokens[at++].Position;
            left = new Binary("or", left, Conjunction(), position);
        }
        return left;
    }

    private Node Conjunction()
    {
        var left = Comparison();
        while (IsName("and"))
        {
            var position = tokens[at++].Position;
            left = new Binary("and", left, Comparison(), position);
        }
        return left;
    }

    private Node Comparison()
    {
        var left = Additive();
        if (Peek.Kind == TokenKind.Symbol && Peek.Text is "=" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = tokens[at++];
            return new Binary(op.Text, left, Additive(), op.Position);
        }
        if (IsName("between"))
        {
            var position = tokens[at++].Position;
            var low = Additive();
            ExpectName("and");
            return new Between(left, low, Additive(), position);
        }
        if (IsName("in"))
        {
            var position = tokens[at++].Position;
            if (IsSymbol("(") && !IsRangeStart())
            {
                at++;
                var tests = Tests(")");
                Expect(")");
                return new InTests(left, tests, position);
            }
            return new InTests(left, [Test()], position);
        }
        return left;
    }

    /// <summary>Comma separated unary tests, up to the closing symbol (or the end).</summary>
    private List<Node> Tests(string? close)
    {
        var tests = new List<Node> { Test() };
        while (IsSymbol(","))
        {
            at++;
            tests.Add(Test());
        }
        if (close is not null && !IsSymbol(close))
        {
            throw new FeelSyntaxException("expected:" + close, Peek.Position);
        }
        return tests;
    }

    private Node Test()
    {
        var position = Peek.Position;
        if (Peek.Kind == TokenKind.Symbol && Peek.Text is "<" or "<=" or ">" or ">=" or "=" or "!=")
        {
            var op = tokens[at++].Text;
            return new TestNode(op, Additive(), position);
        }
        if (IsRangeStart())
        {
            return new TestNode("range", Primary(), position);
        }
        return new TestNode("=", Expression(), position);
    }

    /// <summary>A range starts with <c>[</c>, <c>(</c> or <c>]</c> and has <c>..</c> before its close.</summary>
    private bool IsRangeStart()
    {
        if (!(IsSymbol("[") || IsSymbol("(") || IsSymbol("]")))
        {
            return false;
        }
        var nesting = 0;
        for (var i = at; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Symbol)
            {
                continue;
            }
            if (t.Text is "(" or "[" or "{")
            {
                nesting++;
            }
            else if (t.Text is ")" or "]" or "}")
            {
                nesting--;
                if (nesting <= 0 && i > at)
                {
                    return false;
                }
            }
            else if (t.Text == ".." && nesting == 1)
            {
                return true;
            }
        }
        return false;
    }

    private Node Additive()
    {
        var left = Multiplicative();
        while (Peek.Kind == TokenKind.Symbol && Peek.Text is "+" or "-")
        {
            var op = tokens[at++];
            left = new Binary(op.Text, left, Multiplicative(), op.Position);
        }
        return left;
    }

    private Node Multiplicative()
    {
        var left = Power();
        while (Peek.Kind == TokenKind.Symbol && Peek.Text is "*" or "/")
        {
            var op = tokens[at++];
            left = new Binary(op.Text, left, Power(), op.Position);
        }
        return left;
    }

    private Node Power()
    {
        var left = UnaryMinus();
        if (IsSymbol("**"))
        {
            var op = tokens[at++];
            return new Binary("**", left, UnaryMinus(), op.Position);
        }
        return left;
    }

    private Node UnaryMinus()
    {
        if (IsSymbol("-"))
        {
            var position = tokens[at++].Position;
            return new Unary("-", UnaryMinus(), position);
        }
        return Postfix();
    }

    private Node Postfix()
    {
        var node = Primary();
        while (true)
        {
            if (IsSymbol("."))
            {
                var position = tokens[at++].Position;
                if (Peek.Kind != TokenKind.Name)
                {
                    throw new FeelSyntaxException("expected:name", Peek.Position);
                }
                node = new Path(node, tokens[at++].Text, position);
            }
            else if (IsSymbol("[") && !IsRangeStart())
            {
                var position = tokens[at++].Position;
                var selector = Expression();
                Expect("]");
                node = new Index(node, selector, position);
            }
            else
            {
                return node;
            }
        }
    }

    private Node Primary()
    {
        Enter();
        try
        {
            var token = Peek;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    at++;
                    return new Literal(token.Number, token.Position);
                case TokenKind.String:
                    at++;
                    return new Literal(token.Text, token.Position);
                case TokenKind.Name:
                    return NameOrCall();
            }
            if (IsRangeStart())
            {
                var lowClosed = IsSymbol("[");
                at++;
                var low = Expression();
                Expect("..");
                var high = Expression();
                bool highClosed;
                if (IsSymbol("]"))
                {
                    highClosed = true;
                }
                else if (IsSymbol(")") || IsSymbol("["))
                {
                    highClosed = false;
                }
                else
                {
                    throw new FeelSyntaxException("expected:]", Peek.Position);
                }
                at++;
                return new RangeNode(low, lowClosed, high, highClosed, token.Position);
            }
            if (IsSymbol("("))
            {
                at++;
                var inner = Expression();
                Expect(")");
                return inner;
            }
            if (IsSymbol("["))
            {
                at++;
                var items = new List<Node>();
                if (!IsSymbol("]"))
                {
                    items.Add(Expression());
                    while (IsSymbol(","))
                    {
                        at++;
                        items.Add(Expression());
                    }
                }
                Expect("]");
                return new ListNode(items, token.Position);
            }
            if (IsSymbol("{"))
            {
                at++;
                var entries = new List<(string, Node)>();
                if (!IsSymbol("}"))
                {
                    do
                    {
                        if (IsSymbol(","))
                        {
                            at++;
                        }
                        var key = Peek.Kind is TokenKind.Name or TokenKind.String ? tokens[at++].Text : throw new FeelSyntaxException("expected:key", Peek.Position);
                        Expect(":");
                        entries.Add((key, Expression()));
                    }
                    while (IsSymbol(","));
                }
                Expect("}");
                return new ContextNode(entries, token.Position);
            }
            if (IsSymbol("?"))
            {
                at++;
                return new NameRef("?", token.Position);
            }
            throw new FeelSyntaxException("unexpected:" + token.Text, token.Position);
        }
        finally
        {
            depth--;
        }
    }

    private Node NameOrCall()
    {
        var token = tokens[at];
        switch (token.Text)
        {
            case "true":
                at++;
                return new Literal(true, token.Position);
            case "false":
                at++;
                return new Literal(false, token.Position);
            case "null":
                at++;
                return new Literal(null, token.Position);
        }
        // The longest run of names that is a built-in function followed by "(".
        for (var length = Math.Min(4, tokens.Count - at - 1); length >= 1; length--)
        {
            var parts = tokens.Skip(at).Take(length).ToList();
            if (parts.Any(p => p.Kind != TokenKind.Name))
            {
                continue;
            }
            var name = string.Join(' ', parts.Select(p => p.Text));
            if (Functions.Contains(name) && tokens[at + length].Kind == TokenKind.Symbol && tokens[at + length].Text == "(")
            {
                at += length + 1;
                var args = new List<Node>();
                if (!IsSymbol(")"))
                {
                    args.Add(Expression());
                    while (IsSymbol(","))
                    {
                        at++;
                        args.Add(Expression());
                    }
                }
                Expect(")");
                return new Call(name, args, token.Position);
            }
        }
        if (token.Text is "if" or "then" or "else" or "and" or "or" or "between" or "in")
        {
            throw new FeelSyntaxException("unexpected:" + token.Text, token.Position);
        }
        at++;
        return new NameRef(token.Text, token.Position);
    }
}
