using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Spaces;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 slice 4 (T009, T010, US4, FR-007, FR-008): owners invite partners by email; the partner becomes a guest at
/// their first sign-in and sees only the spaces they are invited to; Confidential files stay closed to guests unless the
/// tenant's rule and the owner both allow it, and Restricted files always do; access ends on its date with the owners
/// told seven days before. Guests of one tenant reach nothing in another. Names and addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpaceGuestsTests(TenantWorld world) : IAsyncLifetime
{
    private const string Api = "/api/v1/spaces";
    private const string Libraries = "/api/v1/documents/libraries";
    private DevHostFactory _dev = null!;

    public Task InitializeAsync()
    {
        _dev = new DevHostFactory(world.Database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dev.DisposeAsync();

    private TestEmailSender Email => world.Factory.Email;

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>Omar owns spaces; Laila is a member; Huda a viewer; all work in Gulf. Guests are allowed with 60 days at most.</summary>
    private async Task<TestTenant> SetupAsync(bool guests = true)
    {
        var t = await world.CreateTenantAsync("sg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila", "huda" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        if (guests)
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        }
        return t;
    }

    private static async Task<JsonElement> SpaceAsync(TestTenant t, string name, string visibility = "private") =>
        await OkAsync(t["omar"].Client.PostAsJsonAsync(Api, new
        {
            name, description = $"Work on {name}.", visibility, template = "project", allowJoin = visibility == "public",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        }), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid library, string name, string classification) =>
        client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true&classification={classification}",
            new ByteArrayContent(Encoding.UTF8.GetBytes($"The {name} file.")));

    private async Task<HttpClient> SignInAsync(TestTenant t, string email)
    {
        var response = await _dev.Tenant(t.Key).PostAsJsonAsync("/dev/signin", new { email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return _dev.Tenant(t.Key, DevHostFactory.SessionToken(response));
    }

    private Task<int> RunAsync(TestTenant t, Func<SpaceGuests, DateTimeOffset, Task<int>> step, DateTimeOffset? at = null) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var done = await step(sp.GetRequiredService<SpaceGuests>(), at ?? DateTimeOffset.UtcNow);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        });

    [Fact]
    public async Task An_invited_partner_signs_in_as_a_guest_and_sees_only_their_space()
    {
        var t = await SetupAsync();
        var omar = t["omar"].Client;
        var space = (await SpaceAsync(t, "Vendor rollout")).GetProperty("id").GetGuid();
        var other = (await SpaceAsync(t, "Budget 2027")).GetProperty("id").GetGuid();
        var open = (await SpaceAsync(t, "Town hall", "public")).GetProperty("id").GetGuid();
        var address = $"rania@partner-{t.Key}.example";

        // Omar invites Rania as a member for 30 days; the invitation email goes out, the audit never holds the address.
        var guests = await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = address.ToUpperInvariant(), role = "member", days = 30 }));
        var invitation = Assert.Single(guests.GetProperty("invitations").EnumerateArray());
        Assert.Equal(address, invitation.GetProperty("email").GetString());
        Assert.Equal("sent", invitation.GetProperty("emailState").GetString());
        Assert.Contains($"/spaces/{space}", Assert.Single(Email.To(address), m => m.TenantId == t.Id).Message.Text, StringComparison.Ordinal);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like '%partner-%'"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SpacesGuestInvited);
        Assert.Equal("conflict.spaces.guest_invited", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = address }), HttpStatusCode.Conflict));

        // Only owners invite; guests are members or viewers; a person of the tenant is added as a member instead; the tenant caps the days.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/guests", new { email = "someone@partner.example" })).StatusCode);
        Assert.Equal("validation.spaces.guest_role", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = "a@partner.example", role = "owner" }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.guest_is_member", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = t["laila"].Email }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.guest_access_days", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = "b@partner.example", days = 61 }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.email", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = "not-an-address" }), HttpStatusCode.BadRequest));

        // Rania's first sign-in makes her a guest in the space, labelled as one with her end date.
        var rania = await SignInAsync(t, address);
        var me = await OkAsync(rania.GetAsync("/api/v1/me"));
        var raniaId = me.GetProperty("id").GetGuid();
        var member = (await OkAsync(omar.GetAsync($"{Api}/{space}/members"))).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == raniaId);
        Assert.True(member.GetProperty("guest").GetBoolean());
        Assert.Equal("member", member.GetProperty("role").GetString());
        Assert.True(member.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));
        Assert.Empty((await OkAsync(omar.GetAsync($"{Api}/{space}/guests"))).GetProperty("invitations").EnumerateArray());

        // She sees and works in that space only: no other space, public or private, no directory and no tenant search.
        var mine = await OkAsync(rania.GetAsync(Api));
        Assert.Equal(space, Assert.Single(mine.EnumerateArray()).GetProperty("id").GetGuid());
        await OkAsync(rania.GetAsync($"{Api}/{space}"));
        await OkAsync(rania.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Send the delivery plan" }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NotFound, (await rania.GetAsync($"{Api}/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rania.GetAsync($"{Api}/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rania.GetAsync($"{Api}/{open}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rania.PostAsync($"{Api}/{open}:join", null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await rania.GetAsync("/api/v1/people")).StatusCode);
        var search = await rania.GetAsync("/api/v1/search?q=Budget");
        Assert.DoesNotContain("Budget 2027", await search.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await rania.PostAsJsonAsync(Api, new { name = "Mine", visibility = "private", template = "project" })).StatusCode);

        // A guest never owns; invited to a second space, the same identity is added at once.
        Assert.Equal("validation.spaces.guest_role", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{space}/members", new { userId = raniaId, role = "owner" }),
            HttpStatusCode.BadRequest));
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{other}/guests", new { email = address, role = "viewer" }));
        Assert.Equal(2, (await OkAsync(rania.GetAsync(Api))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await rania.PostAsJsonAsync($"{Api}/{other}/tasks", new { title = "Viewer task" })).StatusCode);
        Assert.Equal("conflict.spaces.guest_exists", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/{other}/guests", new { email = address }), HttpStatusCode.Conflict));

        // Another tenant knows nothing of her: the same address is refused there, and its people cannot reach the guest list.
        var elsewhere = await SetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await _dev.Tenant(elsewhere.Key).PostAsJsonAsync("/dev/signin", new { email = address })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere["omar"].Client.GetAsync($"{Api}/{space}/guests")).StatusCode);
    }

    [Fact]
    public async Task Classified_files_stay_closed_to_guests_unless_the_tenant_and_the_owner_allow_them()
    {
        var t = await SetupAsync();
        var omar = t["omar"].Client;
        var created = await SpaceAsync(t, "Audit 2027");
        var space = created.GetProperty("id").GetGuid();
        var library = created.GetProperty("libraryId").GetGuid();
        var plan = (await OkAsync(UploadAsync(omar, library, "plan.txt", "internal"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var ledger = (await OkAsync(UploadAsync(omar, library, "ledger.txt", "confidential"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var payroll = (await OkAsync(UploadAsync(omar, library, "payroll.txt", "restricted"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var address = $"karim@audit-{t.Key}.example";
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = address }));
        var karim = await SignInAsync(t, address);

        async Task<string[]> Listed() => (await OkAsync(karim.GetAsync($"{Libraries}/{library}/items"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).Order().ToArray();

        // A viewer guest by default: reads the Internal file, adds nothing, and the classified ones are neither listed nor opened.
        Assert.Equal(["plan.txt"], await Listed());
        await OkAsync(karim.GetAsync($"/api/v1/documents/{plan}"));
        Assert.Equal("authz.denied.documents_guest_classified", await ProblemKeyAsync(karim.GetAsync($"/api/v1/documents/{ledger}"), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.documents_guest_classified", await ProblemKeyAsync(karim.GetAsync($"/api/v1/documents/{payroll}/download-token"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(karim, library, "notes.txt", "internal")).StatusCode);
        // Members of the tenant are not affected.
        Assert.Equal(3, (await OkAsync(t["huda"].Client.GetAsync($"{Libraries}/{library}/items"))).GetProperty("items").GetArrayLength());

        // The owner alone cannot open Confidential files to guests while the tenant's rule says no.
        Assert.Equal("validation.spaces.guest_confidential_rule",
            await ProblemKeyAsync(omar.PutAsJsonAsync($"{Api}/{space}/guests/files", new { confidential = true }), HttpStatusCode.BadRequest));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new
        {
            creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, guestConfidential = true, version = 1,
        }));
        Assert.Equal(["plan.txt"], await Listed());
        var view = await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/guests/files", new { confidential = true }));
        Assert.True(view.GetProperty("confidential").GetBoolean());

        // Both allow: Confidential opens; Restricted never does (spec 026 FR-011).
        Assert.Equal(["ledger.txt", "plan.txt"], await Listed());
        await OkAsync(karim.GetAsync($"/api/v1/documents/{ledger}"));
        Assert.Equal("authz.denied.documents_guest_classified", await ProblemKeyAsync(karim.GetAsync($"/api/v1/documents/{payroll}"), HttpStatusCode.Forbidden));

        // The tenant turning its rule off closes them again in every space.
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new
        {
            creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, guestConfidential = false, version = 2,
        }));
        Assert.Equal(["plan.txt"], await Listed());
    }

    [Fact]
    public async Task Guest_access_ends_on_its_date_and_the_owners_are_told_a_week_before()
    {
        var t = await SetupAsync();
        var omar = t["omar"].Client;
        var space = (await SpaceAsync(t, "Office move")).GetProperty("id").GetGuid();
        var address = $"sami@movers-{t.Key}.example";
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = address, role = "member", days = 10 }));
        var sami = await SignInAsync(t, address);
        var samiId = (await OkAsync(sami.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid();

        // Ten days out nothing is said; inside the last week the owners hear once.
        Assert.Equal(0, await RunAsync(t, (g, now) => g.RemindExpiringAsync(now, 100, default)));
        var soon = DateTimeOffset.UtcNow.AddDays(4);
        Assert.Equal(1, await RunAsync(t, (g, now) => g.RemindExpiringAsync(now, 100, default), soon));
        Assert.Equal(0, await RunAsync(t, (g, now) => g.RemindExpiringAsync(now, 100, default), soon));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where template_key = 'spaces.guest.expiring' and user_id = @u",
            ("u", t["omar"].Id)));

        // An owner renews, which needs the owner and stays within the tenant's limit.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PutAsJsonAsync($"{Api}/{space}/guests/{samiId}", new { days = 20 })).StatusCode);
        var renewed = await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/guests/{samiId}", new { days = 20 }));
        Assert.True(Assert.Single(renewed.GetProperty("guests").EnumerateArray()).GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(19));

        // When the date passes Sami leaves the space, the owners are told, and with no space left he is disabled and signed out.
        var later = DateTimeOffset.UtcNow.AddDays(21);
        Assert.Equal(1, await RunAsync(t, (g, now) => g.ExpireAsync(now, 100, default), later));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SpacesGuestExpired);
        Assert.DoesNotContain((await OkAsync(omar.GetAsync($"{Api}/{space}/members"))).EnumerateArray(), m => m.GetProperty("userId").GetGuid() == samiId);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where template_key = 'spaces.guest.expired'"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await sami.GetAsync(Api)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _dev.Tenant(t.Key).PostAsJsonAsync("/dev/signin", new { email = address })).StatusCode);

        // An invitation nobody takes up lapses; owners can also cancel one.
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = $"late@movers-{t.Key}.example", days = 7 }));
        var cancel = (await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/guests", new { email = $"never@movers-{t.Key}.example", days = 7 })))
            .GetProperty("invitations").EnumerateArray().Single(i => i.GetProperty("email").GetString()!.StartsWith("never@", StringComparison.Ordinal)).GetProperty("id").GetGuid();
        Assert.Single((await OkAsync(omar.DeleteAsync($"{Api}/{space}/guests/invitations/{cancel}"))).GetProperty("invitations").EnumerateArray());
        await RunAsync(t, (g, now) => g.ExpireAsync(now, 100, default), DateTimeOffset.UtcNow.AddDays(8));
        Assert.Empty((await OkAsync(omar.GetAsync($"{Api}/{space}/guests"))).GetProperty("invitations").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await _dev.Tenant(t.Key).PostAsJsonAsync("/dev/signin", new { email = $"late@movers-{t.Key}.example" })).StatusCode);
    }

    [Fact]
    public async Task Guests_need_the_tenant_rule_and_the_package()
    {
        var t = await SetupAsync(guests: false);
        var space = (await SpaceAsync(t, "Partner portal")).GetProperty("id").GetGuid();
        var view = await OkAsync(t["omar"].Client.GetAsync($"{Api}/{space}/guests"));
        Assert.False(view.GetProperty("allowed").GetBoolean());
        Assert.False(view.GetProperty("canInvite").GetBoolean());
        Assert.Equal("conflict.spaces.guests_off",
            await ProblemKeyAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/{space}/guests", new { email = "x@partner.example" }), HttpStatusCode.Conflict));

        // Workplace has no spaces and so no guests: the tenant rule cannot be set at all.
        var basic = await world.CreateTenantAsync("sgb-" + Guid.NewGuid().ToString("N")[..8], "workplace", "en", ["en", "ar"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new
        {
            creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0,
        })).StatusCode);
    }
}
