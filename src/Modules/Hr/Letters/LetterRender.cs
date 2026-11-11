using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamBeats.Modules.Hr.Letters;

/// <summary>
/// Pure letter text rules (spec 022 FR-021): which placeholders a template may use, filling them, and the printable
/// page. Templates are plain text; every value and every line is HTML encoded, so a template or an HR value can never
/// become markup or load anything.
/// </summary>
public static partial class LetterRender
{
    public const string Name = "name";
    public const string EmployeeNumber = "employee_number";
    public const string JobTitle = "job_title";
    public const string Grade = "grade";
    public const string HireDate = "hire_date";
    public const string Company = "company";
    public const string Addressee = "addressee";
    public const string Date = "date";
    public const string Reference = "reference";

    /// <summary>Placeholders filled from HR, the request and the letter itself.</summary>
    public static readonly IReadOnlyList<string> Known = [Name, EmployeeNumber, JobTitle, Grade, HireDate, Company, Addressee, Date, Reference];

    /// <summary>Placeholders that come from the person's HR record; HR types the value in when HR has none.</summary>
    public static readonly IReadOnlyList<string> FromHr = [Name, EmployeeNumber, JobTitle, Grade, HireDate];

    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,39})\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    public static IReadOnlyList<string> Placeholders(string template) =>
        PlaceholderPattern().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Western digits as Arabic-Indic digits, for an Arabic letter when the tenant uses them (spec 022 section 10).</summary>
    public static string ArabicDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c);
        }
        return sb.ToString();
    }

    public static string FormatDate(DateOnly date, string language, bool arabicDigits)
    {
        var text = date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo(language == "ar" ? "ar-AE" : "en-GB"));
        return language == "ar" && arabicDigits ? ArabicDigits(text) : text;
    }

    /// <summary>
    /// The page the converter prints: the company, the letter text in paragraphs, and the reference, verification code
    /// and verification address at the foot. Arabic pages run right to left. Nothing is loaded from outside.
    /// </summary>
    public static string Html(string language, string company, string body, IReadOnlyList<(string Label, string Value)> footer)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var rtl = language == "ar";
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<!doctype html><html lang=\"{language}\" dir=\"{(rtl ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">");
        html.Append("<style>body{font-family:'Noto Naskh Arabic','Noto Sans','DejaVu Sans',sans-serif;font-size:12pt;line-height:1.6;color:#111;margin:0}")
            .Append("header{font-size:16pt;font-weight:700;margin-bottom:24pt}p{margin:0 0 10pt}")
            .Append("footer{margin-top:36pt;padding-top:8pt;border-top:1px solid #999;font-size:9pt;color:#333}footer span{display:block}")
            .Append("bdi{unicode-bidi:isolate}</style></head><body>");
        html.Append(CultureInfo.InvariantCulture, $"<header>{E(company)}</header><main>");
        foreach (var paragraph in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var lines = paragraph.Split('\n').Select(E);
            html.Append("<p>").Append(string.Join("<br>", lines)).Append("</p>");
        }
        html.Append("</main><footer>");
        foreach (var (label, value) in footer)
        {
            html.Append(CultureInfo.InvariantCulture, $"<span>{E(label)}: <bdi dir=\"ltr\">{E(value)}</bdi></span>");
        }
        html.Append("</footer></body></html>");
        return html.ToString();
    }

    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    /// <summary>A verification code of 12 characters from an alphabet without look-alikes, in groups of four (about 59 bits).</summary>
    public static string NewCode()
    {
        var chars = new char[14];
        var j = 0;
        for (var i = 0; i < 12; i++)
        {
            if (i is 4 or 8)
            {
                chars[j++] = '-';
            }
            chars[j++] = CodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>The code as typed, upper case, without spaces or dashes, or null when it cannot be a code.</summary>
    public static string? NormalizeCode(string? code)
    {
        var clean = new string((code ?? string.Empty).Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 12 && clean.All(c => CodeAlphabet.Contains(c, StringComparison.Ordinal)) ? clean : null;
    }

    public static byte[] CodeHash(string normalized) => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
}
