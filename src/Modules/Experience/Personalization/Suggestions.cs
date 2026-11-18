using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Shell;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

/// <summary>One suggestion: what it would do, why it is offered (a reason key with its count), and what it would add.</summary>
public sealed record SuggestionView(string Key, string Kind, string ReasonKey, int Count, string Label, string? Href, string? ComponentRef);

public sealed record SuggestionRequest(string? Key, Guid? PageId);

/// <summary>The outcome of accepting, declining or undoing a suggestion.</summary>
public sealed record SuggestionDecisionView(string Key, string State, bool CanUndo);

/// <summary>
/// Safe suggestions (spec 008 FR-035, T025). Rule-based: pin a page the person opens often, and add a component that
/// colleagues in their unit added to the same page. Each says why it is offered, changes nothing until the person accepts,
/// and an accepted one can be undone. Accepting goes through the same paths the person would use themselves (favorites and
/// the personal layout), so their checks apply. A suggestion answered once is never offered again. Suggestions are off for
/// someone who may not personalize, and a component suggestion only appears where the page's personalization policy lets
/// them add.
/// </summary>
public sealed class Suggestions(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    PersonalizationService personalization,
    Favorites favorites,
    ShellService shell,
    IClock clock)
{
    public const string PinPage = "pin_page";
    public const string AddComponent = "add_component";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Undone = "undone";

    /// <summary>Visits to one page before pinning it is suggested.</summary>
    public const int PinAfterVisits = 5;

    /// <summary>Colleagues who must have added a component before it is suggested; fewer could point at one person.</summary>
    public const int ColleaguesNeeded = 3;

    public const int MaxSuggestions = 3;

    private const string PinPrefix = "pin:";
    private const string ComponentPrefix = "component:";

    private async Task<bool> EnabledAsync(CancellationToken ct) => await authz.HoldsAnywhereAsync(Permissions.PersonalLayoutEdit, ct);

    private async Task<Dictionary<string, (string State, JsonElement? Undo)>> DecisionsAsync(Guid user, CancellationToken ct) =>
        (await session.QueryAsync("select key, state, undo from experience.suggestion_decision where user_id = @user", new { user },
            r => (Key: r.GetString(0), State: r.GetString(1), Undo: r.IsDBNull(2) ? (JsonElement?)null : JsonDocument.Parse(r.GetString(2)).RootElement), ct))
        .ToDictionary(x => x.Key, x => (x.State, x.Undo), StringComparer.Ordinal);

    /// <summary>Suggestions for the person now, for <paramref name="pageId"/> when given; at most <see cref="MaxSuggestions"/>.</summary>
    public async Task<IReadOnlyList<SuggestionView>> ListAsync(Guid? pageId, string? device, string locale, CancellationToken ct) =>
        (await ListAllAsync(pageId, device, locale, ct)).Take(MaxSuggestions).ToList();

    private async Task<IReadOnlyList<SuggestionView>> PinsAsync(Guid user, IReadOnlyDictionary<string, (string, JsonElement?)> decided, string locale,
        CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "select path, label, visit_count from experience.recent_visit where user_id = @user and visit_count >= @min order by visit_count desc, visited_at desc limit 10",
            new { user, min = PinAfterVisits }, r => (Path: r.GetString(0), Label: r.GetString(1), Count: r.GetInt32(2)), ct);
        var candidates = rows.Where(r => !decided.ContainsKey(PinPrefix + r.Path) && r.Path == r.Path.ToLowerInvariant()).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }
        // Only an address the person may open now and has not already pinned.
        var openable = await shell.OpenableAsync(candidates.Select(c => c.Path).ToList(), locale, ct);
        var pinned = (await favorites.ListAsync(locale, ct)).Where(f => f.Kind == "page" && f.Href is not null).Select(f => f.Href!).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(c => openable.ContainsKey(c.Path) && !pinned.Contains(c.Path))
            .Select(c => new SuggestionView(PinPrefix + c.Path, PinPage, "personalization.suggestions.reasons.pin_page", c.Count, c.Label, c.Path, null))
            .Take(1).ToList();
    }

    private async Task<IReadOnlyList<SuggestionView>> ComponentsAsync(Guid user, Guid pageId, string? device,
        IReadOnlyDictionary<string, (string, JsonElement?)> decided, string locale, CancellationToken ct)
    {
        var (shown, region) = await personalization.ViewerSummaryAsync(pageId, device, ct);
        if (region is null)
        {
            return [];
        }
        var units = (await hierarchy.GetActiveMembershipsAsync(user, ct)).Select(m => m.OrgUnitId).Distinct().ToList();
        if (units.Count == 0)
        {
            return [];
        }
        var colleagues = (await hierarchy.GetMemberUserIdsAsync(units, ct)).Where(id => id != user).ToArray();
        if (colleagues.Length < ColleaguesNeeded)
        {
            return [];
        }
        var layouts = await session.QueryAsync(
            "select user_id, operations from experience.personal_layout where page_id = @pageId and user_id = any(@colleagues)",
            new { pageId, colleagues }, r => (User: r.GetGuid(0), Ops: r.GetString(1)), ct);
        var added = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var (who, ops) in layouts)
        {
            foreach (var op in JsonSerializer.Deserialize<List<LayoutOperation>>(ops, JsonOptions) ?? [])
            {
                if (op.Op == LayoutEngine.Add && op.ComponentRef is { } reference)
                {
                    (added.TryGetValue(reference, out var set) ? set : added[reference] = []).Add(who);
                }
            }
        }
        var eligible = (await personalization.AddPanelAsync(pageId, region, device, ct)).Where(i => i.Eligible).ToDictionary(i => i.ComponentRef, StringComparer.Ordinal);
        return added.Where(a => a.Value.Count >= ColleaguesNeeded && eligible.ContainsKey(a.Key) && !shown.Contains(a.Key.Split('@')[0])
                && !decided.ContainsKey(ComponentKey(pageId, a.Key)))
            .OrderByDescending(a => a.Value.Count).ThenBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new SuggestionView(ComponentKey(pageId, a.Key), AddComponent, "personalization.suggestions.reasons.add_component", a.Value.Count,
                eligible[a.Key].Name, null, a.Key))
            .Take(1).ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string ComponentKey(Guid pageId, string reference) => $"{ComponentPrefix}{pageId:N}:{reference}";

    /// <summary>The placement ID an accepted component suggestion uses, so undo finds it again.</summary>
    public static string PlacementFor(string reference)
    {
        var id = "my-s-" + new string(reference.Split('@')[0].ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return id.Length > 38 ? id[..38].TrimEnd('-') : id;
    }

    /// <summary>
    /// Accepts a suggestion that is offered now. It is checked again, so an old or made up key changes nothing.
    /// </summary>
    public async Task<SuggestionDecisionView> AcceptAsync(SuggestionRequest request, string? device, string locale, CancellationToken ct)
    {
        var offered = await FindOfferedAsync(request, device, locale, ct);
        JsonElement undo;
        if (offered.Kind == PinPage)
        {
            var favorite = await favorites.AddAsync(new FavoriteRequest(offered.Href, null, null, offered.Label), locale, ct);
            undo = JsonSerializer.SerializeToElement(new { favoriteId = favorite.Id });
        }
        else
        {
            var pageId = request.PageId!.Value;
            var (_, region) = await personalization.ViewerSummaryAsync(pageId, device, ct);
            var layout = await personalization.GetLayoutAsync(pageId, device, ct);
            var placement = PlacementFor(offered.ComponentRef!);
            var operations = layout.Operations.Where(o => o.PlacementId != placement)
                .Append(new LayoutOperation(LayoutEngine.Add, placement, offered.ComponentRef, region)).ToList();
            await personalization.SaveLayoutAsync(pageId, layout.ETag, new LayoutRequest(operations), device, ct);
            undo = JsonSerializer.SerializeToElement(new { pageId, placementId = placement });
        }
        await DecideAsync(offered.Key, Accepted, undo, ct);
        return new SuggestionDecisionView(offered.Key, Accepted, true);
    }

    /// <summary>Declines a suggestion that is offered now; it is not offered again.</summary>
    public async Task<SuggestionDecisionView> DeclineAsync(SuggestionRequest request, string? device, string locale, CancellationToken ct)
    {
        var offered = await FindOfferedAsync(request, device, locale, ct);
        await DecideAsync(offered.Key, Declined, null, ct);
        return new SuggestionDecisionView(offered.Key, Declined, false);
    }

    /// <summary>
    /// Undoes an accepted suggestion: removes the favorite or the placement it added. If the person already removed it
    /// themselves, there is nothing left to undo and that is fine. It is not offered again.
    /// </summary>
    public async Task<SuggestionDecisionView> UndoAsync(SuggestionRequest request, string? device, CancellationToken ct)
    {
        var user = context.RequireUser();
        var key = request.Key ?? throw AppException.Validation("key", "validation.personalization.suggestion");
        if (!(await DecisionsAsync(user, ct)).TryGetValue(key, out var decision) || decision.State != Accepted || decision.Undo is not { } undo)
        {
            throw AppException.NotFound();
        }
        if (key.StartsWith(PinPrefix, StringComparison.Ordinal))
        {
            var id = undo.GetProperty("favoriteId").GetGuid();
            await session.ExecAsync("delete from experience.favorite where id = @id and user_id = @user", new { id, user }, ct);
        }
        else
        {
            var pageId = undo.GetProperty("pageId").GetGuid();
            var placement = undo.GetProperty("placementId").GetString();
            var layout = await personalization.GetLayoutAsync(pageId, device, ct);
            if (layout.Operations.Any(o => o.PlacementId == placement))
            {
                var operations = layout.Operations.Where(o => o.PlacementId != placement).ToList();
                await personalization.SaveLayoutAsync(pageId, layout.ETag, new LayoutRequest(operations), device, ct);
            }
        }
        await DecideAsync(key, Undone, null, ct);
        return new SuggestionDecisionView(key, Undone, false);
    }

    private async Task<SuggestionView> FindOfferedAsync(SuggestionRequest request, string? device, string locale, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw AppException.Validation("key", "validation.personalization.suggestion");
        }
        if (request.Key.StartsWith(ComponentPrefix, StringComparison.Ordinal) && request.PageId is null)
        {
            throw AppException.Validation("pageId", "validation.personalization.suggestion");
        }
        var offered = (await ListAllAsync(request.PageId, device, locale, ct)).FirstOrDefault(s => s.Key == request.Key);
        return offered ?? throw AppException.Validation("key", "validation.personalization.suggestion");
    }

    /// <summary>Every suggestion offered now, without the display limit, so any listed one can be answered.</summary>
    private async Task<IReadOnlyList<SuggestionView>> ListAllAsync(Guid? pageId, string? device, string locale, CancellationToken ct)
    {
        if (!await EnabledAsync(ct))
        {
            return [];
        }
        var user = context.RequireUser();
        var decided = await DecisionsAsync(user, ct);
        var result = new List<SuggestionView>(await PinsAsync(user, decided, locale, ct));
        if (pageId is { } page)
        {
            result.AddRange(await ComponentsAsync(user, page, device, decided, locale, ct));
        }
        return result;
    }

    private Task DecideAsync(string key, string state, JsonElement? undo, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into experience.suggestion_decision (tenant_id, user_id, key, state, undo, decided_at)
            values (platform.current_tenant_id(), @user, @key, @state, @undo, @now)
            on conflict (tenant_id, user_id, key) do update set state = excluded.state, undo = excluded.undo, decided_at = excluded.decided_at
            """, new { user = context.RequireUser(), key, state, undo = undo is { } u ? Jsonb.From(u) : null, now = clock.UtcNow }, ct);
}
