using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// A forced security update to a platform template (spec 020 FR-005, edge case "a platform security update to a
/// template"). Unlike a template version, which is only offered, it applies to every tenant agent built on the template,
/// whatever its version: on <paramref name="EffectiveAt"/>, announced at least 7 days before, or at once when
/// <paramref name="Critical"/>. It can only take things away (tools, autonomy, classification, tool calls), so applying it
/// without a review never widens what an agent may do.
/// </summary>
public sealed record TemplateSecurityUpdate(
    string TemplateId,
    int Version,
    Dictionary<string, string>? Reason,
    DateTimeOffset AnnouncedAt,
    DateTimeOffset? EffectiveAt = null,
    bool Critical = false,
    List<string>? RemoveTools = null,
    string? MaxAutonomy = null,
    string? MaxClassification = null,
    int? MaxToolCalls = null)
{
    public const int NoticeDays = 7;

    /// <summary>When it applies: the announcement for a critical fix, the set date otherwise.</summary>
    public DateTimeOffset AppliesAt => Critical ? AnnouncedAt : EffectiveAt ?? AnnouncedAt.AddDays(NoticeDays);

    public bool AppliesBy(DateTimeOffset now) => now >= AppliesAt;
}

/// <summary>The forced security updates the platform ships (a port, so tests can name their own).</summary>
public interface ITemplateSecurityUpdates
{
    IReadOnlyList<TemplateSecurityUpdate> All { get; }
}

/// <summary>
/// The updates shipped with the product as embedded JSON under <c>SecurityUpdates/</c>, released and signed like the
/// templates themselves. A non-critical update with less than 7 days' notice, a version that is not new, or an unknown
/// template, tool, autonomy level or classification stops the product from starting, so a bad update never ships.
/// </summary>
public sealed class ShippedTemplateSecurityUpdates : ITemplateSecurityUpdates
{
    private static readonly Lazy<IReadOnlyList<TemplateSecurityUpdate>> Library = new(Load);

    public IReadOnlyList<TemplateSecurityUpdate> All => Library.Value;

    private static IReadOnlyList<TemplateSecurityUpdate> Load()
    {
        var assembly = typeof(ShippedTemplateSecurityUpdates).Assembly;
        var list = new List<TemplateSecurityUpdate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".SecurityUpdates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            list.AddRange(JsonSerializer.Deserialize<List<TemplateSecurityUpdate>>(stream, AgentPolicyService.Json) ?? []);
        }
        TemplateSecurity.Check(list);
        return list;
    }
}

public static class TemplateSecurity
{
    /// <summary>Throws when an update could not be applied safely (see <see cref="ShippedTemplateSecurityUpdates"/>).</summary>
    public static void Check(IReadOnlyList<TemplateSecurityUpdate> updates)
    {
        foreach (var u in updates)
        {
            _ = AgentTemplates.Find(u.TemplateId) ?? throw new InvalidOperationException($"Security update for unknown template {u.TemplateId}.");
            if (u.Version <= 0 || updates.Count(x => x.TemplateId == u.TemplateId && x.Version == u.Version) > 1)
            {
                throw new InvalidOperationException($"Security update {u.TemplateId} {u.Version} needs a unique positive version.");
            }
            if (!u.Critical && (u.EffectiveAt is not { } at || at < u.AnnouncedAt.AddDays(TemplateSecurityUpdate.NoticeDays)))
            {
                throw new InvalidOperationException($"Security update {u.TemplateId} {u.Version} needs {TemplateSecurityUpdate.NoticeDays} days' notice unless critical.");
            }
            if ((u.MaxAutonomy is { } a && AgentAutonomy.Rank(a) < 0) || (u.MaxClassification is { } c && Classifications.Rank(c) < 0) || u.MaxToolCalls is < 1)
            {
                throw new InvalidOperationException($"Security update {u.TemplateId} {u.Version} has an unknown limit.");
            }
            if (u.Reason is not { Count: > 0 } || u.Reason.Values.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException($"Security update {u.TemplateId} {u.Version} needs a reason.");
            }
        }
    }

    /// <summary>The template's updates in force at <paramref name="now"/>, oldest first.</summary>
    public static IReadOnlyList<TemplateSecurityUpdate> InForce(this ITemplateSecurityUpdates updates, string? templateId, DateTimeOffset now) =>
        templateId is null ? [] : updates.All.Where(u => u.TemplateId == templateId && u.AppliesBy(now)).OrderBy(u => u.Version).ToList();

    /// <summary>All of the template's updates, announced or in force, oldest first.</summary>
    public static IReadOnlyList<TemplateSecurityUpdate> Of(this ITemplateSecurityUpdates updates, string? templateId) =>
        templateId is null ? [] : updates.All.Where(u => u.TemplateId == templateId).OrderBy(u => u.Version).ToList();

    /// <summary>The definition with every update applied: listed tools gone, and each limit no higher than the update's.</summary>
    public static AgentDefinition Apply(AgentDefinition definition, IReadOnlyList<TemplateSecurityUpdate> updates)
    {
        var d = definition;
        foreach (var u in updates)
        {
            if (u.RemoveTools is { Count: > 0 } removed && d.Tools is { } tools)
            {
                d = d with { Tools = tools.Where(t => !removed.Contains(t, StringComparer.Ordinal)).ToList() };
            }
            if (u.MaxAutonomy is { } autonomy && AgentAutonomy.Rank(d.Autonomy ?? AgentPolicy.DefaultMaxAutonomy) > AgentAutonomy.Rank(autonomy))
            {
                d = d with { Autonomy = autonomy };
            }
            if (u.MaxClassification is { } classification && Classifications.Rank(d.MaxClassification ?? "internal") > Classifications.Rank(classification))
            {
                d = d with { MaxClassification = classification };
            }
            if (u.MaxToolCalls is { } calls && (d.MaxToolCalls is null || d.MaxToolCalls > calls))
            {
                d = d with { MaxToolCalls = calls };
            }
        }
        return d;
    }

    /// <summary>Whether a definition holds something an update in force takes away.</summary>
    public static bool Breaks(AgentDefinition definition, IReadOnlyList<TemplateSecurityUpdate> updates)
    {
        var applied = Apply(definition, updates);
        return applied.Autonomy != definition.Autonomy || applied.MaxClassification != definition.MaxClassification || applied.MaxToolCalls != definition.MaxToolCalls ||
            (applied.Tools?.Count ?? 0) != (definition.Tools?.Count ?? 0);
    }
}

/// <summary>What the editor shows about a security update of the agent's template.</summary>
public sealed record TemplateSecurityView(int Version, Dictionary<string, string>? Reason, DateTimeOffset AppliesAt, bool Critical, bool InForce,
    List<string>? RemoveTools, string? MaxAutonomy, string? MaxClassification, int? MaxToolCalls);

/// <summary>
/// Announces and records forced security updates (spec 020 FR-005, section 7 "forced updates are audited and announced").
/// Runs every minute per tenant: the owners of each agent built on the template are told once when an update is
/// announced with a set date, and once when it applies, which is also audited. The runtime and the draft checks apply an
/// update in force on their own, so an agent is safe from its date whether or not this has run.
/// </summary>
public sealed class AgentTemplateSecurity(IDbSession session, ITemplateSecurityUpdates updates, IAuditWriter audit, INotificationSender notifications, IClock clock)
{
    public const string AnnouncedTemplate = "agents.template_security_announced";
    public const string AppliedTemplate = "agents.template_security_applied";

    public async Task<int> ApplyAsync(CancellationToken ct)
    {
        if (updates.All.Count == 0)
        {
            return 0;
        }
        var now = clock.UtcNow;
        var templates = updates.All.Select(u => u.TemplateId).Distinct().ToArray();
        var rows = await session.QueryAsync(
            """
            select id, org_unit_id, template_id, template_security_announced, template_security_applied, owner_user_id, backup_owner_user_id
            from agents.agent where template_id = any(@templates) and status <> 'retired'
            """, new { templates },
            r => (Id: r.GetGuid(0), Unit: r.GetGuid(1), Template: r.GetString(2), Announced: r.GetInt32(3), Applied: r.GetInt32(4), Owner: r.GetGuid(5),
                Backup: r.Get<Guid?>("backup_owner_user_id")), ct);
        var changed = 0;
        foreach (var row in rows)
        {
            var mine = updates.Of(row.Template);
            var owners = row.Backup is { } backup && backup != row.Owner ? new[] { row.Owner, backup } : [row.Owner];
            var due = mine.Where(u => u.Version > row.Applied && u.AppliesBy(now)).ToList();
            var announced = mine.Where(u => u.Version > row.Announced && !u.AppliesBy(now)).ToList();
            if (due.Count == 0 && announced.Count == 0)
            {
                continue;
            }
            if (announced.Count > 0)
            {
                await notifications.SendAsync(new NotificationRequest(owners, NotificationCategories.SecurityNotice, AnnouncedTemplate, $"/admin/agents/{row.Id}",
                    DedupeKey: $"{AnnouncedTemplate}:{row.Id:N}:{announced[^1].Version}"), ct);
            }
            if (due.Count > 0)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.AgentTemplateSecurityApplied, "agent", row.Id.ToString(), TargetOrgUnitId: row.Unit,
                    Changes: new
                    {
                        template = row.Template,
                        updates = due.Select(u => new { u.Version, u.Critical, u.AppliesAt, u.RemoveTools, u.MaxAutonomy, u.MaxClassification, u.MaxToolCalls }),
                    }), ct);
                await notifications.SendAsync(new NotificationRequest(owners, NotificationCategories.SecurityNotice, AppliedTemplate, $"/admin/agents/{row.Id}",
                    DedupeKey: $"{AppliedTemplate}:{row.Id:N}:{due[^1].Version}"), ct);
            }
            var applied = due.Count > 0 ? due[^1].Version : row.Applied;
            var noticed = Math.Max(applied, announced.Count > 0 ? announced[^1].Version : row.Announced);
            await session.ExecAsync(
                "update agents.agent set template_security_applied = @applied, template_security_announced = @noticed where id = @id",
                new { id = row.Id, applied, noticed }, ct);
            changed++;
        }
        return changed;
    }
}
