using System.Globalization;

namespace Gamehook.Domain.Property;

// Deliberately small grammar: numeric literals, x, parentheses, unary signs and
// arithmetic. Anything else remains JavaScript; never partially accept an expression.
internal static class InlineScriptingProvider
{
    public static bool TryCompile(string source, out Func<double, double> evaluate)
    {
        try
        {
            if (source.Contains("++", StringComparison.Ordinal) || source.Contains("--", StringComparison.Ordinal))
                throw new FormatException();
            var parser = new Parser(source);
            evaluate = parser.Parse();
            return true;
        }
        catch (FormatException)
        {
            evaluate = null!;
            return false;
        }
    }

    private sealed class Parser(string source)
    {
        private int position;

        public Func<double, double> Parse()
        {
            var result = Sum();
            SkipSpace();
            if (position != source.Length) throw new FormatException();
            return result;
        }

        private void SkipSpace()
        {
            while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
        }

        private bool Take(char token)
        {
            SkipSpace();
            if (position == source.Length || source[position] != token) return false;
            position++;
            return true;
        }

        private Func<double, double> Sum()
        {
            var left = Product();
            while (true)
            {
                var previous = left;
                if (Take('+')) { var right = Product(); left = x => previous(x) + right(x); }
                else if (Take('-')) { var right = Product(); left = x => previous(x) - right(x); }
                else return left;
            }
        }

        private Func<double, double> Product()
        {
            var left = Atom();
            while (true)
            {
                var previous = left;
                if (Take('*')) { var right = Atom(); left = x => previous(x) * right(x); }
                else if (Take('/')) { var right = Atom(); left = x => previous(x) / right(x); }
                else if (Take('%')) { var right = Atom(); left = x => previous(x) % right(x); }
                else return left;
            }
        }

        private Func<double, double> Atom()
        {
            SkipSpace();
            // ++ and -- have JavaScript mutation semantics, not repeated unary signs.
            if (source.AsSpan(position).StartsWith("++") || source.AsSpan(position).StartsWith("--"))
                throw new FormatException();
            if (Take('+')) return Atom();
            if (Take('-')) { var operand = Atom(); return x => -operand(x); }
            if (Take('('))
            {
                var value = Sum();
                if (!Take(')')) throw new FormatException();
                return value;
            }
            if (Take('x')) return x => x;
            foreach (var name in new[] { "Math.floor", "Math.round" })
            {
                if (!source.AsSpan(position).StartsWith(name)) continue;
                position += name.Length;
                if (!Take('(')) throw new FormatException();
                var argument = Sum();
                if (!Take(')')) throw new FormatException();
                return name == "Math.floor"
                    ? x => Math.Floor(argument(x))
                    : x => Round(argument(x));
            }
            var start = position;
            if (source.AsSpan(position).StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                position += 2;
                var digits = position;
                while (position < source.Length && char.IsAsciiHexDigit(source[position])) position++;
                if (!ulong.TryParse(source.AsSpan(digits, position - digits), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var hex)) throw new FormatException();
                return _ => hex;
            }
            while (position < source.Length && (char.IsAsciiDigit(source[position]) || source[position] == '.')) position++;
            var literal = source[start..position];
            // Legacy JavaScript leading-zero literals can be octal.
            if (literal.Length > 1 && literal[0] == '0' && char.IsAsciiDigit(literal[1])) throw new FormatException();
            if (!double.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                throw new FormatException();
            return _ => number;
        }

        private static double Round(double value)
        {
            var floor = Math.Floor(value);
            var result = value - floor < 0.5 ? floor : Math.Ceiling(value);
            return result == 0 ? Math.CopySign(0, value) : result;
        }
    }
}
