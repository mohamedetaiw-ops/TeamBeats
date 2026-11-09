using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>
/// People for search (spec 012 section 2, spec 010): active members who are not guests, with their names in every
/// locale and, when everyone may see them, their job title, department and office. Nothing a viewer could not see on the
/// card is indexed; a change to those fields' visibility re-reads every card. Finding people needs <c>people.view</c>.
/// </summary>
public sealed class PeopleSearchSource(IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, ProfileStore store) : ISearchSource
{
    public const string SourceName = "people";

    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal) { ProfileFields.JobTitle, ProfileFields.Department, ProfileFields.Office };

    public string Source => SourceName;

    public static string Link(Guid id) => $"/people/{id}";

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        // The profile is a projection refreshed on demand; read the account, memberships and units as they are now.
        await store.RefreshAsync(itemId, ct);
        var person = await store.GetAsync(itemId, ct);
        if (person is null || !person.Active || person.MembershipType == Tenancy.Contracts.MembershipTypes.Guest)
        {
            return [];
        }
        var policies = await store.PoliciesAsync(ct);
        var facts = policies.Where(p => Fields.Contains(p.Key) && p.Visibility == FieldVisibility.Everyone).Select(person.Value).OfType<string>()
            .Where(v => v.Length > 0).ToList();
        var names = person.Names.Where(n => n.Value.Length > 0).ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
        if (names.Count == 0)
        {
            names["en"] = person.DisplayName;
        }
        return [.. names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => new SearchUnit(n.Key, SearchKinds.Person, n.Key, n.Value,
            string.Join("\n", facts.Prepend(person.DisplayName == n.Value ? null : person.DisplayName).OfType<string>()), Link(itemId), "internal", person.PrimaryUnitId))];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        if (after is null)
        {
            // A rebuild starts from the accounts and memberships as they are now, not the last projection.
            await store.RefreshAllAsync(ct);
        }
        return await session.QueryAsync(
            "select user_id from people.profile where active and membership_type <> 'guest' and (@after::uuid is null or user_id > @after) order by user_id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);
    }

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.PeopleDirectory, ct) || !await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
        {
            return new HashSet<Guid>();
        }
        return (await session.QueryAsync("select user_id from people.profile where user_id = any(@ids) and active and membership_type <> 'guest'",
            new { ids = itemIds.ToArray() }, r => r.GetGuid(0), ct)).ToHashSet();
    }
}
