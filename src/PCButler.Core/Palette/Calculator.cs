// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Globalization;
using System.Text;

namespace PCButler.Core.Palette;

/// <summary>The answer to a calculation: as shown, and as copied (a plain number where there is one).</summary>
public readonly record struct CalcAnswer(string Shown, string Copy);

/// <summary>
/// The command palette's calculator: numbers with + - * / ^, brackets, percent, a few functions
/// (sqrt, abs, round, floor, ceil, log, ln, sin, cos, tan, pi, e), and sizes, speeds and times:
/// "5 GB in MB", "4 GB at 100 Mbps" (how long it takes), "2 h * 50 MB/s". Sizes count in 1024s,
/// as File Explorer does; speeds in bits (Mbps) count in 1000s, as internet speeds do.
/// Small and hand-written, so nothing heavy is loaded to work out a sum.
/// </summary>
public static class Calculator
{
    private enum Kind { Number, Bytes, Rate, Seconds }

    private readonly record struct Value(double Amount, Kind Kind);

    private sealed class Unit(string name, double factor, Kind kind)
    {
        public string Name { get; } = name;
        public double Factor { get; } = factor;
        public Kind Kind { get; } = kind;
    }

    private const double K = 1024;

    private static readonly Dictionary<string, Unit> Units = Build();

    private static Dictionary<string, Unit> Build()
    {
        var units = new Dictionary<string, Unit>(StringComparer.Ordinal);
        void Add(string names, string shown, double factor, Kind kind)
        {
            var unit = new Unit(shown, factor, kind);
            foreach (var name in names.Split(' '))
                units[name] = unit;
        }
        Add("b byte bytes", "bytes", 1, Kind.Bytes);
        Add("kb kib", "KB", K, Kind.Bytes);
        Add("mb mib", "MB", K * K, Kind.Bytes);
        Add("gb gib", "GB", K * K * K, Kind.Bytes);
        Add("tb tib", "TB", K * K * K * K, Kind.Bytes);
        Add("bps", "bps", 1 / 8.0, Kind.Rate);
        Add("kbps", "Kbps", 1e3 / 8, Kind.Rate);
        Add("mbps", "Mbps", 1e6 / 8, Kind.Rate);
        Add("gbps", "Gbps", 1e9 / 8, Kind.Rate);
        Add("b/s", "B/s", 1, Kind.Rate);
        Add("kb/s", "KB/s", K, Kind.Rate);
        Add("mb/s", "MB/s", K * K, Kind.Rate);
        Add("gb/s", "GB/s", K * K * K, Kind.Rate);
        Add("ms", "ms", 0.001, Kind.Seconds);
        Add("s sec secs second seconds", "s", 1, Kind.Seconds);
        Add("min mins minute minutes", "min", 60, Kind.Seconds);
        Add("h hr hrs hour hours", "h", 3600, Kind.Seconds);
        Add("d day days", "days", 86400, Kind.Seconds);
        return units;
    }

    /// <summary>Work out the text; false when it is not a calculation this understands.</summary>
    public static bool TryEvaluate(string text, out CalcAnswer answer)
    {
        answer = default;
        var plain = Normalize(text);
        if (plain.Length == 0)
            return false;
        // "… in MB" or "… to minutes": the unit wanted for the answer.
        Unit? wanted = null;
        foreach (var word in new[] { " in ", " to " })
        {
            var at = plain.LastIndexOf(word, StringComparison.Ordinal);
            if (at > 0 && Units.TryGetValue(plain[(at + word.Length)..].Trim(), out var unit))
            {
                wanted = unit;
                plain = plain[..at];
                break;
            }
        }
        try
        {
            var parser = new Parser(plain);
            var value = parser.Full();
            if (!parser.AtEnd || !double.IsFinite(value.Amount))
                return false;
            if (wanted is not null)
            {
                if (wanted.Kind != value.Kind)
                    return false;
                var amount = value.Amount / wanted.Factor;
                answer = new CalcAnswer(Group(amount) + " " + wanted.Name, Plain(amount));
                return true;
            }
            answer = value.Kind switch
            {
                Kind.Bytes => Size(value.Amount),
                Kind.Rate => new CalcAnswer(Group(value.Amount * 8 / 1e6) + " Mbps", Plain(value.Amount * 8 / 1e6)),
                Kind.Seconds => Duration(value.Amount),
                _ => new CalcAnswer(Group(value.Amount), Plain(value.Amount)),
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static readonly char[] Operators = ['+', '-', '*', '/', '^', '%'];

    /// <summary>Does the text look like a sum someone typed without "=" (12*7, (3+4)/2, 5 GB in MB)?</summary>
    public static bool LooksLikeMath(string text)
    {
        var plain = Normalize(text);
        if (plain.Length < 2 || !(char.IsAsciiDigit(plain[0]) || plain[0] is '(' or '-' or '.'))
            return false;
        return plain.IndexOfAny(Operators, 1) > 0 || plain.Contains(" in ", StringComparison.Ordinal)
               || plain.Contains(" to ", StringComparison.Ordinal) || plain.Contains(" at ", StringComparison.Ordinal);
    }

    /// <summary>Lower case, Arabic-Indic digits as 0-9, × and ÷ as * and /, thousands commas dropped.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c is >= '٠' and <= '٩')
                c = (char)('0' + (c - '٠'));
            else if (c is >= '۰' and <= '۹')
                c = (char)('0' + (c - '۰'));
            else if (c is '٫')
                c = '.';
            else if (c is '×')
                c = '*';
            else if (c is '÷')
                c = '/';
            else if (c is ',' or '٬' && index > 0 && index + 1 < text.Length && char.IsAsciiDigit(builder.Length > 0 ? builder[^1] : ' ')
                     && (char.IsAsciiDigit(text[index + 1]) || text[index + 1] is >= '٠' and <= '٩'))
                continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Trim();
    }

    private static CalcAnswer Size(double bytes)
    {
        string[] names = ["bytes", "KB", "MB", "GB", "TB"];
        var step = 0;
        var amount = bytes;
        while (Math.Abs(amount) >= K && step < names.Length - 1)
        {
            amount /= K;
            step++;
        }
        amount = Math.Round(amount, step == 0 ? 0 : 2);
        return new CalcAnswer(Group(amount) + " " + names[step], Plain(bytes));
    }

    private static CalcAnswer Duration(double seconds)
    {
        var total = Math.Round(seconds);
        if (Math.Abs(seconds) < 60)
            return new CalcAnswer(Group(Math.Round(seconds, 2)) + " s", Plain(seconds));
        var parts = new List<string>();
        var left = (long)Math.Abs(total);
        if (left >= 86400)
            parts.Add(left / 86400 + " d");
        if (left % 86400 / 3600 > 0)
            parts.Add(left % 86400 / 3600 + " h");
        if (left % 3600 / 60 > 0)
            parts.Add(left % 3600 / 60 + " min");
        if (left % 60 > 0)
            parts.Add(left % 60 + " s");
        return new CalcAnswer((total < 0 ? "-" : "") + string.Join(" ", parts), Plain(seconds));
    }

    private static double Tidy(double value) => Math.Abs(value) < 1e15 ? Math.Round(value, 10) : value;

    private static string Group(double value) =>
        Math.Abs(value) >= 1e15 ? value.ToString("G15", CultureInfo.InvariantCulture) : Tidy(value).ToString("#,0.##########", CultureInfo.InvariantCulture);

    private static string Plain(double value) =>
        Math.Abs(value) >= 1e15 ? value.ToString("G15", CultureInfo.InvariantCulture) : Tidy(value).ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Reads the text left to right, one rule per level of precedence.</summary>
    private sealed class Parser(string text)
    {
        private int at;

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return at >= text.Length;
            }
        }

        private void SkipSpace()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
        }

        private bool Take(char c)
        {
            SkipSpace();
            if (at < text.Length && text[at] == c)
            {
                at++;
                return true;
            }
            return false;
        }

        private bool TakeWord(string word)
        {
            SkipSpace();
            if (string.CompareOrdinal(text, at, word, 0, word.Length) == 0
                && (at + word.Length >= text.Length || !char.IsLetter(text[at + word.Length])))
            {
                at += word.Length;
                return true;
            }
            return false;
        }

        /// <summary>A sum, or "size at speed": how long the size takes at that speed.</summary>
        public Value Full()
        {
            var value = Sum();
            if (TakeWord("at"))
                value = Divide(value, Sum());
            return value;
        }

        private Value Sum()
        {
            var value = Product();
            while (true)
            {
                if (Take('+'))
                    value = Add(value, Product(), 1);
                else if (Take('-'))
                    value = Add(value, Product(), -1);
                else
                    return value;
            }
        }

        private Value Product()
        {
            var value = Power();
            while (true)
            {
                if (Take('*'))
                    value = Multiply(value, Power());
                else if (Take('/'))
                    value = Divide(value, Power());
                else
                    return value;
            }
        }

        private Value Power()
        {
            var value = Unary();
            if (!Take('^'))
                return value;
            var exponent = Power(); // right to left: 2^3^2 = 2^9
            if (value.Kind != Kind.Number || exponent.Kind != Kind.Number)
                throw new FormatException();
            return new Value(Math.Pow(value.Amount, exponent.Amount), Kind.Number);
        }

        private Value Unary()
        {
            if (Take('-'))
            {
                var value = Unary();
                return value with { Amount = -value.Amount };
            }
            if (Take('+'))
                return Unary();
            return Postfix();
        }

        private Value Postfix()
        {
            var value = Primary();
            if (Take('%'))
            {
                if (value.Kind != Kind.Number)
                    throw new FormatException();
                value = value with { Amount = value.Amount / 100 };
            }
            // A unit straight after a number or a bracket: 5 gb, (2+3) min.
            var mark = at;
            if (value.Kind == Kind.Number && Word() is { } name)
            {
                if (Units.TryGetValue(name, out var unit))
                    return new Value(value.Amount * unit.Factor, unit.Kind);
                at = mark; // "at", "in" and such belong to the rule above
            }
            return value;
        }

        private Value Primary()
        {
            SkipSpace();
            if (at >= text.Length)
                throw new FormatException();
            if (Take('('))
            {
                var inner = Full();
                if (!Take(')'))
                    throw new FormatException();
                return inner;
            }
            if (char.IsAsciiDigit(text[at]) || text[at] == '.')
            {
                var start = at;
                while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '.'))
                    at++;
                if (!double.TryParse(text.AsSpan(start, at - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    throw new FormatException();
                return new Value(number, Kind.Number);
            }
            var name = Word() ?? throw new FormatException();
            switch (name)
            {
                case "pi":
                    return new Value(Math.PI, Kind.Number);
                case "e":
                    return new Value(Math.E, Kind.Number);
            }
            if (Units.TryGetValue(name, out var unit))
                return new Value(unit.Factor, unit.Kind); // "gb in mb": one of them
            if (!Take('('))
                throw new FormatException();
            var argument = Full();
            if (!Take(')') || argument.Kind != Kind.Number)
                throw new FormatException();
            var x = argument.Amount;
            return new Value(name switch
            {
                "sqrt" => Math.Sqrt(x),
                "abs" => Math.Abs(x),
                "round" => Math.Round(x, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(x),
                "ceil" => Math.Ceiling(x),
                "log" => Math.Log10(x),
                "ln" => Math.Log(x),
                "sin" => Math.Sin(x),
                "cos" => Math.Cos(x),
                "tan" => Math.Tan(x),
                _ => throw new FormatException(),
            }, Kind.Number);
        }

        /// <summary>Letters, and "/s" after a size (mb/s).</summary>
        private string? Word()
        {
            SkipSpace();
            var start = at;
            while (at < text.Length && char.IsLetter(text[at]))
                at++;
            if (at == start)
                return null;
            var word = text[start..at];
            if (at + 1 < text.Length && text[at] == '/' && text[at + 1] == 's' && (at + 2 >= text.Length || !char.IsLetter(text[at + 2]))
                && Units.ContainsKey(word + "/s"))
            {
                at += 2;
                word += "/s";
            }
            return word;
        }

        private static Value Add(Value left, Value right, int sign)
        {
            if (left.Kind != right.Kind)
                throw new FormatException();
            return new Value(left.Amount + sign * right.Amount, left.Kind);
        }

        private static Value Multiply(Value left, Value right)
        {
            var kind = (left.Kind, right.Kind) switch
            {
                (Kind.Number, var other) => other,
                (var other, Kind.Number) => other,
                (Kind.Rate, Kind.Seconds) or (Kind.Seconds, Kind.Rate) => Kind.Bytes,
                _ => throw new FormatException(),
            };
            return new Value(left.Amount * right.Amount, kind);
        }

        private static Value Divide(Value left, Value right)
        {
            if (right.Amount == 0)
                throw new FormatException();
            var kind = (left.Kind, right.Kind) switch
            {
                (var same, var other) when same == other => Kind.Number,
                (var other, Kind.Number) => other,
                (Kind.Bytes, Kind.Rate) => Kind.Seconds,
                (Kind.Bytes, Kind.Seconds) => Kind.Rate,
                _ => throw new FormatException(),
            };
            return new Value(left.Amount / right.Amount, kind);
        }
    }
}
