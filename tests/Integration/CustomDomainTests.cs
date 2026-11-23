using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Domains;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T021 (US7, FR-010): custom domains. A host is served only after its TXT record proves the tenant controls
/// it; each verified host reaches its own tenant with its own title, description and sign-in message; a host belongs to
/// the tenant that verified it first; a removed record stops the host and tells administrators, as does a certificate
/// about to end; without the package, for an employee and for another tenant's domain it is refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CustomDomainTests(TenantWorld world)
{
    private const string Domains = "/api/v1/tenant/domains";

    private static string NewHost(string label) => $"{label}-{Guid.NewGuid().ToString("N")[..8]}.intranet-test.example";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("cdm-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "staff", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        return t;
    }

    private async Task<JsonElement> AddAsync(TestTenant t, string host)
    {
        var added = await t["admin"].Client.PostAsJsonAsync(Domains, new { host });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        return await added.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> VerifiedAsync(TestTenant t, string host)
    {
        var domain = await AddAsync(t, host);
        world.Factory.Dns.SetTxt(domain.GetProperty("recordName").GetString()!, domain.GetProperty("recordValue").GetString()!);
        var verified = await t["admin"].Client.PostAsync($"{Domains}/{domain.GetProperty("id").GetGuid()}/verify", null);
        Assert.True(verified.IsSuccessStatusCode, await verified.Content.ReadAsStringAsync());
        return await verified.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_domain_is_served_only_once_its_record_is_verified_and_then_shows_its_own_presentation()
    {
        var t = await FreshAsync();
        var host = NewHost("acme");
        var domain = await AddAsync(t, host);
        var id = domain.GetProperty("id").GetGuid();
        Assert.Equal("pending", domain.GetProperty("state").GetString());
        Assert.Equal($"_teambeats-verify.{host}", domain.GetProperty("recordName").GetString());

        // Pending: the host reaches no tenant, and the edge is told not to issue a certificate for it.
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.ClientFor(host).GetAsync("/api/v1/site/presentation")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.ClientFor(host, t["admin"].Token).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.ClientFor("api.example").GetAsync($"/api/v1/domains/tls-allowed?domain={host}")).StatusCode);

        // Verifying without the record keeps it pending and says why.
        var missing = await (await t["admin"].Client.PostAsync($"{Domains}/{id}/verify", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", missing.GetProperty("state").GetString());
        Assert.Equal("record_missing", missing.GetProperty("failure").GetString());

        world.Factory.Dns.SetTxt($"_teambeats-verify.{host}", "v=spf1 -all", domain.GetProperty("recordValue").GetString()!);
        var verified = await (await t["admin"].Client.PostAsync($"{Domains}/{id}/verify", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", verified.GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TenantDomainVerified, id.ToString());

        // Verified: the host is this tenant, the session works there, and the edge may issue its certificate.
        var me = await world.Factory.ClientFor(host, t["admin"].Token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(t.Id, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await world.Factory.ClientFor("api.example").GetAsync($"/api/v1/domains/tls-allowed?domain={host.ToUpperInvariant()}")).StatusCode);

        var set = await t["admin"].Client.PutAsJsonAsync($"{Domains}/{id}/presentation", new
        {
            appTitle = new Dictionary<string, string> { ["en"] = "Acme Hub", ["ar"] = "منصة أكمي" },
            description = new Dictionary<string, string> { ["en"] = "Acme's intranet" },
            signInMessage = new Dictionary<string, string> { ["en"] = "Sign in with your Acme account" },
        });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        var shown = await world.Factory.ClientFor(host).GetFromJsonAsync<JsonElement>("/api/v1/site/presentation");
        Assert.True(shown.GetProperty("customDomain").GetBoolean());
        Assert.Equal("Acme Hub", shown.GetProperty("appTitle").GetString());
        Assert.Equal("Sign in with your Acme account", shown.GetProperty("signInMessage").GetString());
        // The tenant's own subdomain keeps the platform presentation.
        var own = await world.Factory.TenantClient(t.Key).GetFromJsonAsync<JsonElement>("/api/v1/site/presentation");
        Assert.False(own.GetProperty("customDomain").GetBoolean());

        // Removing the domain stops it at once.
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"{Domains}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.ClientFor(host).GetAsync("/api/v1/site/presentation")).StatusCode);
    }

    [Fact]
    public async Task Each_domain_reaches_its_own_tenant_and_a_host_belongs_to_the_first_tenant_to_verify_it()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var hostA = NewHost("a");
        var hostB = NewHost("b");
        await VerifiedAsync(a, hostA);
        await VerifiedAsync(b, hostB);
        Assert.Equal(a.Id, (await world.Factory.ClientFor(hostA, a["admin"].Token).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal(b.Id, (await world.Factory.ClientFor(hostB, b["admin"].Token).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("tenant").GetProperty("id").GetGuid());
        // A session of B presented on A's domain finds nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.ClientFor(hostA, b["admin"].Token).GetAsync("/api/v1/me")).StatusCode);

        // B claims A's host with its own record: refused, and A keeps it.
        var claim = await (await b["admin"].Client.PostAsJsonAsync(Domains, new { host = hostA })).Content.ReadFromJsonAsync<JsonElement>();
        world.Factory.Dns.SetTxt($"_teambeats-verify.{hostA}", claim.GetProperty("recordValue").GetString()!);
        var refused = await b["admin"].Client.PostAsync($"{Domains}/{claim.GetProperty("id").GetGuid()}/verify", null);
        Assert.Equal("conflict.tenant.domain_in_use", await KeyAsync(refused));
        Assert.Equal(a.Id, (await world.Factory.ClientFor(hostA, a["admin"].Token).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("tenant").GetProperty("id").GetGuid());

        // B never sees or changes A's domains.
        var aDomain = (await a["admin"].Client.GetFromJsonAsync<JsonElement>(Domains))[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.DeleteAsync($"{Domains}/{aDomain}")).StatusCode);
        Assert.DoesNotContain((await b["admin"].Client.GetFromJsonAsync<JsonElement>(Domains)).EnumerateArray(), d => d.GetProperty("id").GetGuid() == aDomain);
    }

    [Fact]
    public async Task A_removed_record_stops_the_domain_and_an_ending_certificate_tells_administrators()
    {
        var t = await FreshAsync();
        var host = NewHost("check");
        var domain = await VerifiedAsync(t, host);
        var id = domain.GetProperty("id").GetGuid();

        // The certificate ends in three days: administrators hear of it once.
        world.Factory.Certificates.Set(host, DateTimeOffset.UtcNow.AddDays(3));
        await DomainCheckWorker.RunForTenantAsync(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), t.Id, CancellationToken.None);
        await DomainCheckWorker.RunForTenantAsync(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), t.Id, CancellationToken.None);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @user and template_key = 'tenancy.domain.certificate'", ("user", t["admin"].Id)));

        // The record is gone at the next daily check: the domain stops, the subdomain still works, administrators are told.
        world.Factory.Dns.Remove(domain.GetProperty("recordName").GetString()!);
        await world.Database.TenantScalarAsync<int>(t.Id,
            "with u as (update tenancy.tenant_domain set checked_at = now() - interval '2 days' where id = @id returning 1) select count(*)::int from u", ("id", id));
        await DomainCheckWorker.RunForTenantAsync(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), t.Id, CancellationToken.None);
        var after = (await t["admin"].Client.GetFromJsonAsync<JsonElement>(Domains))[0];
        Assert.Equal("failed", after.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.ClientFor(host).GetAsync("/api/v1/site/presentation")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Factory.TenantClient(t.Key, t["admin"].Token).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @user and template_key = 'tenancy.domain.stopped'", ("user", t["admin"].Id)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TenantDomainFailed, id.ToString());
    }

    [Fact]
    public async Task Hosts_are_checked_and_the_feature_needs_the_package_and_settings_rights()
    {
        var t = await FreshAsync();
        Assert.Equal("validation.tenant.domain_host", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Domains, new { host = "not a host" })));
        Assert.Equal("validation.tenant.domain_host", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Domains, new { host = $"other.{ApiFactory.BaseDomain}" })));
        var host = NewHost("dup");
        await AddAsync(t, host);
        Assert.Equal("conflict.tenant.domain_exists", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Domains, new { host })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["staff"].Client.PostAsJsonAsync(Domains, new { host = NewHost("staff") })).StatusCode);

        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await small["admin"].Client.PostAsJsonAsync(Domains, new { host = NewHost("small") })).StatusCode);
    }

    [Theory]
    [InlineData("\"teambeats-verify=abc\"", "teambeats-verify=abc")]
    [InlineData("\"teambeats-\" \"verify=abc\"", "teambeats-verify=abc")]
    [InlineData("plain", "plain")]
    public void Txt_values_join_their_quoted_strings(string data, string value) => Assert.Equal(value, DohTxtLookup.Unquote(data));
}
