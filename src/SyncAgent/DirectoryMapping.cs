using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.SyncAgent;

/// <summary>One LDAP entry as read: its DN and the raw values of each attribute (names are case-insensitive).</summary>
public sealed class LdapEntry(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<byte[]>> attributes)
{
    private readonly Dictionary<string, IReadOnlyList<byte[]>> _attributes = new(attributes, StringComparer.OrdinalIgnoreCase);

    public string DistinguishedName { get; } = distinguishedName;

    public IReadOnlyList<byte[]> Values(string name) => _attributes.TryGetValue(name, out var v) ? v : [];

    public string? Text(string name) => Values(name) is [var first, ..] ? System.Text.Encoding.UTF8.GetString(first) : null;

    public IEnumerable<string> Texts(string name) => Values(name).Select(v => System.Text.Encoding.UTF8.GetString(v));

    public bool Has(string name) => _attributes.ContainsKey(name);
}

/// <summary>
/// Turns Active Directory entries into the canonical directory records of spec 003 (FR-018). Accounts are identified by
/// <c>objectGUID</c>, never by name, email or DN. Every mapped attribute is read for every changed entry, so an absent
/// attribute is reported as cleared (an empty string) rather than "not reported".
/// </summary>
public static class DirectoryMapping
{
    /// <summary>The user attributes the agent reads. Nothing else leaves the customer network.</summary>
    public static readonly string[] UserAttributes =
    [
        "objectGUID", "userAccountControl", "displayName", "mail", "userPrincipalName", "title", "physicalDeliveryOfficeName", "telephoneNumber",
        "employeeID", "preferredLanguage", "company", "department", "division", "mobile", "manager", "isDeleted", "objectClass",
    ];

    public static readonly string[] GroupAttributes = ["objectGUID", "displayName", "cn", "isDeleted", "objectClass"];

    private const int AccountDisabled = 0x2;

    /// <summary>The <c>objectGUID</c> in the standard form, or null when the entry has none.</summary>
    public static string? ObjectGuid(LdapEntry entry) =>
        entry.Values("objectGUID") is [{ Length: 16 } bytes, ..] ? new Guid(bytes).ToString("D") : null;

    public static bool IsDeleted(LdapEntry entry) => string.Equals(entry.Text("isDeleted"), "TRUE", StringComparison.OrdinalIgnoreCase);

    public static bool IsGroup(LdapEntry entry) => entry.Texts("objectClass").Any(c => string.Equals(c, "group", StringComparison.OrdinalIgnoreCase));

    public static DirectoryUserRecord? User(LdapEntry entry, Func<string, string?> managerId)
    {
        var id = ObjectGuid(entry);
        if (id is null)
        {
            return null;
        }
        if (IsDeleted(entry))
        {
            return new DirectoryUserRecord(id, true, null, null, null, null, null, null, null, null, null);
        }
        var control = int.TryParse(entry.Text("userAccountControl"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var uac)
            ? uac : AccountDisabled;
        string Value(string name) => Limit(entry.Text(name)) ?? string.Empty;
        var manager = entry.Text("manager") is { Length: > 0 } dn ? managerId(dn) ?? string.Empty : string.Empty;
        return new DirectoryUserRecord(
            id, false, (control & AccountDisabled) == 0, Value("displayName"), Value("mail"), Value("userPrincipalName"), Value("title"),
            Value("physicalDeliveryOfficeName"), Value("telephoneNumber"), Value("employeeID"), Value("preferredLanguage"),
            CompanyName: Value("company"), Department: Value("department"), Division: Value("division"), WorkPhone: Value("telephoneNumber"),
            MobilePhone: Value("mobile"), ManagerId: manager);
    }

    /// <summary>
    /// A group with its whole current membership as objectGUIDs. The agent reads every member of a changed group, so the
    /// record replaces the membership TeamBeats knew (<see cref="DirectoryGroupRecord.MembersComplete"/>).
    /// </summary>
    public static DirectoryGroupRecord? Group(LdapEntry entry, IReadOnlyCollection<string> members)
    {
        var id = ObjectGuid(entry);
        if (id is null)
        {
            return null;
        }
        if (IsDeleted(entry))
        {
            return new DirectoryGroupRecord(id, true, null, [], []);
        }
        var name = Limit(entry.Text("displayName")) ?? Limit(entry.Text("cn")) ?? string.Empty;
        return new DirectoryGroupRecord(id, false, name, members.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), [], MembersComplete: true);
    }

    /// <summary>
    /// The LDAP filter for a read: the object kind, the administrator's own filter when set, and changes after the cursor
    /// (<c>uSNChanged</c>) on an incremental read.
    /// </summary>
    public static string Filter(string kind, string? custom, long? afterUsn) =>
        "(&" + kind + (custom ?? string.Empty) + (afterUsn is { } usn ? $"(uSNChanged>={usn + 1})" : string.Empty) + ")";

    public const string UserKind = "(objectCategory=person)(objectClass=user)";
    public const string GroupKind = "(objectClass=group)";

    private static string? Limit(string? s) => s is { Length: > 400 } ? s[..400] : s;
}
