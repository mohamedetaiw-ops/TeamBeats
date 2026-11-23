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
