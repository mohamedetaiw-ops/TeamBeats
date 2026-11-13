using System.Globalization;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Security.Contracts;

namespace TeamBeats.Modules.Security;

/// <summary>The built-in kinds of sensitive data (spec 026 plan, launch countries).</summary>
public static class SensitiveTypes
{
    public const string SaudiId = "saudi_id";
    public const string EmiratesId = "emirates_id";
    public const string EgyptId = "egypt_id";
    public const string QatarId = "qatar_id";
    public const string Iban = "iban";
    public const string PaymentCard = "payment_card";
    public const string Passport = "passport";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<string> BuiltIn = [SaudiId, EmiratesId, EgyptId, QatarId, Iban, PaymentCard, Passport];
}

/// <summary>A tenant pattern ready to run.</summary>
public sealed record TenantPattern(string Name, Regex Regex);

/// <summary>
/// Pattern matching for sensitive data (spec 026 US6, FR-015). Every built-in type checks its structure as well as its
/// shape (check digits, dates, codes) so ordinary numbers are not reported. Matches are counted, never returned.
/// </summary>
public static partial class SensitivePatterns
{
    /// <summary>The longest text scanned per part; the rest is not looked at.</summary>
    public const int MaxChars = 1_000_000;

    public static readonly TimeSpan TenantTimeout = TimeSpan.FromMilliseconds(100);

    [GeneratedRegex(@"(?<!\d)784[- ]?\d{4}[- ]?\d{7}[- ]?\d(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Emirates();

    [GeneratedRegex(@"(?<!\d)[23]\d{13}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Egypt();

    [GeneratedRegex(@"(?<!\d)[23]\d{10}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Qatar();

    [GeneratedRegex(@"(?<!\d)[12]\d{9}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Saudi();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanShape();

    [GeneratedRegex(@"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex Card();

    [GeneratedRegex(@"(?:passport|جواز)[^\n]{0,40}?(?<![A-Z0-9])([A-Z]{1,2}\d{6,8})(?![A-Z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Passport();

    private static readonly HashSet<int> EgyptGovernorates = [1, 2, 3, 4, 11, 12, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23, 24, 25, 26, 27, 28, 29, 31, 32, 33, 34, 35, 88];

    /// <summary>
    /// Counts each kind of sensitive data in the text. A stretch of text counts once, for the most specific kind (an
    /// Emirates ID is not also a card number). Passports never block, since their form is too common to be sure.
    /// </summary>
    public static IReadOnlyList<(string Type, string? Name, int Count, bool Blocking)> Find(string text, IReadOnlyList<TenantPattern> tenant)
    {
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
        }
        var taken = new List<(int Start, int End)>();
        var counts = new List<(string Type, string? Name, int Count, bool Blocking)>();

        void Run(string type, Regex regex, Func<Match, bool> valid, bool blocking = true, int group = 0)
        {
            var count = 0;
            foreach (Match m in regex.Matches(text))
            {
                var g = m.Groups[group];
                var span = (g.Index, g.Index + g.Length);
                if (taken.Any(t => t.Start < span.Item2 && span.Item1 < t.End) || !valid(m))
                {
                    continue;
                }
                taken.Add(span);
                count++;
            }
            if (count > 0)
            {
                counts.Add((type, null, count, blocking));
            }
        }

        Run(SensitiveTypes.EmiratesId, Emirates(), m => Luhn(Digits(m.Value)));
        Run(SensitiveTypes.EgyptId, Egypt(), m => EgyptValid(m.Value));
        Run(SensitiveTypes.Iban, IbanShape(), m => IbanValid(m.Value.Replace(" ", "", StringComparison.Ordinal)));
        Run(SensitiveTypes.PaymentCard, Card(), m => Digits(m.Value) is { Length: >= 13 and <= 19 } d && d[0] is >= '2' and <= '6' && Luhn(d));
        Run(SensitiveTypes.QatarId, Qatar(), m => int.Parse(m.Value.AsSpan(3, 3), CultureInfo.InvariantCulture) > 0);
        Run(SensitiveTypes.SaudiId, Saudi(), m => Luhn(m.Value));
        Run(SensitiveTypes.Passport, Passport(), _ => true, blocking: false, group: 1);

        foreach (var pattern in tenant)
        {
            var count = Count(pattern.Regex, text);
            if (count > 0)
            {
                counts.Add((SensitiveTypes.Custom, pattern.Name, count, true));
            }
        }
        return counts;
    }

    /// <summary>Matches of a tenant pattern; a pattern that runs out of time counts what it found before.</summary>
    public static int Count(Regex regex, string text)
    {
        var count = 0;
        try
        {
            for (var m = regex.Match(text); m.Success && count < 10_000; m = m.NextMatch())
            {
                if (m.Length > 0)
                {
                    count++;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return count;
    }

    /// <summary>Compiles a tenant pattern with the time limit, or null when it is not a valid expression.</summary>
    public static Regex? Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TenantTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    public static bool Luhn(string digits)
    {
        var sum = 0;
        var doubled = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubled)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }
            sum += d;
            doubled = !doubled;
        }
        return sum % 10 == 0;
    }

    /// <summary>Egyptian national ID: century digit, a real birth date, and a governorate code.</summary>
    public static bool EgyptValid(string id)
    {
        var century = id[0] == '2' ? 1900 : 2000;
        var year = century + int.Parse(id.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(id.AsSpan(3, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(id.AsSpan(5, 2), CultureInfo.InvariantCulture);
        var governorate = int.Parse(id.AsSpan(7, 2), CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && EgyptGovernorates.Contains(governorate);
    }

    /// <summary>IBAN: 15 to 34 characters and the ISO 13616 mod 97 check.</summary>
    public static bool IbanValid(string iban)
    {
        if (iban.Length is < 15 or > 34)
        {
            return false;
        }
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value > 9 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }

    /// <summary>The findings for each part, as the contract returns them.</summary>
    public static List<SensitiveFinding> Scan(IReadOnlyList<SensitiveText> parts, IReadOnlyList<TenantPattern> tenant) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .SelectMany(p => Find(p.Text, tenant).Select(f => new SensitiveFinding(f.Type, f.Name, p.Location, f.Count, f.Blocking)))
            .ToList();
}
