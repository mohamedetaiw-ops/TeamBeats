using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Audit;

public static class AuditModule
{
    public static readonly MigrationSource Migrations = new("audit", typeof(AuditModule).Assembly);

    public const int MinimumRetentionDays = 365;

    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<IAuditStream, AuditStream>();
        services.AddScoped<IAuditExports, AuditExports>();
        services.AddScoped<AuditMaintenance>();
        services.AddMessageHandler<AuditExportRequested, AuditExportHandler>("audit.export");
        return services;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").RequireTenantUser().InAdminArea().WithTags("Audit").RequireRateLimiting("audit");

        group.MapGet("/events", async (string? actor, string? action, string? targetType, string? targetId,
            DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit, AuditQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.SearchAsync(new AuditFilter(ParseGuid(actor), action, targetType, targetId, from, to), cursor, limit ?? 50, ct)));

        group.MapPost("/exports", async (ExportRequest request, IAuthorizationService authz, IDbSession session,
            ITenantContext context, IAuditWriter audit, IOutbox outbox, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditExport, AuthzTarget.Tenant, ct);
            var format = request.Format?.ToLowerInvariant() ?? "csv";
            if (format is not ("csv" or "json"))
            {
                throw AppException.Validation("format", "validation.audit.export_format");
            }

            var id = Ids.New();
            var filter = new AuditFilter(request.Actor, request.Action, request.TargetType, null, request.From, request.To);
            await session.ExecAsync(
                "insert into audit.export_job (tenant_id, id, requested_by, format, filter, status) values (@t, @id, @by, @f, @filter, 'pending')",
                new { t = context.RequireTenant(), id, by = context.RequireUser(), f = format, filter = Jsonb.From(filter) }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.AuditExported, "audit_export", id.ToString(), Changes: new { format, filter }), ct);
            await outbox.EnqueueAsync(new AuditExportRequested(id), ct);
            await session.CommitAsync(ct);
            return Results.Accepted($"/api/v1/audit/exports/{id}", new { id, status = "pending" });
        }).RequireStepUp();

        group.MapGet("/exports/{id:guid}", async (Guid id, IAuthorizationService authz, AuditQueries queries, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditExport, AuthzTarget.Tenant, ct);
            return Results.Ok(await queries.GetExportAsync(id, ct) ?? throw AppException.NotFound());
        });

        group.MapGet("/exports/{id:guid}/file", async (Guid id, IAuthorizationService authz, AuditQueries queries,
            TenantObjectStorage storage, IClock clock, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditExport, AuthzTarget.Tenant, ct);
            var job = await queries.GetExportAsync(id, ct) ?? throw AppException.NotFound();
            if (job.Status != "completed" || job.ExpiresAt < clock.UtcNow)
            {
                throw AppException.NotFound();
            }
            var stream = await storage.OpenReadAsync(AuditExportHandler.RelativeKey(id, job.Format), ct) ?? throw AppException.NotFound();
            return Results.File(stream, job.Format == "csv" ? "text/csv" : "application/json", $"audit-{id}.{job.Format}");
        });

        group.MapGet("/retention", async (IAuthorizationService authz, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditView, AuthzTarget.Tenant, ct);
            var days = await session.ScalarAsync<int?>("select days from audit.retention", null, ct) ?? MinimumRetentionDays;
            return Results.Ok(new { days, minimumDays = MinimumRetentionDays });
        });

        group.MapPut("/retention", async (RetentionRequest request, IAuthorizationService authz, IDbSession session,
            ITenantContext context, IAuditWriter audit, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditRetentionManage, AuthzTarget.Tenant, ct);
            if (request.Days < MinimumRetentionDays)
            {
                throw AppException.Validation("days", "validation.audit.retention_below_minimum");
            }
            var previous = await session.ScalarAsync<int?>("select days from audit.retention", null, ct) ?? MinimumRetentionDays;
            await session.ExecAsync(
                """
                insert into audit.retention (tenant_id, days) values (@t, @d)
                on conflict (tenant_id) do update set days = excluded.days, updated_at = now()
                """, new { t = context.RequireTenant(), d = request.Days }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.AuditRetentionChanged, "audit_stream", context.TenantId.ToString(),
                Changes: new { from = previous, to = request.Days }), ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { days = request.Days, minimumDays = MinimumRetentionDays });
        });

        group.MapGet("/integrity", async (IAuthorizationService authz, AuditMaintenance maintenance, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.AuditView, AuthzTarget.Tenant, ct);
            var result = await maintenance.VerifyAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;
}

public sealed record ExportRequest(string? Format, Guid? Actor, string? Action, string? TargetType, DateTimeOffset? From, DateTimeOffset? To);

public sealed record RetentionRequest(int Days);

public sealed record AuditExportRequested(Guid JobId) : IIntegrationEvent;

public sealed record AuditFilter(Guid? Actor, string? Action, string? TargetType, string? TargetId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record AuditEventView(
    Guid Id, DateTimeOffset OccurredAt, Guid? ActorId, string ActorType, string Action, string TargetType, string? TargetId,
    Guid? TargetOrgUnitId, string Outcome, string? CorrelationId, JsonElement? Changes, long? ChainPosition);

public sealed record AuditPage(IReadOnlyList<AuditEventView> Items, string? NextCursor);

public sealed record ExportJobView(Guid Id, string Format, string Status, long? RowCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt);

public sealed record IntegrityResult(long Checked, long? FirstBreak, long SealedThrough, long Unsealed, bool Intact);

/// <summary>Scoped audit search (AUD-004): tenant via RLS, organization scope via the caller's coverage.</summary>
public sealed class AuditQueries(IDbSession session, IAuthorizationService authz)
{
    public async Task<AuditPage> SearchAsync(AuditFilter filter, string? cursor, int limit, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.AuditView, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }

        limit = Math.Clamp(limit, 1, 200);
        DateTimeOffset? cursorAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            cursorAt = DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture);
            cursorId = Guid.Parse(parts[1]);
        }

        var items = await session.QueryAsync(
            """
            select id, occurred_at, actor_id, actor_type, action, target_type, target_id, target_org_unit_id, outcome,
                   correlation_id, changes::text as changes, chain_pos
            from audit.audit_event
            where (@actor::uuid is null or actor_id = @actor)
              and (@action::text is null or action = @action)
              and (@targetType::text is null or target_type = @targetType)
              and (@targetId::text is null or target_id = @targetId)
              and (@from::timestamptz is null or occurred_at >= @from)
              and (@to::timestamptz is null or occurred_at < @to)
              and (@whole or target_org_unit_id = any(@units))
              and (@cursorAt::timestamptz is null or (occurred_at, id) < (@cursorAt, @cursorId))
            order by occurred_at desc, id desc
            limit @limit
            """,
            new
            {
                actor = filter.Actor,
                action = filter.Action,
                targetType = filter.TargetType,
                targetId = filter.TargetId,
                from = filter.From,
                to = filter.To,
                whole = coverage.WholeTenant,
                units = coverage.Units.ToArray(),
                cursorAt,
                cursorId,
                limit = limit + 1,
            },
            Map, ct);

        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.OccurredAt:O}|{last.Id}"));
        }
        return new AuditPage(items, next);
    }

    public static AuditEventView Map(Npgsql.NpgsqlDataReader r)
    {
        var changes = r.Get<string>("changes");
        return new AuditEventView(
            r.GetGuid(r.GetOrdinal("id")),
            r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("occurred_at")),
            r.Get<Guid?>("actor_id"),
            r.GetString(r.GetOrdinal("actor_type")),
            r.GetString(r.GetOrdinal("action")),
            r.GetString(r.GetOrdinal("target_type")),
            r.Get<string>("target_id"),
            r.Get<Guid?>("target_org_unit_id"),
            r.GetString(r.GetOrdinal("outcome")),
            r.Get<string>("correlation_id"),
            changes is null ? null : JsonDocument.Parse(changes).RootElement.Clone(),
            r.Get<long?>("chain_pos"));
    }

    public Task<ExportJobView?> GetExportAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            "select id, format, status, row_count, created_at, completed_at, expires_at from audit.export_job where id = @id",
            new { id },
            r => new ExportJobView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<long?>("row_count"),
                r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("completed_at"), r.Get<DateTimeOffset?>("expires_at")),
            ct);
}

/// <summary>Sealing, verification and retention purge for the current tenant.</summary>
public sealed class AuditMaintenance(IDbSession session, ITenantContext context, IAuditWriter audit, IObjectStorage storage, ILogger<AuditMaintenance> logger)
{
    /// <summary>Export files live 7 days (spec 006 section 8), then the file is deleted and the job keeps only metadata.</summary>
    public async Task<int> DeleteExpiredExportsAsync(CancellationToken ct)
    {
        var expired = await session.QueryAsync(
            "select id, object_key from audit.export_job where status = 'completed' and expires_at < now() and object_key is not null",
            null, r => (Id: r.GetGuid(0), Key: r.GetString(1)), ct);
        foreach (var (id, key) in expired)
        {
            await storage.DeleteAsync(key, ct);
            await session.ExecAsync("update audit.export_job set object_key = null where id = @id", new { id }, ct);
        }
        return expired.Count;
    }

    public async Task<int> SealAsync(int maxRows, CancellationToken ct) =>
        await session.ScalarAsync<int>("select audit.seal_pending(@n)", new { n = maxRows }, ct);

    public async Task<IntegrityResult> VerifyAsync(CancellationToken ct)
    {
        var (checkedRows, firstBreak, sealedThrough) = (await session.QueryAsync(
            "select checked, first_break, sealed_through from audit.verify_chain()", null,
            r => (r.GetInt64(0), r.Get<long?>("first_break"), r.GetInt64(2)), ct)).Single();
        var unsealed = await session.ScalarAsync<long>("select count(*) from audit.audit_event where chain_pos is null", null, ct);
        if (firstBreak is not null)
        {
            logger.LogError("Audit chain integrity failure for tenant {TenantId} at position {Position}", context.TenantId, firstBreak);
            await audit.WriteAsync(new AuditEntry(AuditActions.AuditIntegrityFailed, "audit_stream", context.TenantId.ToString(),
                AuditOutcome.Failure, Changes: new { position = firstBreak }), ct);
        }
        return new IntegrityResult(checkedRows, firstBreak, sealedThrough, unsealed, firstBreak is null);
    }

    public async Task<long> PurgeAsync(CancellationToken ct) =>
        await session.ScalarAsync<long>("select audit.purge_expired(@actor, @corr)",
            new { actor = context.ActorType == ActorType.User ? context.ActorId : null, corr = Correlation.Id }, ct);
}

/// <summary>Streams an export file to tenant-scoped storage (AUD-007). Runs in the worker with tenant context from the message.</summary>
public sealed class AuditExportHandler(IDbSession session, TenantObjectStorage storage, IClock clock) : IMessageHandler<AuditExportRequested>
{
    public static string RelativeKey(Guid jobId, string format) => $"exports/audit/{jobId}.{format}";

    public async Task HandleAsync(AuditExportRequested message, CancellationToken ct)
    {
        var job = await session.QuerySingleOrDefaultAsync(
            "select format, filter::text from audit.export_job where id = @id and status = 'pending'",
            new { id = message.JobId }, r => (Format: r.GetString(0), Filter: r.GetString(1)), ct);
        if (job == default)
        {
            return;
        }

        var filter = JsonSerializer.Deserialize<AuditFilter>(job.Filter, JsonDefaults.Options)!;
        var rows = await session.QueryAsync(
            """
            select id, occurred_at, actor_id, actor_type, action, target_type, target_id, target_org_unit_id, outcome,
                   correlation_id, changes::text as changes, chain_pos
            from audit.audit_event
            where (@actor::uuid is null or actor_id = @actor)
              and (@action::text is null or action = @action)
              and (@targetType::text is null or target_type = @targetType)
              and (@from::timestamptz is null or occurred_at >= @from)
              and (@to::timestamptz is null or occurred_at < @to)
            order by occurred_at, id
            """,
            new { actor = filter.Actor, action = filter.Action, targetType = filter.TargetType, from = filter.From, to = filter.To },
            AuditQueries.Map, ct);

        using var buffer = new MemoryStream();
        if (job.Format == "json")
        {
            await JsonSerializer.SerializeAsync(buffer, rows, JsonDefaults.Options, ct);
        }
        else
        {
            await using var writer = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync("id,occurred_at,actor_id,actor_type,action,target_type,target_id,target_org_unit_id,outcome,correlation_id,changes");
            foreach (var e in rows)
            {
                await writer.WriteLineAsync(string.Join(',',
                    e.Id, e.OccurredAt.ToString("O", CultureInfo.InvariantCulture), e.ActorId, Csv(e.ActorType), Csv(e.Action), Csv(e.TargetType),
                    Csv(e.TargetId), e.TargetOrgUnitId, Csv(e.Outcome), Csv(e.CorrelationId), Csv(e.Changes?.GetRawText())));
            }
        }

        buffer.Position = 0;
        await storage.PutAsync(RelativeKey(message.JobId, job.Format), buffer, job.Format == "csv" ? "text/csv" : "application/json", ct);
        await session.ExecAsync(
            """
            update audit.export_job set status = 'completed', row_count = @n, object_key = @key,
              completed_at = @now, expires_at = @expires where id = @id
            """,
            new { n = (long)rows.Count, key = storage.FullKey(RelativeKey(message.JobId, job.Format)), now = clock.UtcNow, expires = clock.UtcNow.AddDays(7), id = message.JobId },
            ct);
    }

    /// <summary>CSV escaping, with formula-injection protection for spreadsheet tools.</summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
