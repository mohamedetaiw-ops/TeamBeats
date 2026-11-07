using System.Globalization;
using System.Text;

namespace TeamBeats.Modules.People;

/// <summary>
/// Text normalization for people search (spec 010 FR-006, US1 scenario 2). Arabic letter variants and diacritics are
/// folded; Latin names also get a consonant skeleton so common transliterations meet ("Mohamed", "Mohammed" and
/// "Muhammad" all become <c>mhmd</c>). Phone numbers are matched as digits.
/// </summary>
public static class SearchText
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var raw in decomposed)
        {
            var c = raw;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == 'ـ')
            {
                continue; // Latin accents, Arabic tashkeel and the superscript alef; tatweel.
            }
            c = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', // alef with hamza or madda, alef wasla
                'ى' => 'ي', // alef maksura
                'ة' => 'ه', // ta marbuta
                'ؤ' => 'و', // waw with hamza
                'ئ' => 'ي', // ya with hamza
                _ => c,
            };
            if (char.IsWhiteSpace(c) || c is ',' or ';' or '(' or ')' or '-' or '_' or '/')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Space-separated skeletons of the Latin words in a name, with a leading and trailing space for word matching.</summary>
    public static string Skeletons(string? name)
    {
        var words = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Skeleton).Where(s => s.Length > 0).ToList();
        return words.Count == 0 ? string.Empty : " " + string.Join(' ', words) + " ";
    }

    /// <summary>First letter, then the consonants with doubled letters collapsed. Empty for words that are not Latin.</summary>
    public static string Skeleton(string word)
    {
        if (word.Length == 0 || !word.All(c => c is >= 'a' and <= 'z'))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(word.Length);
        sb.Append(word[0]);
        foreach (var c in word.AsSpan(1))
        {
            if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y' || sb[^1] == c)
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>ASCII digits only; Arabic-Indic and Eastern Arabic-Indic digits are converted.</summary>
    public static string Digits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (c is >= '٠' and <= '٩')
            {
                sb.Append((char)('0' + (c - '٠')));
            }
            else if (c is >= '۰' and <= '۹')
            {
                sb.Append((char)('0' + (c - '۰')));
            }
        }
        return sb.ToString();
    }

    /// <summary>One location key per place: trimmed, inner spaces collapsed, case folded (US5 scenario 2).</summary>
    public static string? LocationKey(string? value)
    {
        var parts = value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts is null || parts.Length == 0 ? null : string.Join(' ', parts).ToLowerInvariant();
    }
}
