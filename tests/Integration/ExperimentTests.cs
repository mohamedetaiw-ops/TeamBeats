using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Personalization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 R3 US9 scenario 8 and FR-022 (T024): a page publisher proposes showing an earlier release of a page to a share
/// of people; only a tenant administrator other than the proposer approves it; only people who agreed take part, each
/// always in the same arm; stopping shows everyone the current release with nothing left behind; results are counts per
/// arm with the minimum group size; the package decides and tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ExperimentTests(TenantWorld world)
{
    private const string Experiments = "/api/v1/experience/experiments";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.BadRequest)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : body.GetProperty("detailKey").GetString()!;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private const int People = 12;

    private sealed record World(TestTenant T, Guid Page, string Slug);

    /// <summary>A tenant page published twice, so release 1 can be tried against release 2.</summary>
    private async Task<World> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("ex-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "pub", MembershipTypes.Employee);
        await world.AssignAsync(t, "pub", BuiltInRoles.PagePublisher, "tenant", null);
        for (var i = 1; i <= People; i++)
        {
            await world.SeedUserAsync(t, "e" + i, MembershipTypes.Employee);
        }
        var slug = "ex-" + Guid.NewGuid().ToString("N")[..8];
        var page = (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug, orgUnitId = (Guid?)null }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page}/publish", new { reason = "first version" }));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page}/publish", new { reason = "second version" }));
        return new World(t, page, slug);
    }

    private static async Task<int> ReleaseAsync(HttpClient client, string slug) =>
        (await OkAsync(client.GetAsync($"/api/v1/experience/render?slug={slug}"))).GetProperty("release").GetInt32();

    [Fact]
    public async Task An_approved_experiment_shows_the_variant_only_to_consenting_people_in_a_stable_arm_and_stopping_leaves_nothing()
    {
        var w = await FreshAsync();
        var t = w.T;
        Assert.Equal(2, await ReleaseAsync(t["e1"].Client, w.Slug));

        // Proposals are checked and need page.publish.
        Assert.Equal("validation.experience.experiment_percent", await ProblemKeyAsync(t["pub"].Client.PostAsJsonAsync(Experiments,
            new { pageId = w.Page, name = "Shorter home", variantRelease = 1, percent = 60 })));
        Assert.Equal("validation.experience.experiment_release", await ProblemKeyAsync(t["pub"].Client.PostAsJsonAsync(Experiments,
            new { pageId = w.Page, name = "Shorter home", variantRelease = 2, percent = 50 })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e1"].Client.PostAsJsonAsync(Experiments,
            new { pageId = w.Page, name = "Shorter home", variantRelease = 1, percent = 50 })).StatusCode);
        var proposed = await OkAsync(t["pub"].Client.PostAsJsonAsync(Experiments, new { pageId = w.Page, name = "Shorter home", variantRelease = 1, percent = 50 }),
            HttpStatusCode.Created);
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal("proposed", proposed.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperimentProposed, id.ToString());
        Assert.Equal("validation.experience.experiment_open", await ProblemKeyAsync(t["pub"].Client.PostAsJsonAsync(Experiments,
            new { pageId = w.Page, name = "Again", variantRelease = 1, percent = 10 }), HttpStatusCode.Conflict));

        // Proposed is not running; only a tenant administrator approves.
        await OkAsync(t["e1"].Client.PutAsJsonAsync("/api/v1/me/experiments", new { consented = true }));
        Assert.Equal(2, await ReleaseAsync(t["e1"].Client, w.Slug));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["pub"].Client.PostAsJsonAsync($"{Experiments}/{id}/approve", new { })).StatusCode);
        Assert.Equal("running", (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Experiments}/{id}/approve", new { }))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperimentApproved, id.ToString());

        // Every person who agreed is in the arm the hash gives, every time; someone who did not agree sees the current release.
        for (var i = 1; i <= People; i++)
        {
            var person = t["e" + i];
            if (i <= People - 1)
            {
                await OkAsync(person.Client.PutAsJsonAsync("/api/v1/me/experiments", new { consented = true }));
            }
            var expected = i <= People - 1 && TeamBeats.Modules.Experience.Personalization.Experiments.InVariant(id, person.Id, 50) ? 1 : 2;
            Assert.Equal(expected, await ReleaseAsync(person.Client, w.Slug));
            Assert.Equal(expected, await ReleaseAsync(person.Client, w.Slug));
        }
        await DispatchAsync();
        var exposed = await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(distinct user_key) from analytics.event where name = 'experiment.exposure' and fields->>'experimentId' = @id", ("id", id.ToString()));
        Assert.Equal(People - 1, exposed);

        // Results per arm, counted by person; an arm under the minimum group size shows only how many.
        var results = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/analytics/experiments/{id}"));
        var arms = results.GetProperty("arms").EnumerateArray().ToDictionary(a => a.GetProperty("arm").GetString()!);
        Assert.Equal(People - 1, arms["control"].GetProperty("people").GetInt32() + arms["variant"].GetProperty("people").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e1"].Client.GetAsync($"/api/v1/analytics/experiments/{id}")).StatusCode);

        // Stopping returns everyone to the current release, and nothing about people was stored by the experiment.
        Assert.Equal("stopped", (await OkAsync(t["pub"].Client.PostAsJsonAsync($"{Experiments}/{id}/stop", new { }))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperimentStopped, id.ToString());
        for (var i = 1; i <= People; i++)
        {
            Assert.Equal(2, await ReleaseAsync(t["e" + i].Client, w.Slug));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await t["pub"].Client.PostAsJsonAsync($"{Experiments}/{id}/stop", new { })).StatusCode);
        Assert.Single((await OkAsync(t["admin"].Client.GetAsync(Experiments))).GetProperty("experiments").EnumerateArray());
    }

    [Fact]
    public async Task The_proposer_cannot_approve_their_own_and_the_package_and_tenant_decide()
    {
        var w = await FreshAsync();
        var t = w.T;
        var own = (await OkAsync(t["admin"].Client.PostAsJsonAsync(Experiments, new { pageId = w.Page, name = "Mine", variantRelease = 1, percent = 20 }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal("validation.experience.experiment_own", await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Experiments}/{own}/approve", new { })));
        Assert.Equal("validation.experience.experiment_choice", await ProblemKeyAsync(t["e1"].Client.PutAsJsonAsync("/api/v1/me/experiments", new { })));

        // Another tenant can neither see nor act on it.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.PostAsJsonAsync($"{Experiments}/{own}/stop", new { })).StatusCode);
        Assert.Empty((await OkAsync(other.T["admin"].Client.GetAsync(Experiments))).GetProperty("experiments").EnumerateArray());

        // A package without experiments: none can be proposed and nobody is asked to take part.
        var plain = await FreshAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.T["pub"].Client.PostAsJsonAsync(Experiments,
            new { pageId = plain.Page, name = "Nope", variantRelease = 1, percent = 20 })).StatusCode);
        Assert.False((await OkAsync(plain.T["e1"].Client.GetAsync("/api/v1/me/experiments"))).GetProperty("available").GetBoolean());
        Assert.Equal("validation.experience.experiment_unavailable", await ProblemKeyAsync(plain.T["e1"].Client.PutAsJsonAsync("/api/v1/me/experiments",
            new { consented = true })));
    }
}
