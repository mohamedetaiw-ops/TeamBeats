using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.Documents;

/// <summary>A library the caller may read something in, named in the caller's language.</summary>
public sealed record PublicLibrary(Guid Id, string Name, string Scope, Guid? OrgUnitId);

/// <summary>A document in a library list: the version the caller may open, never someone else's draft.</summary>
public sealed record PublicDocumentSummary(Guid Id, Guid LibraryId, Guid? FolderId, string Name, string Classification, string? VersionLabel, DateTimeOffset UpdatedAt);

/// <summary>A version of a document as the public API shows it: what the file is, never who holds it or where it is stored.</summary>
public sealed record PublicDocumentVersion(Guid Id, string Label, string State, long Size, string ContentType, string FileName, string? Note, DateTimeOffset CreatedAt);

/// <summary>A document with its metadata and the version a reader gets: the version in force, or the current one for contributors.</summary>
public sealed record PublicDocument(Guid Id, Guid LibraryId, Guid? FolderId, string Name, string? Number, IReadOnlyDictionary<string, JsonElement> Metadata,
    string Classification, PublicDocumentVersion? Version, string ContentUrl);

/// <summary>
/// The public operations of <c>documents:read</c> (spec 035 section 6, spec 011): libraries, documents, versions and the
/// file itself from the built-in store. Each call goes through the library's own rights, for the person or for the app's
/// own roles; anything the caller may not read answers not found. The file is served on the call, so no download link
/// outlives the token that asked for it, and confidential and restricted files are audited as any download is.
/// </summary>
public static class DocumentPublicEndpoints
{
    private sealed record Position(int O);

    public static void MapDocumentPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapPublicApi("Public: documents");
        docs.MapGet("/libraries", async (Libraries libraries, RequestLocale locale, ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var list = (await libraries.ListAsync(ct)).Select(l => new PublicLibrary(l.Id, Name(l.Names, locale.Locale, fallback), l.Scope, l.OrgUnitId)).ToList();
            return Results.Ok(new PublicPage<PublicLibrary>(list, null));
        }).RequireScope(ScopeCatalogue.DocumentsRead).Produces<PublicPage<PublicLibrary>>();

        docs.MapGet("/libraries/{id:guid}/documents", async (Guid id, Guid? folderId, bool? recursive, string? cursor, int? limit, DocumentFiles files,
            CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var offset = PublicEndpoints.DecodeCursor<Position>(cursor)?.O ?? 0;
            if (offset < 0)
            {
                throw TeamBeats.Primitives.AppException.Validation("cursor", "validation.api.cursor");
            }
            var page = await files.ItemsAsync(id, new ItemQuery(folderId, recursive ?? false, null, null, null, "modified", true), null, size, offset, ct);
            var items = page.Items.Select(i => new PublicDocumentSummary(i.Id, id, i.FolderId, i.Name, i.Classification, i.VersionLabel, i.UpdatedAt)).ToList();
            var next = offset + items.Count < page.Total && items.Count > 0 ? PublicEndpoints.EncodeCursor(new Position(offset + items.Count)) : null;
            return Results.Ok(new PublicPage<PublicDocumentSummary>(items, next));
        }).RequireScope(ScopeCatalogue.DocumentsRead).Produces<PublicPage<PublicDocumentSummary>>();

        docs.MapGet("/documents/{id:guid}", async (Guid id, HttpContext http, DocumentFiles files, CancellationToken ct) =>
        {
            var view = await files.GetAsync(id, ct);
            var version = view.Rights.Contribute ? view.Current ?? view.Effective : view.Effective;
            return PublicEndpoints.Resource(http, new PublicDocument(view.Id, view.LibraryId, view.FolderId, view.Name, view.Number, view.Metadata,
                view.Classification, version is null ? null : Version(version), $"{http.Request.Scheme}://{http.Request.Host}{PublicEndpoints.Version1}/documents/{view.Id}/content"));
        }).RequireScope(ScopeCatalogue.DocumentsRead).Produces<PublicDocument>();

        docs.MapGet("/documents/{id:guid}/versions", async (Guid id, DocumentVersions versions, CancellationToken ct) =>
            Results.Ok(new PublicPage<PublicDocumentVersion>((await versions.VersionsAsync(id, ct)).Select(Version).ToList(), null)))
            .RequireScope(ScopeCatalogue.DocumentsRead).Produces<PublicPage<PublicDocumentVersion>>();

        docs.MapGet("/documents/{id:guid}/content", async (Guid id, Guid? versionId, HttpResponse response, DocumentFiles files, TeamBeats.Persistence.IDbSession session,
            CancellationToken ct) =>
        {
            var (content, type, fileName) = await files.ReadFileAsync(id, versionId, ct);
            await session.CommitAsync(ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers.CacheControl = "private, no-store";
            return Results.File(content, type, fileName);
        }).RequireScope(ScopeCatalogue.DocumentsRead).Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
    }

    private static PublicDocumentVersion Version(VersionView v) => new(v.Id, v.Label, v.State, v.Size, v.ContentType, v.FileName, v.Note, v.CreatedAt);

    private static string Name(IReadOnlyDictionary<string, string> names, string locale, string fallback) =>
        names.TryGetValue(locale, out var name) ? name : names.TryGetValue(fallback, out var other) ? other : names.Values.FirstOrDefault() ?? string.Empty;
}
