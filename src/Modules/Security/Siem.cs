using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>
/// A SIEM destination as the security pages show it. <paramref name="Target"/> is the URL or <c>host:port</c>;
/// <paramref name="SecretRef"/> is a secret manager reference, never a secret.
/// </summary>
public sealed record SiemDestinationView(
    Guid Id, string Name, string Transport, string Target, string? SecretRef, bool Enabled, long Position, long StreamEnd, DateTimeOffset? LastSuccessAt,
    DateTimeOffset? FailingSince, string? LastError, int Version, DateTimeOffset CreatedAt);

/// <summary>
/// A new or changed destination. <paramref name="HistoryDays"/> (0 to 7, new destinations only) starts the stream that
/// many days back; by default it starts at the current end.
/// </summary>
public sealed record SiemDestinationBody(
    string? Name, string? Transport, string? Url, string? SecretRef, string? Host, int? Port, int? HistoryDays = null, int? Version = null);

/// <summary>The SIEM page: the destinations, and whether the caller may change them (every change is checked again).</summary>
public sealed record SiemPageView(IReadOnlyList<SiemDestinationView> Destinations, bool CanManage);

/// <summary>The answer of a test event (US4 scenario 3).</summary>
public sealed record SiemTestResult(bool Delivered, string? ErrorClass);

/// <summary>
/// SIEM destinations of the tenant (spec 026 US4, FR-014). Each one reads the tenant's audit stream from its own place,
/// so an outage holds events back without losing them, and they go out in order once it ends.
/// </summary>
public sealed class SiemService(
    IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit, ISiemDelivery delivery, IAuditStream stream,
    ITenantContext context, IClock clock)
{
    public const int MaxDestinations = 2;
    public const int MaxHistoryDays = 7;

    private async Task DemandAsync(string permission, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SecuritySiem, ct);
        await authz.DemandAsync(permission, AuthzTarget.Tenant, ct);
    }

    private async Task DemandViewAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SecuritySiem, ct);
        foreach (var permission in new[] { Permissions.SecurityManage, Permissions.SecurityView })
        {
            if ((await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                return;
            }
        }
        throw AppException.Denied();
    }

    private const string Columns =
        "id, name, transport, target, secret_ref, enabled, last_position, last_success_at, failing_since, last_error, version, created_at, connection_id";

    private sealed record Row(SiemDestinationView View, Guid ConnectionId);

    private static Row Read(Npgsql.NpgsqlDataReader r, long end) => new(new SiemDestinationView(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetInt64(6), end,
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10), r.GetFieldValue<DateTimeOffset>(11)), r.GetGuid(12));

    private async Task<Row?> FindAsync(Guid id, CancellationToken ct)
    {
        var end = await stream.EndAsync(ct);
        return await session.QuerySingleOrDefaultAsync($"select {Columns} from security.siem_destination where id = @id", new { id }, r => Read(r, end), ct);
    }

    public async Task<SiemPageView> ListAsync(CancellationToken ct)
    {
        await DemandViewAsync(ct);
        var end = await stream.EndAsync(ct);
        var destinations = (await session.QueryAsync($"select {Columns} from security.siem_destination order by created_at, id", null, r => Read(r, end), ct))
            .Select(r => r.View).ToList();
        return new SiemPageView(destinations, (await authz.CheckAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct)).Allowed);
    }

    private static (SiemDestinationRequest Request, string Target) Check(SiemDestinationBody body)
    {
        var errors = new ValidationErrors();
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
        {
            errors.Add("name", "validation.siem.name");
        }
        if (body.Transport is null || !SiemTransports.All.Contains(body.Transport))
        {
            errors.Add("transport", "validation.siem.transport");
        }
        if (body.HistoryDays is < 0 or > MaxHistoryDays)
        {
            errors.Add("historyDays", "validation.siem.history_days");
        }
        errors.ThrowIfAny();
        var https = body.Transport == SiemTransports.Https;
        var request = new SiemDestinationRequest(name!, body.Transport!, https ? body.Url?.Trim() : null, https ? body.SecretRef?.Trim() : null,
            https ? null : body.Host?.Trim().ToLowerInvariant(), https ? null : body.Port);
        return (request, https ? request.Url ?? "" : $"{request.Host}:{request.Port ?? 6514}");
    }

    public async Task<SiemDestinationView> CreateAsync(SiemDestinationBody body, CancellationToken ct)
    {
        await DemandAsync(Permissions.SecurityManage, ct);
        var (request, target) = Check(body);
        if (await session.ScalarAsync<long>("select count(*) from security.siem_destination", null, ct) >= MaxDestinations)
        {
            throw AppException.Conflict("conflict.siem.limit");
        }
        var connection = await delivery.CreateAsync(request, ct);
        var start = body.HistoryDays is { } days and > 0
            ? await stream.PositionBeforeAsync(clock.UtcNow.AddDays(-days), ct)
            : await stream.EndAsync(ct);
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into security.siem_destination (tenant_id, id, name, transport, target, secret_ref, connection_id, last_position)
            values (platform.current_tenant_id(), @id, @name, @transport, @target, @secretRef, @connection, @start)
            """, new { id, name = request.Name, transport = request.Transport, target, secretRef = request.SecretRef, connection, start }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySiemConfigured, "siem_destination", id.ToString(),
            Changes: new { change = "created", request.Name, request.Transport, target, request.SecretRef, historyDays = body.HistoryDays ?? 0 }), ct);
        return (await FindAsync(id, ct))!.View;
    }

    public async Task<SiemDestinationView> UpdateAsync(Guid id, SiemDestinationBody body, CancellationToken ct)
    {
        await DemandAsync(Permissions.SecurityManage, ct);
        var current = await FindAsync(id, ct) ?? throw AppException.NotFound();
        if (body.Version is { } version && version != current.View.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        var (request, target) = Check(body with { HistoryDays = null });
        if (request.Transport != current.View.Transport)
        {
            throw AppException.Validation("transport", "validation.siem.transport_fixed");
        }
        await delivery.UpdateAsync(current.ConnectionId, request, ct);
        await session.ExecAsync(
            """
            update security.siem_destination set name = @name, target = @target, secret_ref = @secretRef, version = version + 1, updated_at = now(),
              failing_since = null, failures = 0, next_attempt_at = null, last_error = null, alerted_at = null
            where id = @id
            """, new { id, name = request.Name, target, secretRef = request.SecretRef }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySiemConfigured, "siem_destination", id.ToString(),
            Changes: new
            {
                change = "updated",
                before = new { current.View.Name, current.View.Target, current.View.SecretRef },
                after = new { request.Name, Target = target, request.SecretRef },
            }), ct);
        return (await FindAsync(id, ct))!.View;
    }

    public async Task<SiemDestinationView> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await DemandAsync(Permissions.SecurityManage, ct);
        var current = await FindAsync(id, ct) ?? throw AppException.NotFound();
        await delivery.SetEnabledAsync(current.ConnectionId, enabled, ct);
        await session.ExecAsync(
            """
            update security.siem_destination set enabled = @enabled, version = version + 1, updated_at = now(),
              failing_since = null, failures = 0, next_attempt_at = null, last_error = null, alerted_at = null
            where id = @id
            """, new { id, enabled }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySiemConfigured, "siem_destination", id.ToString(),
            Changes: new { change = enabled ? "enabled" : "disabled", current.View.Name }), ct);
        return (await FindAsync(id, ct))!.View;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.SecurityManage, ct);
        var current = await FindAsync(id, ct) ?? throw AppException.NotFound();
        await delivery.DeleteAsync(current.ConnectionId, ct);
        await session.ExecAsync("delete from security.siem_destination where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySiemConfigured, "siem_destination", id.ToString(),
            Changes: new { change = "deleted", current.View.Name, current.View.Transport, current.View.Target }), ct);
    }

    /// <summary>Sends one <c>security.siem_test</c> event outside the stream and says whether it arrived.</summary>
    public async Task<SiemTestResult> TestAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.SecurityManage, ct);
        var current = await FindAsync(id, ct) ?? throw AppException.NotFound();
        var eventId = Ids.New();
        var test = JsonSerializer.SerializeToElement(new
        {
            id = eventId,
            tenant = context.RequireTenant(),
            time = clock.UtcNow,
            position = (long?)null,
            action = SiemEvents.TestAction,
            outcome = "success",
            actor = new { type = "user", id = context.ActorId },
            target = new { type = "siem_destination", id = id.ToString(), orgUnit = (Guid?)null },
            sourceIp = (string?)null,
            correlationId = (string?)null,
            changes = (object?)null,
        }, JsonDefaults.Options);
        var result = await delivery.SendAsync(current.ConnectionId, [test], "siem_test_" + eventId.ToString("N"), ct);
        return new SiemTestResult(result.Outcome == ConnectorOutcomes.Succeeded, result.Outcome == ConnectorOutcomes.Succeeded ? null : result.ErrorClass ?? result.Outcome);
    }
}

/// <summary>The event schema of the stream (documented in <c>docs/security/siem-schema.md</c>).</summary>
public static class SiemEvents
{
    public const string TestAction = "security.siem_test";

    /// <summary>Actor types in snake case, whatever form the audit row stored them in.</summary>
    public static string ActorTypeName(string stored) => stored.ToLowerInvariant() switch
    {
        "platformoperator" or "platform_operator" => "platform_operator",
        var other => other,
    };

    public static JsonElement From(Guid tenant, AuditStreamEvent e) => JsonSerializer.SerializeToElement(new
    {
        id = e.Id,
        tenant,
        time = e.OccurredAt,
        position = e.Position,
        action = e.Action,
        outcome = e.Outcome.ToLowerInvariant(),
        actor = new { type = ActorTypeName(e.ActorType), id = e.ActorId },
        target = new { type = e.TargetType, id = e.TargetId, orgUnit = e.TargetOrgUnitId },
        sourceIp = e.SourceIp,
        correlationId = e.CorrelationId,
        changes = e.Changes,
    }, JsonDefaults.Options);
}

/// <summary>
/// Sends each enabled destination what the stream holds after its place, one batch at a time (spec 026 US4 scenarios 1
/// and 2). A failed batch keeps the place and waits with backoff; the failure is audited at most once an hour, and after
/// 24 hours the security officers get an alert.
/// </summary>
public sealed class SiemStreamer(
    IDbSession session, IAuditStream stream, ISiemDelivery delivery, IAuditWriter audit, AlertService alerts, ITenantContext context, IClock clock)
{
    public const int BatchSize = 500;
    public const int MaxBatchesPerRun = 20;
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AlertAfter = TimeSpan.FromHours(24);

    private sealed record Due(Guid Id, Guid ConnectionId, string Name, long Position, int Failures, DateTimeOffset? FailingSince,
        DateTimeOffset? FailureAuditedAt, DateTimeOffset? AlertedAt);

    /// <summary>Runs the due destinations of the current tenant; returns how many events went out.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await session.QueryAsync(
            """
            select id, connection_id, name, last_position, failures, failing_since, failure_audited_at, alerted_at from security.siem_destination
            where enabled and (next_attempt_at is null or next_attempt_at <= @now) order by created_at, id
            """, new { now },
            r => new Due(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)), ct);
        var tenant = context.RequireTenant();
        var sent = 0;
        foreach (var destination in due)
        {
            var position = destination.Position;
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                var events = await stream.ReadAsync(position, BatchSize, ct);
                if (events.Count == 0)
                {
                    break;
                }
                var key = $"siem_{destination.Id:N}_{events[0].Position}_{events[^1].Position}";
                var result = await delivery.SendAsync(destination.ConnectionId, events.Select(e => SiemEvents.From(tenant, e)).ToList(), key, ct);
                if (result.Outcome != ConnectorOutcomes.Succeeded)
                {
                    await FailAsync(destination, result.ErrorClass ?? result.Outcome, now, ct);
                    break;
                }
                position = events[^1].Position;
                sent += events.Count;
                await session.ExecAsync(
                    """
                    update security.siem_destination set last_position = @position, last_success_at = @now, failing_since = null, failures = 0,
                      last_error = null, next_attempt_at = null, alerted_at = null
                    where id = @id
                    """, new { id = destination.Id, position, now }, ct);
                if (events.Count < BatchSize)
                {
                    break;
                }
            }
        }
        return sent;
    }

    private async Task FailAsync(Due destination, string errorClass, DateTimeOffset now, CancellationToken ct)
    {
        var failures = destination.Failures + 1;
        var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 10 * Math.Pow(2, Math.Min(failures - 1, 10))));
        var failingSince = destination.FailingSince ?? now;
        var auditNow = destination.FailureAuditedAt is not { } audited || now - audited >= TimeSpan.FromHours(1);
        var alertNow = destination.AlertedAt is null && now - failingSince >= AlertAfter;
        await session.ExecAsync(
            """
            update security.siem_destination set failures = @failures, failing_since = @failingSince, last_error = @errorClass,
              next_attempt_at = @next, failure_audited_at = case when @auditNow then @now else failure_audited_at end,
              alerted_at = case when @alertNow then @now else alerted_at end
            where id = @id
            """, new { id = destination.Id, failures, failingSince, errorClass = errorClass.Length > 100 ? errorClass[..100] : errorClass, next = now + backoff, auditNow, alertNow, now }, ct);
        if (auditNow)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.SecuritySiemDeliveryFailed, "siem_destination", destination.Id.ToString(), AuditOutcome.Failure,
                Changes: new { destination.Name, errorClass, failingSince }), ct);
        }
        if (alertNow)
        {
            await alerts.RaiseAsync(AlertKinds.SiemFailing, null, new { destinationId = destination.Id, name = destination.Name, errorClass, failingSince },
                $"{AlertKinds.SiemFailing}:{destination.Id}:{failingSince:O}", ct);
        }
    }
}

/// <summary>Streams every active tenant's audit events to its SIEM destinations every 10 seconds (spec 026 section 15: within 60 seconds).</summary>
public sealed class SiemStreamWorker(IServiceScopeFactory scopes, ILogger<SiemStreamWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Security:Siem:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SIEM streaming could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<SiemStreamer>().RunAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SIEM streaming failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
