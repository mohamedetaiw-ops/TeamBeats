using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Search;

/// <summary>A search result the caller may read, with an absolute link to it in TeamBeats.</summary>
public sealed record PublicSearchResult(Guid ItemId, string Source, string Kind, string Title, string Snippet, string WebUrl, string Locale,
    DateTimeOffset? UpdatedAt, bool Recommended);

/// <summary>
/// A page of results. <c>total</c> counts only what the caller may read and stops at 1,000, with <c>more</c> set when
/// there were more; <c>degraded</c> says semantic retrieval should have run and could not.
/// </summary>
public sealed record PublicSearchPage(string Query, int Total, bool More, IReadOnlyDictionary<string, int> Facets, bool Degraded,
    IReadOnlyList<PublicSearchResult> Items, string? NextCursor);

/// <summary>
/// The public operation of <c>search:query</c> (spec 035 section 6, spec 012): the same search as TeamBeats, with each
/// owning module filtering the candidates for the caller, so a person finds what they may read and an application app
/// what its own roles cover. Pages hold 20 results; the cursor carries the page.
/// </summary>
public static class SearchPublicEndpoints
{
    private sealed record Position(int P);

    public static void MapSearchPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var search = app.MapPublicApi("Public: search");
        search.MapGet("/search", async (string? q, string? kind, DateTimeOffset? from, DateTimeOffset? to, string? cursor, HttpContext http, SearchService service,
            IDbSession session, CancellationToken ct) =>
        {
            var page = PublicEndpoints.DecodeCursor<Position>(cursor)?.P ?? 1;
            if (page is < 1 or > SearchService.MaxPages)
            {
                throw TeamBeats.Primitives.AppException.Validation("cursor", "validation.api.cursor");
            }
            var results = await service.SearchAsync(q, kind, from, to, page, ct);
            await session.CommitAsync(ct);
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var next = page * results.PageSize < results.Total && page < SearchService.MaxPages ? PublicEndpoints.EncodeCursor(new Position(page + 1)) : null;
            return Results.Ok(new PublicSearchPage(results.Query, results.Total, results.More, results.Facets, results.Degraded,
                results.Results.Select(r => new PublicSearchResult(r.ItemId, r.Source, r.Kind, r.Title, r.Snippet, Absolute(origin, r.Link), r.Locale, r.UpdatedAt,
                    r.Recommended)).ToList(), next));
        }).RequireScope(ScopeCatalogue.SearchQuery).Produces<PublicSearchPage>();
    }

    private static string Absolute(string origin, string link) => link.StartsWith('/') ? origin + link : link;
}
