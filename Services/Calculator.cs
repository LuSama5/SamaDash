using System.Globalization;
using System.Text.RegularExpressions;

namespace SamaDash.Services;

/// <summary>Calcoli (+ - * / ^ % e parentesi) e conversioni di unità scritti nella ricerca.</summary>
public static partial class Calculator
{
    static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string Format(double value) =>
        Math.Round(value, 10).ToString("#,0.##########", Italian);

    // ---------- Espressioni ----------

    [GeneratedRegex(@"^[\d\s.,+\-*/^%()x×÷]+$")]
    private static partial Regex MathChars();

    [GeneratedRegex(@"\d\s*[+\-*/^%x×÷]|[+\-*/^x×÷]\s*[\d(]|\)\s*[\d(]")]
    private static partial Regex HasOperator();

    // "2024-10-05" o "2024-10" sono date (nomi di file), non sottrazioni
    [GeneratedRegex(@"^\d{4}-\d{1,2}(-\d{1,2})?$")]
    private static partial Regex LooksLikeDate();

    /// <summary>True solo per espressioni vere: un numero da solo (es. "2024") non conta.</summary>
    public static bool TryEvaluate(string input, out double result)
    {
        result = 0;
        input = input.Trim();
        if (!MathChars().IsMatch(input) || !HasOperator().IsMatch(input) || !input.Any(char.IsDigit) ||
            LooksLikeDate().IsMatch(input))
            return false;
        try
        {
            var parser = new Parser(input.Replace('x', '*').Replace('×', '*').Replace('÷', '/'));
            result = parser.ParseExpression();
            if (!parser.AtEnd) return false;
            return double.IsFinite(result);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    sealed class Parser(string text)
    {
        int pos;

        public bool AtEnd { get { SkipSpaces(); return pos >= text.Length; } }

        // expr := term (('+'|'-') term)*
        public double ParseExpression()
        {
            double value = ParseTerm();
            while (true)
            {
                if (Accept('+')) value += ParseTerm();
                else if (Accept('-')) value -= ParseTerm();
                else return value;
            }
        }

        // term := unary (('*'|'/') unary)*
        double ParseTerm()
        {
            double value = ParseUnary();
            while (true)
            {
                if (Accept('*')) value *= ParseUnary();
                else if (Accept('/'))
                {
                    double divisor = ParseUnary();
                    if (divisor == 0) throw new FormatException("divisione per zero");
                    value /= divisor;
                }
                else return value;
            }
        }

        // il segno vale dopo la potenza: -2^2 = -4
        double ParseUnary()
        {
            if (Accept('-')) return -ParseUnary();
            if (Accept('+')) return ParseUnary();
            return ParsePower();
        }

        // power := percent ('^' unary)?   (associativa a destra, ammette 2^-1)
        double ParsePower()
        {
            double value = ParsePercent();
            return Accept('^') ? Math.Pow(value, ParseUnary()) : value;
        }

        // "50%" = 0,5
        double ParsePercent()
        {
            double value = ParsePrimary();
            while (Accept('%')) value /= 100;
            return value;
        }

        double ParsePrimary()
        {
            if (Accept('('))
            {
                double value = ParseExpression();
                if (!Accept(')')) throw new FormatException("parentesi non chiusa");
                return value;
            }
            SkipSpaces();
            int start = pos;
            while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] is '.' or ',')) pos++;
            if (start == pos) throw new FormatException("numero atteso");
            return ParseNumber(text[start..pos]);
        }

        bool Accept(char c)
        {
            SkipSpaces();
            if (pos < text.Length && text[pos] == c)
            {
                pos++;
                return true;
            }
            return false;
        }

        void SkipSpaces()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }
    }

    /// <summary>Virgola e punto valgono entrambi come separatore decimale.</summary>
    static double ParseNumber(string s)
    {
        s = s.Replace(',', '.');
        if (s.Count(c => c == '.') > 1) throw new FormatException("numero non valido");
        return double.Parse(s, CultureInfo.InvariantCulture);
    }

    // ---------- Unità ----------

    [GeneratedRegex(@"^\s*(-?[\d.,]+)\s*([a-zA-Z°/]+)\s+(?:in|to|a)\s+([a-zA-Z°/]+)\s*$", RegexOptions.IgnoreCase)]
    public static partial Regex ConversionPattern();

    // fattore verso l'unità base di ogni gruppo
    static readonly Dictionary<string, (string Group, double Factor)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = ("len", 0.001), ["cm"] = ("len", 0.01), ["m"] = ("len", 1), ["km"] = ("len", 1000),
        ["inch"] = ("len", 0.0254), ["pollici"] = ("len", 0.0254), ["ft"] = ("len", 0.3048), ["piedi"] = ("len", 0.3048),
        ["yd"] = ("len", 0.9144), ["mi"] = ("len", 1609.344), ["miglia"] = ("len", 1609.344),
        ["g"] = ("mass", 0.001), ["kg"] = ("mass", 1), ["t"] = ("mass", 1000),
        ["oz"] = ("mass", 0.028349523125), ["lb"] = ("mass", 0.45359237), ["lbs"] = ("mass", 0.45359237),
        ["ml"] = ("vol", 0.001), ["l"] = ("vol", 1), ["gal"] = ("vol", 3.785411784),
        ["kmh"] = ("speed", 1), ["km/h"] = ("speed", 1), ["mph"] = ("speed", 1.609344), ["m/s"] = ("speed", 3.6),
        ["b"] = ("data", 1), ["kb"] = ("data", 1024), ["mb"] = ("data", 1024d * 1024),
        ["gb"] = ("data", 1024d * 1024 * 1024), ["tb"] = ("data", 1024d * 1024 * 1024 * 1024),
        ["c"] = ("temp", 0), ["°c"] = ("temp", 0), ["f"] = ("temp", 0), ["°f"] = ("temp", 0), ["k"] = ("temp", 0),
    };

    /// <summary>Es. "10 km in mi" → "6,2137 mi".</summary>
    public static bool TryConvertUnits(string input, out string result)
    {
        result = "";
        var match = ConversionPattern().Match(input);
        if (!match.Success) return false;
        string from = match.Groups[2].Value, to = match.Groups[3].Value;
        if (!Units.TryGetValue(from, out var a) || !Units.TryGetValue(to, out var b) || a.Group != b.Group)
            return false;

        double amount;
        try { amount = ParseNumber(match.Groups[1].Value); }
        catch (FormatException) { return false; }

        double value = a.Group == "temp"
            ? FromKelvin(ToKelvin(amount, from), to)
            : amount * a.Factor / b.Factor;
        result = $"{Format(Math.Round(value, 4))} {to}";
        return true;
    }

    static double ToKelvin(double v, string unit) => unit.TrimStart('°').ToLowerInvariant() switch
    {
        "c" => v + 273.15,
        "f" => (v - 32) * 5 / 9 + 273.15,
        _ => v,
    };

    static double FromKelvin(double v, string unit) => unit.TrimStart('°').ToLowerInvariant() switch
    {
        "c" => v - 273.15,
        "f" => (v - 273.15) * 9 / 5 + 32,
        _ => v,
    };
}
