using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>An organization unit or work location as a person's profile names it, in the caller's language.</summary>
public sealed record PublicPlace(string Id, string Name);

/// <summary>A person in a directory list: name, and the job title and unit when the field policy shows them.</summary>
public sealed record PublicPersonSummary(Guid Id, string DisplayName, string? JobTitle, PublicPlace? Unit, PublicPlace? Location);

/// <summary>A directory profile with the fields the caller may see under the field policy (spec 010), and the reporting line.</summary>
public sealed record PublicPerson(Guid Id, string DisplayName, PublicPlace? Unit, IReadOnlyList<PublicPlace> Units, PublicPlace? Location,
    IReadOnlyDictionary<string, string> Fields, PublicPersonSummary? Manager, int DirectReports);

/// <summary>A person's place in the org chart: the management chain upwards, peers and direct reports, with their totals.</summary>
public sealed record PublicOrgChart(PublicPersonSummary Person, IReadOnlyList<PublicPersonSummary> Chain, IReadOnlyList<PublicPersonSummary> Peers, int PeerCount,
    IReadOnlyList<PublicPersonSummary> Reports, int ReportCount);

/// <summary>
/// The public operations of <c>people:read</c> (spec 035 section 6, spec 010): directory search, profiles and the org
/// chart. Field policy applies as in TeamBeats: a person sees what they see in the directory, and an application app
/// sees the people in the units its own <c>people.view</c> covers, with the fields every employee sees.
/// </summary>
public static class PeoplePublicEndpoints
{
    private sealed record Position(int O);

    public static void MapPeoplePublicEndpoints(this IEndpointRouteBuilder app)
    {
        var people = app.MapPublicApi("Public: people");
        people.MapGet("/people", async (string? q, Guid? unitId, string? cursor, int? limit, PeopleService service, IDbSession session, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var offset = PublicEndpoints.DecodeCursor<Position>(cursor)?.O ?? 0;
            if (offset < 0)
            {
                throw TeamBeats.Primitives.AppException.Validation("cursor", "validation.api.cursor");
            }
            var names = await NamesAsync(locale, settings, ct);
            var page = await service.SearchAsync(q, unitId, null, offset, size, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new PublicPage<PublicPersonSummary>(page.Items.Select(c => Summary(c, names)).ToList(),
                page.Next is { } next ? PublicEndpoints.EncodeCursor(new Position(next)) : null));
        }).RequireScope(ScopeCatalogue.PeopleRead).RequiresCapability(Capabilities.PeopleDirectory).Produces<PublicPage<PublicPersonSummary>>();

        people.MapGet("/people/{id:guid}", async (Guid id, HttpContext http, PeopleService service, IDbSession session, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var names = await NamesAsync(locale, settings, ct);
            var view = await service.GetAsync(id, ct);
            await session.CommitAsync(ct);
            return PublicEndpoints.Resource(http, new PublicPerson(view.Id, names.Person(view.DisplayName, view.Names), Unit(view.Unit, names),
                view.Units.Select(u => Unit(u, names)!).ToList(), Location(view.Location, names),
                view.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal), view.Manager is { } m ? Summary(m, names) : null, view.DirectReports));
        }).RequireScope(ScopeCatalogue.PeopleRead).RequiresCapability(Capabilities.PeopleDirectory).Produces<PublicPerson>();

        people.MapGet("/people/{id:guid}/org-chart", async (Guid id, HttpContext http, PeopleService service, IDbSession session, RequestLocale locale,
            ITenantSettingsReader settings, CancellationToken ct) =>
        {
            var names = await NamesAsync(locale, settings, ct);
            var chart = await service.OrgChartAsync(id, ct);
            await session.CommitAsync(ct);
            return PublicEndpoints.Resource(http, new PublicOrgChart(Summary(chart.Person, names), chart.Chain.Select(c => Summary(c, names)).ToList(),
                chart.Peers.Select(c => Summary(c, names)).ToList(), chart.PeerCount, chart.Reports.Select(c => Summary(c, names)).ToList(), chart.ReportCount));
        }).RequireScope(ScopeCatalogue.PeopleRead).RequiresCapability(Capabilities.PeopleDirectory).Produces<PublicOrgChart>();
    }

    private sealed record Names(string Locale, string Fallback)
    {
        public string Pick(IReadOnlyDictionary<string, string> names, string otherwise) =>
            names.TryGetValue(Locale, out var name) ? name : names.TryGetValue(Fallback, out var other) ? other : otherwise;

        public string Person(string displayName, IReadOnlyDictionary<string, string> names) => names.TryGetValue(Locale, out var name) ? name : displayName;
    }

    private static async Task<Names> NamesAsync(RequestLocale locale, ITenantSettingsReader settings, CancellationToken ct) =>
        new(locale.Locale, (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale);

    private static PublicPersonSummary Summary(PersonCard card, Names names) =>
        new(card.Id, names.Person(card.DisplayName, card.Names), card.JobTitle, Unit(card.Unit, names), Location(card.Location, names));

    private static PublicPlace? Unit(UnitRef? unit, Names names) =>
        unit is null ? null : new PublicPlace(unit.Id.ToString(), names.Pick(unit.Names, unit.Names.Values.FirstOrDefault() ?? string.Empty));

    private static PublicPlace? Location(LocationRef? location, Names names) =>
        location is null ? null : new PublicPlace(location.Key, names.Pick(location.Names, location.Label));
}

/// <summary>Whether the principal in context may read a person through the public API (spec 035 FR-018), by the endpoint's own read.</summary>
public sealed class PersonVisibility(PeopleService people) : PublicReadVisibility
{
    public override string ResourceType => PublicResourceTypes.Person;

    protected override Task ReadAsync(Guid resourceId, CancellationToken ct) => people.GetAsync(resourceId, ct);
}
