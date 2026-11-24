using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Entra ID profile photos through Microsoft Graph (spec 010 US3 scenario 3), with the app token of the directory sync
/// consent (User.Read.All covers photos). The photo's metadata is read first and the image only when its media tag
/// changed, so a sync does not download photos it already has. Photos larger than <see cref="MaxBytes"/> are not read.
/// </summary>
public sealed class MicrosoftDirectoryPhotos(IDbSession session, IMicrosoftGraph graph, IHttpClientFactory httpClients) : IDirectoryPhotos
{
    public const int MaxBytes = 4 * 1024 * 1024;

    private readonly Dictionary<Guid, AppToken> _tokens = [];

    private Task<string?> DirectoryOfAsync(Guid connectionId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            """
            select c.external_tenant_id from integrations.connection c join integrations.connection_feature f on f.tenant_id = c.tenant_id and f.connection_id = c.id
            where c.id = @id and c.kind = 'microsoft365' and c.enabled and f.feature = @f and f.enabled and f.consent_status = 'granted'
            """, new { id = connectionId, f = Microsoft365Features.DirectorySync }, r => r.GetString(0), ct);

    public async Task<bool> SupportsAsync(Guid connectionId, CancellationToken ct = default) => await DirectoryOfAsync(connectionId, ct) is not null;

    public async Task<DirectoryPhoto> ReadAsync(Guid connectionId, string directoryId, string? knownTag, CancellationToken ct = default)
    {
        if (!Guid.TryParse(directoryId, out var objectId))
        {
            return new DirectoryPhoto(DirectoryPhotoState.None);
        }
        var token = await TokenAsync(connectionId, ct);
        using var client = httpClients.CreateClient(HttpMicrosoftGraph.ClientName);
        var path = $"{HttpMicrosoftGraph.GraphBase}/users/{objectId:D}/photo";
        string? tag;
        using (var meta = await SendAsync(client, token, path, ct))
        {
            if (meta.StatusCode == HttpStatusCode.NotFound)
            {
                return new DirectoryPhoto(DirectoryPhotoState.None);
            }
            Ensure(meta);
            using var doc = JsonDocument.Parse(await meta.Content.ReadAsStringAsync(ct));
            tag = doc.RootElement.TryGetProperty("@odata.mediaEtag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        if (tag is null)
        {
            return new DirectoryPhoto(DirectoryPhotoState.None);
        }
        if (tag == knownTag)
        {
            return new DirectoryPhoto(DirectoryPhotoState.Unchanged, tag);
        }
        using var image = await SendAsync(client, token, path + "/$value", ct);
        if (image.StatusCode == HttpStatusCode.NotFound)
        {
            return new DirectoryPhoto(DirectoryPhotoState.None);
        }
        Ensure(image);
        if (image.Content.Headers.ContentLength is > MaxBytes)
        {
            return new DirectoryPhoto(DirectoryPhotoState.Changed, tag);
        }
        var data = await image.Content.ReadAsByteArrayAsync(ct);
        return data.Length > MaxBytes
            ? new DirectoryPhoto(DirectoryPhotoState.Changed, tag)
            : new DirectoryPhoto(DirectoryPhotoState.Changed, tag, image.Content.Headers.ContentType?.MediaType, data);
    }

    private async Task<AppToken> TokenAsync(Guid connectionId, CancellationToken ct)
    {
        if (_tokens.TryGetValue(connectionId, out var cached))
        {
            return cached;
        }
        var directory = await DirectoryOfAsync(connectionId, ct) ?? throw new DirectoryUnavailableException("connection_unusable");
        try
        {
            return _tokens[connectionId] = await graph.AcquireAppTokenAsync(directory, ct);
        }
        catch (GraphConsentMissingException)
        {
            throw new DirectoryUnavailableException("consent_missing");
        }
        catch (GraphUnavailableException)
        {
            throw new DirectoryUnavailableException("unreachable");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, AppToken token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new DirectoryUnavailableException("unreachable");
        }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 401 or 403)
        {
            throw new DirectoryUnavailableException("consent_missing");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new DirectoryUnavailableException("unreachable");
        }
    }
}
