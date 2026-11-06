using System.Globalization;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Omnibox;

// Respostas instantâneas na omnibox: contas e conversões de unidades, calculadas localmente.
public static partial class InstantAnswer
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static string? Answer(string input) => Calculate(input) ?? Convert(input);

    // ---------- Contas ----------

    public static string? Calculate(string input)
    {
        var text = input.Trim().TrimStart('=').Trim().ToLowerInvariant();
        if (text.Length == 0 || !text.Any(char.IsDigit)) return null;
        if (!HasOperation().IsMatch(text)) return null;
        if (Regex.Replace(text, "sqrt|pi", "").Any(char.IsLetter)) return null;

        var parser = new Parser(text);
        var value = parser.Parse();
        return value is { } v && double.IsFinite(v) ? Format(v, 9) : null;
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public double? Parse()
        {
            try
            {
                var value = Expression();
                Skip();
                return _i == text.Length ? value : null;
            }
            catch (FormatException) { return null; }
        }

        private double Expression()
        {
            var value = Term();
            while (true)
            {
                if (Take('+')) value += Term();
                else if (Take('-')) value -= Term();
                else return value;
            }
        }

        private double Term()
        {
            var value = Power();
            while (true)
            {
                if (Take('*') || Take('×')) value *= Power();
                else if (Take('/') || Take('÷'))
                {
                    var divisor = Power();
                    if (divisor == 0) throw new FormatException();
                    value /= divisor;
                }
                else return value;
            }
        }

        private double Power()
        {
            var value = Unary();
            return Take('^') ? Math.Pow(value, Power()) : value;
        }

        private double Unary()
        {
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            var value = Primary();
            while (Take('%')) value /= 100;
            return value;
        }

        private double Primary()
        {
            Skip();
            if (Take('('))
            {
                var inner = Expression();
                if (!Take(')')) throw new FormatException();
                return inner;
            }
            if (Word("sqrt"))
            {
                if (!Take('(')) throw new FormatException();
                var inner = Expression();
                if (!Take(')') || inner < 0) throw new FormatException();
                return Math.Sqrt(inner);
            }
            if (Word("pi")) return Math.PI;

            var start = _i;
            var separators = 0;
            while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] is '.' or ','))
            {
                if (text[_i] is '.' or ',') separators++;
                _i++;
            }
            if (_i == start || separators > 1) throw new FormatException();
            return double.Parse(text[start.._i].Replace(',', '.'), CultureInfo.InvariantCulture);
        }

        private bool Take(char c)
        {
            Skip();
            if (_i < text.Length && text[_i] == c) { _i++; return true; }
            return false;
        }

        private bool Word(string word)
        {
            Skip();
            if (string.CompareOrdinal(text, _i, word, 0, word.Length) != 0) return false;
            _i += word.Length;
            return true;
        }

        private void Skip()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
        }
    }

    // ---------- Conversões ----------

    private sealed record Unit(string Dimension, string Symbol, double Factor);

    private static readonly IReadOnlyDictionary<string, Unit> Units = BuildUnits();

    private static Dictionary<string, Unit> BuildUnits()
    {
        var units = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(Unit unit, params string[] names) { foreach (var n in names) units[n] = unit; }
        Add(new("length", "km", 1000), "km", "quilómetro", "quilómetros", "quilometros");
        Add(new("length", "m", 1), "m", "metro", "metros");
        Add(new("length", "cm", 0.01), "cm", "centímetro", "centímetros");
        Add(new("length", "mm", 0.001), "mm");
        Add(new("length", "mi", 1609.344), "mi", "milha", "milhas", "mile", "miles");
        Add(new("length", "ft", 0.3048), "ft", "pé", "pés", "feet");
        Add(new("length", "in", 0.0254), "in", "polegada", "polegadas", "inch", "inches");
        Add(new("mass", "kg", 1), "kg", "quilo", "quilos");
        Add(new("mass", "g", 0.001), "g", "grama", "gramas");
        Add(new("mass", "lb", 0.45359237), "lb", "lbs", "libra", "libras");
        Add(new("mass", "oz", 0.028349523125), "oz", "onça", "onças");
        Add(new("volume", "L", 1), "l", "litro", "litros");
        Add(new("volume", "mL", 0.001), "ml");
        Add(new("volume", "gal", 3.785411784), "gal", "galão", "galões");
        Add(new("temperature", "°C", 0), "c", "°c", "celsius");
        Add(new("temperature", "°F", 0), "f", "°f", "fahrenheit");
        Add(new("temperature", "K", 0), "k", "kelvin");
        return units;
    }

    public static string? Convert(string input)
    {
        var match = ConversionPattern().Match(input.Trim());
        if (!match.Success) return null;
        if (!Units.TryGetValue(match.Groups[2].Value.Replace(" ", ""), out var from)) return null;
        if (!Units.TryGetValue(match.Groups[3].Value.Replace(" ", ""), out var to)) return null;
        if (from.Dimension != to.Dimension) return null;

        var value = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var result = from.Dimension == "temperature" ? ConvertTemperature(value, from.Symbol, to.Symbol) : value * from.Factor / to.Factor;
        return Format(result, 2) + " " + to.Symbol;
    }

    private static double ConvertTemperature(double value, string from, string to)
    {
        var celsius = from switch { "°F" => (value - 32) * 5 / 9, "K" => value - 273.15, _ => value };
        return to switch { "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
    }

    private static string Format(double value, int decimals)
    {
        var rounded = Math.Round(value, decimals);
        if (Math.Abs(rounded) < Math.Pow(10, -decimals)) rounded = 0;
        return rounded.ToString("0." + new string('#', decimals), Pt);
    }

    [GeneratedRegex(@"[-+*/^%×÷]|sqrt")]
    private static partial Regex HasOperation();

    [GeneratedRegex(@"^(-?\d+(?:[.,]\d+)?)\s*(°?\s*[a-zA-Zçãéó]+)\s+(?:em|to|in|para)\s+(°?\s*[a-zA-Zçãéõó]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConversionPattern();
}
