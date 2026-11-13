using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>A service in the catalogue, named in the caller's language, with a link to it inside TeamBeats.</summary>
public sealed record PublicService(Guid Id, string Kind, string Name, string? Description, Guid? CategoryId, string? Url, string WebUrl);

/// <summary>One service with the form of its published version, so an app knows which fields to send.</summary>
public sealed record PublicServiceDetail(Guid Id, string Kind, string Name, string? Description, Guid? CategoryId, int? Version, FormDefinition Form,
    bool AllowOnBehalf, bool HasApproval, string Classification, string WebUrl);

/// <summary>A request as its list shows it: status, current step, who filed it and for whom.</summary>
public sealed record PublicRequest(Guid Id, string Number, Guid ServiceId, string ServiceName, string Status, Guid RequesterId, string RequesterName,
    Guid? OnBehalfOfId, string? OnBehalfOfName, string? CurrentStep, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? ClosedAt,
    string WebUrl);

/// <summary>An entry of a request's tracking: what happened, at which step, by whom.</summary>
public sealed record PublicTrackingEntry(string Event, string? Step, string? ActorName, DateTimeOffset At);

/// <summary>A request with the data filed and its tracking. Attachments are listed by name only; they open inside TeamBeats.</summary>
public sealed record PublicRequestDetail(PublicRequest Request, int ServiceVersion, IReadOnlyDictionary<string, JsonElement> Data,
    IReadOnlyList<string> Attachments, IReadOnlyList<PublicTrackingEntry> Tracking);

/// <summary>A request to file: the service, the form data and, for someone else, the person it is for.</summary>
public sealed record PublicRequestSubmit(Guid ServiceId, Dictionary<string, JsonElement>? Data, Guid? OnBehalfOf);

/// <summary>
/// The public operations of <c>services:read</c>, <c>requests:read</c> and <c>requests:submit</c> (spec 035 section 6,
/// spec 016). A person sees the catalogue and their own requests as in TeamBeats and files for themselves, or for others
/// where 016 allows. An application app sees nothing without a request role (US1 scenario 3); with one it sees the
/// services and requests of the units the role covers (US1 scenario 4), and with the request filer role files a request
/// for one named person there. Attachments are not uploaded through the API in R2.
/// </summary>
public static class ServicePublicEndpoints
{
    private sealed record Cursor(DateTimeOffset At, Guid Id);

    public static void MapServicePublicEndpoints(this IEndpointRouteBuilder app)
    {
        var services = app.MapPublicApi("Public: services");
        services.MapGet("/services", async (string? q, Guid? categoryId, HttpContext http, Catalogue catalogue, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var view = await catalogue.ListAsync(q, categoryId, ct);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var origin = Origin(http);
            var items = view.Services.Select(s => new PublicService(s.Id, s.Kind, Pick(s.Names, locale.Locale, fallback) ?? string.Empty,
                Pick(s.Descriptions, locale.Locale, fallback), s.CategoryId, s.Url, origin + ServiceSearchSource.Link(s.Id))).ToList();
            return Results.Ok(new PublicPage<PublicService>(items, null));
        }).RequireScope(ScopeCatalogue.ServicesRead).Produces<PublicPage<PublicService>>();

        services.MapGet("/services/{id:guid}", async (Guid id, HttpContext http, Catalogue catalogue, RequestLocale locale, ITenantSettingsReader settings,
            CancellationToken ct) =>
        {
            var detail = await catalogue.DetailAsync(id, ct);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var s = detail.Service;
            return PublicEndpoints.Resource(http, new PublicServiceDetail(s.Id, s.Kind, Pick(s.Names, locale.Locale, fallback) ?? string.Empty,
                Pick(s.Descriptions, locale.Locale, fallback), s.CategoryId, detail.Version, detail.Form, detail.AllowOnBehalf, detail.HasApproval,
                detail.Classification, Origin(http) + ServiceSearchSource.Link(s.Id)));
        }).RequireScope(ScopeCatalogue.ServicesRead).Produces<PublicServiceDetail>();

        var requests = app.MapPublicApi("Public: requests");
        requests.MapGet("/requests", async (string? status, string? cursor, int? limit, HttpContext http, Requests service, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var after = PublicEndpoints.DecodeCursor<Cursor>(cursor);
            var rows = await service.PageAsync(string.IsNullOrWhiteSpace(status) ? null : status, after?.At, after?.Id, size + 1, ct);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var origin = Origin(http);
            var items = rows.Take(size).Select(r => Summary(r, locale.Locale, fallback, origin)).ToList();
            var next = rows.Count > size ? PublicEndpoints.EncodeCursor(new Cursor(rows[size - 1].CreatedAt, rows[size - 1].Id)) : null;
            return Results.Ok(new PublicPage<PublicRequest>(items, next));
        }).RequireScope(ScopeCatalogue.RequestsRead).Produces<PublicPage<PublicRequest>>();

        requests.MapGet("/requests/{id:guid}", async (Guid id, HttpContext http, Requests service, RequestLocale locale, ITenantSettingsReader settings,
            CancellationToken ct) =>
        {
            var detail = await service.DetailAsync(id, ct);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            return PublicEndpoints.Resource(http, Detail(detail, locale.Locale, fallback, Origin(http)));
        }).RequireScope(ScopeCatalogue.RequestsRead).Produces<PublicRequestDetail>();

        requests.MapPost("/requests", async (PublicRequestSubmit request, HttpContext http, Requests service, IDbSession session, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var (detail, _) = await service.SubmitAsync(new RequestSubmit(request.ServiceId, request.Data, request.OnBehalfOf, null), ct);
            var fallback = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            var body = Detail(detail, locale.Locale, fallback, Origin(http));
            await session.CommitAsync(ct);
            return Results.Created($"{PublicEndpoints.Version1}/requests/{detail.Request.Id}", body);
        }).RequireScope(ScopeCatalogue.RequestsSubmit).Idempotent().Produces<PublicRequestDetail>(StatusCodes.Status201Created);
    }

    private static PublicRequest Summary(RequestSummary r, string locale, string fallback, string origin) =>
        new(r.Id, r.Number, r.ServiceId, Pick(r.ServiceNames, locale, fallback) ?? string.Empty, r.Status, r.Requester.Id, r.Requester.DisplayName,
            r.OnBehalfOf?.Id, r.OnBehalfOf?.DisplayName, Pick(r.CurrentStep, locale, fallback), r.CreatedAt, r.SubmittedAt, r.ClosedAt,
            origin + RequestNotifier.Link(r.Id));

    private static PublicRequestDetail Detail(RequestDetail d, string locale, string fallback, string origin) =>
        new(Summary(d.Request, locale, fallback, origin), d.ServiceVersion, d.Data, d.Attachments.Select(a => a.FileName).ToList(),
            d.Tracking.Select(t => new PublicTrackingEntry(t.Event, t.Step, t.Actor?.DisplayName, t.At)).ToList());

    private static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string? Pick(IReadOnlyDictionary<string, string>? values, string locale, string fallback) =>
        values is null || values.Count == 0 ? null : values.TryGetValue(locale, out var v) ? v : values.TryGetValue(fallback, out var f) ? f : values.Values.First();
}

/// <summary>Whether the principal in context may read a request through the public API (spec 035 FR-018), by the endpoint's own read.</summary>
public sealed class RequestVisibility(Requests requests) : PublicReadVisibility
{
    public override string ResourceType => PublicResourceTypes.Request;

    protected override Task ReadAsync(Guid resourceId, CancellationToken ct) => requests.DetailAsync(resourceId, ct);
}
