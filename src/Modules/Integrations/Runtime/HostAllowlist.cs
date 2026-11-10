using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

public sealed record AllowlistEntry(string Host, DateTimeOffset AddedAt);

public sealed record AllowlistRequest(string? Host);

/// <summary>
/// The hosts this tenant's outbound calls may reach (spec 017 FR-012): custom REST base URLs and outbound webhooks are
/// checked against it before any call, and the address check of <see cref="SafeHttp"/> runs on every connect after
/// that. Entries are exact host names or <c>*.example.com</c>; IP addresses are not accepted. Changes are audited.
/// </summary>
public sealed partial class HostAllowlist(IDbSession session, ITenantContext context, IAuditWriter audit, IClock clock)
{
    public const int MaxEntries = 200;

    [GeneratedRegex(@"^(\*\.)?[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostPattern();

    public Task<List<AllowlistEntry>> ListAsync(CancellationToken ct) =>
        session.QueryAsync("select host, added_at from integrations.host_allowlist order by host collate \"C\"", null,
            r => new AllowlistEntry(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1)), ct);

    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct) =>
        uri.Scheme == Uri.UriSchemeHttps && SafeHttp.HostAllowed(uri.IdnHost, (await ListAsync(ct)).Select(e => e.Host));

    public async Task<AllowlistEntry> AddAsync(AllowlistRequest request, CancellationToken ct)
    {
        var host = Normalize(request.Host);
        if (await session.ScalarAsync<long>("select count(*) from integrations.host_allowlist", null, ct) >= MaxEntries)
        {
            throw AppException.Conflict("conflict.integrations.allowlist_full");
        }
        var now = clock.UtcNow;
        var added = await session.ExecAsync(
            """
            insert into integrations.host_allowlist (tenant_id, host, added_by, added_at)
            values (platform.current_tenant_id(), @host, @actor, @now) on conflict do nothing
            """, new { host, actor = context.ActorId, now }, ct);
        if (added > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HostAllowlistChanged, "host_allowlist", host, Changes: new { added = host }), ct);
        }
        return (await ListAsync(ct)).First(e => e.Host == host);
    }

    public async Task RemoveAsync(string host, CancellationToken ct)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (await session.ExecAsync("delete from integrations.host_allowlist where host = @normalized", new { normalized }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HostAllowlistChanged, "host_allowlist", normalized, Changes: new { removed = normalized }), ct);
    }

    private static string Normalize(string? host)
    {
        var h = host?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        try
        {
            // International names are kept in their ASCII form, the form DNS and the request use.
            if (h.Length > 0 && !h.All(char.IsAscii))
            {
                var wildcard = h.StartsWith("*.", StringComparison.Ordinal);
                var ascii = new System.Globalization.IdnMapping().GetAscii(wildcard ? h[2..] : h);
                h = wildcard ? "*." + ascii : ascii;
            }
        }
        catch (ArgumentException)
        {
            h = string.Empty;
        }
        if (h.Length is 0 or > 253 || !HostPattern().IsMatch(h) || System.Net.IPAddress.TryParse(h, out _) || h.Split('.')[^1].All(char.IsDigit))
        {
            throw AppException.Validation("host", "validation.integrations.allowlist_host_invalid");
        }
        return h;
    }
}
