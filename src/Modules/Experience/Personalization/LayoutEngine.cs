using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Personalization;

/// <summary>One personal change over the published page: add, hide, remove or move (spec 008 plan, personal layout model).</summary>
public sealed record LayoutOperation(string? Op, string? PlacementId, string? ComponentRef = null, string? RegionId = null,
    JsonElement? Props = null, int? Position = null);

/// <summary>What a viewer may do in one region, after policy, audience, package and region protection.</summary>
public sealed record RegionRights(string RegionId, IReadOnlyList<string> Rights, int MaxPersonalPlacements);

/// <summary>A configured or default policy for one region, as administrators see it.</summary>
public sealed record RegionPolicyView(string RegionId, string Protection, bool Delegable, bool Default, IReadOnlyList<string> Rights,
    Guid? AudienceRuleId, int MaxPersonalPlacements);

public sealed record SkippedOperation(int Index, string Op, string PlacementId, string ReasonKey);

public sealed record LaidOutPlacement(Placement Placement, bool Personal);

public sealed record LayoutResult(IReadOnlyDictionary<string, List<LaidOutPlacement>> Regions, IReadOnlyList<SkippedOperation> Skipped);

/// <summary>
/// Applies the documented precedence (FR-031): legal and package constraints and region locks first, then audience
/// rules, then the administrator's published arrangement, then the user's operations where the policy allows them.
/// An operation that no longer applies is skipped and reported, never deleted, so it returns if the policy returns.
/// </summary>
public sealed class LayoutEngine(IDbSession session, IEntitlementService entitlements)
{
    public const string Add = "add";
    public const string Hide = "hide";
    public const string Remove = "remove";
    public const string Move = "move";
    public const string RightAdd = "add";
    public const string RightHide = "hide";
    public const string RightRemove = "remove";
    public const string RightReorder = "reorder";
    public static readonly IReadOnlyList<string> AllRights = [RightAdd, RightHide, RightRemove, RightReorder];
    public const int DefaultMaxPersonal = 12;
    public const int MaxOperations = 100;

    private sealed record PolicyRow(string RegionId, Guid? AudienceRuleId, string[] Rights, int Max);

    private async Task<IReadOnlyDictionary<string, PolicyRow>> RowsAsync(Guid pageId, CancellationToken ct) =>
        (await session.QueryAsync(
            "select region_id, audience_rule_id, rights, max_personal_placements from experience.personalization_policy where page_id = @pageId",
            new { pageId }, r => new PolicyRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetFieldValue<string[]>(2), r.GetInt32(3)), ct))
        .ToDictionary(p => p.RegionId, StringComparer.Ordinal);

    /// <summary>A region can be delegated when it is not locked, and beyond personal regions only with the package capability.</summary>
    public static bool Delegable(RegionPolicy policy, bool personalizationEntitled) =>
        policy.Protection == RegionProtection.Optional && (policy.Personal || personalizationEntitled);

    public async Task<IReadOnlyList<RegionPolicyView>> PolicyAsync(Guid pageId, TemplateDefinition template, CancellationToken ct)
    {
        var rows = await RowsAsync(pageId, ct);
        var entitled = await entitlements.IsEnabledAsync(Capabilities.ExperiencePersonalization, ct);
        return template.Regions.Select(r =>
        {
            var delegable = Delegable(r.Policy, entitled);
            if (rows.TryGetValue(r.Id, out var row))
            {
                return new RegionPolicyView(r.Id, r.Policy.Protection, delegable, false, delegable ? row.Rights : [], row.AudienceRuleId, row.Max);
            }
            // Default for new tenants: personal regions (the home page) are delegated to everyone; all else is not personalizable.
            return new RegionPolicyView(r.Id, r.Policy.Protection, delegable, true, delegable && r.Policy.Personal ? AllRights : [], null, DefaultMaxPersonal);
        }).ToList();
    }

    /// <summary>The viewer's rights per region: an audience-limited policy grants nothing to people outside the audience.</summary>
    public async Task<IReadOnlyDictionary<string, RegionRights>> RightsAsync(Guid pageId, TemplateDefinition template, AudienceFacts viewer,
        IReadOnlyDictionary<Guid, AudienceRule> rules, bool canPersonalize, CancellationToken ct)
    {
        var result = new Dictionary<string, RegionRights>(StringComparer.Ordinal);
        foreach (var p in await PolicyAsync(pageId, template, ct))
        {
            var inAudience = p.AudienceRuleId is not { } rule || (rules.TryGetValue(rule, out var r) && AudienceMatcher.Matches(r.Conditions, viewer));
            result[p.RegionId] = new RegionRights(p.RegionId, inAudience && canPersonalize ? p.Rights : [], p.MaxPersonalPlacements);
        }
        return result;
    }

    /// <summary>The caller's saved operations for a page and their revision; revision 0 when there is none.</summary>
    public async Task<(int Revision, IReadOnlyList<LayoutOperation> Operations)> OperationsAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select revision, operations::text from experience.personal_layout where user_id = @userId and page_id = @pageId",
            new { userId, pageId }, r => (Found: true, Revision: r.GetInt32(0), Json: r.GetString(1)), ct);
        return row.Found ? (row.Revision, JsonSerializer.Deserialize<List<LayoutOperation>>(row.Json, JsonDefaults.Options)!) : (0, []);
    }

    /// <summary>Removes placements whose audience rule does not match the viewer. An unknown rule matches nobody.</summary>
    public static IReadOnlyList<PageRegion> ForAudience(IReadOnlyList<PageRegion> regions, IReadOnlyDictionary<Guid, AudienceRule> rules,
        AudienceFacts viewer) =>
        regions.Select(r => r with
        {
            Placements = r.Placements.Where(p => p.VisibilityRuleRef is null
                || (Guid.TryParse(p.VisibilityRuleRef, out var id) && rules.TryGetValue(id, out var rule) && AudienceMatcher.Matches(rule.Conditions, viewer)))
                .ToList(),
        }).ToList();

    /// <summary>
    /// Replays the operations in order. Each is checked against the current rights; one that fails is skipped with its
    /// reason. <paramref name="extraCheck"/> lets a save add checks for new placements (component eligibility).
    /// </summary>
    public static LayoutResult Apply(IReadOnlyList<PageRegion> regions, IReadOnlyList<LayoutOperation> operations,
        IReadOnlyDictionary<string, RegionRights> rights, Func<LayoutOperation, string?>? extraCheck = null)
    {
        var layout = regions.ToDictionary(r => r.Id, r => r.Placements.Select(p => new LaidOutPlacement(p, false)).ToList(), StringComparer.Ordinal);
        var skipped = new List<SkippedOperation>();
        bool Has(string region, string right) => rights.TryGetValue(region, out var r) && r.Rights.Contains(right);
        (string Region, int Index)? Find(string id)
        {
            foreach (var (region, list) in layout)
            {
                var i = list.FindIndex(p => p.Placement.Id == id);
                if (i >= 0)
                {
                    return (region, i);
                }
            }
            return null;
        }
        bool Full(string region) => layout[region].Count(p => p.Personal) >= (rights.TryGetValue(region, out var r) ? r.MaxPersonalPlacements : 0);
        static int Clamp(int? position, int count) => Math.Clamp(position ?? count, 0, count);

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var id = op.PlacementId ?? "";
            string? reason = null;
            var found = Find(id);
            switch (op.Op)
            {
                case Add:
                    if (op.RegionId is not { } region || !layout.ContainsKey(region) || !Has(region, RightAdd))
                    {
                        reason = "validation.personalization.region_not_delegated";
                    }
                    else if (found is not null)
                    {
                        reason = "validation.personalization.placement_exists";
                    }
                    else if (Full(region))
                    {
                        reason = "validation.personalization.region_full";
                    }
                    else if ((reason = extraCheck?.Invoke(op)) is null)
                    {
                        layout[region].Insert(Clamp(op.Position, layout[region].Count),
                            new LaidOutPlacement(new Placement(id, op.ComponentRef!, null, op.Props), true));
                    }
                    break;
                case Hide or Remove:
                    if (found is not { } at)
                    {
                        reason = "validation.personalization.placement_unknown";
                    }
                    else
                    {
                        var personal = layout[at.Region][at.Index].Personal;
                        // Users remove their own placements; hiding or removing a page placement needs that right in its region.
                        var allowed = personal ? rights.TryGetValue(at.Region, out var r) && r.Rights.Count > 0
                            : Has(at.Region, op.Op == Hide ? RightHide : RightRemove);
                        if (!allowed)
                        {
                            reason = rights.TryGetValue(at.Region, out var rr) && rr.Rights.Count > 0
                                ? "validation.personalization.not_allowed" : "validation.personalization.protected";
                        }
                        else
                        {
                            layout[at.Region].RemoveAt(at.Index);
                        }
                    }
                    break;
                case Move:
                    if (found is not { } from)
                    {
                        reason = "validation.personalization.placement_unknown";
                    }
                    else if (!Has(from.Region, RightReorder))
                    {
                        reason = "validation.personalization.protected";
                    }
                    else if (op.RegionId is not { } to || !layout.ContainsKey(to) || !Has(to, RightReorder)
                        || (to != from.Region && !Has(to, RightAdd)))
                    {
                        reason = "validation.personalization.region_not_delegated";
                    }
                    else
                    {
                        var item = layout[from.Region][from.Index];
                        if (to != from.Region && item.Personal && Full(to))
                        {
                            reason = "validation.personalization.region_full";
                        }
                        else if (to != from.Region && (reason = extraCheck?.Invoke(op with { ComponentRef = item.Placement.ComponentRef })) is not null)
                        {
                        }
                        else
                        {
                            layout[from.Region].RemoveAt(from.Index);
                            layout[to].Insert(Clamp(op.Position, layout[to].Count), item);
                        }
                    }
                    break;
                default:
                    reason = "validation.personalization.operation";
                    break;
            }
            if (reason is not null)
            {
                skipped.Add(new SkippedOperation(i, op.Op ?? "", id, reason));
            }
        }
        return new LayoutResult(layout, skipped);
    }
}
