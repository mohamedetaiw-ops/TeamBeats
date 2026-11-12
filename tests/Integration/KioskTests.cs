using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Identity.Kiosk;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using TeamBeats.TenantContext;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 slice 4 (US4, FR-014 to FR-017): a device administrator enrols a kiosk for a unit; people of that unit who
/// hold kiosk sign-in use it with a badge and a PIN; five wrong PINs lock the badge everywhere and tell its owner; a
/// kiosk session reaches only the kiosk's features, ends after the kiosk's idle time and the moment the kiosk is
/// revoked; other tenants' badges and devices are strangers; the package decides.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class KioskTests(TenantWorld world)
{
    private const string Admin = "/api/v1/identity/kiosks";
    private const string Badges = "/api/v1/identity/badges";

    private sealed record Org(TestTenant T, Guid Riyadh, Guid Africa);

    /// <summary>A tablet: the device cookie it keeps and the session cookie of whoever is signed in on it.</summary>
    private sealed class Tablet(HttpClient client)
    {
        public string? Device { get; set; }
        public string? Session { get; set; }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            var cookies = new[] { Device is null ? null : $"{KioskRules.DeviceCookie}={Device}", Session is null ? null : $"tb_session={Session}" }.OfType<string>();
            if (cookies.Any())
            {
                request.Headers.Add("Cookie", string.Join("; ", cookies));
            }
            var response = await client.SendAsync(request);
            foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = header.Split(';')[0].Split('=', 2);
                if (pair[0] == KioskRules.DeviceCookie)
                {
                    Device = pair[1].Length == 0 ? null : pair[1];
                }
                else if (pair[0] == "tb_session")
                {
                    Session = pair[1].Length == 0 ? null : pair[1];
                    LastSessionCookie = header;
                }
            }
            return response;
        }

        public string? LastSessionCookie { get; private set; }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

        public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

        public Task<HttpResponseMessage> SignInAsync(string badge, string pin, bool setPin = false) =>
            PostAsync("/api/v1/kiosk/signin", new { badge, pin, setPin });
    }

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ks-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var riyadh = await world.CreateUnitAsync(t, "Riyadh", holding, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "department");
        foreach (var name in new[] { "devadmin", "khaled", "nour", "sami" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "khaled", "Riyadh");
        await world.AddMembershipAsync(t, "sami", "Riyadh");
        await world.AddMembershipAsync(t, "nour", "Africa");
        if (package != "workplace")
        {
            await world.AssignAsync(t, "devadmin", BuiltInRoles.DeviceAdministrator, "tenant", null);
            await world.AssignAsync(t, "khaled", BuiltInRoles.KioskUser, "tenant", null);
            await world.AssignAsync(t, "nour", BuiltInRoles.KioskUser, "tenant", null);
        }
        return new Org(t, riyadh, africa);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var problem = await OkAsync(call, expected);
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.TryGetProperty("detailKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : problem.GetProperty("code").GetString()!;
    }

    /// <summary>Creates a kiosk for Riyadh and enrols a tablet with its code.</summary>
    private async Task<(Guid Id, Tablet Tablet)> KioskAsync(Org o, string[]? features = null, string name = "Warehouse tablet")
    {
        var created = await OkAsync(o.T["devadmin"].Client.PostAsJsonAsync(Admin, new
        {
            name, orgUnitId = o.Riyadh, location = "Gate 2", features = features ?? SharedDeviceRules.Features.ToArray(), idleMinutes = 2,
        }), HttpStatusCode.Created);
        var tablet = new Tablet(world.Factory.TenantClient(o.T.Key));
        await OkAsync(tablet.PostAsync("/api/v1/kiosk/enrol", new { code = created.GetProperty("code").GetString() }));
        return (created.GetProperty("device").GetProperty("id").GetGuid(), tablet);
    }

    private async Task<string> BadgeAsync(Org o, string user)
    {
        var issued = await OkAsync(o.T["devadmin"].Client.PostAsync($"{Badges}/{o.T[user].Id}", null));
        Assert.Contains("<svg", issued.GetProperty("qrSvg").GetString());
        return issued.GetProperty("code").GetString()!;
    }

    private async Task NumberAsync(TestTenant t, string user, string number)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ProfileStore>().RefreshAsync(t[user].Id, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with u as (update people.profile set directory_values = jsonb_build_object('employeeNumber', @n::text) where user_id = @u returning 1) select count(*) from u",
            ("n", number), ("u", t[user].Id)));
    }

    [Fact]
    public async Task A_device_administrator_enrols_a_kiosk_and_its_people_sign_in_with_badge_and_PIN()
    {
        var o = await FreshAsync();
        var devadmin = o.T["devadmin"].Client;

        // Only device administrators enrol kiosks, within the rules (FR-014).
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["khaled"].Client.PostAsJsonAsync(Admin, new { name = "X", orgUnitId = o.Riyadh })).StatusCode);
        Assert.Equal("validation.kiosk.idle_range", await ProblemAsync(devadmin.PostAsJsonAsync(Admin, new { name = "X", orgUnitId = o.Riyadh, idleMinutes = 20 }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.kiosk.feature_unknown", await ProblemAsync(devadmin.PostAsJsonAsync(Admin,
            new { name = "X", orgUnitId = o.Riyadh, features = new[] { "payslips" } }), HttpStatusCode.BadRequest));

        var created = await OkAsync(devadmin.PostAsJsonAsync(Admin, new
        {
            name = "Warehouse tablet", orgUnitId = o.Riyadh, location = "Gate 2", features = new[] { SharedDeviceRules.Announcements, SharedDeviceRules.Leave },
        }), HttpStatusCode.Created);
        var id = created.GetProperty("device").GetProperty("id").GetGuid();
        var code = created.GetProperty("code").GetString()!;
        Assert.Equal(KioskStatuses.Waiting, created.GetProperty("device").GetProperty("status").GetString());
        Assert.Equal(KioskRules.DefaultIdleMinutes, created.GetProperty("device").GetProperty("idleMinutes").GetInt32());
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskEnrolled, id.ToString()));
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DeviceEnrolled", o.T.Id));

        // The tablet redeems the code once and keeps the device token in an HttpOnly cookie.
        var tablet = new Tablet(world.Factory.TenantClient(o.T.Key));
        Assert.False((await OkAsync(tablet.GetAsync("/api/v1/kiosk/device"))).GetProperty("enrolled").GetBoolean());
        Assert.Equal("validation.kiosk.enrol_code", await ProblemAsync(tablet.PostAsync("/api/v1/kiosk/enrol", new { code = "AAAA-BBBB-CCCC" }), HttpStatusCode.BadRequest));
        var enrolled = await OkAsync(tablet.PostAsync("/api/v1/kiosk/enrol", new { code = code.ToLowerInvariant() }));
        Assert.True(enrolled.GetProperty("enrolled").GetBoolean());
        Assert.Equal("Gate 2", enrolled.GetProperty("location").GetString());
        Assert.NotNull(tablet.Device);
        var other = new Tablet(world.Factory.TenantClient(o.T.Key));
        Assert.Equal("validation.kiosk.enrol_code", await ProblemAsync(other.PostAsync("/api/v1/kiosk/enrol", new { code }), HttpStatusCode.BadRequest));
        Assert.Equal(KioskStatuses.Enrolled, (await OkAsync(devadmin.GetAsync(Admin))).EnumerateArray().Single().GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["khaled"].Client.GetAsync(Admin)).StatusCode);

        // Badges: issued by a device administrator; only the hash is kept, so the code is never answered again.
        var badge = await BadgeAsync(o, "khaled");
        Assert.StartsWith("TB", badge);
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceBadgeIssued, o.T["khaled"].Id.ToString()));
        var status = await OkAsync(devadmin.GetAsync($"{Badges}/{o.T["khaled"].Id}"));
        Assert.True(status.GetProperty("issued").GetBoolean());
        Assert.False(status.GetProperty("pinSet").GetBoolean());
        Assert.DoesNotContain(badge, status.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["khaled"].Client.PostAsync($"{Badges}/{o.T["sami"].Id}", null)).StatusCode);

        // First use: the scanned badge chooses its PIN, 4 to 8 digits; then Khaled is signed in for this browser session only.
        Assert.Equal("conflict.kiosk.pin_not_set", await ProblemAsync(tablet.SignInAsync(badge, ""), HttpStatusCode.Conflict));
        Assert.Equal("validation.kiosk.pin_format", await ProblemAsync(tablet.SignInAsync(badge, "12", setPin: true), HttpStatusCode.BadRequest));
        await OkAsync(tablet.SignInAsync(badge.ToLowerInvariant(), "2468", setPin: true), HttpStatusCode.NoContent);
        Assert.NotNull(tablet.Session);
        Assert.DoesNotContain("max-age", tablet.LastSessionCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", tablet.LastSessionCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskSignIn, o.T["khaled"].Id.ToString()));
        var me = await OkAsync(tablet.GetAsync("/api/v1/me"));
        Assert.Equal(o.T["khaled"].Id, me.GetProperty("id").GetGuid());
        Assert.Equal(2, me.GetProperty("sharedDevice").GetProperty("idleMinutes").GetInt32());
        Assert.True((await OkAsync(devadmin.GetAsync($"{Badges}/{o.T["khaled"].Id}"))).GetProperty("pinSet").GetBoolean());

        // US4 scenario 3: the kiosk's features only, enforced by the API; payslips, mail, documents, offline and push never.
        foreach (var path in new[] { "/api/v1/me/payslips", "/api/v1/me/mail", "/api/v1/me/offline", "/api/v1/me/push", "/api/v1/me/notifications",
                     "/api/v1/me/policies", "/api/v1/me/attendance", "/api/v1/services", Admin, "/api/v1/me/kiosk-badge" })
        {
            Assert.Equal("authz.denied.shared_device", await ProblemAsync(tablet.GetAsync(path), HttpStatusCode.Forbidden));
        }
        Assert.Equal("authz.denied.shared_device", await ProblemAsync(tablet.PostAsync("/api/v1/me/sessions/revoke-all"), HttpStatusCode.Forbidden));
        await OkAsync(tablet.GetAsync("/api/v1/content/feed"));
        await OkAsync(tablet.GetAsync("/api/v1/me/leave"));
        await OkAsync(tablet.GetAsync("/api/v1/experience/shell"));
        // The same person on their own device is not limited.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await o.T["khaled"].Client.GetAsync("/api/v1/me/policies")).StatusCode);

        // The employee number works too once a PIN is set; without one, the badge itself must be scanned.
        await NumberAsync(o.T, "khaled", "E-100");
        await NumberAsync(o.T, "sami", "E-200");
        var first = tablet.Session;
        await OkAsync(tablet.SignInAsync("E-100", "2468"), HttpStatusCode.NoContent);
        Assert.NotEqual(first, tablet.Session);
        var samiBadge = await BadgeAsync(o, "sami");
        Assert.Equal("conflict.kiosk.scan_to_set_pin", await ProblemAsync(tablet.SignInAsync("E-200", "1357", setPin: true), HttpStatusCode.Conflict));

        // Sami has no kiosk sign-in: refused before any PIN is set; Nour belongs to another unit.
        Assert.Equal("authz.denied.kiosk_not_allowed", await ProblemAsync(tablet.SignInAsync(samiBadge, "1357", setPin: true), HttpStatusCode.Forbidden));
        Assert.False((await OkAsync(devadmin.GetAsync($"{Badges}/{o.T["sami"].Id}"))).GetProperty("pinSet").GetBoolean());
        var nourBadge = await BadgeAsync(o, "nour");
        var khaledSession = tablet.Session;
        Assert.Equal("authz.denied.kiosk_not_allowed", await ProblemAsync(tablet.SignInAsync(nourBadge, "9753", setPin: true), HttpStatusCode.Forbidden));
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskSignIn, o.T["nour"].Id.ToString()));

        // The administrator lets other units in: Nour signs in and Khaled, the person before, is signed out.
        var changed = await OkAsync(devadmin.PutAsJsonAsync($"{Admin}/{id}", new { allowOtherUnits = true, version = 2 }));
        Assert.True(changed.GetProperty("allowOtherUnits").GetBoolean());
        // Two changes: the tablet's enrolment and this one.
        Assert.Equal(2L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskChanged, id.ToString()));
        Assert.Equal("conflict.stale_version", await ProblemAsync(devadmin.PutAsJsonAsync($"{Admin}/{id}", new { idleMinutes = 3, version = 1 }), HttpStatusCode.Conflict));
        await OkAsync(tablet.SignInAsync(nourBadge, "9753", setPin: true), HttpStatusCode.NoContent);
        Assert.Equal(o.T["nour"].Id, (await OkAsync(tablet.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid());
        var before = new Tablet(world.Factory.TenantClient(o.T.Key)) { Device = tablet.Device, Session = khaledSession };
        Assert.Equal(HttpStatusCode.Unauthorized, (await before.GetAsync("/api/v1/me")).StatusCode);

        // A kiosk session cookie taken away from the tablet is worth nothing, and that use ends it.
        var stolen = new Tablet(world.Factory.TenantClient(o.T.Key)) { Session = tablet.Session };
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(KioskRules.WrongDevice, await world.Database.TenantScalarAsync<string>(o.T.Id,
            "select revoked_reason from identity.user_session where user_id = @u and kiosk_device_id is not null order by created_at desc limit 1", ("u", o.T["nour"].Id)));
    }

    [Fact]
    public async Task Five_wrong_PINs_lock_the_badge_on_every_kiosk_and_tell_its_owner()
    {
        var o = await FreshAsync();
        var (_, tablet) = await KioskAsync(o);
        var (_, second) = await KioskAsync(o, name: "Canteen tablet");
        var badge = await BadgeAsync(o, "khaled");
        await OkAsync(tablet.SignInAsync(badge, "2468", setPin: true), HttpStatusCode.NoContent);
        await OkAsync(tablet.PostAsync("/api/v1/auth/signout"), HttpStatusCode.NoContent);

        for (var i = 0; i < KioskRules.MaxFailures - 1; i++)
        {
            Assert.Equal("validation.kiosk.not_accepted", await ProblemAsync(tablet.SignInAsync(badge, "1111"), HttpStatusCode.BadRequest));
        }
        Assert.Equal("conflict.kiosk.badge_locked", await ProblemAsync(tablet.SignInAsync(badge, "1111"), HttpStatusCode.Conflict));
        Assert.Equal("conflict.kiosk.badge_locked", await ProblemAsync(tablet.SignInAsync(badge, "2468"), HttpStatusCode.Conflict));
        Assert.Equal("conflict.kiosk.badge_locked", await ProblemAsync(second.SignInAsync(badge, "2468"), HttpStatusCode.Conflict));
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskLockout, o.T["khaled"].Id.ToString()));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(o.T.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'kiosk.badge_locked' and category = 'security_notice'",
            ("u", o.T["khaled"].Id)));
        var locked = await OkAsync(o.T["khaled"].Client.GetAsync("/api/v1/me/kiosk-badge"));
        Assert.True(locked.GetProperty("lockedUntil").ValueKind == JsonValueKind.String);

        // An unknown badge is refused the same way as a wrong PIN, so a refusal says nothing about who holds a badge.
        Assert.Equal("validation.kiosk.not_accepted", await ProblemAsync(tablet.SignInAsync("TB" + new string('0', 20), "2468"), HttpStatusCode.BadRequest));

        // The owner sets a new PIN from their own device, which lifts the lock; an administrator's reset clears the PIN.
        Assert.Equal("validation.kiosk.pin_format", await ProblemAsync(o.T["khaled"].Client.PutAsJsonAsync("/api/v1/me/kiosk-badge/pin", new { pin = "12ab" }),
            HttpStatusCode.BadRequest));
        var reset = await OkAsync(o.T["khaled"].Client.PutAsJsonAsync("/api/v1/me/kiosk-badge/pin", new { pin = "86420" }));
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("lockedUntil").ValueKind);
        await OkAsync(second.SignInAsync(badge, "86420"), HttpStatusCode.NoContent);
        Assert.Equal("conflict.kiosk.no_badge", await ProblemAsync(o.T["sami"].Client.PutAsJsonAsync("/api/v1/me/kiosk-badge/pin", new { pin = "1234" }),
            HttpStatusCode.Conflict));
        var cleared = await OkAsync(o.T["devadmin"].Client.PostAsync($"{Badges}/{o.T["khaled"].Id}:reset-pin", null));
        Assert.False(cleared.GetProperty("pinSet").GetBoolean());
        Assert.Equal(2L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceBadgePinReset, o.T["khaled"].Id.ToString()));
        Assert.Equal("conflict.kiosk.pin_not_set", await ProblemAsync(tablet.SignInAsync(badge, "86420"), HttpStatusCode.Conflict));

        // A new badge for a lost one: the old code stops working.
        var replacement = await BadgeAsync(o, "khaled");
        Assert.Equal("validation.kiosk.not_accepted", await ProblemAsync(tablet.SignInAsync(badge, "1234", setPin: true), HttpStatusCode.BadRequest));
        await OkAsync(tablet.SignInAsync(replacement, "1234", setPin: true), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_kiosk_session_ends_after_the_kiosk_idle_time_and_at_once_when_the_kiosk_is_revoked()
    {
        var o = await FreshAsync();
        var (id, tablet) = await KioskAsync(o);
        var badge = await BadgeAsync(o, "khaled");
        await OkAsync(tablet.SignInAsync(badge, "2468", setPin: true), HttpStatusCode.NoContent);
        await OkAsync(tablet.GetAsync("/api/v1/me"));

        // US4 scenario 2: two minutes without use and the session is over on the server too.
        await world.Database.OwnerExecInTenantAsync(o.T.Id,
            "update identity.user_session set last_seen_at = now() - interval '3 minutes' where user_id = @u and kiosk_device_id is not null", ("u", o.T["khaled"].Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal("idle_timeout", await world.Database.TenantScalarAsync<string>(o.T.Id,
            "select revoked_reason from identity.user_session where user_id = @u and kiosk_device_id is not null", ("u", o.T["khaled"].Id)));

        // US4 scenario 5: revocation stops the tablet and its session at the next request, well within a minute.
        await OkAsync(tablet.SignInAsync(badge, "2468"), HttpStatusCode.NoContent);
        await OkAsync(tablet.GetAsync("/api/v1/me"));
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["khaled"].Client.PostAsync($"{Admin}/{id}:revoke", null)).StatusCode);
        var revoked = await OkAsync(o.T["devadmin"].Client.PostAsync($"{Admin}/{id}:revoke", null));
        Assert.Equal(KioskStatuses.Revoked, revoked.GetProperty("status").GetString());
        Assert.Equal(1L, await world.Database.AuditCountAsync(o.T.Id, AuditActions.DeviceKioskRevoked, id.ToString()));
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DeviceRevoked", o.T.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/v1/me")).StatusCode);
        Assert.False((await OkAsync(tablet.GetAsync("/api/v1/kiosk/device"))).GetProperty("enrolled").GetBoolean());
        Assert.Null(tablet.Device);
        Assert.Equal("conflict.kiosk.revoked", await ProblemAsync(o.T["devadmin"].Client.PostAsync($"{Admin}/{id}:enrol-code", null), HttpStatusCode.Conflict));

        // A replacement tablet: a new code disables the tablet enrolled before.
        var (second, old) = await KioskAsync(o, name: "Dock tablet");
        var renewed = await OkAsync(o.T["devadmin"].Client.PostAsync($"{Admin}/{second}:enrol-code", null));
        Assert.False((await OkAsync(old.GetAsync("/api/v1/kiosk/device"))).GetProperty("enrolled").GetBoolean());
        var fresh = new Tablet(world.Factory.TenantClient(o.T.Key));
        await OkAsync(fresh.PostAsync("/api/v1/kiosk/enrol", new { code = renewed.GetProperty("code").GetString() }));
        await OkAsync(fresh.SignInAsync(badge, "2468"), HttpStatusCode.NoContent);
        Assert.Equal("authz.denied.kiosk_not_enrolled", await ProblemAsync(new Tablet(world.Factory.TenantClient(o.T.Key)).SignInAsync(badge, "2468"),
            HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task Other_tenants_badges_and_devices_are_strangers_and_the_package_decides()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var (_, tabletA) = await KioskAsync(a);
        var (_, tabletB) = await KioskAsync(b);
        var badgeA = await BadgeAsync(a, "khaled");

        // Tenant A's badge on tenant B's kiosk is unknown; tenant B's device token on tenant A's host is not a kiosk.
        Assert.Equal("validation.kiosk.not_accepted", await ProblemAsync(tabletB.SignInAsync(badgeA, "2468", setPin: true), HttpStatusCode.BadRequest));
        var borrowed = new Tablet(world.Factory.TenantClient(a.T.Key)) { Device = tabletB.Device };
        Assert.False((await OkAsync(borrowed.GetAsync("/api/v1/kiosk/device"))).GetProperty("enrolled").GetBoolean());
        Assert.Equal("authz.denied.kiosk_not_enrolled", await ProblemAsync(
            new Tablet(world.Factory.TenantClient(a.T.Key)) { Device = tabletB.Device }.SignInAsync(badgeA, "2468", setPin: true), HttpStatusCode.Forbidden));
        Assert.Single((await OkAsync(b.T["devadmin"].Client.GetAsync(Admin))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["devadmin"].Client.GetAsync($"{Badges}/{a.T["khaled"].Id}")).StatusCode);
        await OkAsync(tabletA.SignInAsync(badgeA, "2468", setPin: true), HttpStatusCode.NoContent);

        // Workplace has no kiosks (spec 030 section 9).
        var w = await FreshAsync("workplace");
        await world.AssignAsync(w.T, "devadmin", BuiltInRoles.DeviceAdministrator, "tenant", null);
        Assert.Equal(ProblemCodes.EntitlementNotInPackage, await ProblemAsync(w.T["devadmin"].Client.PostAsJsonAsync(Admin, new { name = "X", orgUnitId = w.Riyadh }),
            HttpStatusCode.Forbidden));
        Assert.False((await OkAsync(new Tablet(world.Factory.TenantClient(w.T.Key)).GetAsync("/api/v1/kiosk/device"))).GetProperty("enrolled").GetBoolean());
    }

    [Fact]
    public void Every_route_a_kiosk_may_reach_exists()
    {
        var routes = world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, Normalize(e.RoutePattern.RawText!))))
            .ToHashSet();
        var missing = SharedDeviceRules.Routes.Where(r => !routes.Contains((r.Method, Normalize(r.Route)))).Select(r => $"{r.Method} {r.Route}").ToList();
        Assert.True(missing.Count == 0, "Kiosk routes that do not exist: " + string.Join(", ", missing));
    }

    private static string Normalize(string route) => route.Length > 1 ? route.TrimEnd('/') : route;
}
