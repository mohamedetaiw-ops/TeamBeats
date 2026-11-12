using System.Numerics;
using System.Text.RegularExpressions;

namespace TeamBeats.Modules.Hr.DataChanges;

/// <summary>One field a person can ask to change: its key, how long it may be, whether it is required, and its check.</summary>
public sealed record DataChangeField(string Key, int MaxLength, bool Required, IReadOnlyList<string>? Choices = null, string? Check = null);

/// <summary>
/// A group of fields changed together (spec 022 section 3: address, emergency contact, bank details, marital status,
/// dependants). <see cref="Sensitive"/> groups notify the person on every channel (FR-020) and their document goes with
/// the values once the change is written (FR-019). <see cref="DocumentByDefault"/> is the tenant default for "needs a
/// supporting document".
/// </summary>
public sealed record DataChangeGroup(string Key, IReadOnlyList<DataChangeField> Fields, bool Sensitive, bool DocumentByDefault);

/// <summary>The fields of employee data change requests and their checks (pure, no I/O).</summary>
public static partial class DataChangeCatalogue
{
    public const string Address = "address";
    public const string EmergencyContact = "emergency_contact";
    public const string Bank = "bank";
    public const string MaritalStatus = "marital_status";
    public const string Dependants = "dependants";

    private const string Iban = "iban";
    private const string Phone = "phone";
    private const string Count = "count";

    public static readonly IReadOnlyList<DataChangeGroup> Groups =
    [
        new(Address, [new("address_line", 200, true), new("city", 100, true), new("country", 100, true), new("postal_code", 20, false)], false, false),
        new(EmergencyContact, [new("contact_name", 120, true), new("relationship", 60, true), new("phone", 30, true, Check: Phone)], false, false),
        new(Bank, [new("bank_name", 120, true), new("account_name", 120, true), new("iban", 34, true, Check: Iban)], true, true),
        new(MaritalStatus, [new("marital_status", 20, true, ["single", "married", "divorced", "widowed"])], false, true),
        new(Dependants, [new("dependants_count", 2, true, Check: Count)], false, true),
    ];

    public static DataChangeGroup? Find(string? key) => Groups.FirstOrDefault(g => g.Key == key);

    [GeneratedRegex(@"^\+?[0-9 ()-]{6,30}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanPattern();

    /// <summary>An IBAN as typed, upper case and without spaces, when its check digits are right (ISO 13616 mod 97).</summary>
    public static string? NormalizeIban(string? value)
    {
        var clean = new string((value ?? string.Empty).Where(c => c != ' ').Select(char.ToUpperInvariant).ToArray());
        if (!IbanPattern().IsMatch(clean))
        {
            return null;
        }
        var digits = string.Concat((clean[4..] + clean[..4]).Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) % 97 == 1 ? clean : null;
    }

    /// <summary>
    /// The clean values for a group, or the keys of the fields that are wrong. Unknown fields are wrong; an optional
    /// field may be left out. Values are trimmed plain text without control characters.
    /// </summary>
    public static (Dictionary<string, string> Values, List<string> Errors) Check(DataChangeGroup group, IReadOnlyDictionary<string, string?>? input)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var given = input ?? new Dictionary<string, string?>();
        errors.AddRange(given.Keys.Where(k => group.Fields.All(f => f.Key != k)));
        foreach (var field in group.Fields)
        {
            var value = given.TryGetValue(field.Key, out var v) ? v?.Trim() : null;
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required)
                {
                    errors.Add(field.Key);
                }
                continue;
            }
            string? clean = value.Length > field.MaxLength + (field.Check == Iban ? 10 : 0) || value.Any(char.IsControl) ? null : field.Check switch
            {
                Iban => NormalizeIban(value),
                Phone => PhonePattern().IsMatch(value) ? value : null,
                Count => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 20
                    ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                _ => field.Choices is { } choices ? (choices.Contains(value) ? value : null) : value,
            };
            if (clean is null)
            {
                errors.Add(field.Key);
            }
            else
            {
                values[field.Key] = clean;
            }
        }
        return (values, errors);
    }
}
