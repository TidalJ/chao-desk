using System.Globalization;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// Evaluates simple arithmetic typed into the launcher: + − × ÷ (also * /), % (remainder), ^ (power),
/// parentheses and unary minus. Text that is not an expression with at least one operator gives null.
/// </summary>
public static class Calculator
{
    public static double? TryEvaluate(string text)
    {
        string s = text.Replace(" ", "").Replace('×', '*').Replace('÷', '/').Replace('（', '(').Replace('）', ')').Replace("**", "^");
        if (s.Length < 3 || !s.Any(char.IsDigit) || !s.Skip(1).Any(c => c is '+' or '-' or '*' or '/' or '%' or '^')) return null;
        if (s.Any(c => !(char.IsDigit(c) || c is '.' or '+' or '-' or '*' or '/' or '%' or '^' or '(' or ')'))) return null;
        try
        {
            int pos = 0;
            double v = ParseSum(s, ref pos);
            return pos == s.Length && double.IsFinite(v) ? v : null;
        }
        catch (FormatException) { return null; }
    }

    public static string Format(double v) =>
        Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0) ? v.ToString("G10", CultureInfo.InvariantCulture) : Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);

    private static double ParseSum(string s, ref int pos)
    {
        double v = ParseProduct(s, ref pos);
        while (pos < s.Length && s[pos] is '+' or '-')
        {
            char op = s[pos++];
            double r = ParseProduct(s, ref pos);
            v = op == '+' ? v + r : v - r;
        }
        return v;
    }

    private static double ParseProduct(string s, ref int pos)
    {
        double v = ParsePower(s, ref pos);
        while (pos < s.Length && s[pos] is '*' or '/' or '%')
        {
            char op = s[pos++];
            double r = ParsePower(s, ref pos);
            v = op switch { '*' => v * r, '/' => v / r, _ => v % r };
        }
        return v;
    }

    private static double ParsePower(string s, ref int pos)
    {
        double b = ParseUnary(s, ref pos);
        if (pos < s.Length && s[pos] == '^')
        {
            pos++;
            return Math.Pow(b, ParsePower(s, ref pos)); // right-associative
        }
        return b;
    }

    private static double ParseUnary(string s, ref int pos)
    {
        if (pos < s.Length && s[pos] is '-' or '+')
        {
            bool neg = s[pos++] == '-';
            double v = ParseUnary(s, ref pos);
            return neg ? -v : v;
        }
        return ParseAtom(s, ref pos);
    }

    private static double ParseAtom(string s, ref int pos)
    {
        if (pos < s.Length && s[pos] == '(')
        {
            pos++;
            double v = ParseSum(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') throw new FormatException();
            pos++;
            return v;
        }
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
        if (start == pos) throw new FormatException();
        return double.Parse(s.AsSpan(start, pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }
}
