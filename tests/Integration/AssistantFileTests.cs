using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 R2 slice 3 (T013, T014, US5, AST-015): a file brought to a conversation is scanned, read, classified by the
/// person and checked against the AI policy, used only in that conversation and by its owner, and deleted with the
/// conversation or, if no turn used it, by retention. Only Workplace Plus and above have it. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantFileTests(TenantWorld world)
{
    private const string Assistant = "/api/v1/assistant";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("af-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "emp", "emp2" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        return t;
    }

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
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string name, string content, string? classification = null, Guid? conversationId = null)
    {
        var query = $"name={Uri.EscapeDataString(name)}" + (classification is null ? string.Empty : $"&classification={classification}")
            + (conversationId is null ? string.Empty : $"&conversationId={conversationId}");
        return client.PostAsync($"{Assistant}/files?{query}", new ByteArrayContent(Encoding.UTF8.GetBytes(content)));
    }

    private const string Notes = "Project Falcon notes. The Falcon pilot starts in the Riyadh warehouse with forty scanners.";

    [Fact]
    public async Task A_file_is_scanned_classified_and_cited_only_in_its_conversation()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;

        // Uploaded before the first turn, the file waits for it; only its text is kept and the audit has no name or text.
        var file = await OkAsync(UploadAsync(emp, "falcon-notes.txt", Notes), HttpStatusCode.Created);
        var fileId = file.GetProperty("id").GetGuid();
        Assert.Equal("internal", file.GetProperty("classification").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("conversationId").ValueKind);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssistantFileAdded, fileId.ToString());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like '%falcon%' or changes::text like '%Falcon%'"));

        // The turn that names it cites it and binds it to the new conversation.
        var turn = await OkAsync(emp.PostAsJsonAsync($"{Assistant}/turns", new { question = "Where does the Falcon pilot start?", fileIds = new[] { fileId } }));
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        var citation = Assert.Single(turn.GetProperty("citations").EnumerateArray());
        Assert.Equal(AssistantFiles.Source, citation.GetProperty("source").GetString());
        Assert.Equal(fileId, citation.GetProperty("itemId").GetGuid());
        Assert.Null(turn.GetProperty("agent").GetString());
        var conversation = turn.GetProperty("conversationId").GetGuid();
        var listed = await OkAsync(emp.GetAsync($"{Assistant}/files?conversationId={conversation}"));
        Assert.Equal(fileId, Assert.Single(listed.GetProperty("files").EnumerateArray()).GetProperty("id").GetGuid());

        // A later turn of the conversation still has it; a new conversation does not.
        var next = await OkAsync(emp.PostAsJsonAsync($"{Assistant}/turns", new { question = "How many Falcon scanners?", conversationId = conversation }));
        Assert.Contains(next.GetProperty("citations").EnumerateArray(), c => c.GetProperty("itemId").GetGuid() == fileId);
        var fresh = await OkAsync(emp.PostAsJsonAsync($"{Assistant}/turns", new { question = "How many Falcon scanners?" }));
        Assert.DoesNotContain(fresh.GetProperty("citations").EnumerateArray(), c => c.GetProperty("itemId").GetGuid() == fileId);
        Assert.Equal("not_found", await ProblemKeyAsync(emp.PostAsJsonAsync($"{Assistant}/turns",
            new { question = "Falcon?", conversationId = fresh.GetProperty("conversationId").GetGuid(), fileIds = new[] { fileId } }), HttpStatusCode.NotFound));

        // Nobody else can use, list or delete it, in this tenant or another.
        Assert.Equal("not_found", await ProblemKeyAsync(t["emp2"].Client.PostAsJsonAsync($"{Assistant}/turns", new { question = "Falcon?", fileIds = new[] { fileId } }),
            HttpStatusCode.NotFound));
        Assert.Empty((await OkAsync(t["emp2"].Client.GetAsync($"{Assistant}/files?conversationId={conversation}"))).GetProperty("files").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp2"].Client.DeleteAsync($"{Assistant}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp2"].Client.PostAsync($"{Assistant}/files?name=x.txt&conversationId={conversation}",
            new ByteArrayContent("x"u8.ToArray()))).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.PostAsJsonAsync($"{Assistant}/turns", new { question = "Falcon?", fileIds = new[] { fileId } })).StatusCode);

        // Deleting the conversation deletes the file and its text.
        await OkAsync(emp.DeleteAsync($"{Assistant}/conversations/{conversation}"), HttpStatusCode.NoContent);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.conversation_file"));
    }

    [Fact]
    public async Task Infected_unreadable_unknown_and_above_policy_files_are_refused_with_the_reason()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;

        Assert.Equal("conflict.assistant.file_infected", await ProblemKeyAsync(UploadAsync(emp, "notes.txt", TestSignatureScanner.Eicar), HttpStatusCode.Conflict));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssistantFileRefused);
        Assert.Equal("validation.assistant.file_type", await ProblemKeyAsync(UploadAsync(emp, "tool.exe", "MZ"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assistant.file_size", await ProblemKeyAsync(UploadAsync(emp, "empty.txt", string.Empty), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assistant.file_classification", await ProblemKeyAsync(UploadAsync(emp, "notes.txt", Notes, "secret"), HttpStatusCode.BadRequest));
        Assert.Equal("conflict.assistant.file_above_policy", await ProblemKeyAsync(UploadAsync(emp, "notes.txt", Notes, "restricted"), HttpStatusCode.Conflict));
        // A picture with no text in it gives nothing to answer from.
        Assert.Equal("validation.assistant.file_no_text", await ProblemKeyAsync(UploadAsync(emp, "photo.png", "no text here"), HttpStatusCode.BadRequest));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.conversation_file"));

        // A PDF is read through the text recognition service.
        var pdf = await OkAsync(UploadAsync(emp, "falcon.pdf", "text: The Falcon pilot has forty scanners."), HttpStatusCode.Created);
        Assert.Equal("application/pdf", pdf.GetProperty("mediaType").GetString());
        Assert.Contains("forty scanners", await world.Database.TenantScalarAsync<string>(t.Id, "select text from agents.conversation_file where id = @id",
            ("id", pdf.GetProperty("id").GetGuid())));

        // Workplace has no files in the assistant.
        var plain = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(plain["emp"].Client, "notes.txt", Notes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["emp"].Client.GetAsync($"{Assistant}/files")).StatusCode);
    }

    [Fact]
    public async Task A_file_no_turn_used_goes_after_a_day_and_delete_all_takes_every_file()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;
        var waiting = (await OkAsync(UploadAsync(emp, "falcon-notes.txt", Notes), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AssistantHistory>().PurgeAsync(DateTimeOffset.UtcNow, default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.conversation_file"));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AssistantHistory>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(2), default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.conversation_file where id = @id", ("id", waiting)));

        await OkAsync(UploadAsync(emp, "falcon-notes.txt", Notes), HttpStatusCode.Created);
        await OkAsync(emp.DeleteAsync($"{Assistant}/conversations"));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.conversation_file"));
    }
}
