using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 US8 (R2, T016): the assistant in Teams. A person writes to the TeamBeats bot; the bot finds their tenant from
/// the Microsoft 365 directory and their linked Entra object ID, and the knowledge agent answers as them, with their
/// permissions, in a card with the answer, its sources and a link to continue in TeamBeats. A follow-up continues the
/// same conversation, which shows in the web history. Unknown and ambiguous accounts, a plan without the assistant in
/// Teams and forged calls get a short answer or nothing. Bot Framework is the fake of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TeamsAssistantTests(TenantWorld world)
{
    private sealed record Setup(TestTenant T, Guid Directory, string EmpOid, string PiaOid);

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private async Task<TestTenant> TenantAsync()
    {
        var t = await world.CreateTenantAsync("ta-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "pia", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return t;
    }

    /// <summary>Connects the tenant's Microsoft 365 directory with Teams granted.</summary>
    private async Task ConnectAsync(TestTenant t, Guid directory)
    {
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "teams" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        world.Factory.Graph.Consent(directory, ["TeamsActivity.Send"]);
        var start = await admin.GetAsync($"/api/v1/integrations/connections/{connection}/features/teams/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal)));
        await client.GetAsync(relay.Headers.Location!.PathAndQuery);
    }

    private async Task LinkAsync(TestTenant t, Guid directory, string name, string oid)
    {
        await world.Database.OwnerExecInTenantAsync(t.Id,
            "insert into identity.external_identity (tenant_id, id, issuer, subject, user_id) values (@t, @id, @i, @s, @u)",
            ("t", t.Id), ("id", Guid.NewGuid()), ("i", $"https://login.microsoftonline.com/{directory:D}/v2.0"), ("s", oid), ("u", t[name].Id));
        world.Factory.Graph.InstallTeamsApp(directory, oid);
    }

    private async Task<Setup> SetupAsync()
    {
        var t = await TenantAsync();
        var directory = Guid.NewGuid();
        await ConnectAsync(t, directory);
        var emp = Guid.NewGuid().ToString("D");
        var pia = Guid.NewGuid().ToString("D");
        await LinkAsync(t, directory, "emp", emp);
        await LinkAsync(t, directory, "pia", pia);
        return new Setup(t, directory, emp, pia);
    }

    private async Task<Guid> NewsAsync(TestTenant t, string title, string body, string? classification = null)
    {
        var created = await t["pia"].Client.PostAsJsonAsync("/api/v1/content/items",
            new { type = "news", classification, variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["pia"].Client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { })).StatusCode);
        return id;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string body, string? bearer)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, TeamsBotEndpoint.Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (bearer is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return await world.Factory.PlatformClient().SendAsync(message);
    }

    /// <summary>Writes to the bot and lets the assistant answer; returns the cards that came back to the chat.</summary>
    private async Task<List<JsonElement>> ChatAsync(Guid directory, string oid, string text, string locale = "en-US")
    {
        var before = world.Factory.Graph.BotMessages.Where(m => m.UserId == oid).Select(m => m.Seq).DefaultIfEmpty().Max();
        var response = await PostAsync(FakeMicrosoftGraph.BotChat(directory, oid, text, locale), world.Factory.Graph.IssueBotToken());
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        await DispatchAsync();
        return world.Factory.Graph.BotMessages.Where(m => m.UserId == oid && m.Seq > before).OrderBy(m => m.Seq).Select(m => m.Card).ToList();
    }

    private static string CardText(JsonElement card) =>
        card.GetProperty("body").EnumerateArray().Last(b => b.GetProperty("type").GetString() == "TextBlock").GetProperty("text").GetString()!;

    private static List<(string Title, string Url)> Links(JsonElement card) =>
        card.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("type").GetString() == "Action.OpenUrl")
            .Select(a => (a.GetProperty("title").GetString()!, a.GetProperty("url").GetString()!)).ToList();

    private string Text(string key, string locale = "en") => world.Factory.Services.GetRequiredService<TeamBeats.Localization.ILocalizer>().Get(key, locale);

    private Task<long> TurnsAsync(TestTenant t) => world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.turn");

    [Fact]
    public async Task The_answer_and_its_sources_come_back_to_the_chat_and_a_follow_up_continues_the_conversation()
    {
        var s = await SetupAsync();
        var news = await NewsAsync(s.T, "Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        await DispatchAsync();

        var card = Assert.Single(await ChatAsync(s.Directory, s.EmpOid, "What do I check on a forklift before a shift?"));
        Assert.Equal(Text("agents.teams.title"), card.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.Contains("[1]", CardText(card));
        var conversation = await world.Database.TenantScalarAsync<Guid>(s.T.Id, "select conversation_id from agents.teams_chat where user_id = @u", ("u", s.T["emp"].Id));
        var links = Links(card);
        var host = $"https://{s.T.Key}.{ApiFactory.BaseDomain}";
        Assert.Equal((Text("agents.teams.continue"), $"{host}/assistant?c={conversation:D}"), links[0]);
        Assert.Contains(("Forklift inspection checklist", $"{host}/content/read/{news}"), links);

        // The follow-up keeps the conversation, and the whole exchange shows in the person's web history.
        Assert.Single(await ChatAsync(s.Directory, s.EmpOid, "And the forklift horn?"));
        var view = await s.T["emp"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/assistant/conversations/{conversation}");
        Assert.Equal(2, view.GetProperty("turns").GetArrayLength());
        Assert.Equal(conversation, await world.Database.TenantScalarAsync<Guid>(s.T.Id, "select conversation_id from agents.teams_chat where user_id = @u", ("u", s.T["emp"].Id)));
        // Every turn is the person's own.
        Assert.Equal(0L, await world.Database.CountInTenantAsync(s.T.Id, "select count(*) from agents.turn where user_id <> @u", ("u", s.T["emp"].Id)));

        // Arabic from Teams gets the bot's texts in Arabic, right to left.
        var arabic = Assert.Single(await ChatAsync(s.Directory, s.EmpOid, "ما هو جدول الرواتب؟", "ar-AE"));
        Assert.True(arabic.GetProperty("rtl").GetBoolean());
        Assert.Equal(Text("agents.teams.continue", "ar"), Links(arabic)[0].Title);
    }

    [Fact]
    public async Task Teams_answers_draw_on_internal_content_at_most()
    {
        var s = await SetupAsync();
        var secret = await NewsAsync(s.T, "Quarterly bonus pool", "Confidential quarterly bonus pool for the sales directors.", "confidential");
        await DispatchAsync();

        var card = Assert.Single(await ChatAsync(s.Directory, s.PiaOid, "How big is the quarterly bonus pool for sales directors?"));
        Assert.DoesNotContain(Links(card), l => l.Url.Contains(secret.ToString(), StringComparison.Ordinal));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(s.T.Id,
            "select count(*) from agents.turn where user_id = @u and classification in ('confidential', 'restricted')", ("u", s.T["pia"].Id)));
    }

    [Fact]
    public async Task Unknown_ambiguous_and_unentitled_people_get_a_short_answer_and_nothing_else()
    {
        var s = await SetupAsync();

        // A Teams account nobody linked, or a directory no tenant connected.
        var stranger = Guid.NewGuid().ToString("D");
        world.Factory.Graph.InstallTeamsApp(s.Directory, stranger);
        Assert.Equal(Text("integrations.teams.chat.no_account"), CardText(Assert.Single(await ChatAsync(s.Directory, stranger, "Hello"))));
        var elsewhere = Guid.NewGuid();
        world.Factory.Graph.InstallTeamsApp(elsewhere, s.EmpOid);
        Assert.Equal(Text("integrations.teams.chat.no_account"), CardText(Assert.Single(await ChatAsync(elsewhere, s.EmpOid, "Hello"))));
        Assert.Equal(0L, await TurnsAsync(s.T));

        // A person who left the tenant is not found.
        await world.Database.OwnerExecInTenantAsync(s.T.Id, "update tenancy.tenant_membership set status = 'disabled' where user_id = @u", ("u", s.T["pia"].Id));
        Assert.Equal(Text("integrations.teams.chat.no_account"), CardText(Assert.Single(await ChatAsync(s.Directory, s.PiaOid, "Hello"))));

        // The same directory and person in two tenants: the bot cannot tell which one, so neither answers.
        var other = await TenantAsync();
        await ConnectAsync(other, s.Directory);
        await LinkAsync(other, s.Directory, "emp", s.EmpOid);
        Assert.Equal(Text("integrations.teams.chat.several"), CardText(Assert.Single(await ChatAsync(s.Directory, s.EmpOid, "Hello"))));
        Assert.Equal(0L, await TurnsAsync(s.T));
        Assert.Equal(0L, await TurnsAsync(other));

        // A plan without the assistant in Teams.
        var lone = await TenantAsync();
        var directory = Guid.NewGuid();
        await ConnectAsync(lone, directory);
        var oid = Guid.NewGuid().ToString("D");
        await LinkAsync(lone, directory, "emp", oid);
        await world.SetCapabilityAsync(lone, Capabilities.AiAssistantTeams, new { enabled = false });
        Assert.Equal(Text("integrations.teams.chat.unavailable"), CardText(Assert.Single(await ChatAsync(directory, oid, "Hello"))));
        Assert.Equal(0L, await TurnsAsync(lone));
    }

    [Fact]
    public async Task Forged_chat_messages_are_refused()
    {
        var s = await SetupAsync();
        var body = FakeMicrosoftGraph.BotChat(s.Directory, s.EmpOid, "What is in the bonus pool?");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(body, world.Factory.Graph.IssueBotToken(issuer: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(body, world.Factory.Graph.IssueBotToken(serviceUrl: "https://evil.example/teams/"))).StatusCode);
        // A service address that is not https is not Bot Framework.
        var plain = FakeMicrosoftGraph.BotChat(s.Directory, s.EmpOid, "Hello", serviceUrl: "http://smba.example/teams/");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(plain, world.Factory.Graph.IssueBotToken(serviceUrl: "http://smba.example/teams/"))).StatusCode);
        var onTenantHost = await world.Factory.TenantClient(s.T.Key).PostAsync(TeamsBotEndpoint.Path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, onTenantHost.StatusCode);
        await DispatchAsync();
        Assert.DoesNotContain(world.Factory.Graph.BotMessages, m => m.UserId == s.EmpOid);
        Assert.Equal(0L, await TurnsAsync(s.T));
    }
}
