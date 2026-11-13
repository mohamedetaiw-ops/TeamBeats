using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>One client on the platform's verified MCP client list (spec 035 FR-023).</summary>
public sealed record VerifiedMcpClient(string ClientId, string Name, string Publisher, string[] RedirectUris, string[] Scopes, string Status,
    Guid ReviewedBy, DateTimeOffset ReviewedAt)
{
    public bool Active => Status == "active";
}

/// <summary>A listed client as a tenant's AI policy administrator picks it: no reviewer, nothing about other tenants.</summary>
public sealed record VerifiedMcpClientView(string ClientId, string Name, string Publisher, IReadOnlyList<string> Scopes);

/// <summary>What the platform operator records after the security review of a client ID metadata document.</summary>
public sealed record VerifiedMcpClientRequest(string? ClientId, string? Name, string? Publisher, IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? Scopes, string? Status);

/// <summary>
/// The platform's verified MCP client list (spec 035 FR-023, section 13). An MCP client is named by the HTTPS URL of its
/// client ID metadata document. The platform operator reviews the document and records its name, publisher, redirect URIs
/// and the scopes it may ask for; the authorization server accepts only what was recorded, so a document that changes
/// later changes nothing until it is reviewed again. Removing a client is blocking it, which disables it for every tenant.
/// </summary>
public sealed class VerifiedMcpClients(IDbSession session, ITenantContext context, IAuditWriter audit, IClock clock)
{
    public const int MaxClientIdLength = 200;

    private const string Columns = "client_id, name, publisher, redirect_uris, scopes, status, reviewed_by, reviewed_at";

    private static VerifiedMcpClient Read(Npgsql.NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3),
        r.GetFieldValue<string[]>(4), r.GetString(5), r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7));

    public Task<VerifiedMcpClient?> FindAsync(string clientId, CancellationToken ct) =>
        IsMetadataUrl(clientId)
            ? session.QuerySingleOrDefaultAsync($"select {Columns} from publicapi.verified_mcp_client where client_id = @clientId", new { clientId }, Read, ct)
            : Task.FromResult<VerifiedMcpClient?>(null);

    /// <summary>The whole list, for the platform operator.</summary>
    public Task<List<VerifiedMcpClient>> ListAsync(CancellationToken ct) =>
        session.QueryAsync($"select {Columns} from publicapi.verified_mcp_client order by name, client_id", null, Read, ct);

    /// <summary>The clients a tenant may allow, for the AI policy screen.</summary>
    public async Task<IReadOnlyList<VerifiedMcpClientView>> ActiveAsync(CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from publicapi.verified_mcp_client where status = 'active' order by name, client_id", null, Read, ct))
        .Select(c => new VerifiedMcpClientView(c.ClientId, c.Name, c.Publisher, c.Scopes)).ToList();

    /// <summary>Adds or changes a listed client after review; the platform audit records who did it.</summary>
    public async Task<VerifiedMcpClient> PutAsync(VerifiedMcpClientRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var clientId = request.ClientId?.Trim() ?? "";
        if (!IsMetadataUrl(clientId))
        {
            errors.Add("clientId", "validation.api.verified_client_id");
        }
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80)
        {
            errors.Add("name", "validation.api.name");
        }
        var publisher = request.Publisher?.Trim() ?? "";
        if (publisher.Length is < 1 or > 80)
        {
            errors.Add("publisher", "validation.api.publisher");
        }
        ApiRules.CheckRedirectUris(request.RedirectUris, AppKinds.McpClient, errors);
        ApiRules.CheckScopes(request.Scopes, AppKinds.McpClient, errors);
        var status = request.Status ?? "active";
        if (status is not ("active" or "blocked"))
        {
            errors.Add("status", "validation.api.verified_client_status");
        }
        errors.ThrowIfAny();

        var by = context.ActorId ?? throw AppException.Unauthenticated();
        var entry = new VerifiedMcpClient(clientId, name, publisher, [.. request.RedirectUris!.Distinct()], [.. request.Scopes!.Distinct()], status, by, clock.UtcNow);
        await session.ExecAsync(
            """
            insert into publicapi.verified_mcp_client (client_id, name, publisher, redirect_uris, scopes, status, reviewed_by, reviewed_at)
            values (@ClientId, @Name, @Publisher, @RedirectUris, @Scopes, @Status, @ReviewedBy, @ReviewedAt)
            on conflict (client_id) do update set name = excluded.name, publisher = excluded.publisher, redirect_uris = excluded.redirect_uris,
              scopes = excluded.scopes, status = excluded.status, reviewed_by = excluded.reviewed_by, reviewed_at = excluded.reviewed_at
            """, entry, ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.ApiVerifiedClientChanged, "verified_mcp_client", clientId,
            Changes: new { entry.Name, entry.Publisher, entry.RedirectUris, entry.Scopes, entry.Status }), null, ct);
        return entry;
    }

    /// <summary>
    /// A client ID metadata document URL: HTTPS with a path, no user info, query or fragment, at most 200 characters, so
    /// it fits the external AI client policy and can never be mistaken for a tenant's own <c>tb_</c> client ID.
    /// </summary>
    public static bool IsMetadataUrl(string? value) =>
        value is { Length: > 0 and <= MaxClientIdLength } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.Length > 1 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && !value.Contains('#', StringComparison.Ordinal);
}
