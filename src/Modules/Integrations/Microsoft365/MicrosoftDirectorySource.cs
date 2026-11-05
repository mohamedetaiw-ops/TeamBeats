using System.Net.Http.Headers;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Entra ID directory through Microsoft Graph delta queries (spec 003 plan: users and groups, read only). Microsoft's
/// model is translated here into <see cref="DirectoryUserRecord"/> and <see cref="DirectoryGroupRecord"/>.
/// </summary>
public sealed class MicrosoftDirectorySource(IDbSession session, IMicrosoftGraph graph, IHttpClientFactory httpClients) : IDirectorySource
{
    private const string UserSelect = "id,displayName,mail,userPrincipalName,jobTitle,officeLocation,mobilePhone,businessPhones,employeeId,preferredLanguage,accountEnabled";
    private const int MaxPages = 10_000;

    public string IssuerFor(string externalTenantId) => $"{HttpMicrosoftGraph.LoginAuthority}/{externalTenantId}/v2.0";

    public async Task<DirectoryChanges> ReadChangesAsync(Guid connectionId, string? usersCursor, string? groupsCursor, CancellationToken ct = default)
    {
        var connection = await session.QuerySingleOrDefaultAsync(
            """
            select c.external_tenant_id from integrations.connection c join integrations.connection_feature f on f.connection_id = c.id
            where c.id = @id and c.enabled and f.feature = @f and f.enabled and f.consent_status = 'granted'
            """, new { id = connectionId, f = Microsoft365Features.DirectorySync }, r => r.GetString(0), ct)
            ?? throw new DirectoryUnavailableException("connection_unusable");

        AppToken token;
        try
        {
            token = await graph.AcquireAppTokenAsync(connection, ct);
        }
        catch (GraphConsentMissingException)
        {
            throw new DirectoryUnavailableException("consent_missing");
        }
        catch (GraphUnavailableException)
        {
            throw new DirectoryUnavailableException("unreachable");
        }

        var users = new List<DirectoryUserRecord>();
        var nextUsers = await ReadDeltaAsync(token, Start(usersCursor, $"{HttpMicrosoftGraph.GraphBase}/users/delta?$select={UserSelect}"),
            item => users.Add(MapUser(item)), ct);
        var groups = new List<DirectoryGroupRecord>();
        var nextGroups = await ReadDeltaAsync(token, Start(groupsCursor, $"{HttpMicrosoftGraph.GraphBase}/groups/delta?$select=id,displayName,members"),
            item => groups.Add(MapGroup(item)), ct);
        return new DirectoryChanges(users, MergeGroups(groups), nextUsers, nextGroups);
    }

    /// <summary>Cursors are Graph delta links this adapter stored; anything else starts a full read.</summary>
    private static string Start(string? cursor, string full) =>
        cursor is not null && cursor.StartsWith(HttpMicrosoftGraph.GraphBase + "/", StringComparison.Ordinal) ? cursor : full;

    private async Task<string> ReadDeltaAsync(AppToken token, string url, Action<JsonElement> onItem, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(HttpMicrosoftGraph.ClientName);
        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (HttpRequestException)
            {
                throw new DirectoryUnavailableException("unreachable");
            }
            using (response)
            {
                if ((int)response.StatusCode is 401 or 403)
                {
                    throw new DirectoryUnavailableException("consent_missing");
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new DirectoryUnavailableException("unreachable");
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    onItem(item.Clone());
                }
                if (doc.RootElement.TryGetProperty("@odata.deltaLink", out var delta))
                {
                    return delta.GetString()!;
                }
                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()!
                    : throw new DirectoryUnavailableException("unreachable");
                if (!url.StartsWith(HttpMicrosoftGraph.GraphBase + "/", StringComparison.Ordinal))
                {
                    throw new DirectoryUnavailableException("unexpected_link");
                }
            }
        }
        throw new DirectoryUnavailableException("too_many_pages");
    }

    private static DirectoryUserRecord MapUser(JsonElement u)
    {
        var removed = u.TryGetProperty("@removed", out _);
        string? Text(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var phone = Text("mobilePhone")
            ?? (u.TryGetProperty("businessPhones", out var phones) && phones.ValueKind == JsonValueKind.Array && phones.GetArrayLength() > 0 ? phones[0].GetString() : null);
        bool? enabled = u.TryGetProperty("accountEnabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        return new DirectoryUserRecord(u.GetProperty("id").GetString()!.ToLowerInvariant(), removed, enabled, Text("displayName"), Text("mail"),
            Text("userPrincipalName"), Text("jobTitle"), Text("officeLocation"), phone, Text("employeeId"), Text("preferredLanguage"));
    }

    private static DirectoryGroupRecord MapGroup(JsonElement g)
    {
        var added = new List<string>();
        var removedMembers = new List<string>();
        if (g.TryGetProperty("members@delta", out var members))
        {
            foreach (var m in members.EnumerateArray())
            {
                var isUser = !m.TryGetProperty("@odata.type", out var type) || type.GetString() == "#microsoft.graph.user";
                if (!isUser)
                {
                    continue;
                }
                (m.TryGetProperty("@removed", out _) ? removedMembers : added).Add(m.GetProperty("id").GetString()!.ToLowerInvariant());
            }
        }
        var name = g.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new DirectoryGroupRecord(g.GetProperty("id").GetString()!.ToLowerInvariant(), g.TryGetProperty("@removed", out _), name, added, removedMembers);
    }

    /// <summary>Graph may return one group on several pages; merge them in order.</summary>
    private static List<DirectoryGroupRecord> MergeGroups(List<DirectoryGroupRecord> groups) =>
        groups.GroupBy(g => g.ExternalId).Select(set => set.Aggregate((a, b) => new DirectoryGroupRecord(a.ExternalId, b.Removed,
            b.DisplayName ?? a.DisplayName, [.. a.MembersAdded, .. b.MembersAdded], [.. a.MembersRemoved, .. b.MembersRemoved]))).ToList();
}
