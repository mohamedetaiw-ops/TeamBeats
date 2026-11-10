using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.SyncAgent;

/// <summary>The agent's identity on this machine: its ID, its tenant host, and its private key. The key never leaves here.</summary>
public sealed record AgentIdentity(Guid AgentId, Uri Server, ECDsa Key);

public sealed class AgentRefusedException(HttpStatusCode status) : Exception($"TeamBeats refused the agent request ({(int)status}).")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Talks to the tenant's TeamBeats host (spec 017 FR-016). Every request after enrolment is signed with the agent's key
/// over method, path, timestamp and body (<see cref="AgentProtocol"/>). Outbound HTTPS only.
/// </summary>
public sealed class AgentClient(HttpClient http, TimeProvider time)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record Enrolled(Guid AgentId, Guid ConnectionId);

    /// <summary>Enrols with a one-time code and the public half of a new key. Returns the agent ID.</summary>
    public async Task<Enrolled> EnrolAsync(Uri server, string code, string name, ECDsa key, string version, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(new Uri(server, "/api/v1/agent/enrol"),
            new { code, name, publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), version }, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentRefusedException(response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<Enrolled>(Json, ct))!;
    }

    public Task HeartbeatAsync(AgentIdentity agent, string version, CancellationToken ct) =>
        SendAsync(agent, "/api/v1/agent/heartbeat", new { version }, ct);

    /// <summary>The next job, or null when there is none for this agent now.</summary>
    public async Task<AgentJobView?> PullAsync(AgentIdentity agent, CancellationToken ct)
    {
        var response = await SendAsync(agent, "/api/v1/agent/jobs:pull", null, ct);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<AgentJobView>(Json, ct);
    }

    public Task RecordsAsync(AgentIdentity agent, Guid jobId, AgentRecordBatch batch, CancellationToken ct) =>
        SendAsync(agent, $"/api/v1/agent/jobs/{jobId:D}/records", batch, ct);

    public Task CompleteAsync(AgentIdentity agent, Guid jobId, AgentJobResult result, CancellationToken ct) =>
        SendAsync(agent, $"/api/v1/agent/jobs/{jobId:D}:complete", result, ct);

    private async Task<HttpResponseMessage> SendAsync(AgentIdentity agent, string path, object? payload, CancellationToken ct)
    {
        var body = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(agent.Server, path)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };
        request.Headers.TryAddWithoutValidation(AgentProtocol.AgentHeader, agent.AgentId.ToString("D"));
        request.Headers.TryAddWithoutValidation(AgentProtocol.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AgentProtocol.SignatureHeader, AgentProtocol.Sign(agent.Key, "POST", path, timestamp, body));
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentRefusedException(response.StatusCode);
        }
        return response;
    }
}
