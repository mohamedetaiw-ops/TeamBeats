using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 slice 1 (T002, T003): spaces from templates, public, private and secret visibility in the directory, the
/// space page, members and search, the space roles in authorization, archive and restore, the tenant rules, the
/// ownerless edge case, and the package, tenant and administrator boundaries. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpacesTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>Holding over Gulf and Africa. Omar, Laila, Huda and Sara work in Gulf; Kofi works in Africa. Huda manages Omar.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sp-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila", "huda", "sara", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "omar", "laila", "huda", "sara" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        if (package != "workplace")
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["omar"].Id}/manager", new { managerId = t["huda"].Id })).StatusCode);
        }
        return new Org(t, gulf, africa);
    }

    private static object NewSpace(string name, string visibility, string template = "project", object[]? members = null, bool allowJoin = false, string? kind = null) => new
    {
        name,
        description = $"Everything about {name}.",
        visibility,
        template,
        kind,
        allowJoin,
        members = members ?? [],
    };

    private static object Member(TestTenant t, string name, string role = "member") => new { userId = t[name].Id, role };

    private static async Task<Guid> CreateAsync(HttpClient client, object body) =>
        (await OkAsync(client.PostAsJsonAsync(Api, body), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid[]> SearchAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}&kind=space"))).GetProperty("results").EnumerateArray()
        .Select(r => r.GetProperty("itemId").GetGuid()).ToArray();

    private static async Task<Guid[]> DirectoryAsync(HttpClient client, string scope = "") =>
        (await OkAsync(client.GetAsync($"{Api}?scope={scope}"))).EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToArray();

    private Task<long> AuditAsync(Org o, string action, Guid? target = null) => target is { } id
        ? world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a and target_id = @s", ("a", action), ("s", id.ToString()))
        : world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    private Task<long> NotificationsAsync(Org o, string user, string template) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    [Fact]
    public async Task A_project_space_starts_from_its_template_with_the_creator_as_owner_and_tells_the_members()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var view = await OkAsync(omar.PostAsJsonAsync(Api, NewSpace("New ERP rollout", "private", members: [Member(o.T, "laila"), Member(o.T, "huda", "viewer")])),
            HttpStatusCode.Created);
        var id = view.GetProperty("id").GetGuid();
        Assert.Equal("project", view.GetProperty("kind").GetString());
        Assert.Equal("owner", view.GetProperty("rights").GetProperty("role").GetString());
        Assert.True(view.GetProperty("rights").GetProperty("owns").GetBoolean());
        Assert.Equal(3, view.GetProperty("memberCount").GetInt32());

        // The template's list, three columns (the last one done) and three starter tasks were copied into the space.
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from spaces.task_list where space_id = @s", ("s", id)));
        Assert.Equal(3L, await world.Database.TenantScalarAsync<long>(o.T.Id,
            "select count(*) from spaces.task_column c join spaces.task_list l on l.id = c.list_id where l.space_id = @s", ("s", id)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(o.T.Id,
            "select count(*) from spaces.task_column c join spaces.task_list l on l.id = c.list_id where l.space_id = @s and c.done", ("s", id)));
        Assert.Equal(3L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from spaces.task where space_id = @s", ("s", id)));

        // The roles are assignments in authorization on the space itself (FR-002).
        Assert.Equal(3L, await world.Database.TenantScalarAsync<long>(o.T.Id,
            "select count(*) from authz.role_assignment where scope_type = 'resource' and scope_resource_type = 'space' and scope_id = @s and revoked_at is null", ("s", id)));

        // Laila and Huda were told; Omar, who made it, was not.
        Assert.Equal(1L, await NotificationsAsync(o, "laila", "spaces.member.added"));
        Assert.Equal(1L, await NotificationsAsync(o, "huda", "spaces.member.added"));
        Assert.Equal(0L, await NotificationsAsync(o, "omar", "spaces.member.added"));

        var members = await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/{id}/members"));
        Assert.Equal(new[] { "owner", "member", "viewer" }, members.EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToArray());
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesCreated, id));

        // A team space is ongoing and has no dates; the team template gives it one starter task.
        var team = await CreateAsync(omar, NewSpace("Finance team", "public", "team"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from spaces.task where space_id = @s", ("s", team)));
        Assert.Equal("validation.spaces.team_dates",
            await ProblemKeyAsync(await omar.PostAsJsonAsync(Api, new { name = "Ops", visibility = "public", template = "team", startsOn = "2027-01-01" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.kind",
            await ProblemKeyAsync(await omar.PostAsJsonAsync(Api, new { name = "Ops", visibility = "public", template = "team", kind = "project" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.member",
            await ProblemKeyAsync(await omar.PostAsJsonAsync(Api, NewSpace("Ops", "public", members: [new { userId = Guid.NewGuid(), role = "member" }])), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Private_and_secret_spaces_stay_with_their_members_in_the_directory_the_page_and_search()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var open = await CreateAsync(omar, NewSpace("Brightwater open forum", "public"));
        var closed = await CreateAsync(omar, NewSpace("Brightwater pricing review", "private", members: [Member(o.T, "laila")]));
        var hidden = await CreateAsync(omar, NewSpace("Brightwater acquisition", "secret", members: [Member(o.T, "laila", "viewer")]));
        await world.DispatchTenantAsync(o.T.Id);

        // Kofi is not in the private or secret space. He sees the public and private ones listed, never the secret one.
        var kofi = o.T["kofi"].Client;
        var listed = await DirectoryAsync(kofi);
        Assert.Contains(open, listed);
        Assert.Contains(closed, listed);
        Assert.DoesNotContain(hidden, listed);
        var directory = await OkAsync(kofi.GetAsync(Api));
        Assert.False(directory.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == closed).GetProperty("canOpen").GetBoolean());
        Assert.True(directory.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == open).GetProperty("canOpen").GetBoolean());

        // The private space shows what the directory shows, without its members; the secret one does not exist for him.
        var page = await OkAsync(kofi.GetAsync($"{Api}/{closed}"));
        Assert.False(page.GetProperty("rights").GetProperty("reads").GetBoolean());
        Assert.Equal("authz.denied.spaces_private", await ProblemKeyAsync(await kofi.GetAsync($"{Api}/{closed}/members"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NotFound, (await kofi.GetAsync($"{Api}/{hidden}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kofi.GetAsync($"{Api}/{hidden}/members")).StatusCode);

        // Search follows the same rule (FR-003, FR-016).
        Assert.Equal([open], await SearchAsync(kofi, "Brightwater"));
        var found = await SearchAsync(o.T["laila"].Client, "Brightwater");
        Assert.Equal(3, found.Length);
        Assert.Contains(hidden, found);

        // Laila is a viewer of the secret space: she reads it but cannot change it.
        var laila = o.T["laila"].Client;
        var hers = await OkAsync(laila.GetAsync($"{Api}/{hidden}"));
        Assert.True(hers.GetProperty("rights").GetProperty("reads").GetBoolean());
        Assert.False(hers.GetProperty("rights").GetProperty("contributes").GetBoolean());
        Assert.Equal("authz.denied.spaces_owner", await ProblemKeyAsync(await laila.PutAsJsonAsync($"{Api}/{hidden}",
            new { name = "Renamed", visibility = "secret", version = hers.GetProperty("version").GetInt32() }), HttpStatusCode.Forbidden));

        // The tenant administrator holds spaces.manage: they see the secret space's metadata with scope=all, but not its content.
        var admin = o.T["admin"].Client;
        Assert.DoesNotContain(hidden, await DirectoryAsync(admin));
        Assert.Contains(hidden, await DirectoryAsync(admin, "all"));
        var adminView = await OkAsync(admin.GetAsync($"{Api}/{hidden}"));
        Assert.False(adminView.GetProperty("rights").GetProperty("reads").GetBoolean());
        Assert.True(adminView.GetProperty("rights").GetProperty("manages").GetBoolean());
        Assert.DoesNotContain(hidden, await SearchAsync(admin, "Brightwater"));

        // Making the private space public opens it to everyone at once.
        var before = await OkAsync(omar.GetAsync($"{Api}/{closed}"));
        await OkAsync(omar.PutAsJsonAsync($"{Api}/{closed}", new
        {
            name = "Brightwater pricing review", description = "Open now.", visibility = "public", version = before.GetProperty("version").GetInt32(),
        }));
        Assert.True((await OkAsync(kofi.GetAsync($"{Api}/{closed}"))).GetProperty("rights").GetProperty("reads").GetBoolean());
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesChanged, closed));
        // A stale version is refused.
        Assert.Equal("conflict.spaces.changed", await ProblemKeyAsync(await omar.PutAsJsonAsync($"{Api}/{closed}", new
        {
            name = "Again", visibility = "public", version = before.GetProperty("version").GetInt32(),
        }), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task People_join_public_spaces_that_allow_it_and_owners_manage_members_but_never_drop_the_last_owner()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var joinable = await CreateAsync(omar, NewSpace("Running club", "public", "team", allowJoin: true));
        var closedDoor = await CreateAsync(omar, NewSpace("Board papers", "public", "team"));
        var kofi = o.T["kofi"].Client;

        var joined = await OkAsync(kofi.PostAsync($"{Api}/{joinable}:join", null));
        Assert.Equal("member", joined.GetProperty("rights").GetProperty("role").GetString());
        Assert.True(joined.GetProperty("rights").GetProperty("contributes").GetBoolean());
        Assert.Equal("authz.denied.spaces_join", await ProblemKeyAsync(await kofi.PostAsync($"{Api}/{closedDoor}:join", null), HttpStatusCode.Forbidden));
        // In a public space he is not in, he reads but does not contribute.
        var reader = await OkAsync(kofi.GetAsync($"{Api}/{closedDoor}"));
        Assert.True(reader.GetProperty("rights").GetProperty("reads").GetBoolean());
        Assert.False(reader.GetProperty("rights").GetProperty("contributes").GetBoolean());

        // Only owners change members. Omar makes Laila an owner, then he can leave; the last owner cannot.
        Assert.Equal("authz.denied.spaces_owner", await ProblemKeyAsync(await kofi.PostAsJsonAsync($"{Api}/{joinable}/members", Member(o.T, "sara")), HttpStatusCode.Forbidden));
        Assert.Equal("conflict.spaces.last_owner", await ProblemKeyAsync(await omar.PostAsync($"{Api}/{joinable}:leave", null), HttpStatusCode.Conflict));
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{joinable}/members", Member(o.T, "laila", "owner")));
        Assert.Equal(HttpStatusCode.NoContent, (await omar.PostAsync($"{Api}/{joinable}:leave", null)).StatusCode);
        var laila = o.T["laila"].Client;
        Assert.Equal("conflict.spaces.last_owner",
            await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{joinable}/members", Member(o.T, "laila", "member")), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NoContent, (await laila.DeleteAsync($"{Api}/{joinable}/members/{o.T["kofi"].Id}")).StatusCode);
        Assert.False((await OkAsync(kofi.GetAsync($"{Api}/{joinable}"))).GetProperty("rights").GetProperty("contributes").GetBoolean());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(o.T.Id,
            "select count(*) from authz.role_assignment where scope_id = @s and subject_id = @u and revoked_at is null", ("s", joinable), ("u", o.T["kofi"].Id)));
        Assert.True(await AuditAsync(o, AuditActions.SpacesMemberRemoved, joinable) >= 2);
        // Kofi joining and Laila being added are both recorded.
        Assert.Equal(2L, await AuditAsync(o, AuditActions.SpacesMemberAdded, joinable));

        // Space roles are given only in the space, never through role administration or a custom role.
        var admin = o.T["admin"].Client;
        Assert.Equal("validation.role.module_assigned", await ProblemKeyAsync(await admin.PostAsJsonAsync("/api/v1/authz/assignments", new
        {
            subjectType = "user", subjectId = o.T["sara"].Id, roleKey = BuiltInRoles.SpaceOwner, scopeType = "resource", scopeId = joinable, scopeResourceType = "space",
        }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.role.unknown_permission", await ProblemKeyAsync(await admin.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "space_peeker", names = new { en = "Space peeker" }, permissions = new[] { Permissions.SpacesView }, delegable = false,
        }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task An_archived_space_is_read_only_until_an_owner_or_a_spaces_administrator_restores_it()
    {
        var o = await SetupAsync();
        var omar = o.T["omar"].Client;
        var id = await CreateAsync(omar, NewSpace("Office move", "private", members: [Member(o.T, "laila")]));
        Assert.Equal("authz.denied.spaces_owner", await ProblemKeyAsync(await o.T["laila"].Client.PostAsync($"{Api}/{id}:archive", null), HttpStatusCode.Forbidden));
        var archived = await OkAsync(omar.PostAsync($"{Api}/{id}:archive", null));
        Assert.Equal("archived", archived.GetProperty("status").GetString());
        Assert.False(archived.GetProperty("rights").GetProperty("contributes").GetBoolean());
        Assert.Equal("conflict.spaces.archived", await ProblemKeyAsync(await omar.PutAsJsonAsync($"{Api}/{id}", new
        {
            name = "Office move", visibility = "private", version = archived.GetProperty("version").GetInt32(),
        }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.spaces.archived", await ProblemKeyAsync(await omar.PostAsJsonAsync($"{Api}/{id}/members", Member(o.T, "sara")), HttpStatusCode.Conflict));
        // Archived spaces leave the directory but stay in "my spaces" and in the administrators' full list.
        Assert.DoesNotContain(id, await DirectoryAsync(omar));
        Assert.Contains(id, await DirectoryAsync(omar, "mine"));

        // The tenant administrator restores it without being a member.
        var restored = await OkAsync(o.T["admin"].Client.PostAsync($"{Api}/{id}:restore", null));
        Assert.Equal("active", restored.GetProperty("status").GetString());
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesArchived, id));
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesRestored, id));
    }

    [Fact]
    public async Task The_tenant_limits_who_creates_spaces_and_its_templates_never_change_spaces_already_made()
    {
        var o = await SetupAsync();
        var admin = o.T["admin"].Client;
        var rules = await OkAsync(admin.GetAsync($"{Api}/manage/settings"));
        Assert.False(rules.GetProperty("guestsAllowed").GetBoolean());
        Assert.Equal(90, rules.GetProperty("guestDays").GetInt32());

        // Only Gulf creates spaces now: Kofi in Africa is refused and his rights say so; Omar in Gulf may.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/manage/settings", new { creationUnits = new[] { o.Gulf }, guestsAllowed = false, guestDays = 90, version = 0 }));
        Assert.False((await OkAsync(o.T["kofi"].Client.GetAsync($"{Api}/rights"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal("authz.denied.spaces_create_unit",
            await ProblemKeyAsync(await o.T["kofi"].Client.PostAsJsonAsync(Api, NewSpace("Africa launch", "public")), HttpStatusCode.Forbidden));
        Assert.True((await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/rights"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesSettingsChanged));
        Assert.Equal("validation.spaces.guest_days", await ProblemKeyAsync(
            await admin.PutAsJsonAsync($"{Api}/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = false, guestDays = 3, version = 1 }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Api}/manage/settings")).StatusCode);

        // A tenant template with two lists and a starter task in the second one.
        var template = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/templates", new
        {
            name = "Client delivery",
            kind = "project",
            definition = new
            {
                lists = new object[]
                {
                    new { name = "Discovery", columns = new object[] { new { name = "Open" }, new { name = "Closed", done = true } } },
                    new { name = "Delivery", columns = new object[] { new { name = "Backlog" }, new { name = "Building" }, new { name = "Shipped", done = true } } },
                },
                tasks = new object[] { new { title = "Kick-off with the client", list = 1, column = 0 } },
            },
        }), HttpStatusCode.Created);
        var templateId = template.GetProperty("id").GetGuid();
        var options = await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/templates"));
        Assert.Contains(options.EnumerateArray(), t => t.GetProperty("key").GetString() == templateId.ToString() && !t.GetProperty("builtIn").GetBoolean());
        Assert.Equal("validation.spaces.template_task_place", await ProblemKeyAsync(await admin.PostAsJsonAsync($"{Api}/manage/templates", new
        {
            name = "Broken", kind = "team",
            definition = new { lists = new object[] { new { name = "Only", columns = new object[] { new { name = "One" } } } }, tasks = new object[] { new { title = "Lost", list = 3 } } },
        }), HttpStatusCode.BadRequest));

        var space = await CreateAsync(o.T["omar"].Client, NewSpace("Northwind delivery", "private", templateId.ToString()));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from spaces.task_list where space_id = @s", ("s", space)));
        Assert.Equal("Kick-off with the client", await world.Database.TenantScalarAsync<string>(o.T.Id,
            "select t.title from spaces.task t join spaces.task_list l on l.id = t.list_id where t.space_id = @s and l.name = 'Delivery'", ("s", space)));

        // Changing and retiring the template leaves the space as it was (FR-012).
        await OkAsync(admin.PutAsJsonAsync($"{Api}/manage/templates/{templateId}", new
        {
            name = "Client delivery v2", kind = "project", version = template.GetProperty("version").GetInt32(),
            definition = new { lists = new object[] { new { name = "Everything", columns = new object[] { new { name = "Open" } } } } },
        }));
        await OkAsync(admin.PostAsync($"{Api}/manage/templates/{templateId}:retire", null));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from spaces.task_list where space_id = @s", ("s", space)));
        Assert.Equal(3L, await AuditAsync(o, AuditActions.SpacesTemplateChanged, templateId));
        Assert.Equal("validation.spaces.template",
            await ProblemKeyAsync(await o.T["omar"].Client.PostAsJsonAsync(Api, NewSpace("Late", "private", templateId.ToString())), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task When_the_last_owner_leaves_the_company_the_creators_manager_takes_over_or_administrators_are_asked()
    {
        var o = await SetupAsync();
        // Omar made both; Huda is his manager.
        var omar = o.T["omar"].Client;
        var withManager = await CreateAsync(omar, NewSpace("Warehouse audit", "private", members: [Member(o.T, "laila")]));
        await OkAsync(o.T["admin"].Client.PostAsync($"/api/v1/admin/users/{o.T["omar"].Id}:disable", null), HttpStatusCode.NoContent);
        await world.DispatchTenantAsync(o.T.Id);
        var members = await OkAsync(o.T["huda"].Client.GetAsync($"{Api}/{withManager}/members"));
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == o.T["huda"].Id && m.GetProperty("role").GetString() == "owner");
        Assert.Equal(1L, await NotificationsAsync(o, "huda", "spaces.space.ownership"));
        Assert.Equal(1L, await AuditAsync(o, AuditActions.SpacesOwnerReassigned, withManager));

        // Sara has no manager: when she leaves, the spaces administrators (here the tenant administrator) are asked.
        var sara = o.T["sara"].Client;
        var orphan = await CreateAsync(sara, NewSpace("Canteen survey", "secret"));
        await OkAsync(o.T["admin"].Client.PostAsync($"/api/v1/admin/users/{o.T["sara"].Id}:disable", null), HttpStatusCode.NoContent);
        await world.DispatchTenantAsync(o.T.Id);
        Assert.Equal(1L, await NotificationsAsync(o, "admin", "spaces.space.ownerless"));
        // The administrator gives it an owner without reading it.
        await OkAsync(o.T["admin"].Client.PostAsJsonAsync($"{Api}/{orphan}/members", Member(o.T, "laila", "owner")));
        Assert.True((await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/{orphan}"))).GetProperty("rights").GetProperty("owns").GetBoolean());
    }

    [Fact]
    public async Task Spaces_stay_in_their_tenant_and_need_the_package()
    {
        var o = await SetupAsync();
        var id = await CreateAsync(o.T["omar"].Client, NewSpace("Tender pack", "public"));

        var other = await world.CreateTenantAsync("spo-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Api}/{id}:join", null)).StatusCode);
        Assert.Empty(await DirectoryAsync(other["admin"].Client, "all"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from spaces.space", Array.Empty<(string, object)>()));

        var basic = await SetupAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["omar"].Client.PostAsJsonAsync(Api, NewSpace("No package", "public"))).StatusCode);
        Assert.False((await OkAsync(basic.T["omar"].Client.GetAsync($"{Api}/rights"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["omar"].Client.GetAsync(Api)).StatusCode);
    }
}
