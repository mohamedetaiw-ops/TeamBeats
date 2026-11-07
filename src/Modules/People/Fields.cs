namespace TeamBeats.Modules.People;

/// <summary>
/// Where a field's value comes from (spec 010 FR-002): the directory, the person themselves (employee-editable) or an
/// administrator. HR system ownership arrives with spec 019 in R2.
/// </summary>
public static class FieldSources
{
    public const string Directory = "directory";
    public const string Person = "person";
    public const string Admin = "admin";
}

/// <summary>
/// Who may see a field (spec 010 FR-003). <c>hr</c> (HR officers in scope and administrators) is needed for the
/// default rule of employee number in section 6. The person and administrators covering them always see every field.
/// </summary>
public static class FieldVisibility
{
    public const string Everyone = "everyone";
    public const string Subtree = "subtree";
    public const string ManagersAndHr = "managers_hr";
    public const string Hr = "hr";
    public const string Private = "private";

    public static readonly IReadOnlyList<string> All = [Everyone, Subtree, ManagersAndHr, Hr, Private];
}

/// <summary>A built-in profile field with its default source and visibility (section 6) and the sources a tenant may pick.</summary>
public sealed record FieldDefinition(string Key, string DefaultSource, string DefaultVisibility, IReadOnlyList<string> Sources, bool IsDate = false, bool IsPhone = false);

/// <summary>A field's effective policy in one tenant.</summary>
public sealed record FieldPolicy(string Key, string Source, string Visibility, bool Custom, IReadOnlyDictionary<string, string> Names, int Position)
{
    public bool EmployeeEditable => Source == FieldSources.Person;
}

public static class ProfileFields
{
    public const string Email = "email";
    public const string JobTitle = "jobTitle";
    public const string Department = "department";
    public const string Office = "office";
    public const string WorkPhone = "workPhone";
    public const string MobilePhone = "mobilePhone";
    public const string PreferredLanguage = "preferredLanguage";
    public const string TimeZone = "timeZone";
    public const string HireDate = "hireDate";
    public const string EmployeeNumber = "employeeNumber";

    public const int MaxCustomFields = 10;
    public const int MaxValueLength = 200;
    public const string CustomPrefix = "custom.";

    private static readonly string[] Synced = [FieldSources.Directory, FieldSources.Person, FieldSources.Admin];
    private static readonly string[] Local = [FieldSources.Person, FieldSources.Admin];

    /// <summary>Email always comes from the account; the tenant sets only who sees it.</summary>
    public static readonly IReadOnlyList<FieldDefinition> BuiltIn =
    [
        new(Email, FieldSources.Directory, FieldVisibility.Everyone, [FieldSources.Directory]),
        new(JobTitle, FieldSources.Directory, FieldVisibility.Everyone, Synced),
        new(Department, FieldSources.Directory, FieldVisibility.Everyone, Synced),
        new(Office, FieldSources.Directory, FieldVisibility.Everyone, Synced),
        new(WorkPhone, FieldSources.Directory, FieldVisibility.Everyone, Synced, IsPhone: true),
        new(MobilePhone, FieldSources.Directory, FieldVisibility.ManagersAndHr, Synced, IsPhone: true),
        new(PreferredLanguage, FieldSources.Directory, FieldVisibility.Everyone, Synced),
        new(TimeZone, FieldSources.Person, FieldVisibility.Everyone, Local),
        new(HireDate, FieldSources.Directory, FieldVisibility.ManagersAndHr, Synced, IsDate: true),
        new(EmployeeNumber, FieldSources.Directory, FieldVisibility.Hr, Synced),
    ];

    public static readonly IReadOnlyDictionary<string, FieldDefinition> ByKey = BuiltIn.ToDictionary(f => f.Key, StringComparer.Ordinal);

    /// <summary>Custom fields are text, employee or administrator owned (FR-016).</summary>
    public static readonly IReadOnlyList<string> CustomSources = Local;

    /// <summary>Fields people search may match, when visible to everyone.</summary>
    public static readonly IReadOnlyList<string> Searchable = [Email, JobTitle, Department, WorkPhone, MobilePhone];

    public static int PositionOf(string key) => BuiltIn.Select((f, i) => (f, i)).First(x => x.f.Key == key).i;

    public static bool IsCustom(string key) => key.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>The default policy for every built-in field.</summary>
    public static IReadOnlyList<FieldPolicy> Defaults() =>
        BuiltIn.Select((f, i) => new FieldPolicy(f.Key, f.DefaultSource, f.DefaultVisibility, false, new Dictionary<string, string>(), i)).ToList();
}
