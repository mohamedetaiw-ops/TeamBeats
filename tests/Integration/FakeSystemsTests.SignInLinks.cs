using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Api.DevHost;
using TeamBeats.DevFakes;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 003 US7: the direct sign-in link for the staging test employee. <c>/dev/fake/signin/employee</c> runs the real
/// single sign-on flow and the fake Microsoft page picks the configured fictional person (Ahmed Mansour, Finance) by
/// itself, so the tester lands signed in as an ordinary employee with no picker and no password.
/// </summary>
public sealed partial class FakeSystemsTests
{
    private const string EmployeeEmail = "ahmed.mansour@staging.example";

    /// <summary>Follows the direct link through single sign-on, as a browser would, and returns the session.</summary>
    private async Task<(string? Session, HttpResponseMessage Authorize)> FollowEmployeeLinkAsync(string pickOverride = "")
    {
        var tenant = _staging.Tenant(TenantKey);
        var link = await tenant.GetAsync(FakeEndpoints.SignInLinkPrefix + "employee");
        Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        Assert.Equal(TeamBeats.Modules.Identity.Sso.SsoEndpoints.StartPath, link.Headers.Location!.ToString());
        var pick = pickOverride.Length > 0 ? pickOverride : Cookie(link, FakeEndpoints.PickCookie);
        Assert.Equal(EmployeeEmail, WebUtility.UrlDecode(Cookie(link, FakeEndpoints.PickCookie)));

        var start = await tenant.GetAsync(link.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith($"/dev/fake/entra/{Directory}/oauth2/v2.0/authorize?", location, StringComparison.Ordinal);

        var browser = _staging.Tenant(TenantKey);
        browser.DefaultRequestHeaders.Add("Cookie", $"{FakeEndpoints.PickCookie}={pick}");
        var authorize = await browser.GetAsync(location);
        if (authorize.StatusCode != HttpStatusCode.Redirect)
        {
            return (null, authorize);
        }

        var back = _staging.Tenant(TenantKey);
        back.DefaultRequestHeaders.Add("Cookie", $"tb_sso={Cookie(start, "tb_sso")}");
        var landed = await back.GetAsync(authorize.Headers.Location!.PathAndQuery);
        return (Cookie(landed, "tb_session"), authorize);
    }

    [Fact]
    public async Task Employee_link_signs_in_the_test_employee_without_the_picker()
    {
        var (session, authorize) = await FollowEmployeeLinkAsync();

        // The fake page skipped the picker, returned a code and cleared the one-time pick.
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.Contains("code=", authorize.Headers.Location!.Query, StringComparison.Ordinal);
        Assert.Contains(authorize.Headers.GetValues("Set-Cookie"), c => c.StartsWith(FakeEndpoints.PickCookie + "=;", StringComparison.Ordinal));
        Assert.NotNull(session);

        var employee = _staging.Tenant(TenantKey, session);
        var me = await employee.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains("Ahmed Mansour", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An ordinary employee: no tenant administration.
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/v1/tenant/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Employee_link_session_is_not_accepted_on_another_tenant()
    {
        var (session, _) = await FollowEmployeeLinkAsync();
        Assert.NotNull(session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _staging.Tenant(world.A.Key, session).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Single_sign_on_returns_to_the_public_host_behind_the_web_proxy()
    {
        // Staging: the web app proxies /api and /dev to the API's private address and passes the public host in
        // X-Forwarded-Host. The callback Microsoft returns to must be the public host, never the private one.
        var publicHost = $"{TenantKey}.{ApiFactory.BaseDomain}";
        var request = new HttpRequestMessage(HttpMethod.Get, TeamBeats.Modules.Identity.Sso.SsoEndpoints.StartPath);
        request.Headers.Host = "teambeats-staging-api.internal:8080";
        request.Headers.Add("X-Forwarded-Host", publicHost);
        request.Headers.Add("X-Forwarded-Proto", "https");
        var start = await _staging.Tenant(TenantKey).SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = WebUtility.UrlDecode(start.Headers.Location!.ToString());
        Assert.Contains($"redirect_uri=https://{publicHost}/api/v1/auth/signin/sso/callback", location, StringComparison.Ordinal);
        Assert.DoesNotContain(".internal", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_sign_in_link_is_not_found_and_sets_nothing()
    {
        var response = await _staging.Tenant(TenantKey).GetAsync(FakeEndpoints.SignInLinkPrefix + "admin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(Cookie(response, FakeEndpoints.PickCookie));
    }

    [Fact]
    public async Task A_pick_for_a_person_who_cannot_sign_in_shows_the_picker_instead()
    {
        // A disabled leaver is not offered by the fake directory, so a pick naming them gives no code.
        var (session, authorize) = await FollowEmployeeLinkAsync("former.employee%40staging.example");
        Assert.Null(session);
        Assert.Equal(HttpStatusCode.OK, authorize.StatusCode);
        Assert.Contains("<form method=\"post\">", await authorize.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_in_links_must_name_an_enabled_person_of_the_scenario()
    {
        var scenario = _staging.Services.GetRequiredService<FakeSystems>().Scenario;
        FakeSystemsSetup.ValidateSignInLinks(new Dictionary<string, string> { ["employee"] = EmployeeEmail }, scenario);
        Assert.Throws<InvalidOperationException>(() => FakeSystemsSetup.ValidateSignInLinks(
            new Dictionary<string, string> { ["employee"] = "nobody@staging.example" }, scenario));
        Assert.Throws<InvalidOperationException>(() => FakeSystemsSetup.ValidateSignInLinks(
            new Dictionary<string, string> { ["leaver"] = "former.employee@staging.example" }, scenario));
    }

    [Fact]
    public void Staging_configures_the_employee_link()
    {
        var links = FakeSystemsSetup.ReadSignInLinks(_staging.Services.GetRequiredService<IConfiguration>());
        Assert.Equal(EmployeeEmail, links["employee"]);
    }

    [Fact]
    public void Production_guard_refuses_a_sign_in_link()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [FakeSystemsSetup.SignInLinksKey + ":employee"] = EmployeeEmail,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ProductionGuards.EnsureDevSignInDisabled(config));
    }

    [Fact]
    public async Task Production_host_has_no_sign_in_links()
    {
        var response = await world.Factory.TenantClient(world.A.Key).GetAsync(FakeEndpoints.SignInLinkPrefix + "employee");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
