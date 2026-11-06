using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using TeamBeats.Api;
using TeamBeats.Api.DevHost;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Fake external systems for the development host and staging (docs/testing/fake-systems.md): the staging scenario is
/// seeded through the real use cases, single sign-on and admin consent run through the fake Microsoft pages with the
/// real adapters, the fake Graph serves mail, calendar, free/busy and rooms, and the fake OpenRouter answers chat
/// completions. Guards keep fakes out of production and make them mandatory in staging.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed partial class FakeSystemsTests(TenantWorld world) : IAsyncLifetime
{
    private const string TenantKey = "fakes-demo";
    private const string Directory = "5a7e0c3d-1f2b-4c6d-9e8f-0a1b2c3d4e5f";
    private const string Layla = "00000000-0000-4000-8000-000000000001";
    private const string Former = "00000000-0000-4000-8000-000000000022";
    private DevHostFactory _staging = null!;

    public Task InitializeAsync()
    {
        _staging = new DevHostFactory(world.Database, DevAuthModule.StagingEnvironment, new Dictionary<string, string?>
        {
            ["Tenancy:Hosts:Mode"] = "Shared",
            ["Dev:MigrateOnStart"] = "false",
            ["Dev:Fakes:RunDirectorySync"] = "false",
            ["Dev:SeedTenant:Key"] = TenantKey,
            [TeamBeats.Modules.Integrations.Microsoft365.TokenProtector.KeySetting] = ApiFactory.TokenKey,
        });
        _ = _staging.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _staging.DisposeAsync();

    private async Task<string> TestAdminAsync()
    {
        var response = await _staging.Tenant(TenantKey).PostAsJsonAsync("/dev/signin", new { username = "admin" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return DevHostFactory.SessionToken(response)!;
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..]
            : null;

    private static FormUrlEncodedContent FormFrom(string html, string oid)
    {
        var fields = HiddenField().Matches(html).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Value));
        fields["oid"] = oid;
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>Single sign-on through the fake Entra ID page: start, pick the account, return to the callback.</summary>
    private async Task<(HttpResponseMessage Landed, string? Session, string PickerHtml)> SignInWithFakeMicrosoftAsync(string oid)
    {
        var tenant = _staging.Tenant(TenantKey);
        var start = await tenant.GetAsync("/api/v1/auth/signin/sso");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith($"/dev/fake/entra/{Directory}/oauth2/v2.0/authorize?", location, StringComparison.Ordinal);
        var binding = Cookie(start, "tb_sso");

        var page = await tenant.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var picked = await tenant.PostAsync(location.Split('?')[0], FormFrom(html, oid));
        Assert.Equal(HttpStatusCode.Redirect, picked.StatusCode);
        var callback = picked.Headers.Location!;

        var back = _staging.Tenant(TenantKey);
        back.DefaultRequestHeaders.Add("Cookie", $"tb_sso={binding}");
        var landed = await back.GetAsync(callback.PathAndQuery);
        return (landed, Cookie(landed, "tb_session"), html);
    }

    [Fact]
    public async Task An_employee_connects_Outlook_through_the_fake_Microsoft_page()
    {
        // Spec 018: the real connect flow (PKCE, login hint, delegated scopes) against the fake page and token endpoint.
        var (_, session, _) = await SignInWithFakeMicrosoftAsync(Layla);
        var employee = _staging.Tenant(TenantKey, session);
        var start = await employee.GetAsync("/api/v1/me/outlook/connect");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith($"/dev/fake/entra/{Directory}/oauth2/v2.0/authorize?", location, StringComparison.Ordinal);

        var html = await (await employee.GetAsync(location)).Content.ReadAsStringAsync();
        var picked = await employee.PostAsync(location.Split('?')[0], FormFrom(html, Layla));
        var callback = picked.Headers.Location!;
        Assert.Contains("code=", callback.Query, StringComparison.Ordinal);

        // Shared mode: Microsoft returns to the platform host, which relays to the tenant host.
        var relay = await _staging.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"https://{callback.Host}"), AllowAutoRedirect = false, HandleCookies = false,
        }).GetAsync(callback.PathAndQuery);
        var finish = _staging.Tenant(TenantKey, session);
        finish.DefaultRequestHeaders.Add("Cookie", $"tb_outlook={Cookie(start, "tb_outlook")}");
        await finish.GetAsync((relay.Headers.Location ?? callback).PathAndQuery);

        var state = await (await employee.GetAsync("/api/v1/me/outlook")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", state.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Staging_scenario_is_seeded_through_the_real_use_cases()
    {
        var admin = _staging.Tenant(TenantKey, await TestAdminAsync());

        var idp = await (await admin.GetAsync("/api/v1/tenant/identity-provider")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"https://login.microsoftonline.com/{Directory}/v2.0", idp.GetProperty("issuer").GetString());

        var connections = await (await admin.GetAsync("/api/v1/integrations/connections")).Content.ReadFromJsonAsync<JsonElement>();
        var connection = Assert.Single(connections.EnumerateArray());
        Assert.Equal(Directory, connection.GetProperty("externalTenantId").GetString());
        var features = connection.GetProperty("features").EnumerateArray().Where(f => f.GetProperty("enabled").GetBoolean()).ToList();
        Assert.Equal(4, features.Count);
        Assert.All(features, f => Assert.Equal("granted", f.GetProperty("consentStatus").GetString()));

        var directory = await (await admin.GetAsync("/api/v1/tenant/directory")).Content.ReadFromJsonAsync<JsonElement>();
        var run = directory.GetProperty("runs").EnumerateArray().Last();
        Assert.Equal("succeeded", run.GetProperty("outcome").GetString());

        // 23 fictional people: 22 enabled and one disabled leaver. Synced users carry Arabic and English names.
        var tenantId = await world.Database.OwnerScalarAsync<Guid>($"select id from tenancy.tenant where key = '{TenantKey}'");
        Assert.Equal(23L, await world.Database.CountInTenantAsync(tenantId, "select count(*) from identity.directory_user"));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(tenantId, "select count(*) from identity.user_account where display_name = @n", ("n", "عمر الفارسي")));
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        await _staging.Services.GetRequiredService<ScenarioSeeder>().SeedAsync(TenantKey, CancellationToken.None);
        var tenantId = await world.Database.OwnerScalarAsync<Guid>($"select id from tenancy.tenant where key = '{TenantKey}'");
        Assert.Equal(1L, await world.Database.CountInTenantAsync(tenantId, "select count(*) from integrations.connection"));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(tenantId, "select count(*) from identity.idp_config"));
    }

    [Fact]
    public async Task A_fictional_employee_signs_in_through_the_fake_Microsoft_page()
    {
        var (landed, session, html) = await SignInWithFakeMicrosoftAsync(Layla);
        Assert.Contains("Layla Haddad", html, StringComparison.Ordinal);
        Assert.True(session is not null, landed.Headers.Location?.ToString());
        Assert.Contains("نورة القحطاني", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, landed.StatusCode);
        Assert.NotNull(session);

        var me = await _staging.Tenant(TenantKey, session).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        // Layla is in "TeamBeats Administrators", which the scenario maps to tenant administrator.
        Assert.Equal(HttpStatusCode.OK, (await _staging.Tenant(TenantKey, session).GetAsync("/api/v1/tenant/settings")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_is_not_offered_and_cannot_sign_in()
    {
        var (landed, session, html) = await SignInWithFakeMicrosoftAsync(Former);
        Assert.DoesNotContain("Former Employee", html, StringComparison.Ordinal);
        Assert.Null(session);
        Assert.Contains("/signin/problem", landed.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fake_directory_session_is_not_accepted_on_another_tenant()
    {
        var (_, session, _) = await SignInWithFakeMicrosoftAsync(Layla);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _staging.Tenant(world.A.Key, session).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_consent_goes_to_the_fake_consent_page_and_back()
    {
        var token = await TestAdminAsync();
        var admin = _staging.Tenant(TenantKey, token);
        var connections = await (await admin.GetAsync("/api/v1/integrations/connections")).Content.ReadFromJsonAsync<JsonElement>();
        var id = connections.EnumerateArray().First().GetProperty("id").GetGuid();

        var start = await admin.GetAsync($"/api/v1/integrations/connections/{id}/features/mail/consent");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith($"/dev/fake/entra/{Directory}/v2.0/adminconsent?", location, StringComparison.Ordinal);
        var page = await admin.GetAsync(location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Mail.ReadBasic", html, StringComparison.Ordinal);

        var fields = HiddenField().Matches(html).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        fields["decision"] = "accept";
        var accepted = await admin.PostAsync(location.Split('?')[0], new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var callback = accepted.Headers.Location!;
        Assert.Contains("admin_consent=True", callback.Query, StringComparison.Ordinal);

        var back = _staging.Tenant(TenantKey, token);
        back.DefaultRequestHeaders.Add("Cookie", $"tb_consent={Cookie(start, "tb_consent")}");
        var done = await back.GetAsync(callback.PathAndQuery);
        Assert.Contains("consent=granted", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_pages_refuse_to_redirect_off_the_test_environment()
    {
        var url = $"/dev/fake/entra/{Directory}/oauth2/v2.0/authorize?client_id=x&redirect_uri={Uri.EscapeDataString("https://attacker.example.com/cb")}&state=s&nonce=n&code_challenge=c";
        Assert.Equal(HttpStatusCode.BadRequest, (await _staging.Tenant(TenantKey).GetAsync(url)).StatusCode);
        var consent = $"/dev/fake/entra/{Directory}/v2.0/adminconsent?client_id=x&redirect_uri={Uri.EscapeDataString("https://attacker.example.com/cb")}&state=s";
        Assert.Equal(HttpStatusCode.BadRequest, (await _staging.Tenant(TenantKey).GetAsync(consent)).StatusCode);
    }

    [Fact]
    public async Task Fake_graph_serves_delegated_mail_calendar_free_busy_and_rooms()
    {
        var fakes = _staging.Services.GetRequiredService<TeamBeats.DevFakes.FakeSystems>();
        using var http = new HttpClient(fakes.Graph, disposeHandler: false);
        async Task<HttpClient> UserClient(string scopes)
        {
            var code = fakes.Graph.IssueDelegatedCode(Guid.Parse(Directory), Layla, FakeMicrosoftGraph.ClientId, "https://fakes-demo.teambeats.test/cb", null, scopes);
            var token = await http.PostAsync($"https://login.microsoftonline.com/{Directory}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = FakeMicrosoftGraph.ClientId,
                ["client_secret"] = FakeMicrosoftGraph.ClientSecret, ["redirect_uri"] = "https://fakes-demo.teambeats.test/cb",
            }));
            Assert.Equal(HttpStatusCode.OK, token.StatusCode);
            var access = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
            var client = new HttpClient(fakes.Graph, disposeHandler: false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
            return client;
        }

        var user = await UserClient("https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.ReadBasic https://graph.microsoft.com/Calendars.ReadBasic");
        var mail = await (await user.GetAsync("https://graph.microsoft.com/v1.0/me/messages?$top=5")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(mail.GetProperty("value").GetArrayLength() >= 3);
        Assert.Contains(mail.GetProperty("value").EnumerateArray(), m => m.GetProperty("subject").GetString() == "طلب إجازة سنوية");

        var today = DateTimeOffset.UtcNow.Date;
        var view = await (await user.GetAsync($"https://graph.microsoft.com/v1.0/me/calendarView?startDateTime={today.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}&endDateTime={today.AddDays(7):yyyy-MM-ddTHH:mm:ssZ}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(view.GetProperty("value").GetArrayLength() >= 4);

        var schedule = await user.PostAsync("https://graph.microsoft.com/v1.0/me/calendar/getSchedule", new StringContent(JsonSerializer.Serialize(new
        {
            schedules = new[] { "omar.alfarsi@staging.example" },
            startTime = new { dateTime = today.AddDays(4).ToString("yyyy-MM-ddT00:00:00"), timeZone = "UTC" },
            endTime = new { dateTime = today.AddDays(5).ToString("yyyy-MM-ddT00:00:00"), timeZone = "UTC" },
            availabilityViewInterval = 60,
        }), Encoding.UTF8, "application/json"));
        var free = await schedule.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains('2', free.GetProperty("value")[0].GetProperty("availabilityView").GetString()!);

        var rooms = await (await user.GetAsync("https://graph.microsoft.com/v1.0/places/microsoft.graph.room")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, rooms.GetProperty("value").GetArrayLength());

        // Without the mail scope the mailbox is refused, as Graph does.
        var calendarOnly = await UserClient("https://graph.microsoft.com/Calendars.ReadBasic");
        Assert.Equal(HttpStatusCode.Forbidden, (await calendarOnly.GetAsync("https://graph.microsoft.com/v1.0/me/messages")).StatusCode);
    }

    [Fact]
    public async Task Fake_openrouter_answers_in_the_question_language_and_simulates_failures()
    {
        var fakes = _staging.Services.GetRequiredService<TeamBeats.DevFakes.FakeSystems>();
        var factory = _staging.Services.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(FakeSystemsSetup.OpenRouterClient);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TeamBeats.DevFakes.FakeOpenRouter.ApiKey);
        Task<HttpResponseMessage> Ask(string text) => client.PostAsJsonAsync("https://openrouter.ai/api/v1/chat/completions",
            new { model = "fake/teambeats-small", messages = new[] { new { role = "user", content = text } } });

        var english = await (await Ask("What is the leave policy?")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("This is a test answer", english.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.True(english.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
        var arabic = await (await Ask("ما هي سياسة الإجازات؟")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("هذا رد تجريبي", arabic.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)429, (await Ask("please [fake:429]")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask("hello")).StatusCode);
        Assert.True(fakes.OpenRouter.Requests >= 4);
    }

    [Fact]
    public async Task Fake_control_api_can_take_a_system_down()
    {
        var tenant = _staging.Tenant(TenantKey);
        Assert.Equal(HttpStatusCode.OK, (await tenant.PostAsync("/dev/fake/outage/entra?down=true", null)).StatusCode);
        try
        {
            var start = await tenant.GetAsync("/api/v1/auth/signin/sso");
            // Discovery may be cached from an earlier sign-in; then the token step fails instead. Either way no session.
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        }
        finally
        {
            await tenant.PostAsync("/dev/fake/outage/entra?down=false", null);
        }
        var status = await (await tenant.GetAsync("/dev/fake")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("staging-demo", status.GetProperty("scenario").GetString());
    }

    [Fact]
    public void Staging_refuses_to_start_without_fakes()
    {
        var env = new FakeEnvironment(DevAuthModule.StagingEnvironment);
        var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dev:Fakes:Enabled"] = "false" }).Build();
        Assert.Throws<InvalidOperationException>(() => FakeSystemsSetup.Validate(off, env));
    }

    [Fact]
    public void Fakes_refuse_a_real_Microsoft_application_and_non_test_environments()
    {
        var real = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dev:Fakes:Enabled"] = "true", ["Dev:Fakes:Scenario"] = "scenarios/staging-demo.json",
            ["Integrations:Microsoft365:ClientId"] = "11111111-2222-4333-8444-555555555555",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => FakeSystemsSetup.Validate(real, new FakeEnvironment("Development")));
        var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dev:Fakes:Enabled"] = "true", ["Dev:Fakes:Scenario"] = "scenarios/staging-demo.json",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => FakeSystemsSetup.Validate(on, new FakeEnvironment("Production")));
    }

    [Fact]
    public void Production_guard_refuses_fake_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dev:Fakes:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => ProductionGuards.EnsureDevSignInDisabled(config));
    }

    [Fact]
    public void Scenario_with_a_real_looking_address_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), "tb-scenario-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """
            { "name": "bad", "directories": [ { "id": "5a7e0c3d-1f2b-4c6d-9e8f-0a1b2c3d4e5f",
              "users": [ { "id": "u1", "displayName": "Someone", "mail": "someone@contoso.com" } ] } ] }
            """);
        try
        {
            Assert.Throws<InvalidOperationException>(() => TeamBeats.DevFakes.FakeScenario.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([a-z_]+)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenField();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
