using System.Globalization;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

/// <summary>
/// Who an audience rule targets (spec 008 FR-028). Every criterion that is given must match; inside a criterion any
/// listed value matches. Org units match their whole subtree. Attributes are the user attributes of the spec 004
/// catalogue, from the directory. Targeting decides what a person sees, never what they may do (FR-029).
/// </summary>
public sealed record AudienceConditions(
    IReadOnlyList<Guid>? OrgUnits,
    IReadOnlyList<string>? MembershipTypes,
    IReadOnlyList<string>? Locales,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Attributes);

public sealed record AudienceRule(Guid Id, IReadOnlyDictionary<string, string> Labels, AudienceConditions Conditions);

/// <summary>The facts a rule is evaluated on: a real viewer, or a synthetic persona in the targeting preview.</summary>
public sealed record AudienceFacts(IReadOnlySet<Guid> Units, string? MembershipType, string Locale, IReadOnlyDictionary<string, string> Attributes);

public static class AudienceMatcher
{
    public static bool Matches(AudienceConditions c, AudienceFacts f) =>
        (c.OrgUnits is not { Count: > 0 } || c.OrgUnits.Any(f.Units.Contains))
        && (c.MembershipTypes is not { Count: > 0 } || (f.MembershipType is { } t && c.MembershipTypes.Contains(t)))
        && (c.Locales is not { Count: > 0 } || c.Locales.Contains(f.Locale))
        && (c.Attributes is not { Count: > 0 } || c.Attributes.All(a =>
            f.Attributes.TryGetValue(a.Key, out var actual) && a.Value.Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase))));
}

/// <summary>Loads audience rules and the current viewer's facts, once per request.</summary>
public sealed class AudienceEvaluator(
    IDbSession session,
    ITenantContext context,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IEnumerable<IUserAttributeProvider> attributeProviders,
    RequestLocale locale)
{
    private IReadOnlyDictionary<Guid, AudienceRule>? rules;
    private AudienceFacts? viewer;

    public async Task<IReadOnlyDictionary<Guid, AudienceRule>> RulesAsync(CancellationToken ct) =>
        rules ??= (await session.QueryAsync("select rule_id, labels::text, conditions::text from experience.audience_rule", null,
            r => new AudienceRule(r.GetGuid(0),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1), JsonDefaults.Options)!,
                JsonSerializer.Deserialize<AudienceConditions>(r.GetString(2), JsonDefaults.Options)!), ct))
            .ToDictionary(r => r.Id);

    public void Forget() => rules = null;

    public async Task<AudienceFacts> ViewerAsync(CancellationToken ct)
    {
        if (viewer is not null)
        {
            return viewer;
        }
        var user = context.RequireUser();
        var units = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(user, ct))
        {
            units.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(m.OrgUnitId, ct));
        }
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in attributeProviders)
        {
            foreach (var (key, value) in await provider.GetAsync(user, ct))
            {
                attributes.TryAdd(key, value);
            }
        }
        return viewer = new AudienceFacts(units, (await memberships.GetAsync(user, ct))?.MembershipType, locale.Locale, attributes);
    }

    /// <summary>A persona of the targeting preview: an org unit with its parents, and the given type, language and attributes.</summary>
    public async Task<AudienceFacts> PersonaAsync(Guid? orgUnitId, string? membershipType, string? personaLocale,
        IReadOnlyDictionary<string, string>? attributes, CancellationToken ct)
    {
        var units = orgUnitId is { } u ? (await hierarchy.GetAncestorsAndSelfAsync(u, ct)).ToHashSet() : new HashSet<Guid>();
        return new AudienceFacts(units, membershipType, personaLocale ?? locale.Locale,
            attributes ?? new Dictionary<string, string>());
    }

    /// <summary>Checks a rule's conditions; at least one criterion, known values only.</summary>
    public static void Validate(AudienceConditions? c, ValidationErrors errors)
    {
        if (c is null || (c.OrgUnits is not { Count: > 0 } && c.MembershipTypes is not { Count: > 0 } && c.Locales is not { Count: > 0 }
            && c.Attributes is not { Count: > 0 }))
        {
            errors.Add("conditions", "validation.audience.empty");
            return;
        }
        if (c.MembershipTypes?.Any(t => !Tenancy.Contracts.MembershipTypes.All.Contains(t)) == true)
        {
            errors.Add("conditions.membershipTypes", "validation.audience.membership_type");
        }
        if (c.Locales?.Any(l => !Locales.IsSupported(l)) == true)
        {
            errors.Add("conditions.locales", "validation.audience.locale");
        }
        foreach (var (key, values) in c.Attributes ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (!ConditionAttributes.All.TryGetValue(key, out var attribute) || attribute.Source != AttributeSource.User
                || values is not { Count: > 0 } || values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200)
                || (attribute.Kind == AttributeKind.Number && values.Any(v => !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
                || (attribute.Choices is { } choices && values.Any(v => !choices.Contains(v))))
            {
                errors.Add($"conditions.attributes.{key}", "validation.audience.attribute");
            }
        }
        if ((c.OrgUnits?.Count ?? 0) + (c.MembershipTypes?.Count ?? 0) + (c.Locales?.Count ?? 0) + (c.Attributes?.Count ?? 0) > 50)
        {
            errors.Add("conditions", "validation.audience.too_many");
        }
    }
}
