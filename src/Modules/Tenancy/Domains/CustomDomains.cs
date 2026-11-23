using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy.Domains;

public sealed record AddDomainRequest(string? Host);

public sealed record DomainPresentationRequest(IReadOnlyDictionary<string, string>? AppTitle, IReadOnlyDictionary<string, string>? Description,
    IReadOnlyDictionary<string, string>? SignInMessage);

/// <summary>A custom domain with the DNS record that proves control of it and what it shows.</summary>
public sealed record DomainView(Guid Id, string Host, string State, string RecordName, string RecordValue, IReadOnlyDictionary<string, string> AppTitle,
    IReadOnlyDictionary<string, string> Description, IReadOnlyDictionary<string, string> SignInMessage, DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt, DateTimeOffset? CheckedAt, string? Failure, DateTimeOffset? CertificateExpiresAt);

public sealed class CustomDomainOptions
{
    public const string Section = "Tenancy:Domains";

    public int MaxPerTenant { get; set; } = 5;

    /// <summary>A DNS over HTTPS resolver that answers in the JSON format (RFC 8484 servers such as Cloudflare and Google).</summary>
    public string Resolver { get; set; } = "https://cloudflare-dns.com/dns-query";

    /// <summary>Administrators are told this many days before a certificate ends.</summary>
    public int CertificateWarningDays { get; set; } = 14;

    /// <summary>How often a verified domain's record and certificate are checked again.</summary>
    public TimeSpan RecheckEvery { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Custom domains (spec 033 R2 T021, US7, FR-010). A tenant administrator adds a host and publishes a TXT record at
/// <c>_teambeats-verify.{host}</c>; verifying reads it through DNS over HTTPS and only then routes the host to the tenant.
/// Only verified hosts are served, one tenant per host (the first to verify it). Each domain has its own app title,
/// browser description and sign-in message per language. A daily check takes the route away when the record disappears
/// and tells administrators when the certificate is about to end; the tenant's own subdomain keeps working throughout.
/// Certificates are issued by the installation's edge on demand, after it asks <c>/api/v1/domains/tls-allowed</c>.
/// </summary>
public sealed partial class CustomDomains(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IDnsTxtLookup dns,
    ICustomDomainResolver resolver,
    IOptions<HostResolutionOptions> hosts,
    IOptions<CustomDomainOptions> options,
    IAuditWriter audit,
    IClock clock)
{
    public const string RecordPrefix = "_teambeats-verify";
    public const string ValuePrefix = "teambeats-verify=";
    public const int TitleLength = 60;
    public const int DescriptionLength = 160;
    public const int MessageLength = 280;

    private sealed record Row(Guid Id, string Host, string State, string Token, Dictionary<string, string> AppTitle, Dictionary<string, string> Description,
        Dictionary<string, string> SignInMessage, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt, DateTimeOffset? CheckedAt, string? Failure,
        DateTimeOffset? CertificateExpiresAt);

    public async Task<IReadOnlyList<DomainView>> ListAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.TenantSettingsView, AuthzTarget.Tenant, ct);
        return (await RowsAsync(null, ct)).Select(View).ToList();
    }

    public async Task<DomainView> AddAsync(AddDomainRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.ExperienceCustomDomain, ct);
        var host = Normalize(request.Host);
        if (host is null || IsInstallationHost(host))
        {
            throw AppException.Validation("host", "validation.tenant.domain_host");
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('tenancy.domain:' || @t::text))", new { t = context.RequireTenant() }, ct);
        var rows = await RowsAsync(null, ct);
        if (rows.Any(r => r.Host == host))
        {
            throw AppException.Conflict("conflict.tenant.domain_exists");
        }
        if (rows.Count >= options.Value.MaxPerTenant)
        {
            throw AppException.Validation("host", "validation.tenant.domain_limit");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into tenancy.tenant_domain (tenant_id, id, host, state, token, created_by, created_at)
            values (platform.current_tenant_id(), @id, @host, 'pending', @token, @by, @now)
            """, new { id, host, token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), by = context.ActorId ?? throw AppException.Denied(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantDomainAdded, "tenant_domain", id.ToString(), Changes: new { host }), ct);
        return View((await RowsAsync(id, ct)).Single());
    }

    /// <summary>
    /// Reads the TXT record and routes the host when it holds this domain's value. A missing record leaves the domain
    /// pending with the reason; a host another tenant verified first is refused.
    /// </summary>
    public async Task<DomainView> VerifyAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        var row = (await RowsAsync(id, ct)).SingleOrDefault() ?? throw AppException.NotFound();
        await entitlements.DemandAsync(Capabilities.ExperienceCustomDomain, ct);
        var failure = await CheckRecordAsync(row, ct);
        var now = clock.UtcNow;
        if (failure is not null)
        {
            await session.ExecAsync("update tenancy.tenant_domain set checked_at = @now, failure = @failure where id = @id", new { id, now, failure }, ct);
            return View((await RowsAsync(id, ct)).Single());
        }
        await session.ExecAsync(
            "update tenancy.tenant_domain set state = 'verified', verified_at = coalesce(verified_at, @now), checked_at = @now, failure = null where id = @id",
            new { id, now }, ct);
        if (!await RouteAsync(row.Host, ct))
        {
            // Another tenant serves this host already; this tenant's row goes back to pending, never served.
            await session.ExecAsync("update tenancy.tenant_domain set state = 'pending', verified_at = null, failure = 'in_use' where id = @id", new { id }, ct);
            throw AppException.Conflict("conflict.tenant.domain_in_use");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantDomainVerified, "tenant_domain", id.ToString(), Changes: new { host = row.Host }), ct);
        return View((await RowsAsync(id, ct)).Single());
    }

    public async Task<DomainView> SetPresentationAsync(Guid id, DomainPresentationRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        var row = (await RowsAsync(id, ct)).SingleOrDefault() ?? throw AppException.NotFound();
        await entitlements.DemandAsync(Capabilities.ExperienceCustomDomain, ct);
        var errors = new ValidationErrors();
        var title = Texts(request.AppTitle, TitleLength, "appTitle", errors);
        var description = Texts(request.Description, DescriptionLength, "description", errors);
        var message = Texts(request.SignInMessage, MessageLength, "signInMessage", errors);
        errors.ThrowIfAny();
        await session.ExecAsync(
            "update tenancy.tenant_domain set app_title = @title, description = @description, sign_in_message = @message where id = @id",
            new { id, title = Jsonb.From(title), description = Jsonb.From(description), message = Jsonb.From(message) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantDomainChanged, "tenant_domain", id.ToString(),
            Changes: new { host = row.Host, before = new { row.AppTitle, row.Description, row.SignInMessage }, after = new { appTitle = title, description, signInMessage = message } }), ct);
        return View((await RowsAsync(id, ct)).Single());
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        var row = (await RowsAsync(id, ct)).SingleOrDefault() ?? throw AppException.NotFound();
        await session.ExecAsync("delete from tenancy.tenant_domain where id = @id", new { id }, ct);
        await RouteAsync(row.Host, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantDomainRemoved, "tenant_domain", id.ToString(), Changes: new { host = row.Host }), ct);
    }

    /// <summary>
    /// The daily check of the current tenant's verified domains, run by <see cref="DomainCheckWorker"/> as the system actor:
    /// a domain whose record is gone stops being served. Returns the hosts that stopped.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecheckAsync(CancellationToken ct)
    {
        var due = clock.UtcNow - options.Value.RecheckEvery;
        var stopped = new List<string>();
        foreach (var row in (await RowsAsync(null, ct)).Where(r => r.State == "verified" && (r.CheckedAt is null || r.CheckedAt < due)))
        {
            var failure = await CheckRecordAsync(row, ct);
            // A resolver that cannot answer is not proof the record is gone; the domain keeps its route until the next check.
            if (failure is null or "lookup_failed")
            {
                await session.ExecAsync("update tenancy.tenant_domain set checked_at = @now, failure = @failure where id = @id",
                    new { id = row.Id, now = clock.UtcNow, failure }, ct);
                continue;
            }
            await session.ExecAsync("update tenancy.tenant_domain set state = 'failed', checked_at = @now, failure = @failure where id = @id",
                new { id = row.Id, now = clock.UtcNow, failure = "record_removed" }, ct);
            await RouteAsync(row.Host, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.TenantDomainFailed, "tenant_domain", row.Id.ToString(), Changes: new { host = row.Host, reason = "record_removed" }), ct);
            stopped.Add(row.Host);
        }
        return stopped;
    }

    /// <summary>Records what the certificate probe saw for a verified host and whether administrators should hear of it now.</summary>
    public async Task<bool> RecordCertificateAsync(string host, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = (await RowsAsync(null, ct)).SingleOrDefault(r => r.Host == host && r.State == "verified");
        if (row is null)
        {
            return false;
        }
        var alertedAt = await session.ScalarAsync<DateTime?>("select certificate_alerted_at from tenancy.tenant_domain where id = @id", new { id = row.Id }, ct);
        DateTimeOffset? alerted = alertedAt is { } a ? new DateTimeOffset(DateTime.SpecifyKind(a, DateTimeKind.Utc)) : null;
        var warn = expiresAt is null || expiresAt < now.AddDays(options.Value.CertificateWarningDays);
        var tell = warn && (alerted is null || alerted < now.AddDays(-1));
        await session.ExecAsync(
            "update tenancy.tenant_domain set certificate_expires_at = @expires, certificate_checked_at = @now, certificate_alerted_at = case when @tell then @now else certificate_alerted_at end where id = @id",
            new { id = row.Id, expires = expiresAt, now, tell }, ct);
        return tell;
    }

    public async Task<IReadOnlyList<string>> VerifiedHostsAsync(CancellationToken ct) =>
        (await RowsAsync(null, ct)).Where(r => r.State == "verified").Select(r => r.Host).ToList();

    /// <summary>The presentation of one of the current tenant's verified hosts in one language, the tenant default otherwise.</summary>
    public async Task<DomainPresentation?> PresentationAsync(string host, string locale, string fallback, CancellationToken ct)
    {
        var row = (await RowsAsync(null, ct)).SingleOrDefault(r => r.Host == host.ToLowerInvariant() && r.State == "verified");
        if (row is null)
        {
            return null;
        }
        string? Pick(Dictionary<string, string> texts) => texts.GetValueOrDefault(locale) ?? texts.GetValueOrDefault(fallback) ?? texts.Values.FirstOrDefault();
        return new DomainPresentation(row.Host, Pick(row.AppTitle), Pick(row.Description), Pick(row.SignInMessage));
    }

    private async Task<string?> CheckRecordAsync(Row row, CancellationToken ct)
    {
        var values = await dns.TxtAsync($"{RecordPrefix}.{row.Host}", ct);
        if (values is null)
        {
            return "lookup_failed";
        }
        return values.Any(v => v.Trim() == ValuePrefix + row.Token) ? null : "record_missing";
    }

    private async Task<bool> RouteAsync(string host, CancellationToken ct)
    {
        var routed = await session.ScalarAsync<bool>("select tenancy.sync_domain_route(@host)", new { host }, ct);
        resolver.Forget(host);
        return routed;
    }

    private bool IsInstallationHost(string host)
    {
        var h = hosts.Value;
        var baseDomain = h.BaseDomain.ToLowerInvariant();
        return host == baseDomain || host.EndsWith("." + baseDomain, StringComparison.Ordinal)
            || (h.DedicatedPlatformHost is { } platform && host == platform.ToLowerInvariant());
    }

    /// <summary>A host name in its ASCII form, or null when it is not one: two labels at least, letters, digits and hyphens.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(input.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        return ascii.Length <= 253 && HostPattern().IsMatch(ascii) ? ascii : null;
    }

    private static Dictionary<string, string> Texts(IReadOnlyDictionary<string, string>? input, int max, string field, ValidationErrors errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, text) in input ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (!Locales.IsSupported(locale) || text.Trim().Length > max || text.Any(char.IsControl))
            {
                errors.Add($"{field}.{locale}", "validation.tenant.domain_text");
                continue;
            }
            result[locale] = text.Trim();
        }
        return result;
    }

    private static DomainView View(Row r) => new(r.Id, r.Host, r.State, $"{RecordPrefix}.{r.Host}", ValuePrefix + r.Token, r.AppTitle, r.Description,
        r.SignInMessage, r.CreatedAt, r.VerifiedAt, r.CheckedAt, r.Failure, r.CertificateExpiresAt);

    private async Task<IReadOnlyList<Row>> RowsAsync(Guid? id, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, host, state, token, app_title, description, sign_in_message, created_at, verified_at, checked_at, failure, certificate_expires_at
            from tenancy.tenant_domain
            """ + (id is null ? " order by host" : " where id = @id"), id is { } i ? new { id = i } : null,
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), Read(r.GetString(4)),
                Read(r.GetString(5)), Read(r.GetString(6)), r.GetFieldValue<DateTimeOffset>(7),
                r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)), ct);

    private static Dictionary<string, string> Read(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options) ?? new Dictionary<string, string>();

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])$")]
    private static partial Regex HostPattern();
}
