using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.DevFakes;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 R2 slice 4 (T015, US6, AST-016): a spoken question is transcribed through the AI Gateway and handed back
/// for the person to edit, an answer is read aloud in its language, the audio is kept nowhere, and only Workplace Plus
/// and above have voice. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssistantVoiceTests(TenantWorld world)
{
    private const string Assistant = "/api/v1/assistant";
    private const string Spoken = "Where does the Falcon pilot start?";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("av-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
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

    private static Task<HttpResponseMessage> TranscribeAsync(HttpClient client, byte[] audio, string mediaType = "audio/webm;codecs=opus")
    {
        var content = new ByteArrayContent(audio);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return client.PostAsync($"{Assistant}/transcribe", content);
    }

    private static byte[] Speech(string words) => Encoding.UTF8.GetBytes(FakeOpenRouter.SpeechPrefix + words);

    /// <summary>An answered turn with a citation marker: a file brought to the conversation is the evidence.</summary>
    private static async Task<JsonElement> AnsweredTurnAsync(HttpClient client)
    {
        var notes = "Project Falcon notes. The Falcon pilot starts in the Riyadh warehouse with forty scanners.";
        var file = await OkAsync(client.PostAsync($"{Assistant}/files?name=falcon-notes.txt", new ByteArrayContent(Encoding.UTF8.GetBytes(notes))), HttpStatusCode.Created);
        var turn = await OkAsync(client.PostAsJsonAsync($"{Assistant}/turns", new { question = Spoken, fileIds = new[] { file.GetProperty("id").GetGuid() } }));
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        Assert.Contains("[1]", turn.GetProperty("answer").GetString());
        return turn;
    }

    [Fact]
    public async Task A_spoken_question_comes_back_as_text_to_edit_and_nothing_is_kept()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;
        Assert.Equal(60, (await OkAsync(emp.GetAsync($"{Assistant}/voice"))).GetProperty("maxSeconds").GetInt32());

        // The words come back; no turn is asked until the person sends them.
        var transcript = await OkAsync(TranscribeAsync(emp, Speech(Spoken)));
        Assert.Equal(Spoken, transcript.GetProperty("text").GetString());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.turn"));
        Assert.Equal("openai/whisper-1", world.Factory.OpenRouter.LastModel);

        // The call is metered by the gateway like any other; the words are in no record or audit.
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from ai.usage_record where use_case = 'transcribe' and decision = 'served' and model = 'openai/whisper-1'"));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like '%Falcon%'"));

        // Only browser audio of about a minute is taken.
        Assert.Equal("validation.assistant.audio_type", await ProblemKeyAsync(TranscribeAsync(emp, Speech(Spoken), "video/mp4"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assistant.audio_size", await ProblemKeyAsync(TranscribeAsync(emp, []), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assistant.audio_size",
            await ProblemKeyAsync(TranscribeAsync(emp, new byte[(int)TeamBeats.Modules.Agents.AssistantVoice.MaxAudioBytes + 1]), HttpStatusCode.BadRequest));

        // A provider that cannot be reached is a plain message, never its error.
        world.Factory.OpenRouter.Unavailable = true;
        try
        {
            Assert.Equal("conflict.assistant.voice_unavailable", await ProblemKeyAsync(TranscribeAsync(emp, Speech(Spoken)), HttpStatusCode.Conflict));
        }
        finally
        {
            world.Factory.OpenRouter.Unavailable = false;
        }
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from ai.usage_record where use_case = 'transcribe' and decision = 'denied' and denial = @denial", ("denial", AiDenials.ProviderUnavailable)));
    }

    [Fact]
    public async Task An_answer_is_read_aloud_for_its_owner_only_without_citation_markers()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;
        var turn = await AnsweredTurnAsync(emp);
        var turnId = turn.GetProperty("id").GetGuid();

        var response = await emp.PostAsJsonAsync($"{Assistant}/speak", new { turnId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var audio = await response.Content.ReadAsByteArrayAsync();
        var spoken = Encoding.UTF8.GetString(audio[3..]);
        Assert.StartsWith("ID3", Encoding.UTF8.GetString(audio[..3]));
        Assert.DoesNotContain("[1]", spoken);
        Assert.Equal(turn.GetProperty("answer").GetString()!.Replace(" [1]", string.Empty).Replace("[1]", string.Empty).Trim(), spoken);
        Assert.Equal("openai/gpt-4o-mini-tts", world.Factory.OpenRouter.LastModel);
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.usage_record where use_case = 'speak' and decision = 'served'"));

        // Nobody else hears it, in this tenant or another, and a turn must be named.
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp2"].Client.PostAsJsonAsync($"{Assistant}/speak", new { turnId })).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.PostAsJsonAsync($"{Assistant}/speak", new { turnId })).StatusCode);
        Assert.Equal("validation.required", await ProblemKeyAsync(emp.PostAsJsonAsync($"{Assistant}/speak", new { turnId = (Guid?)null }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Workplace_has_no_voice()
    {
        var t = await FreshAsync("workplace");
        var emp = t["emp"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync($"{Assistant}/voice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TranscribeAsync(emp, Speech(Spoken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.PostAsJsonAsync($"{Assistant}/speak", new { turnId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from ai.usage_record where use_case in ('transcribe', 'speak')"));
    }
}
