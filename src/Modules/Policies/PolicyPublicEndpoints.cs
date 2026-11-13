using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;

namespace TeamBeats.Modules.Policies;

/// <summary>A policy in force, in a public list.</summary>
public sealed record PublicPolicySummary(Guid Id, string Code, string Name, Guid? CategoryId, Guid? OrgUnitId, string Classification, int Version,
    DateTimeOffset? EffectiveFrom, DateTimeOffset? NextReviewAt);

/// <summary>A clause of the version in force, in the caller's language or the binding one.</summary>
public sealed record PublicPolicyClause(string Number, string Anchor, int Level, string Title, BodyDocument Body);

/// <summary>What the signed-in person owes on the policy (delegated calls only): the version and date due, and their last attestation.</summary>
public sealed record PublicAttestation(int? DueVersion, DateTimeOffset? DueAt, bool Overdue, int? LastVersion, DateTimeOffset? LastAt);

/// <summary>The version in force of a policy. Attesting stays a human action inside TeamBeats (section 6).</summary>
public sealed record PublicPolicy(Guid Id, string Code, string Name, string Classification, int Version, DateTimeOffset? EffectiveFrom, string Locale,
    string? BindingLocale, IReadOnlyList<PublicPolicyClause> Clauses, PublicAttestation? Attestation, string WebUrl);

/// <summary>
/// The public operations of <c>policies:read</c> (spec 035 section 6, spec 025): published policies in force and, for a
/// person, their own attestation status. A person reads the policies that apply to them as in TeamBeats; an
/// application app reads the published policies its own <c>content.view</c> covers. Drafts never leave TeamBeats here.
/// </summary>
public static class PolicyPublicEndpoints
{
    private sealed record Position(int O);

    public static void MapPolicyPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var policies = app.MapPublicApi("Public: policies");
        policies.MapGet("/policies", async (string? q, string? cursor, int? limit, PolicyRegister register, RequestLocale locale, CancellationToken ct) =>
        {
            var size = PublicEndpoints.PageSize(limit);
            var offset = PublicEndpoints.DecodeCursor<Position>(cursor)?.O ?? 0;
            if (offset < 0)
            {
                throw TeamBeats.Primitives.AppException.Validation("cursor", "validation.api.cursor");
            }
            var inForce = (await register.ListAsync(null, PolicyStatuses.Published, q, ct)).Where(p => p.CurrentVersion is not null).ToList();
            var page = inForce.Skip(offset).Take(size)
                .Select(p => new PublicPolicySummary(p.Id, p.Code, Pick(p.Names, locale.Locale, null, p.Code), p.CategoryId, p.OrgUnitId, p.Classification,
                    p.CurrentVersion!.Value, p.EffectiveFrom, p.NextReviewAt)).ToList();
            var next = offset + page.Count < inForce.Count ? PublicEndpoints.EncodeCursor(new Position(offset + page.Count)) : null;
            return Results.Ok(new PublicPage<PublicPolicySummary>(page, next));
        }).RequireScope(ScopeCatalogue.PoliciesRead).Produces<PublicPage<PublicPolicySummary>>();

        policies.MapGet("/policies/{id:guid}", async (Guid id, HttpContext http, PolicyRegister register, PolicyCampaigns campaigns, RequestLocale locale,
            CancellationToken ct) =>
        {
            var reading = await register.CurrentAsync(id, ct);
            var wanted = reading.Version.Locales.Contains(locale.Locale) ? locale.Locale : reading.BindingLocale ?? reading.Version.Locales.FirstOrDefault() ?? locale.Locale;
            var clauses = reading.Version.Clauses.Select(c => new PublicPolicyClause(c.Number, c.Anchor, c.Level, Pick(c.Titles, wanted, reading.BindingLocale, c.Number),
                c.Bodies.TryGetValue(wanted, out var body) ? body : reading.BindingLocale is { } b && c.Bodies.TryGetValue(b, out var binding) ? binding
                    : c.Bodies.Values.FirstOrDefault() ?? new BodyDocument([]))).ToList();
            PublicAttestation? attestation = null;
            if (http.Caller().IsDelegated)
            {
                var mine = await campaigns.MineAsync(id, ct);
                attestation = new PublicAttestation(mine.Due?.Version, mine.Due?.DueAt, mine.Due?.Overdue ?? false, mine.LastVersion, mine.LastAt);
            }
            return PublicEndpoints.Resource(http, new PublicPolicy(reading.Policy.Id, reading.Policy.Code,
                Pick(reading.Policy.Names, locale.Locale, reading.BindingLocale, reading.Policy.Code), reading.Policy.Classification, reading.Version.Number ?? 0,
                reading.Version.EffectiveFrom, wanted, reading.BindingLocale, clauses, attestation,
                $"{http.Request.Scheme}://{http.Request.Host}/policies/{reading.Policy.Id}"));
        }).RequireScope(ScopeCatalogue.PoliciesRead).Produces<PublicPolicy>();
    }

    private static string Pick(IReadOnlyDictionary<string, string> values, string locale, string? binding, string otherwise) =>
        values.TryGetValue(locale, out var value) ? value : binding is not null && values.TryGetValue(binding, out var other) ? other : values.Values.FirstOrDefault() ?? otherwise;
}

/// <summary>Whether the principal in context may read a policy through the public API (spec 035 FR-018), by the endpoint's own read.</summary>
public sealed class PolicyVisibility(PolicyRegister register) : PublicReadVisibility
{
    public override string ResourceType => PublicResourceTypes.Policy;

    protected override Task ReadAsync(Guid resourceId, CancellationToken ct) => register.CurrentAsync(resourceId, ct);
}
