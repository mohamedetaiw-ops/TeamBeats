using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Experience.Pages;

/// <summary>A page as the approval flow sees it.</summary>
internal sealed record PageInfo(Guid Id, string Slug, Guid? OrgUnitId, int? CurrentRelease);

/// <summary>
/// What page approvals (spec 032 R2 T023) use of the page lifecycle: they decide who may act, and these do the work
/// without checking again, as the release a decision makes is the approver's.
/// </summary>
public sealed partial class PageService
{
    private static PageInfo Info(PageRow page) => new(page.Id, page.Slug, page.OrgUnitId, page.CurrentRelease);

    internal async Task<PageInfo?> FindAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct) is { } page ? Info(page) : null;

    /// <summary>The page when the caller holds <paramref name="permission"/> at its scope; not found otherwise.</summary>
    internal async Task<PageInfo> EditableInfoAsync(Guid id, string permission, CancellationToken ct) => Info(await EditableAsync(id, permission, ct));

    internal Task<(int Revision, PageDefinition Draft)> DraftOfAsync(Guid id, CancellationToken ct) => DraftAsync(id, ct);

    /// <summary>The findings that would block publishing this definition now.</summary>
    internal async Task<List<Finding>> PublishFindingsAsync(Guid id, PageDefinition definition, CancellationToken ct)
    {
        var page = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        return await ValidateAsync(page, definition, ValidationMode.Publish, ct);
    }

    internal static void Block(IReadOnlyList<Finding> findings) => ThrowIfBlocking(findings);

    /// <summary>The definition of a release, or null for none.</summary>
    internal async Task<PageDefinition?> ReleasedAsync(Guid id, int? release, CancellationToken ct) =>
        release is { } r ? (await ReleaseAsync(id, r, ct)).Definition : null;

    internal async Task<RenderedPage> PreviewDefinitionAsync(PageInfo page, PageDefinition definition, string? previewLocale, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id = page.Id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        return await PreviewOfAsync(row, definition, previewLocale, ct);
    }

    /// <summary>Publishes an approved submission as the next release, published by the approver.</summary>
    internal async Task<ReleaseView> PublishApprovedAsync(PageInfo page, PageDefinition definition, string? reason, Guid submission, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id = page.Id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        var release = await InsertReleaseAsync(row, definition, reason, rollbackOf: null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PagePublished, "page", page.Id.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { release = release.Release, previous = row.CurrentRelease, reason, submission }), ct);
        await outbox.EnqueueAsync(new PagePublished(page.Id, release.Release), ct);
        return release;
    }
}

public sealed partial class PageService
{
    /// <summary>What publishing the page's draft reaches (spec 032 R2 T024, FR-054), for those who may publish or submit it.</summary>
    public async Task<ImpactReport> ImpactAsync(Guid id, ImpactReports impacts, CancellationToken ct)
    {
        // Editors see it too: under an approval route they submit rather than publish.
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        await impacts.DemandAsync(ct);
        var (_, draft) = await DraftAsync(id, ct);
        var current = page.CurrentRelease is { } r ? (await ReleaseAsync(id, r, ct)).Definition : null;
        return await impacts.ForPageAsync(Info(page), draft, current, ct);
    }
}

public sealed partial class PageService
{
    /// <summary>The page for someone holding <paramref name="permission"/> at its scope, with access refusals audited.</summary>
    internal async Task<PageInfo> PublishableInfoAsync(Guid id, CancellationToken ct) => Info(await EditableAsync(id, Permissions.PagePublish, ct, audited: true));

    /// <summary>The findings that block a scheduled release going live now, checked as the system (R2 T025).</summary>
    internal async Task<List<Finding>> GoLiveFindingsAsync(Guid id, PageDefinition definition, CancellationToken ct)
    {
        var page = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        return await ValidateAsync(page, definition, ValidationMode.Publish, ct, system: true);
    }

    /// <summary>Puts a scheduled release live, published by the person who scheduled it.</summary>
    internal async Task<ReleaseView> PublishScheduledAsync(Guid id, PageDefinition definition, string? reason, Guid scheduledBy, Guid schedule, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        var release = await InsertReleaseAsync(row, definition, reason, rollbackOf: null, ct, scheduledBy);
        await audit.WriteAsync(new AuditEntry(AuditActions.PagePublished, "page", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { release = release.Release, previous = row.CurrentRelease, reason, schedule }), ct);
        await outbox.EnqueueAsync(new PagePublished(id, release.Release), ct);
        return release;
    }

    /// <summary>
    /// Ends a scheduled release at its expiry: the release in force before it returns as a new release, or the page goes
    /// offline when it had none. Returns the new release, or null when the page went offline.
    /// </summary>
    internal async Task<int?> ExpireScheduledAsync(Guid id, int? restore, Guid scheduledBy, Guid schedule, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct) ?? throw Primitives.AppException.NotFound();
        int? release = null;
        if (restore is { } r)
        {
            var restored = (await ReleaseAsync(id, r, ct)).Definition;
            release = (await InsertReleaseAsync(row, restored, null, r, ct, scheduledBy)).Release;
            await outbox.EnqueueAsync(new PageRolledBack(id, release.Value, r), ct);
        }
        else
        {
            await session.ExecAsync("update experience.page set current_release = null where id = @id", new { id }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PageExpired, "page", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { expired = row.CurrentRelease, restored = restore, release, schedule }), ct);
        return release;
    }
}
