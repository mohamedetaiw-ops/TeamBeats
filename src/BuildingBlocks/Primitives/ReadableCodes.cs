using System.Security.Cryptography;
using System.Text;

namespace TeamBeats.Primitives;

/// <summary>
/// IDs people read out or type: twelve characters in groups of four from an alphabet without look-alikes, for example
/// <c>K7QM-2XRT-HP4N</c>. Used for certificates that are verified by their ID.
/// </summary>
public static class ReadableCodes
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    public static string New()
    {
        var chars = new StringBuilder(14);
        for (var i = 0; i < 12; i++)
        {
            if (i is 4 or 8)
            {
                chars.Append('-');
            }
            chars.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return chars.ToString();
    }

    /// <summary>The code as stored, from what was typed: upper case, grouped, or null when it cannot be one.</summary>
    public static string? Normalize(string? code)
    {
        var clean = new string((code ?? "").Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 12 && clean.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? $"{clean[..4]}-{clean[4..8]}-{clean[8..]}" : null;
    }
}
