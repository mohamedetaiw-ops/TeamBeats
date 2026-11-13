using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Modules.PublicApi.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>A live news item, announcement or page in a public list, in the caller's language or the fallback.</summary>
public sealed record PublicContentSummary(Guid Id, Guid SiteId, string Type, string Locale, string Title, string? Summary, DateTimeOffset PublishedAt,
    bool Pinned, bool Featured, string WebUrl);

/// <summary>The live version of a content item for a public reader: the body is the structured document the item stores.</summary>
public sealed record PublicContentItem(Guid Id, Guid SiteId, string Type, string Locale, IReadOnlyList<string> Locales, string Title, string? Summary,
    BodyDocument Body, int Version, DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt, bool Expired, string WebUrl);

/// <summary>
/// The public operations of <c>content:read</c> (spec 035 section 6, spec 009). The list is the news feed: for a person
/// it is targeted as in TeamBeats, and for an application app, which has no audience, it is every live item the app's
/// own roles let it read. Reading one item asks <c>content.view</c> as the web page does; anything the caller may not
/// read, and anything not live, answers not found.
/// </summary>
public static class ContentPublicEndpoints
{
    public static void MapContentPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var content = app.MapPublicApi("Public: content");
        content.MapGet("/content", async (Guid? siteId, string? type, string? cursor, int? limit, HttpContext http, ContentFeed feed, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var page = await feed.PageAsync(siteId, type, cursor, size, ct, PublicEndpoints.MaxPageSize);
            var origin = Origin(http);
            return Results.Ok(new PublicPage<PublicContentSummary>(
                page.Items.Select(i => new PublicContentSummary(i.Id, i.SiteId, i.Type, i.Locale, i.Title, i.Summary, i.PublishedAt, i.Pinned, i.Featured,
                    WebUrl(origin, i.Id))).ToList(), page.Next));
        }).RequireScope(ScopeCatalogue.ContentRead).Produces<PublicPage<PublicContentSummary>>();

        content.MapGet("/content/{id:guid}", async (Guid id, HttpContext http, Items items, CancellationToken ct) =>
        {
            var view = await items.ReadAsync(id, ct);
            return PublicEndpoints.Resource(http, new PublicContentItem(view.Id, view.SiteId, view.Type, view.Locale, view.Locales, view.Title, view.Summary,
                view.Body, view.Version, view.PublishedAt, view.UpdatedAt, view.Expired, WebUrl(Origin(http), view.Id)));
        }).RequireScope(ScopeCatalogue.ContentRead).Produces<PublicContentItem>();
    }

    private static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string WebUrl(string origin, Guid id) => $"{origin}/content/read/{id}";
}
