using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Policies for search (spec 012 section 2, spec 025 section 12): the version in force only, so a superseded version
/// is never returned (FR-011). Each clause is a unit with its anchor and number, which is what the assistant cites;
/// the policy's title and code are a unit of their own. Readers are the people the policy applies to, its owner and
/// compliance officers, as on the policy page.
/// </summary>
internal sealed class PolicySearchSource(IDbSession session, IEntitlementService entitlements, PolicyRegister register) : ISearchSource
{
    public string Source => "policies";

    public static string Link(Guid id, string? anchor = null) => $"/policies/{id}" + (anchor is null ? string.Empty : "#" + anchor);

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var policy = await register.RowAsync(itemId, false, ct);
        if (policy is null || policy.Status == "retired" || policy.CurrentVersion is not { } number
            || await register.VersionByNumberAsync(itemId, number, ct) is not { State: "effective" } version)
        {
            return [];
        }
        var numbers = ClauseRules.Numbers(version.Clauses.Select(c => c.Level).ToList());
        var at = version.EffectiveFrom ?? policy.EffectiveFrom ?? policy.UpdatedAt;
        var units = new List<SearchUnit>();
        foreach (var locale in version.Locales)
        {
            var title = version.Titles.GetValueOrDefault(locale) ?? policy.Names.GetValueOrDefault(locale) ?? policy.Code;
            units.Add(new SearchUnit($"{locale}:", SearchKinds.Policy, locale, title, policy.Code, Link(itemId), policy.Classification, policy.OrgUnitId, number, at));
            for (var i = 0; i < version.Clauses.Count; i++)
            {
                var clause = version.Clauses[i];
                var heading = numbers[i] + " " + (clause.Titles.GetValueOrDefault(locale) ?? string.Empty);
                var body = clause.Bodies.TryGetValue(locale, out var b) ? BodyValidator.PlainText(b) : string.Empty;
                units.Add(new SearchUnit($"{locale}:{clause.Key}", SearchKinds.Policy, locale, title, (heading.Trim() + "\n" + body).Trim(),
                    Link(itemId, ClauseRules.Anchor(clause.Key)), policy.Classification, policy.OrgUnitId, number, at));
            }
        }
        return units;
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id from policies.policy where current_version is not null and status <> 'retired' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var allowed = new HashSet<Guid>();
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return allowed;
        }
        var rows = await session.QueryAsync(
            $"select {PolicyRow.Columns} from policies.policy where id = any(@ids) and current_version is not null and status <> 'retired'",
            new { ids = itemIds.ToArray() }, PolicyRow.Read, ct);
        foreach (var policy in rows)
        {
            if (await register.ReadsPublishedAsync(policy, ct))
            {
                allowed.Add(policy.Id);
            }
        }
        return allowed;
    }
}
