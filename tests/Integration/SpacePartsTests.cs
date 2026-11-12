using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 slice 3 (T008): every space gets a library (spec 011) and a discussion (spec 023) whose audience is the
/// space. Owners and members add files and post, viewers read, everyone else (and every other tenant) gets "not found";
/// leaving the space ends access at once, archiving makes both read-only, and permissions there follow the space. A task
/// links library files and shows each reader only what they may open, at the version in force. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpacePartsTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Communities = "/api/v1/engagement/communities";

    private sealed record Org(TestTenant T);

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

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) => (await call).StatusCode;

    /// <summary>Omar, Laila, Huda and Sara work in Gulf; Kofi in Africa.</summary>
    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("sp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila", "huda", "sara", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "omar", "laila", "huda", "sara" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        return new Org(t);
    }

    /// <summary>A private project space Omar owns: Laila and Sara are members, Huda a viewer.</summary>
    private static async Task<JsonElement> SpaceAsync(Org o, string name, string visibility = "private")
    {
        var t = o.T;
        var body = new
        {
            name, description = $"Everything about {name}.", visibility, template = "project", allowJoin = visibility == "public",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["sara"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        };
        return await OkAsync(t["omar"].Client.PostAsJsonAsync(Api, body), HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid library, string name, string content) =>
        client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true", new ByteArrayContent(Encoding.UTF8.GetBytes(content)));

    private static object Post(string text) => new { body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } } };

    [Fact]
    public async Task The_space_library_is_for_the_space_members_and_follows_the_space()
    {
        var o = await SetupAsync();
        var created = await SpaceAsync(o, "Finance system rollout");
        var space = created.GetProperty("id").GetGuid();
        var library = created.GetProperty("libraryId").GetGuid();
        var t = o.T;

        // The library is named after the space and its permissions follow the space roles.
        var view = await OkAsync(t["laila"].Client.GetAsync($"{Libraries}/{library}"));
        Assert.Equal("space", view.GetProperty("scope").GetString());
        Assert.Equal("Finance system rollout", view.GetProperty("names").GetProperty("en").GetString());
        Assert.True(view.GetProperty("rights").GetProperty("contribute").GetBoolean());
        Assert.True((await OkAsync(t["omar"].Client.GetAsync($"{Libraries}/{library}"))).GetProperty("rights").GetProperty("manage").GetBoolean());
        var doc = (await OkAsync(UploadAsync(t["laila"].Client, library, "purchase-steps.txt", "Raise a request, then approve it."), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        // The viewer reads but adds nothing; people outside the space do not even find the library.
        var huda = await OkAsync(t["huda"].Client.GetAsync($"{Libraries}/{library}"));
        Assert.False(huda.GetProperty("rights").GetProperty("contribute").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(UploadAsync(t["huda"].Client, library, "notes.txt", "Hello.")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(t["huda"].Client.GetAsync($"/api/v1/documents/{doc}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Libraries}/{library}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"/api/v1/documents/{doc}")));

        // Permissions in a space library are not changed in Documents: not by breaking inheritance, not in the ACL editor.
        Assert.Equal("conflict.documents.space_permissions",
            await ProblemKeyAsync(t["omar"].Client.PostAsync($"/api/v1/documents/{doc}/permissions/break", null), HttpStatusCode.Conflict));

        // Leaving the space ends access at once.
        await OkAsync(t["omar"].Client.DeleteAsync($"{Api}/{space}/members/{t["sara"].Id}"), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["sara"].Client.GetAsync($"{Libraries}/{library}")));

        // Renaming and archiving the space carry over; an archived space's library is read-only until it is restored.
        var current = await OkAsync(t["omar"].Client.GetAsync($"{Api}/{space}"));
        await OkAsync(t["omar"].Client.PutAsJsonAsync($"{Api}/{space}", new
        {
            name = "Finance rollout", description = "Everything.", visibility = "private", classification = "confidential", version = current.GetProperty("version").GetInt32(),
        }));
        var renamed = await OkAsync(t["laila"].Client.GetAsync($"{Libraries}/{library}"));
        Assert.Equal("Finance rollout", renamed.GetProperty("names").GetProperty("ar").GetString());
        Assert.Equal("confidential", renamed.GetProperty("classificationDefault").GetString());
        await OkAsync(t["omar"].Client.PostAsync($"{Api}/{space}:archive", null));
        Assert.False((await OkAsync(t["laila"].Client.GetAsync($"{Libraries}/{library}"))).GetProperty("rights").GetProperty("contribute").GetBoolean());
        Assert.Equal("conflict.documents.library_archived", await ProblemKeyAsync(UploadAsync(t["laila"].Client, library, "late.txt", "Too late."), HttpStatusCode.Conflict));
        await OkAsync(t["omar"].Client.PostAsync($"{Api}/{space}:restore", null));
        await OkAsync(UploadAsync(t["laila"].Client, library, "back.txt", "Back again."), HttpStatusCode.Created);

        // Another tenant's people never reach it.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other.T["laila"].Client.GetAsync($"{Libraries}/{library}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other.T["omar"].Client.GetAsync($"/api/v1/documents/{doc}")));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.DocLibraryCreated)));
    }

    [Fact]
    public async Task The_space_discussion_has_the_space_members_and_is_not_in_the_directory()
    {
        var o = await SetupAsync();
        var created = await SpaceAsync(o, "Payroll team", "public");
        var space = created.GetProperty("id").GetGuid();
        var community = created.GetProperty("communityId").GetGuid();
        var t = o.T;

        var view = await OkAsync(t["laila"].Client.GetAsync($"{Communities}/{community}"));
        Assert.Equal(space, view.GetProperty("spaceId").GetGuid());
        Assert.Equal(4, view.GetProperty("memberCount").GetInt32());
        Assert.True(view.GetProperty("rights").GetProperty("canPost").GetBoolean());
        Assert.False(view.GetProperty("rights").GetProperty("canJoin").GetBoolean());
        await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Communities}/{community}/posts", Post("The payroll cut-off moves to the 20th.")), HttpStatusCode.Created);

        // The viewer reads and does not post; members are listed from the space, the owner as owner.
        var huda = await OkAsync(t["huda"].Client.GetAsync($"{Communities}/{community}"));
        Assert.False(huda.GetProperty("rights").GetProperty("canPost").GetBoolean());
        Assert.Single((await OkAsync(t["huda"].Client.GetAsync($"{Communities}/{community}/posts"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["huda"].Client.PostAsJsonAsync($"{Communities}/{community}/posts", Post("Hello."))));
        var members = await OkAsync(t["huda"].Client.GetAsync($"{Communities}/{community}/members"));
        Assert.Equal("owner", members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == t["omar"].Id).GetProperty("role").GetString());

        // A public space is open to read, but its discussion is for its members; it is not listed or joined in Communities.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(t["kofi"].Client.GetAsync($"{Api}/{space}/tasks")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Communities}/{community}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Communities}/{community}/posts")));
        Assert.DoesNotContain((await OkAsync(t["laila"].Client.GetAsync(Communities))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == community);
        Assert.Equal("conflict.engagement.space_community",
            await ProblemKeyAsync(t["laila"].Client.PostAsync($"{Communities}/{community}:leave", null), HttpStatusCode.Conflict));
        Assert.Equal("conflict.engagement.space_community",
            await ProblemKeyAsync(t["omar"].Client.PutAsJsonAsync($"{Communities}/{community}/members", new { userId = t["kofi"].Id, role = "member" }), HttpStatusCode.Conflict));

        // Joining the space adds Kofi to the discussion; archiving the space stops posts.
        await OkAsync(t["kofi"].Client.PostAsync($"{Api}/{space}:join", null));
        Assert.True((await OkAsync(t["kofi"].Client.GetAsync($"{Communities}/{community}"))).GetProperty("rights").GetProperty("canPost").GetBoolean());
        await OkAsync(t["omar"].Client.PostAsync($"{Api}/{space}:archive", null));
        Assert.False((await OkAsync(t["laila"].Client.GetAsync($"{Communities}/{community}"))).GetProperty("rights").GetProperty("canPost").GetBoolean());

        // Another tenant's people never reach it.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other.T["laila"].Client.GetAsync($"{Communities}/{community}")));
    }

    [Fact]
    public async Task A_task_links_library_files_and_each_reader_sees_only_what_they_may_open()
    {
        var o = await SetupAsync();
        var t = o.T;
        var created = await SpaceAsync(o, "Office move");
        var space = created.GetProperty("id").GetGuid();
        var library = created.GetProperty("libraryId").GetGuid();
        var doc = (await OkAsync(UploadAsync(t["laila"].Client, library, "floor-plan.txt", "Level 3."), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var task = (await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Book the movers" }), HttpStatusCode.Created))
            .GetProperty("task").GetProperty("id").GetGuid();

        var linked = await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/tasks/{task}/files", new { documentId = doc }));
        var file = Assert.Single(linked.GetProperty("files").EnumerateArray());
        Assert.Equal("floor-plan.txt", file.GetProperty("fileName").GetString());
        Assert.Equal("1.0", file.GetProperty("version").GetString());
        Assert.Single((await OkAsync(t["huda"].Client.GetAsync($"{Api}/{space}/tasks/{task}"))).GetProperty("files").EnumerateArray());

        // A file the person cannot open is refused, and a viewer links nothing.
        var elsewhere = await SpaceAsync(o, "Board papers");
        var hidden = (await OkAsync(UploadAsync(t["omar"].Client, elsewhere.GetProperty("libraryId").GetGuid(), "minutes.txt", "Private."), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["omar"].Client.DeleteAsync($"{Api}/{elsewhere.GetProperty("id").GetGuid()}/members/{t["laila"].Id}"), HttpStatusCode.NoContent);
        Assert.Equal("validation.spaces.task_file",
            await ProblemKeyAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/tasks/{task}/files", new { documentId = hidden }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.task_file",
            await ProblemKeyAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/tasks/{task}/files", new { documentId = Guid.NewGuid() }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["huda"].Client.PostAsJsonAsync($"{Api}/{space}/tasks/{task}/files", new { documentId = doc })));

        // Omar links his private file too; Laila no longer sees it on the task, Omar does.
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/{space}/tasks/{task}/files", new { documentId = hidden }));
        Assert.Equal(2, (await OkAsync(t["omar"].Client.GetAsync($"{Api}/{space}/tasks/{task}"))).GetProperty("files").GetArrayLength());
        Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{Api}/{space}/tasks/{task}"))).GetProperty("files").EnumerateArray());

        var unlinked = await OkAsync(t["laila"].Client.DeleteAsync($"{Api}/{space}/tasks/{task}/files/{doc}"));
        Assert.Empty(unlinked.GetProperty("files").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["laila"].Client.DeleteAsync($"{Api}/{space}/tasks/{task}/files/{doc}")));
    }
}
