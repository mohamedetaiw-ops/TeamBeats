using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 T004 and T005, with the builder basics of US7: the catalogue offers each person the services whose audience
/// includes them, linked apps are http(s) only with their sign-in cue, contractors are told a service is not available,
/// favourites and recent use, search in both languages, unit-scoped owners, versions, the plan quota and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceCatalogueTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";
    private const string Catalogue = "/api/v1/services";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private sealed record Org(TestTenant T, Guid Gulf, Guid Dubai, Guid Africa, Guid Group);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sc-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var dubai = await world.CreateUnitAsync(t, "Dubai", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit, type) in new[]
        {
            ("sara", "Dubai", MembershipTypes.Employee), ("omar", "Africa", MembershipTypes.Employee), ("carl", "Dubai", MembershipTypes.Contractor),
            ("owner_gulf", "Gulf", MembershipTypes.Employee), ("owner_africa", "Africa", MembershipTypes.Employee),
        })
        {
            await world.SeedUserAsync(t, name, type);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "owner_gulf", BuiltInRoles.WorkflowDesigner, "org_subtree", gulf);
        await world.AssignAsync(t, "owner_africa", BuiltInRoles.WorkflowDesigner, "org_subtree", africa);
        var group = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/groups", new { key = "finance", name = "Finance" });
        var groupId = (await group.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True((await t["admin"].Client.PostAsJsonAsync($"/api/v1/authz/groups/{groupId}/members", new { userId = t["omar"].Id })).IsSuccessStatusCode);
        return new Org(t, gulf, dubai, africa, groupId);
    }

    private static object FormDraft(object? route = null) => new
    {
        form = new
        {
            fields = new object[]
            {
                new { key = "boxes", type = "number", labels = Names("Boxes", "عدد العلب"), required = true, rules = new[] { new { kind = "max", value = 5 } } },
            },
        },
        route,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, object settings)
    {
        var response = await client.PostAsJsonAsync(Admin, settings);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PublishFormAsync(HttpClient client, object settings, object? route = null)
    {
        var id = await CreateAsync(client, settings);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Admin}/{id}/draft", FormDraft(route))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private static async Task<JsonElement> CatalogueAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(Catalogue + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static IReadOnlyList<Guid> ServiceIds(JsonElement view) =>
        view.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    [Fact]
    public async Task People_see_only_services_whose_audience_includes_them_and_a_contractor_is_told_it_is_not_available()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var everyone = await PublishFormAsync(admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var gulfOnly = await PublishFormAsync(admin, new { kind = "form", names = Names("Gulf parking", "مواقف الخليج"), audience = new { orgUnits = new[] { o.Gulf } } });
        var finance = await PublishFormAsync(admin, new { kind = "form", names = Names("Expense advance", "سلفة"), audience = new { groups = new[] { o.Group } } });
        var employees = await PublishFormAsync(admin, new { kind = "form", names = Names("Training", "تدريب"), audience = new { membershipTypes = new[] { "employee" } } });
        var draft = await CreateAsync(admin, new { kind = "form", names = Names("Draft", "مسودة") });

        var sara = ServiceIds(await CatalogueAsync(o.T["sara"].Client));
        Assert.Contains(everyone, sara);
        Assert.Contains(gulfOnly, sara); // Dubai is inside Gulf.
        Assert.DoesNotContain(finance, sara);
        Assert.Contains(employees, sara);
        Assert.DoesNotContain(draft, sara);

        var omar = ServiceIds(await CatalogueAsync(o.T["omar"].Client));
        Assert.DoesNotContain(gulfOnly, omar);
        Assert.Contains(finance, omar);

        var carl = ServiceIds(await CatalogueAsync(o.T["carl"].Client));
        Assert.DoesNotContain(employees, carl);
        var direct = await o.T["carl"].Client.GetAsync($"{Catalogue}/{employees}");
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.Equal("services.not_available", (await direct.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Catalogue}/{gulfOnly}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["sara"].Client.GetAsync($"{Catalogue}/{draft}")).StatusCode);

        var detail = await (await o.T["sara"].Client.GetAsync($"{Catalogue}/{everyone}")).ReadAsync<JsonElement>();
        Assert.Equal("boxes", detail.GetProperty("form").GetProperty("fields")[0].GetProperty("key").GetString());
        Assert.Equal(1, detail.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Linked_apps_open_only_http_addresses_and_say_whether_sign_in_is_single_sign_on()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        foreach (var url in new[] { "javascript:alert(1)", "/relative/path", "ftp://files.example", "https://user:pw@help.example", "data:text/html,hi" })
        {
            Assert.Contains("validation.services.url", await ErrorKeysAsync(await admin.PostAsJsonAsync(Admin,
                new { kind = "linked", names = Names("Help Desk", "مكتب المساعدة"), url })));
        }
        Assert.Contains("validation.services.url_not_linked", await ErrorKeysAsync(await admin.PostAsJsonAsync(Admin,
            new { kind = "form", names = Names("Cards", "بطاقات"), url = "https://x.example" })));

        var linked = await CreateAsync(admin, new { kind = "linked", names = Names("Help Desk", "مكتب المساعدة"), url = "https://help.example/portal", sso = true });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{linked}/publish", new { })).StatusCode);
        var card = (await CatalogueAsync(o.T["sara"].Client)).GetProperty("services").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == linked);
        Assert.Equal("linked", card.GetProperty("kind").GetString());
        Assert.Equal("https://help.example/portal", card.GetProperty("url").GetString());
        Assert.True(card.GetProperty("sso").GetBoolean());

        // Module services join the catalogue from their registration, with their launch route and no URL.
        var modules = (await CatalogueAsync(o.T["sara"].Client)).GetProperty("services").EnumerateArray().Where(s => s.GetProperty("kind").GetString() == "module").ToList();
        Assert.Equal(["/attendance", "/leave", "/letters", "/my-details"], modules.Select(m => m.GetProperty("launchRoute").GetString()).Order(StringComparer.Ordinal));
        Assert.All(modules, m => Assert.Equal(JsonValueKind.Null, m.GetProperty("url").ValueKind));
    }

    [Fact]
    public async Task Favourites_recent_use_search_and_categories_shape_the_catalogue()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var logistics = await (await admin.PostAsJsonAsync($"{Admin}/categories", new { names = Names("Logistics", "الخدمات اللوجستية"), position = 1 })).ReadAsync<JsonElement>();
        var categoryId = logistics.GetProperty("id").GetGuid();
        var cards = await PublishFormAsync(admin, new
        {
            kind = "form", names = Names("Business cards", "بطاقات العمل"), categoryId,
            synonyms = new Dictionary<string, string[]> { ["en"] = ["visiting card"], ["ar"] = ["كرت"] },
        });
        var parking = await PublishFormAsync(admin, new { kind = "form", names = Names("Parking", "مواقف") });
        var sara = o.T["sara"].Client;

        Assert.Equal([cards], ServiceIds(await CatalogueAsync(sara, "?q=card")));
        Assert.Equal([cards], ServiceIds(await CatalogueAsync(sara, "?q=" + Uri.EscapeDataString("بطاقات"))));
        Assert.Equal([cards], ServiceIds(await CatalogueAsync(sara, "?q=" + Uri.EscapeDataString("كرت"))));
        Assert.Equal([cards], ServiceIds(await CatalogueAsync(sara, "?q=visiting")));
        Assert.Equal([cards], ServiceIds(await CatalogueAsync(sara, $"?category={categoryId}")));

        Assert.Equal(HttpStatusCode.NoContent, (await sara.PutAsync($"{Catalogue}/{parking}/favourite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sara.PostAsync($"{Catalogue}/{cards}/used", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sara.PostAsync($"{Catalogue}/{parking}/used", null)).StatusCode);
        var view = await CatalogueAsync(sara);
        Assert.True(view.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == parking).GetProperty("favourite").GetBoolean());
        Assert.Equal([parking, cards], view.GetProperty("recent").EnumerateArray().Select(r => r.GetGuid()).ToList());
        Assert.Contains(view.GetProperty("categories").EnumerateArray(), c => c.GetProperty("id").GetGuid() == categoryId);
        Assert.Equal(HttpStatusCode.NoContent, (await sara.DeleteAsync($"{Catalogue}/{parking}/favourite")).StatusCode);

        // A retired service leaves the catalogue and the recent list, and its category can then be emptied and deleted.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Admin}/categories/{categoryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{cards}/retire", new { })).StatusCode);
        view = await CatalogueAsync(sara);
        Assert.DoesNotContain(cards, ServiceIds(view));
        Assert.DoesNotContain(cards, view.GetProperty("recent").EnumerateArray().Select(r => r.GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await sara.PutAsync($"{Catalogue}/{cards}/favourite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Admin}/categories/{categoryId}", new { names = Names("Logistics desk", "اللوجستيات"), position = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Admin}/categories/{categoryId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await sara.PostAsJsonAsync($"{Admin}/categories", new { names = Names("Mine", "لي") })).StatusCode);
        Assert.Equal(2, await world.Database.AuditCountAsync(o.T.Id, AuditActions.ServiceCategoryChanged));
        Assert.Equal(1, await world.Database.AuditCountAsync(o.T.Id, AuditActions.ServiceRetired));
    }

    [Fact]
    public async Task An_owner_scoped_to_a_unit_keeps_the_audience_inside_it_and_never_sees_other_units_services()
    {
        var o = await FreshAsync();
        var gulfOwner = o.T["owner_gulf"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await gulfOwner.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Tenant wide", "للجميع") })).StatusCode);
        Assert.Contains("validation.services.audience_beyond_owner", await ErrorKeysAsync(await gulfOwner.PostAsJsonAsync(Admin,
            new { kind = "form", names = Names("Gulf", "الخليج"), ownerOrgUnitId = o.Gulf })));
        Assert.Contains("validation.services.audience_beyond_owner", await ErrorKeysAsync(await gulfOwner.PostAsJsonAsync(Admin,
            new { kind = "form", names = Names("Gulf", "الخليج"), ownerOrgUnitId = o.Gulf, audience = new { orgUnits = new[] { o.Gulf, o.Africa } } })));

        var gulf = await PublishFormAsync(gulfOwner, new { kind = "form", names = Names("Gulf cards", "بطاقات الخليج"), ownerOrgUnitId = o.Gulf, audience = new { orgUnits = new[] { o.Dubai } } });
        Assert.Contains("validation.services.audience_beyond_owner", await ErrorKeysAsync(await gulfOwner.PutAsJsonAsync($"{Admin}/{gulf}",
            new { names = Names("Gulf cards", "بطاقات الخليج"), ownerOrgUnitId = o.Gulf, audience = new { orgUnits = new[] { o.Africa } } })));

        var africaOwner = o.T["owner_africa"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await africaOwner.GetAsync($"{Admin}/{gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await africaOwner.PutAsJsonAsync($"{Admin}/{gulf}/draft", FormDraft())).StatusCode);
        var africaList = await (await africaOwner.GetAsync(Admin)).ReadAsync<JsonElement>();
        Assert.DoesNotContain(africaList.EnumerateArray(), s => s.GetProperty("service").GetProperty("id").GetGuid() == gulf);
        var gulfList = await (await gulfOwner.GetAsync(Admin)).ReadAsync<JsonElement>();
        Assert.Contains(gulfList.EnumerateArray(), s => s.GetProperty("service").GetProperty("id").GetGuid() == gulf);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["sara"].Client.GetAsync(Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Catalogue}/{gulf}")).StatusCode);
    }

    [Fact]
    public async Task Publishing_creates_versions_requests_keep_theirs_and_an_approval_step_is_published_to_the_engine()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var route = new { approval = new { names = Names("Manager approval", "موافقة المدير"), approvers = new { kind = "permission_at_unit", permission = "workflow.approve" }, dueWorkingDays = 3 } };
        var id = await PublishFormAsync(admin, new { kind = "form", names = Names("Cards", "بطاقات") }, route);
        var definition = await world.Factory.InScopeAsync(o.T.Id, sp => sp.GetRequiredService<IProcessEngine>().GetDefinitionAsync("service." + id.ToString("N")));
        Assert.NotNull(definition);
        Assert.Equal(RouteSources.ServiceRoute, definition!.Source);

        var changed = new
        {
            form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "علب") }, new { key = "mobile", type = "text", labels = Names("Mobile", "الجوال") } } },
            route,
        };
        var draft = await (await admin.PutAsJsonAsync($"{Admin}/{id}/draft", changed)).ReadAsync<JsonElement>();
        Assert.Equal(2, draft.GetProperty("latest").GetProperty("version").GetInt32());
        Assert.Equal("draft", draft.GetProperty("latest").GetProperty("state").GetString());
        var shown = await (await o.T["sara"].Client.GetAsync($"{Catalogue}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(1, shown.GetProperty("version").GetInt32());
        Assert.Equal(1, shown.GetProperty("form").GetProperty("fields").GetArrayLength());
        Assert.True(shown.GetProperty("hasApproval").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        shown = await (await o.T["sara"].Client.GetAsync($"{Catalogue}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(2, shown.GetProperty("version").GetInt32());
        Assert.Equal(1, (await world.Factory.InScopeAsync(o.T.Id, sp => sp.GetRequiredService<IProcessEngine>().GetDefinitionAsync("service." + id.ToString("N"))))!.Version);

        Assert.Contains("validation.services.approvers", await ErrorKeysAsync(await admin.PutAsJsonAsync($"{Admin}/{id}/draft",
            FormDraft(new { approval = new { approvers = new { kind = "role", permission = "approver" } } }))));
        Assert.Contains("validation.services.form_empty", await ErrorKeysAsync(await admin.PostAsJsonAsync($"{Admin}/{await CreateAsync(admin, new { kind = "form", names = Names("Empty", "فارغ") })}/publish", new { })));

        Assert.Equal(2, await world.Database.AuditCountAsync(o.T.Id, AuditActions.ServiceCreated));
        Assert.Equal(2, await world.Database.AuditCountAsync(o.T.Id, AuditActions.ServicePublished));
        Assert.Equal(2, await world.Database.AuditCountAsync(o.T.Id, AuditActions.ServiceChanged));
        Assert.Equal(2, await world.Database.OutboxCountAsync("ServicePublished", o.T.Id));
    }

    [Fact]
    public async Task An_owner_previews_the_draft_prefilled_for_themselves_and_a_submitted_request_keeps_its_form_version()
    {
        var o = await FreshAsync();
        var owner = o.T["owner_gulf"].Client;
        var id = await CreateAsync(owner, new { kind = "form", names = Names("Gulf cards", "بطاقات الخليج"), descriptions = Names("Order cards", "اطلب البطاقات"),
            ownerOrgUnitId = o.Gulf, audience = new { orgUnits = new[] { o.Dubai } } });
        var draft = new
        {
            form = new
            {
                fields = new object[]
                {
                    new { key = "email", type = "text", labels = Names("Email", "البريد"), prefill = "profile.email" },
                    new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true },
                },
            },
            route = new { approval = new { names = Names("Logistics approval", "موافقة اللوجستيات"), approvers = new { kind = "users", users = new[] { o.T["omar"].Id } } } },
        };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{Admin}/{id}/draft", draft)).StatusCode);

        var preview = await (await owner.GetAsync($"{Admin}/{id}/preview")).ReadAsync<JsonElement>();
        Assert.Equal("draft", preview.GetProperty("state").GetString());
        Assert.Equal(2, preview.GetProperty("form").GetProperty("fields").GetArrayLength());
        Assert.Equal(o.T["owner_gulf"].Email, preview.GetProperty("prefill").GetProperty("values").GetProperty("email").GetString());
        Assert.Equal("موافقة اللوجستيات", preview.GetProperty("approvalStep").GetProperty("ar").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["sara"].Client.GetAsync($"{Admin}/{id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["owner_africa"].Client.GetAsync($"{Admin}/{id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["sara"].Client.GetAsync($"{Catalogue}/{id}")).StatusCode); // not published yet

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        var first = await o.T["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = id, data = new { boxes = 1 } });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();

        var changed = new
        {
            form = new
            {
                fields = new object[]
                {
                    new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true },
                    new { key = "title", type = "text", labels = Names("Title on card", "المسمى على البطاقة"), required = true },
                },
            },
            draft.route,
        };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{Admin}/{id}/draft", changed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);

        var kept = await (await o.T["sara"].Client.GetAsync($"/api/v1/requests/{firstId}")).ReadAsync<JsonElement>();
        Assert.Equal(1, kept.GetProperty("serviceVersion").GetInt32());
        Assert.Equal(["email", "boxes"], kept.GetProperty("form").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray());
        var second = await o.T["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = id, data = new { boxes = 1 } });
        Assert.Contains("validation.services.field_required", await ErrorKeysAsync(second));
    }

    [Fact]
    public async Task Services_stop_at_the_plan_quota()
    {
        // Workplace allows 25 catalogue services (r1-implementation-notes); module services do not count.
        var o = await FreshAsync("workplace");
        for (var i = 0; i < 25; i++)
        {
            await CreateAsync(o.T["admin"].Client, new { kind = "form", names = Names("Service " + i, "خدمة " + i) });
        }
        var over = await o.T["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("One more", "واحدة أخرى") });
        Assert.Equal(HttpStatusCode.Forbidden, over.StatusCode);
        var problem = await over.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.Equal("workflow.max_services", problem.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Services_categories_and_favourites_of_another_tenant_are_not_found()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var id = await PublishFormAsync(a.T["admin"].Client, new { kind = "form", names = Names("Cards", "بطاقات") });
        var category = (await (await a.T["admin"].Client.PostAsJsonAsync($"{Admin}/categories", new { names = Names("Logistics", "لوجستيات") })).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await b.T["sara"].Client.GetAsync($"{Catalogue}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["sara"].Client.PutAsync($"{Catalogue}/{id}/favourite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["admin"].Client.GetAsync($"{Admin}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["admin"].Client.PutAsJsonAsync($"{Admin}/categories/{category}", new { names = Names("X", "س") })).StatusCode);
        Assert.Contains("validation.services.category", await ErrorKeysAsync(await b.T["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("X", "س"), categoryId = category })));
        Assert.Contains("validation.services.audience", await ErrorKeysAsync(await b.T["admin"].Client.PostAsJsonAsync(Admin,
            new { kind = "form", names = Names("X", "س"), audience = new { orgUnits = new[] { a.Gulf } } })));
        Assert.DoesNotContain(id, ServiceIds(await CatalogueAsync(b.T["sara"].Client)));
    }
}
