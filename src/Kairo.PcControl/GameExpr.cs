using System.Globalization;
using System.Text;

namespace Kairo.PcControl;

public sealed class GameExprException(string message) : Exception(message);

/// <summary>
/// The small expression language of a game policy: numbers, strings, <c>true</c>/<c>false</c>, names (<c>find.card.x</c>, <c>score</c>),
/// <c>+ - * / %</c>, comparisons, <c>&amp;&amp; || !</c>, parentheses and a few functions (<c>min max abs round floor ceil random</c>).
/// It can do arithmetic and compare, nothing else: no calls into the system, no loops. A policy written by a model is run on this.
/// </summary>
public sealed class GameExpr
{
    private readonly Node _root;

    private GameExpr(Node root, IReadOnlyCollection<string> names)
    {
        _root = root;
        Names = names;
    }

    /// <summary>The names the expression reads, so a policy can be checked against what exists before it runs.</summary>
    public IReadOnlyCollection<string> Names { get; }

    public string Text { get; private init; } = "";

    public static GameExpr Parse(string text)
    {
        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new GameExpr(root, parser.Names) { Text = text.Trim() };
    }

    /// <param name="lookup">Value of a name: a number, a string or a bool; null when there is no such name.</param>
    public object Eval(Func<string, object?> lookup, Func<double> random) => _root.Eval(new Context(lookup, random));

    public bool EvalBool(Func<string, object?> lookup, Func<double> random) => Truthy(Eval(lookup, random));

    public double EvalNumber(Func<string, object?> lookup, Func<double> random) => Eval(lookup, random) switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        var other => throw new GameExprException($"'{Text}' is {other}, a number was expected."),
    };

    internal static bool Truthy(object value) => value switch
    {
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        _ => false,
    };

    private sealed record Context(Func<string, object?> Lookup, Func<double> Random);

    private abstract class Node
    {
        public abstract object Eval(Context c);
    }

    private sealed class Literal(object value) : Node
    {
        public override object Eval(Context c) => value;
    }

    private sealed class Name(string name) : Node
    {
        public override object Eval(Context c) => c.Lookup(name) ?? throw new GameExprException($"There is no value '{name}' now.");
    }

    private sealed class Not(Node inner) : Node
    {
        public override object Eval(Context c) => !Truthy(inner.Eval(c));
    }

    private sealed class Negate(Node inner) : Node
    {
        public override object Eval(Context c) => -ToNumber(inner.Eval(c));
    }

    private sealed class Logic(string op, Node left, Node right) : Node
    {
        public override object Eval(Context c) => op == "&&"
            ? Truthy(left.Eval(c)) && Truthy(right.Eval(c))
            : Truthy(left.Eval(c)) || Truthy(right.Eval(c));
    }

    private sealed class Compare(string op, Node left, Node right) : Node
    {
        public override object Eval(Context c)
        {
            var a = left.Eval(c);
            var b = right.Eval(c);
            if (op is "==" or "!=")
            {
                var equal = Equal(a, b);
                return op == "==" ? equal : !equal;
            }
            if (a is string || b is string)
                throw new GameExprException($"Text can only be compared with == and !=, not {op}.");
            var x = ToNumber(a);
            var y = ToNumber(b);
            return op switch { "<" => x < y, "<=" => x <= y, ">" => x > y, _ => x >= y };
        }

        private static bool Equal(object a, object b) => (a, b) switch
        {
            (string s, string t) => string.Equals(s, t, StringComparison.OrdinalIgnoreCase),
            (string, _) or (_, string) => false,
            _ => Math.Abs(ToNumber(a) - ToNumber(b)) < 1e-9,
        };
    }

    private sealed class Arithmetic(char op, Node left, Node right) : Node
    {
        public override object Eval(Context c)
        {
            var a = left.Eval(c);
            var b = right.Eval(c);
            if (op == '+' && (a is string || b is string))
                return Display(a) + Display(b);
            var x = ToNumber(a);
            var y = ToNumber(b);
            return op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                '/' => y == 0 ? throw new GameExprException("Division by zero.") : x / y,
                _ => y == 0 ? throw new GameExprException("Division by zero.") : x % y,
            };
        }
    }

    private sealed class Call(string name, IReadOnlyList<Node> args) : Node
    {
        public override object Eval(Context c)
        {
            var raw = args.Select(a => a.Eval(c)).ToArray();
            if (MapFunctions.Contains(name))
                return MapCall(name, raw);
            var values = raw.Select(ToNumber).ToArray();
            double One() => values.Length == 1 ? values[0] : throw new GameExprException($"{name}() takes one number.");
            return name switch
            {
                "min" when values.Length >= 2 => values.Min(),
                "max" when values.Length >= 2 => values.Max(),
                "abs" => Math.Abs(One()),
                "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                "random" when values.Length == 0 => c.Random(),
                "random" when values.Length == 1 => Math.Floor(c.Random() * values[0]),
                _ => throw new GameExprException($"{name}() with {values.Length} arguments is not a function here."),
            };
        }
    }

    internal static readonly HashSet<string> Functions = ["min", "max", "abs", "round", "floor", "ceil", "random", "get", "has", "count", "first_free", "find_key", "pair_a", "pair_b"];

    private static readonly HashSet<string> MapFunctions = ["get", "has", "count", "first_free", "find_key", "pair_a", "pair_b"];

    /// <summary>The key a number or a text is stored under: 3 and "3" are the same key.</summary>
    public static string Key(object value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s,
        _ => throw new GameExprException("A key is a number or a text."),
    };

    private static Dictionary<string, object> Map(object[] raw, int index, string function) =>
        raw.Length > index && raw[index] is Dictionary<string, object> map
            ? map
            : throw new GameExprException($"{function}(): argument {index + 1} must be a map (declared in vars as {{}}).");

    private static object MapCall(string name, object[] raw)
    {
        void Arity(int min, int max)
        {
            if (raw.Length < min || raw.Length > max)
                throw new GameExprException($"{name}() takes {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min} to {max}")} arguments, not {raw.Length}.");
        }

        switch (name)
        {
            case "get": // get(map, key) or get(map, key, fallback): what is stored, or the fallback (default: an empty text)
                Arity(2, 3);
                return Map(raw, 0, name).TryGetValue(Key(raw[1]), out var stored) ? stored : raw.Length == 3 ? raw[2] : "";
            case "has":
                Arity(2, 2);
                return Map(raw, 0, name).ContainsKey(Key(raw[1]));
            case "count":
                Arity(1, 1);
                return (double)Map(raw, 0, name).Count;
            case "first_free": // first_free(map, n): the smallest whole number from 0 to n-1 that is not a key of the map, or -1
            {
                Arity(2, 2);
                var map = Map(raw, 0, name);
                var n = (int)ToNumber(raw[1]);
                for (var i = 0; i < n; i++)
                {
                    if (!map.ContainsKey(i.ToString(CultureInfo.InvariantCulture)))
                        return (double)i;
                }
                return -1.0;
            }
            case "find_key": // find_key(map, value, skip, except): a key whose value equals value, not in the skip map, not the key except; or -1
            {
                Arity(4, 4);
                var map = Map(raw, 0, name);
                var skip = Map(raw, 2, name);
                var except = Key(raw[3]);
                foreach (var (key, value) in map)
                {
                    if (key != except && !skip.ContainsKey(key) && Same(value, raw[1]))
                        return Number(key);
                }
                return -1.0;
            }
            default: // pair_a(map, skip) / pair_b(map, skip): the first two keys, not in skip, that hold the same value; or -1
            {
                Arity(2, 2);
                var map = Map(raw, 0, name);
                var skip = Map(raw, 1, name);
                var entries = map.Where(e => !skip.ContainsKey(e.Key)).ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    for (var j = i + 1; j < entries.Count; j++)
                    {
                        if (Same(entries[i].Value, entries[j].Value))
                            return Number(name == "pair_a" ? entries[i].Key : entries[j].Key);
                    }
                }
                return -1.0;
            }
        }
    }

    private static bool Same(object a, object b) => (a, b) switch
    {
        (string s, string t) => string.Equals(s, t, StringComparison.OrdinalIgnoreCase),
        (string, _) or (_, string) => false,
        _ => Math.Abs(ToNumber(a) - ToNumber(b)) < 1e-9,
    };

    private static object Number(string key) => double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : key;

    private static double ToNumber(object value) => value switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s => throw new GameExprException($"The text \"{s}\" is not a number."),
        _ => throw new GameExprException("Not a number."),
    };

    private static string Display(object value) => value switch
    {
        string s => s,
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => "",
    };

    // ---- Parser ----

    private sealed class Parser(string text)
    {
        private int _pos;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => _names;

        public Node ParseAll()
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new GameExprException("The expression is empty.");
            if (text.Length > 400)
                throw new GameExprException("The expression is longer than 400 characters.");
            var node = Or();
            Skip();
            if (_pos < text.Length)
                throw Error($"Unexpected '{text[_pos]}'");
            return node;
        }

        private GameExprException Error(string message) => new($"{message} at position {_pos + 1} of \"{text}\".");

        private void Skip()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) != 0)
                return false;
            _pos += token.Length;
            return true;
        }

        private Node Or()
        {
            var left = And();
            while (Take("||"))
                left = new Logic("||", left, And());
            return left;
        }

        private Node And()
        {
            var left = Comparison();
            while (Take("&&"))
                left = new Logic("&&", left, Comparison());
            return left;
        }

        private Node Comparison()
        {
            var left = Sum();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (Take(op))
                    return new Compare(op, left, Sum());
            }
            return left;
        }

        private Node Sum()
        {
            var left = Product();
            while (true)
            {
                if (Take("+"))
                    left = new Arithmetic('+', left, Product());
                else if (Peek() == '-' && Take("-"))
                    left = new Arithmetic('-', left, Product());
                else
                    return left;
            }
        }

        private Node Product()
        {
            var left = Unary();
            while (true)
            {
                if (Take("*"))
                    left = new Arithmetic('*', left, Unary());
                else if (Take("/"))
                    left = new Arithmetic('/', left, Unary());
                else if (Take("%"))
                    left = new Arithmetic('%', left, Unary());
                else
                    return left;
            }
        }

        private char Peek()
        {
            Skip();
            return _pos < text.Length ? text[_pos] : '\0';
        }

        private Node Unary()
        {
            if (Peek() == '!' && (_pos + 1 >= text.Length || text[_pos + 1] != '='))
            {
                _pos++;
                return new Not(Unary());
            }
            if (Peek() == '-')
            {
                _pos++;
                return new Negate(Unary());
            }
            return Primary();
        }

        private Node Primary()
        {
            var c = Peek();
            if (c == '\0')
                throw Error("The expression ends too soon");
            if (c == '(')
            {
                _pos++;
                var inner = Or();
                if (!Take(")"))
                    throw Error("Missing ')'");
                return inner;
            }
            if (char.IsDigit(c) || c == '.')
                return Number();
            if (c is '"' or '\'')
                return Str(c);
            if (char.IsLetter(c) || c == '_')
                return Identifier();
            throw Error($"Unexpected '{c}'");
        }

        private Node Number()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;
            if (!double.TryParse(text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Error($"'{text[start.._pos]}' is not a number");
            return new Literal(value);
        }

        private Node Str(char quote)
        {
            _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != quote)
                sb.Append(text[_pos++]);
            if (_pos >= text.Length)
                throw Error("A text is not closed");
            _pos++;
            return new Literal(sb.ToString());
        }

        private Node Identifier()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.'))
                _pos++;
            var name = text[start.._pos];
            if (name.EndsWith('.') || name.Contains(".."))
                throw Error($"'{name}' is not a name");
            switch (name)
            {
                case "true":
                    return new Literal(true);
                case "false":
                    return new Literal(false);
            }
            if (Peek() == '(')
            {
                _pos++;
                if (!Functions.Contains(name))
                    throw Error($"'{name}' is not a function (min, max, abs, round, floor, ceil, random)");
                var args = new List<Node>();
                if (!Take(")"))
                {
                    do
                    {
                        args.Add(Or());
                    }
                    while (Take(","));
                    if (!Take(")"))
                        throw Error("Missing ')'");
                }
                return new Call(name, args);
            }
            _names.Add(name);
            return new Name(name);
        }
    }
}
