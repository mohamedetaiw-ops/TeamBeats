namespace TeamBeats.Authorization.Contracts;

/// <summary>Where a condition attribute comes from (spec 004 section 6).</summary>
public enum AttributeSource
{
    User,
    Resource,
    Request,
}

/// <summary>How values of an attribute compare.</summary>
public enum AttributeKind
{
    /// <summary>Free text, compared without regard to case.</summary>
    Text,

    /// <summary>One value from a fixed list.</summary>
    Choice,

    /// <summary>A decimal number; the only kind that allows greater or equal and less or equal.</summary>
    Number,

    /// <summary>true or false.</summary>
    Flag,

    /// <summary>An organization unit ID.</summary>
    OrgUnit,
}

/// <summary>
/// An attribute conditions can test. A multi-valued attribute arrives as a comma-separated list, such as every network
/// zone the request address falls in: equal and in hold when any value matches, not equal and not in when none does.
/// </summary>
public sealed record ConditionAttribute(string Key, AttributeSource Source, AttributeKind Kind, IReadOnlyList<string>? Choices = null, bool MultiValued = false);

/// <summary>
/// The attribute catalogue for grant conditions (spec 004 FR-015, section 6). Adding an attribute is a change to spec 004.
/// User attributes come only from tenant membership, the identity provider, the directory or HR sync, never from
/// the user's own profile edits or the request (spec 004 section 14).
/// </summary>
public static class ConditionAttributes
{
    public const string MembershipType = "membership_type";
    public const string JobTitle = "job_title";
    public const string Office = "office";
    public const string EmploymentStatus = "employment_status";
    public const string JobGrade = "job_grade";
    public const string JobFunction = "job_function";
    public const string WorkCountry = "work_country";
    public const string CostCentre = "cost_centre";
    public const string Classification = "classification";
    public const string OwningOrgUnit = "owning_org_unit";
    public const string MfaPerformed = "mfa_performed";
    public const string StepUpAgeMinutes = "step_up_age_minutes";
    public const string NetworkZone = "network_zone";

    /// <summary>Content classifications (spec 011 FR-019).</summary>
    public static readonly IReadOnlyList<string> Classifications = ["public", "internal", "confidential", "restricted"];

    public static readonly IReadOnlyDictionary<string, ConditionAttribute> All = new ConditionAttribute[]
    {
        new(MembershipType, AttributeSource.User, AttributeKind.Choice, ["employee", "contractor", "guest"]),
        new(JobTitle, AttributeSource.User, AttributeKind.Text),
        new(Office, AttributeSource.User, AttributeKind.Text),
        new(EmploymentStatus, AttributeSource.User, AttributeKind.Text),
        new(JobGrade, AttributeSource.User, AttributeKind.Number),
        new(JobFunction, AttributeSource.User, AttributeKind.Text),
        new(WorkCountry, AttributeSource.User, AttributeKind.Text),
        new(CostCentre, AttributeSource.User, AttributeKind.Text),
        new(Classification, AttributeSource.Resource, AttributeKind.Choice, Classifications),
        new(OwningOrgUnit, AttributeSource.Resource, AttributeKind.OrgUnit),
        new(MfaPerformed, AttributeSource.Request, AttributeKind.Flag),
        new(StepUpAgeMinutes, AttributeSource.Request, AttributeKind.Number),
        new(NetworkZone, AttributeSource.Request, AttributeKind.Text, MultiValued: true),
    }.ToDictionary(a => a.Key, StringComparer.Ordinal);
}

/// <summary>
/// Supplies user attributes for conditions from a system of record the module owns (directory, HR sync). Values are
/// invariant strings (numbers as invariant decimals). A key the tenant has not mapped is left out, so conditions on it
/// fail closed. Keys outside the user part of <see cref="ConditionAttributes"/> are ignored.
/// </summary>
public interface IUserAttributeProvider
{
    Task<IReadOnlyDictionary<string, string>> GetAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Supplies request attributes for conditions (spec 004 section 6, spec 026): facts about the current request, such as
/// the network zones its address falls in, worked out by the module that owns them. Never cached with grants, never
/// taken from client input. A key that does not apply is left out, so conditions on it fail closed.
/// </summary>
public interface IRequestAttributeProvider
{
    Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct);
}

/// <summary>One comparison in a condition, with the value used, for explanations (spec 004 FR-016).</summary>
public sealed record ConditionClause(string Attribute, string Operator, IReadOnlyList<string> Expected, string? Actual, bool Met);

/// <summary>How a grant's condition evaluated.</summary>
public sealed record ConditionTrace(bool Met, IReadOnlyList<ConditionClause> Clauses, IReadOnlyList<string> MissingAttributes);
