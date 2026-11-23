using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Pages;

/// <param name="ETag">The draft version the merge was made against; saving the merged definition with it replaces that version.</param>
/// <param name="Findings">The findings on the merged definition, as a save would report them.</param>
public sealed record MergeView(PageDefinition Definition, string ETag, IReadOnlyList<MergeConflict> Conflicts, IReadOnlyList<Finding> Findings);

public sealed record MergeRequest(PageDefinition? Definition);

/// <summary>An earlier version of the draft, kept when it was replaced.</summary>
public sealed record DraftRevisionView(int Revision, Guid UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// Edit conflicts (spec 032 R2 T026, US6 scenario 6, FR-042) and earlier draft versions (FR-043). A save against a stale
/// version is refused with both versions, the stored one with who saved it and when; the editor then keeps theirs, takes
/// the stored one or merges the two here. Every replaced draft is kept (the last 50 per page), so overwriting loses
/// nothing and an earlier version can be opened again.
/// </summary>
public sealed partial class PageService
{
    /// <summary>
    /// Merges the editor's <paramref name="request"/> definition, started from the version named by <paramref name="baseETag"/>,
    /// into the stored draft. Nothing is saved: the editor reviews the result and saves it with the returned ETag.
    /// </summary>
    public async Task<MergeView> MergeAsync(Guid id, string? baseETag, MergeRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        if (request.Definition is null)
        {
            throw AppException.Validation("definition", "validation.page.definition_required");
        }
        var (revision, theirs) = await DraftAsync(id, ct);
        if (baseETag is null || !baseETag.StartsWith("\"r", StringComparison.Ordinal) || !int.TryParse(baseETag.AsSpan(2, baseETag.Length - 3), out var from)
            || from > revision)
        {
            throw AppException.Validation("ifMatch", "validation.page.merge_base");
        }
        var @base = from == revision ? theirs : await RevisionDefinitionAsync(id, from, ct) ?? throw AppException.Conflict("conflict.page.merge_base_gone");
        var (merged, conflicts) = PageMerge.Merge(@base, request.Definition, theirs);
        return new MergeView(merged, ETag(revision), conflicts, await ValidateAsync(page, merged, ValidationMode.Save, ct));
    }

    /// <summary>The kept earlier versions of the draft, newest first.</summary>
    public async Task<IReadOnlyList<DraftRevisionView>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await EditableAsync(id, Permissions.PageEdit, ct);
        return await session.QueryAsync(
            "select revision, updated_by, updated_at from experience.page_draft_revision where page_id = @id order by revision desc", new { id },
            r => new DraftRevisionView(r.GetInt32(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
    }

    /// <summary>One kept version, to open it again: saving it with the current ETag makes it the draft.</summary>
    public async Task<PageDefinition> RevisionAsync(Guid id, int revision, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await EditableAsync(id, Permissions.PageEdit, ct);
        return await RevisionDefinitionAsync(id, revision, ct) ?? throw AppException.NotFound();
    }

    private async Task<PageDefinition?> RevisionDefinitionAsync(Guid id, int revision, CancellationToken ct)
    {
        var json = await session.QuerySingleOrDefaultAsync("select definition::text from experience.page_draft_revision where page_id = @id and revision = @revision",
            new { id, revision }, r => r.GetString(0), ct);
        return json is null ? null : JsonSerializer.Deserialize<PageDefinition>(json, JsonDefaults.Options);
    }

    /// <summary>Who saved the stored draft and when, for the conflict answer.</summary>
    private async Task<(Guid By, DateTimeOffset At)> DraftAuthorAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select updated_by, updated_at from experience.page_draft where page_id = @id", new { id },
            r => (r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1)), ct);
}
