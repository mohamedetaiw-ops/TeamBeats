using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>
/// Encrypts debug prompt logs (US8 scenario 2) with AES-GCM under the installation's AI log key
/// (<c>Ai:DebugLog:Key</c>, 32 bytes in base64, from the secret store). Tenant and window are bound as associated data,
/// so a row copied to another tenant or window does not decrypt. Without a key no debug window can be opened.
/// </summary>
public sealed class DebugLogProtector(IConfiguration configuration)
{
    public const string KeySetting = "Ai:DebugLog:Key";

    private byte[]? Key()
    {
        var value = configuration[KeySetting];
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool Configured => Key() is not null;

    private static byte[] Associated(Guid tenantId, Guid windowId) => [.. tenantId.ToByteArray(), .. windowId.ToByteArray()];

    public (byte[] Nonce, byte[] Ciphertext) Protect(string text, Guid tenantId, Guid windowId)
    {
        var key = Key() ?? throw new InvalidOperationException("AI debug log key is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Associated(tenantId, windowId));
        }
        CryptographicOperations.ZeroMemory(plain);
        return (nonce, [.. cipher, .. tag]);
    }

    public string? Unprotect(byte[] nonce, byte[] ciphertext, Guid tenantId, Guid windowId)
    {
        var key = Key();
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (key is null || ciphertext.Length < tagSize)
        {
            return null;
        }
        var plain = new byte[ciphertext.Length - tagSize];
        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain, Associated(tenantId, windowId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

public sealed record DebugWindowRequest(string? UseCase, int? Hours);

public sealed record DebugWindowView(Guid Id, string UseCase, Guid OpenedBy, DateTimeOffset OpenedAt, DateTimeOffset EndsAt, DateTimeOffset? ClosedAt, bool Open, long Entries);

public sealed record DebugLogEntry(Guid Id, DateTimeOffset At, string UseCase, string Classification, string Model, string? Text, string? CorrelationId);

/// <summary>
/// Debug windows (spec 013 US8, FR-010): the AI Policy Administrator opens one for a use case for 1 to 72 hours;
/// security officers (<c>audit.read</c> with <c>ai.policy_view</c>) read what it captured; entries older than 30 days are
/// deleted when new ones are written and never shown. Opening and closing are audited.
/// </summary>
public sealed class DebugWindows(IDbSession session, ITenantContext context, IAuthorizationService authz, DebugLogProtector protector, IAuditWriter audit, IClock clock)
{
    public const int MaxHours = 72;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<DebugWindowView>> ListAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed &&
            !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        return await ReadAsync("order by w.opened_at desc limit 50", null, ct);
    }

    private async Task<List<DebugWindowView>> ReadAsync(string tail, Guid? id, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await session.QueryAsync(
            $"""
            select w.id, w.use_case, w.opened_by, w.opened_at, w.ends_at, w.closed_at,
                   (select count(*) from ai.debug_log l where l.window_id = w.id and l.at >= @since)
            from ai.debug_window w {tail}
            """, new { since = now - KeepFor, id },
            r => new DebugWindowView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
                r.Get<DateTimeOffset?>("closed_at"), r.Get<DateTimeOffset?>("closed_at") is null && r.GetFieldValue<DateTimeOffset>(4) > now, r.GetInt64(6)), ct);
    }

    public async Task<DebugWindowView> OpenAsync(DebugWindowRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        if (request.UseCase is not { } useCase || !AiUseCases.All.Contains(useCase) || useCase == AiUseCases.Embed)
        {
            errors.Add("useCase", "validation.ai.use_case");
        }
        if (request.Hours is not (>= 1 and <= MaxHours))
        {
            errors.Add("hours", "validation.ai.debug_hours");
        }
        errors.ThrowIfAny();
        if (!protector.Configured)
        {
            throw AppException.Conflict("conflict.ai.debug_unavailable");
        }
        var now = clock.UtcNow;
        var open = await session.ScalarAsync<long>(
            "select count(*) from ai.debug_window where use_case = @useCase and closed_at is null and ends_at > @now", new { useCase = request.UseCase, now }, ct);
        if (open > 0)
        {
            throw AppException.Conflict("conflict.ai.debug_window_open");
        }
        var id = Ids.New();
        var me = context.RequireUser();
        var ends = now.AddHours(request.Hours!.Value);
        await session.ExecAsync(
            "insert into ai.debug_window (tenant_id, id, use_case, opened_by, opened_at, ends_at) values (platform.current_tenant_id(), @id, @useCase, @me, @now, @ends)",
            new { id, useCase = request.UseCase, me, now, ends }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiLoggingWindowOpened, "ai_debug_window", id.ToString(), Changes: new { useCase = request.UseCase, hours = request.Hours }), ct);
        return new DebugWindowView(id, request.UseCase!, me, now, ends, null, true, 0);
    }

    public async Task<DebugWindowView> CloseAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        var now = clock.UtcNow;
        var changed = await session.ExecAsync(
            "update ai.debug_window set closed_at = @now, closed_by = @me where id = @id and closed_at is null", new { id, now, me = context.RequireUser() }, ct);
        var view = await ReadAsync("where w.id = @id", id, ct);
        if (view.Count == 0)
        {
            throw AppException.NotFound();
        }
        if (changed > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AiLoggingWindowClosed, "ai_debug_window", id.ToString()), ct);
        }
        return view[0];
    }

    public async Task<IReadOnlyList<DebugLogEntry>> EntriesAsync(Guid id, CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.AuditView, AuthzTarget.Tenant, ct)).Allowed ||
            !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var tenant = context.RequireTenant();
        var exists = await session.ScalarAsync<long>("select count(*) from ai.debug_window where id = @id", new { id }, ct);
        if (exists == 0)
        {
            throw AppException.NotFound();
        }
        return await session.QueryAsync(
            "select id, at, use_case, classification, model, nonce, ciphertext, correlation_id from ai.debug_log where window_id = @id and at >= @since order by at desc limit 500",
            new { id, since = clock.UtcNow - KeepFor },
            r => new DebugLogEntry(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4),
                protector.Unprotect(r.GetFieldValue<byte[]>(5), r.GetFieldValue<byte[]>(6), tenant, id), r.Get<string>("correlation_id")), ct);
    }

    /// <summary>
    /// Stores one call in the open window for its use case, if any: Public and Internal only, never Restricted or
    /// Confidential (scenario 3). Runs inside the call record's own transaction. Also deletes entries past 30 days.
    /// </summary>
    internal static async Task CaptureAsync(IDbSession session, DebugLogProtector protector, Guid tenant, DateTimeOffset now, string useCase,
        DataClassification classification, string model, IReadOnlyList<AiMessage> messages, string answer, CancellationToken ct)
    {
        if (classification > DataClassification.Internal || !protector.Configured)
        {
            return;
        }
        var windows = await session.QueryAsync(
            "select id from ai.debug_window where use_case = @useCase and closed_at is null and ends_at > @now and opened_at <= @now limit 1",
            new { useCase, now }, r => r.GetGuid(0), ct);
        if (windows.Count == 0)
        {
            return;
        }
        var window = windows[0];
        var text = JsonSerializer.Serialize(new { messages = messages.Select(m => new { m.Role, m.Content }), answer }, JsonDefaults.Options);
        var (nonce, cipher) = protector.Protect(text, tenant, window);
        await session.ExecAsync(
            """
            insert into ai.debug_log (tenant_id, id, window_id, at, use_case, classification, model, nonce, ciphertext, correlation_id)
            values (platform.current_tenant_id(), @id, @window, @now, @useCase, @classification, @model, @nonce, @cipher, @correlation)
            """,
            new { id = Ids.New(), window, now, useCase, classification = Classifications.Key(classification), model, nonce, cipher, correlation = Correlation.Id }, ct);
        await session.ExecAsync("delete from ai.debug_log where at < @cutoff", new { cutoff = now - KeepFor }, ct);
    }
}
