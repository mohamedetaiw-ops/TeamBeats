using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T011, the scopes of the service catalogue and requests (016). A person sees the catalogue and their own
/// requests as in TeamBeats and files for themselves. An application app sees nothing without a request role (US1
/// scenario 3); the request reader at Acme Gulf reads Gulf requests only and Egypt ones are not found (US1 scenario 4);
/// the request filer files for a named Gulf person, who then owns the request, and cannot file for Egypt or for nobody.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicServiceEndpointTests(TenantWorld world)
{
    private const string Public = "/api/public/v1";
    private const string Admin = "/api/v1/services/admin";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt, Guid Everyone, Guid GulfOnly, Guid EgyptOnly);

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static async Task<Guid> ServiceAsync(TestTenant t, string name, Guid? unit)
    {
        var admin = t["admin"].Client;
        var created = await JsonAsync(await admin.PostAsJsonAsync(Admin, new
        {
            kind = "form", names = Names(name, name), audience = unit is { } u ? new { orgUnits = new[] { u } } : null,
        }), HttpStatusCode.Created);
        var id = created.GetProperty("service").GetProperty("id").GetGuid();
        await JsonAsync(await admin.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true } } },
            allowOnBehalf = true,
        }));
        await JsonAsync(await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { }));
        return id;
    }

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ps-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        foreach (var (name, unit) in new[] { ("gia", "Gulf"), ("gus", "Gulf"), ("eli", "Egypt") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return new Org(t, gulf, egypt, await ServiceAsync(t, "Business cards", null), await ServiceAsync(t, "Gulf parking", gulf),
            await ServiceAsync(t, "Egypt parking", egypt));
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> GetAsync(TestTenant t, string token, string path) => SendAsync(world, t, token, HttpMethod.Get, path);

    private static async Task<Guid> FileAsync(TestTenant t, string user, Guid service) =>
        (await JsonAsync(await t[user].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { boxes = 1 } }), HttpStatusCode.Created))
            .GetProperty("request").GetProperty("id").GetGuid();

    private static Guid[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(Id).ToArray();

    [Fact]
    public async Task A_person_sees_their_catalogue_files_for_themselves_and_reads_only_their_own_requests()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gia = await PersonAsync(world, t, "gia", ["services:read", "requests:read", "requests:submit"]);

        var catalogue = Ids(await JsonAsync(await GetAsync(t, gia, $"{Public}/services")));
        Assert.Contains(o.Everyone, catalogue);
        Assert.Contains(o.GulfOnly, catalogue);
        Assert.DoesNotContain(o.EgyptOnly, catalogue);
        var detail = await JsonAsync(await GetAsync(t, gia, $"{Public}/services/{o.GulfOnly}"));
        Assert.Equal("Gulf parking", detail.GetProperty("name").GetString());
        Assert.Equal("boxes", detail.GetProperty("form").GetProperty("fields")[0].GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(t, gia, $"{Public}/services/{o.EgyptOnly}")).StatusCode);

        var key = Guid.NewGuid().ToString();
        var body = new { serviceId = o.Everyone, data = new { boxes = 3 } };
        var first = await JsonAsync(await SendAsync(world, t, gia, HttpMethod.Post, $"{Public}/requests", body,
            r => r.Headers.TryAddWithoutValidation("Idempotency-Key", key)), HttpStatusCode.Created);
        var filed = first.GetProperty("request");
        Assert.Equal(t["gia"].Id, filed.GetProperty("requesterId").GetGuid());
        Assert.Equal(3, first.GetProperty("data").GetProperty("boxes").GetInt32());
        var again = await SendAsync(world, t, gia, HttpMethod.Post, $"{Public}/requests", body, r => r.Headers.TryAddWithoutValidation("Idempotency-Key", key));
        Assert.Equal(Id(filed), (await JsonAsync(again, HttpStatusCode.Created)).GetProperty("request").GetProperty("id").GetGuid());

        // A person cannot file for a service not offered to them, and reads only their own requests.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(world, t, gia, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.EgyptOnly, data = new { boxes = 1 } })).StatusCode);
        var gus = await FileAsync(t, "gus", o.Everyone);
        Assert.Equal([Id(filed)], Ids(await JsonAsync(await GetAsync(t, gia, $"{Public}/requests"))));
        Assert.Equal(Id(filed), (await JsonAsync(await GetAsync(t, gia, $"{Public}/requests/{Id(filed)}"))).GetProperty("request").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gia, $"{Public}/requests/{gus}")).StatusCode);

        // Another tenant's request is not found, and its token is not accepted here.
        var other = await FreshAsync();
        var foreign = await FileAsync(other.T, "gia", other.Everyone);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gia, $"{Public}/requests/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(other.T, gia, $"{Public}/requests")).StatusCode);
    }

    [Fact]
    public async Task An_app_without_a_request_role_sees_no_services_and_no_requests()
    {
        var o = await FreshAsync();
        var t = o.T;
        var request = await FileAsync(t, "gia", o.Everyone);
        var app = await AppAsync(world, t, ["services:read", "requests:read"], "tenant", null, role: null);

        Assert.Empty(Ids(await JsonAsync(await GetAsync(t, app, $"{Public}/services"))));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, app, $"{Public}/services/{o.Everyone}")).StatusCode);
        Assert.Empty(Ids(await JsonAsync(await GetAsync(t, app, $"{Public}/requests"))));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, app, $"{Public}/requests/{request}")).StatusCode);

        // The App reader role reads content and people, not requests.
        var reader = await AppAsync(world, t, ["requests:read"], "tenant", null);
        Assert.Empty(Ids(await JsonAsync(await GetAsync(t, reader, $"{Public}/requests"))));
    }

    [Fact]
    public async Task The_request_reader_at_Gulf_reads_Gulf_requests_only_and_cannot_file()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulfRequest = await FileAsync(t, "gia", o.Everyone);
        var egyptRequest = await FileAsync(t, "eli", o.Everyone);
        var app = await AppAsync(world, t, ["services:read", "requests:read", "requests:submit"], "org_unit", o.Gulf, BuiltInRoles.AppRequestReader);

        var catalogue = Ids(await JsonAsync(await GetAsync(t, app, $"{Public}/services")));
        Assert.Contains(o.Everyone, catalogue);
        Assert.Contains(o.GulfOnly, catalogue);
        Assert.DoesNotContain(o.EgyptOnly, catalogue);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, app, $"{Public}/services/{o.EgyptOnly}")).StatusCode);

        Assert.Equal([gulfRequest], Ids(await JsonAsync(await GetAsync(t, app, $"{Public}/requests"))));
        var read = await JsonAsync(await GetAsync(t, app, $"{Public}/requests/{gulfRequest}"));
        Assert.Equal(t["gia"].Id, read.GetProperty("request").GetProperty("requesterId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, app, $"{Public}/requests/{egyptRequest}")).StatusCode);

        // Paging: one at a time, newest first, with a cursor to the next page.
        await FileAsync(t, "gus", o.Everyone);
        var page = await JsonAsync(await GetAsync(t, app, $"{Public}/requests?limit=1"));
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.Equal([gulfRequest], Ids(await JsonAsync(await GetAsync(t, app, $"{Public}/requests?limit=1&cursor={Uri.EscapeDataString(cursor!)}"))));

        // Reading is not filing: the reader holds no right to file for anyone.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(world, t, app, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.Everyone, data = new { boxes = 1 }, onBehalfOf = t["gia"].Id })).StatusCode);
    }

    [Fact]
    public async Task The_request_filer_files_for_a_named_Gulf_person_who_then_owns_the_request()
    {
        var o = await FreshAsync();
        var t = o.T;
        var app = await AppAsync(world, t, ["requests:read", "requests:submit"], "org_unit", o.Gulf, BuiltInRoles.AppRequestFiler);

        var filed = await JsonAsync(await SendAsync(world, t, app, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.GulfOnly, data = new { boxes = 2 }, onBehalfOf = t["gia"].Id }), HttpStatusCode.Created);
        var request = filed.GetProperty("request");
        Assert.Equal(t["gia"].Id, request.GetProperty("requesterId").GetGuid());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("onBehalfOfId").ValueKind);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from services.request where id = '{Id(request)}' and requester_app_id is not null"));

        // Gia sees it as her own and may cancel it; the app reads it back.
        var mine = await (await t["gia"].Client.GetAsync("/api/v1/requests")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(Id(request), mine.EnumerateArray().Select(Id));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(t, app, $"{Public}/requests/{Id(request)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["gia"].Client.PostAsJsonAsync($"/api/v1/requests/{Id(request)}/cancel", new { })).StatusCode);

        // Nobody named, someone outside Gulf, or a service not offered to the person: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(world, t, app, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.Everyone, data = new { boxes = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(world, t, app, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.Everyone, data = new { boxes = 1 }, onBehalfOf = t["eli"].Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(world, t, app, HttpMethod.Post, $"{Public}/requests",
            new { serviceId = o.EgyptOnly, data = new { boxes = 1 }, onBehalfOf = t["gia"].Id })).StatusCode);
    }
}
