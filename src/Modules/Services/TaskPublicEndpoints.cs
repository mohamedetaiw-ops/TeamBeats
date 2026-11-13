using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>A task waiting for the signed-in person, named in their language, with a link to act on it inside TeamBeats.</summary>
public sealed record PublicTask(Guid TaskId, string Source, string Title, Guid? RequestId, string? RequestNumber, Guid? ServiceId, string? RequesterName,
    string? OnBehalfOfName, DateTimeOffset CreatedAt, DateTimeOffset? DueAt, string? WebUrl);

/// <summary>
/// The public operation of <c>tasks:read</c> (spec 035 section 6, spec 016): the person's own task inbox, for delegated
/// apps only. Approving, rejecting and attesting stay human actions inside TeamBeats, so the API lists and links only.
/// </summary>
public static class TaskPublicEndpoints
{
    public static void MapTaskPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapPublicApi("Public: tasks");
        tasks.MapGet("/tasks", async (Guid? serviceId, HttpContext http, Tasks inbox, RequestLocale locale, ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var items = (await inbox.InboxAsync(serviceId, ct)).Select(i => new PublicTask(i.TaskId, i.Source,
                Pick(i.Title, locale.Locale, fallback) ?? Pick(i.Step, locale.Locale, fallback) ?? Pick(i.ServiceNames, locale.Locale, fallback) ?? i.Name,
                i.RequestId, i.Number, i.ServiceId, i.Requester?.DisplayName, i.OnBehalfOf?.DisplayName, i.CreatedAt, i.DueAt,
                i.Link is { } link ? (link.StartsWith('/') ? origin + link : link) : null)).ToList();
            return Results.Ok(new PublicPage<PublicTask>(items, null));
        }).RequireScope(ScopeCatalogue.TasksRead).Produces<PublicPage<PublicTask>>();
    }

    private static string? Pick(IReadOnlyDictionary<string, string>? values, string locale, string fallback) =>
        values is null || values.Count == 0 ? null : values.TryGetValue(locale, out var v) ? v : values.TryGetValue(fallback, out var f) ? f : values.Values.First();
}
