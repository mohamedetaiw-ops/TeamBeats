using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.SyncAgents;

/// <summary>A one-time enrolment code, shown once. Only its hash is kept.</summary>
public sealed record EnrolmentCodeView(string Code, DateTimeOffset ExpiresAt);

/// <summary>An enrolled agent as the administrator sees it. The public key thumbprint identifies it; nothing secret.</summary>
public sealed record SyncAgentView(Guid Id, string Name, string Thumbprint, DateTimeOffset EnrolledAt, DateTimeOffset? LastContactAt, string? Version,
    bool Online, DateTimeOffset? RevokedAt);

/// <summary>An agent job as the administrator sees it.</summary>
public sealed record AgentJobAdminView(Guid Id, string Kind, string Status, Guid? TakenBy, int Attempts, string? ErrorClass, int? Users, int? Groups,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record EnrolRequest(string? Code, string? Name, string? PublicKey, string? Version);

/// <summary>What the agent gets back at enrolment: its ID, which it names in every signed request.</summary>
public sealed record EnrolResponse(Guid AgentId, Guid ConnectionId);

public sealed record HeartbeatRequest(string? Version);

/// <summary>An agent whose signed request was accepted, bound to one connection of the tenant on the host.</summary>
public sealed record AuthenticatedAgent(Guid Id, Guid ConnectionId);

/// <summary>
/// On-premises sync agents (spec 017 US6, FR-016 to FR-018). An administrator makes a one-time code for an Active
/// Directory connection; the agent enrols with it from the customer network and sends the public half of a key pair it
/// made, which binds it to this tenant and this connection. After that every agent request is signed with the private key
/// and checked against the stored public key, so revoking the agent refuses its very next request. Jobs are pulled with
/// a lease, and only one agent works for a connection at a time. A connection whose agents have been silent for 5 minutes
/// is marked degraded and the owner is told (through <see cref="ConnectorUnavailable"/>).
/// </summary>
public sealed class SyncAgentService(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : IAgentDirectory
{
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 3;
    public const int MaxRecordsPerKind = 1000;
    public const string DirectorySync = "directory_sync";

    // ---------------------------------------------------------------- administrator side

    /// <summary>Makes a new one-time code for the connection. Older unused codes stop working.</summary>
    public async Task<EnrolmentCodeView> CreateEnrolmentCodeAsync(Guid connectionId, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        await RequireEntitlementAsync(ct);
        var now = clock.UtcNow;
        var code = NewCode();
        await session.ExecAsync("delete from integrations.agent_enrolment where connection_id = @connectionId and used_at is null", new { connectionId }, ct);
        await session.ExecAsync(
            """
            insert into integrations.agent_enrolment (tenant_id, id, connection_id, code_hash, expires_at, created_by, created_at)
            values (platform.current_tenant_id(), @id, @connectionId, @hash, @expires, @actor, @now)
            """, new { id = Ids.New(), connectionId, hash = Hash(code), expires = now + CodeLifetime, actor = context.ActorId, now }, ct);
        return new EnrolmentCodeView(code, now + CodeLifetime);
    }

    public async Task<IReadOnlyList<SyncAgentView>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        var since = clock.UtcNow - OfflineAfter;
        return await session.QueryAsync(
            """
            select id, name, thumbprint, enrolled_at, last_contact_at, version, revoked_at
            from integrations.sync_agent where connection_id = @connectionId order by enrolled_at
            """, new { connectionId },
            r =>
            {
                var last = r.Get<DateTimeOffset?>("last_contact_at");
                var revoked = r.Get<DateTimeOffset?>("revoked_at");
                return new SyncAgentView(r.Get<Guid>("id"), r.Get<string>("name")!, r.Get<string>("thumbprint")!, r.Get<DateTimeOffset>("enrolled_at"), last,
                    r.Get<string?>("version"), revoked is null && last > since, revoked);
            }, ct);
    }

    /// <summary>Revokes an agent. Its next request is refused, and a job it held goes back to the queue.</summary>
    public async Task RevokeAsync(Guid connectionId, Guid agentId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var revoked = await session.ExecAsync(
            "update integrations.sync_agent set revoked_at = @now, revoked_by = @actor where id = @agentId and connection_id = @connectionId and revoked_at is null",
            new { agentId, connectionId, now, actor = context.ActorId }, ct);
        if (revoked == 0)
        {
            var exists = await session.ScalarAsync<bool>("select exists (select 1 from integrations.sync_agent where id = @agentId and connection_id = @connectionId)",
                new { agentId, connectionId }, ct);
            if (!exists)
            {
                throw AppException.NotFound();
            }
            return;
        }
        await session.ExecAsync(
            "update integrations.agent_job set status = 'queued', taken_by = null, lease_until = null where taken_by = @agentId and status = 'taken'",
            new { agentId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentRevoked, "connection", connectionId.ToString(), Changes: new { agent = agentId }), ct);
    }

    /// <summary>Queues a directory read for the connection's agent. One job waits or runs at a time.</summary>
    public async Task<AgentJobAdminView> EnqueueAsync(Guid connectionId, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        await RequireEntitlementAsync(ct);
        var now = clock.UtcNow;
        var id = await TryEnqueueAsync(connectionId, ct) ?? throw AppException.Conflict("conflict.integrations.agent_job_open");
        return new AgentJobAdminView(id, DirectorySync, "queued", null, 0, null, null, null, now, null);
    }

    /// <summary>Queues a read from the cursors of the last succeeded job, or returns null when one waits or runs.</summary>
    private async Task<Guid?> TryEnqueueAsync(Guid connectionId, CancellationToken ct)
    {
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = LockKey(connectionId) }, ct);
        var open = await session.ScalarAsync<bool>(
            "select exists (select 1 from integrations.agent_job where connection_id = @connectionId and status in ('queued', 'taken'))", new { connectionId }, ct);
        if (open)
        {
            return null;
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.agent_job (tenant_id, id, connection_id, kind, status, payload, created_by, created_at)
            select platform.current_tenant_id(), @id, @connectionId, @kind, 'queued',
              coalesce((select jsonb_strip_nulls(jsonb_build_object('usersCursor', users_cursor, 'groupsCursor', groups_cursor))
                        from integrations.agent_job where connection_id = @connectionId and status = 'succeeded'
                        order by finished_at desc limit 1), '{}'::jsonb),
              @actor, @now
            """, new { id, connectionId, kind = DirectorySync, actor = context.ActorId, now = clock.UtcNow }, ct);
        return id;
    }

    /// <summary>Spec 003 asks for a directory read (see <see cref="IAgentDirectory"/>).</summary>
    public async Task<Guid?> RequestReadAsync(Guid connectionId, CancellationToken ct) =>
        await IsUsableAsync(connectionId, ct) ? await TryEnqueueAsync(connectionId, ct) : null;

    public async Task<bool> IsUsableAsync(Guid connectionId, CancellationToken ct) =>
        await session.ScalarAsync<bool>("select exists (select 1 from integrations.connection where id = @connectionId and kind = @kind and enabled)",
            new { connectionId, kind = OnPremDirectoryPack.KindName }, ct)
        && await EntitledAsync(ct);

    /// <summary>The records and next cursors of a succeeded job, in the canonical directory form.</summary>
    public async Task<DirectoryChanges?> ReadResultAsync(Guid jobId, CancellationToken ct)
    {
        var job = await session.QuerySingleOrDefaultAsync(
            "select users_cursor, groups_cursor from integrations.agent_job where id = @jobId and status = 'succeeded'",
            new { jobId }, r => (Users: r.Get<string?>("users_cursor"), Groups: r.Get<string?>("groups_cursor")), ct);
        if (job == default && !await session.ScalarAsync<bool>("select exists (select 1 from integrations.agent_job where id = @jobId and status = 'succeeded')", new { jobId }, ct))
        {
            return null;
        }
        var records = await session.QueryAsync(
            "select kind, record::text from integrations.agent_directory_record where job_id = @jobId order by kind, external_id",
            new { jobId }, r => (Kind: r.GetString(0), Json: r.GetString(1)), ct);
        var users = records.Where(r => r.Kind == "user").Select(r => JsonSerializer.Deserialize<DirectoryUserRecord>(r.Json, JsonDefaults.Options)!).ToList();
        var groups = records.Where(r => r.Kind == "group").Select(r => JsonSerializer.Deserialize<DirectoryGroupRecord>(r.Json, JsonDefaults.Options)!).ToList();
        return new DirectoryChanges(users, groups, job.Users ?? string.Empty, job.Groups ?? string.Empty);
    }

    public async Task<IReadOnlyList<AgentJobAdminView>> JobsAsync(Guid connectionId, CancellationToken ct)
    {
        await RequireConnectionAsync(connectionId, ct);
        return await session.QueryAsync(
            """
            select id, kind, status, taken_by, attempts, error_class, user_count, group_count, created_at, finished_at
            from integrations.agent_job where connection_id = @connectionId order by created_at desc limit 50
            """, new { connectionId },
            r => new AgentJobAdminView(r.Get<Guid>("id"), r.Get<string>("kind")!, r.Get<string>("status")!, r.Get<Guid?>("taken_by"), r.Get<int>("attempts"),
                r.Get<string?>("error_class"), r.Get<int?>("user_count"), r.Get<int?>("group_count"), r.Get<DateTimeOffset>("created_at"),
                r.Get<DateTimeOffset?>("finished_at")), ct);
    }

    /// <summary>
    /// Run by the connector worker on every tick: an enabled Active Directory connection with an enrolled agent that has
    /// not been in touch for 5 minutes becomes degraded and its owner is told; it is healthy again once an agent is back.
    /// Jobs whose lease ran out go back to the queue, or fail after <see cref="MaxAttempts"/>.
    /// </summary>
    public async Task RefreshAsync(DateTimeOffset now, CancellationToken ct)
    {
        var since = now - OfflineAfter;
        var degraded = await session.QueryAsync(
            """
            update integrations.connection c set status = 'degraded', updated_at = @now
            where c.kind = @kind and c.enabled and c.status = 'healthy'
              and exists (select 1 from integrations.sync_agent a where a.connection_id = c.id and a.revoked_at is null)
              and not exists (select 1 from integrations.sync_agent a where a.connection_id = c.id and a.revoked_at is null and a.last_contact_at > @since)
            returning c.id
            """, new { kind = OnPremDirectoryPack.KindName, now, since }, r => r.GetGuid(0), ct);
        foreach (var id in degraded)
        {
            await outbox.EnqueueAsync(new ConnectorUnavailable(id, ConnectionStatuses.Degraded), ct);
        }
        var restored = await session.QueryAsync(
            """
            update integrations.connection c set status = 'healthy', updated_at = @now
            where c.kind = @kind and c.enabled and c.status = 'degraded'
              and exists (select 1 from integrations.sync_agent a where a.connection_id = c.id and a.revoked_at is null and a.last_contact_at > @since)
            returning c.id
            """, new { kind = OnPremDirectoryPack.KindName, now, since }, r => r.GetGuid(0), ct);
        foreach (var id in restored)
        {
            await outbox.EnqueueAsync(new ConnectorRestored(id), ct);
        }

        var expired = await session.QueryAsync(
            """
            update integrations.agent_job set
              status = case when attempts >= @max then 'failed' else 'queued' end,
              error_class = case when attempts >= @max then @notResponding else error_class end,
              finished_at = case when attempts >= @max then @now else null end,
              taken_by = null, lease_until = null
            where status = 'taken' and lease_until < @now
            returning id, connection_id, kind, status
            """, new { max = MaxAttempts, notResponding = Runtime.ConnectorErrorClasses.NotResponding, now },
            r => (Id: r.GetGuid(0), Connection: r.GetGuid(1), Kind: r.GetString(2), Status: r.GetString(3)), ct);
        foreach (var job in expired.Where(j => j.Status == "failed"))
        {
            await outbox.EnqueueAsync(new AgentJobFinished(job.Id, job.Connection, job.Kind, false, Runtime.ConnectorErrorClasses.NotResponding, 0, 0), ct);
        }
    }

    // ---------------------------------------------------------------- agent side

    /// <summary>
    /// Enrols an agent with a one-time code. The code must be unused and not expired, and the public key must be an
    /// ECDSA P-256 key in SubjectPublicKeyInfo form (Base64). The code is spent whatever happens next.
    /// </summary>
    public async Task<EnrolResponse> EnrolAsync(EnrolRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
        {
            errors.Add("name", "validation.sync_agent.name");
        }
        if (request.Version is { } v && (v.Length > 40 || v.Any(char.IsControl)))
        {
            errors.Add("version", "validation.sync_agent.version");
        }
        var spki = PublicKey(request.PublicKey);
        if (spki is null)
        {
            errors.Add("publicKey", "validation.sync_agent.public_key");
        }
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100)
        {
            errors.Add("code", "validation.sync_agent.code");
        }
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        var connectionId = await session.ScalarAsync<Guid?>(
            """
            update integrations.agent_enrolment e set used_at = @now
            where e.code_hash = @hash and e.used_at is null and e.expires_at > @now
              and exists (select 1 from integrations.connection c where c.id = e.connection_id and c.kind = @kind)
            returning e.connection_id
            """, new { hash = Hash(request.Code!.Trim()), now, kind = OnPremDirectoryPack.KindName }, ct);
        if (connectionId is null || !await EntitledAsync(ct))
        {
            throw new AppException(ProblemCodes.Unauthenticated, 401, "integrations.agent.code_invalid");
        }
        var thumbprint = AgentProtocol.Thumbprint(spki!);
        var taken = await session.ScalarAsync<bool>("select exists (select 1 from integrations.sync_agent where thumbprint = @thumbprint)", new { thumbprint }, ct);
        if (taken)
        {
            throw AppException.Conflict("conflict.integrations.agent_key_in_use");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into integrations.sync_agent (tenant_id, id, connection_id, name, public_key, thumbprint, enrolled_at, last_contact_at, version)
            values (platform.current_tenant_id(), @id, @connectionId, @name, @key, @thumbprint, @now, @now, @version)
            """, new { id, connectionId, name, key = Convert.ToBase64String(spki!), thumbprint, now, version = request.Version }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentEnrolled, "connection", connectionId.Value.ToString(),
            Changes: new { agent = id, name, thumbprint }), ct);
        return new EnrolResponse(id, connectionId.Value);
    }

    /// <summary>
    /// Checks a signed agent request: the agent exists in this tenant and is not revoked, the package includes the
    /// on-premises agent, the timestamp is inside 5 minutes and not older than the newest one already accepted, and the
    /// signature verifies with the stored public key. Records the contact. Any failure is the same plain refusal.
    /// </summary>
    public async Task<AuthenticatedAgent> AuthenticateAsync(string? agentHeader, string? timestampHeader, string? signature, string method,
        string pathAndQuery, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var refused = new AppException(ProblemCodes.Unauthenticated, 401, "integrations.agent.signature_invalid");
        if (!Guid.TryParse(agentHeader, out var agentId)
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || string.IsNullOrEmpty(signature) || signature.Length > 200)
        {
            throw refused;
        }
        var now = clock.UtcNow;
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > AgentProtocol.Window.TotalSeconds)
        {
            throw refused;
        }
        var agent = await session.QuerySingleOrDefaultAsync(
            """
            select a.connection_id, a.public_key, a.last_request_ts from integrations.sync_agent a
            join integrations.connection c on c.id = a.connection_id
            where a.id = @agentId and a.revoked_at is null
            """, new { agentId }, r => (Connection: r.GetGuid(0), Key: r.GetString(1), Last: r.GetInt64(2)), ct);
        if (agent == default || timestamp < agent.Last || !await EntitledAsync(ct) || !Verify(agent.Key, signature, method, pathAndQuery, timestamp, body.Span))
        {
            throw refused;
        }
        await session.ExecAsync(
            "update integrations.sync_agent set last_contact_at = @now, last_request_ts = greatest(last_request_ts, @timestamp) where id = @agentId",
            new { agentId, now, timestamp }, ct);
        return new AuthenticatedAgent(agentId, agent.Connection);
    }

    public Task HeartbeatAsync(AuthenticatedAgent agent, HeartbeatRequest? request, CancellationToken ct)
    {
        var version = request?.Version is { Length: > 0 and <= 40 } v && !v.Any(char.IsControl) ? v : null;
        return version is null
            ? Task.CompletedTask
            : session.ExecAsync("update integrations.sync_agent set version = @version where id = @id", new { version, id = agent.Id }, ct);
    }

    /// <summary>
    /// Gives the agent the next job of its connection, or null. A job already held by another agent with a live lease
    /// blocks the connection, so one agent at a time does the work. The connection must be enabled.
    /// </summary>
    public async Task<AgentJobView?> PullAsync(AuthenticatedAgent agent, CancellationToken ct)
    {
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = LockKey(agent.ConnectionId) }, ct);
        var settings = await session.QuerySingleOrDefaultAsync("select settings::text from integrations.connection where id = @id and enabled",
            new { id = agent.ConnectionId }, r => r.GetString(0), ct);
        if (settings is null)
        {
            return null;
        }
        var now = clock.UtcNow;
        var held = await session.QuerySingleOrDefaultAsync(
            "select id, taken_by, kind, payload::text from integrations.agent_job where connection_id = @id and status = 'taken' and lease_until >= @now limit 1",
            new { id = agent.ConnectionId, now }, r => (Id: r.GetGuid(0), By: r.Get<Guid?>("taken_by"), Kind: r.GetString(2), Payload: r.GetString(3)), ct);
        if (held != default)
        {
            // The holder asking again gets the same job with a longer lease; anyone else waits.
            if (held.By != agent.Id)
            {
                return null;
            }
            await ExtendAsync(held.Id, now, ct);
            return View(held.Id, held.Kind, settings, held.Payload, now + Lease);
        }
        var job = await session.QuerySingleOrDefaultAsync(
            """
            update integrations.agent_job set status = 'taken', taken_by = @agent, lease_until = @lease, attempts = attempts + 1
            where id = (select id from integrations.agent_job where connection_id = @id
              and (status = 'queued' or (status = 'taken' and attempts < @max)) order by created_at limit 1)
            returning id, kind, payload::text
            """, new { id = agent.ConnectionId, agent = agent.Id, lease = now + Lease, max = MaxAttempts },
            r => (Id: r.GetGuid(0), Kind: r.GetString(1), Payload: r.GetString(2)), ct);
        return job == default ? null : View(job.Id, job.Kind, settings, job.Payload, now + Lease);
    }

    /// <summary>Stores a batch of directory records for a job the agent holds, and extends its lease.</summary>
    public async Task<int> RecordsAsync(AuthenticatedAgent agent, Guid jobId, AgentRecordBatch? batch, CancellationToken ct)
    {
        var users = batch?.Users ?? [];
        var groups = batch?.Groups ?? [];
        var errors = new ValidationErrors();
        if (users.Count > MaxRecordsPerKind || users.Any(u => u is null || !ValidId(u.ExternalId)))
        {
            errors.Add("users", "validation.sync_agent.records");
        }
        if (groups.Count > MaxRecordsPerKind || groups.Any(g => g is null || !ValidId(g.ExternalId)))
        {
            errors.Add("groups", "validation.sync_agent.records");
        }
        errors.ThrowIfAny();
        await RequireHeldAsync(agent, jobId, ct);
        foreach (var u in users)
        {
            await UpsertAsync(jobId, "user", u.ExternalId, Jsonb.From(u), ct);
        }
        foreach (var g in groups)
        {
            await UpsertAsync(jobId, "group", g.ExternalId, Jsonb.From(g), ct);
        }
        await ExtendAsync(jobId, clock.UtcNow, ct);
        return users.Count + groups.Count;
    }

    /// <summary>Ends a job the agent holds, with the counts of records it returned, and tells the module that reads them.</summary>
    public async Task CompleteAsync(AuthenticatedAgent agent, Guid jobId, AgentJobResult? result, CancellationToken ct)
    {
        var ok = result?.Ok ?? false;
        var errorClass = ok ? null : Known(result?.ErrorClass);
        await RequireHeldAsync(agent, jobId, ct);
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where kind = 'user'), count(*) filter (where kind = 'group')
            from integrations.agent_directory_record where job_id = @jobId
            """, new { jobId }, r => (Users: (int)r.GetInt64(0), Groups: (int)r.GetInt64(1)), ct);
        await session.ExecAsync(
            """
            update integrations.agent_job set status = @status, error_class = @errorClass, user_count = @users, group_count = @groups,
              users_cursor = @usersCursor, groups_cursor = @groupsCursor, finished_at = @now, lease_until = null
            where id = @jobId
            """, new { jobId, status = ok ? "succeeded" : "failed", errorClass, users = counts.Users, groups = counts.Groups,
                usersCursor = ok ? Cursor(result?.UsersCursor) : null, groupsCursor = ok ? Cursor(result?.GroupsCursor) : null, now = clock.UtcNow }, ct);
        await outbox.EnqueueAsync(new AgentJobFinished(jobId, agent.ConnectionId, DirectorySync, ok, errorClass, counts.Users, counts.Groups), ct);
    }

    // ---------------------------------------------------------------- helpers

    private static AgentJobView View(Guid id, string kind, string settings, string payload, DateTimeOffset leaseUntil)
    {
        var p = Parse(payload);
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new AgentJobView(id, kind, Parse(settings), leaseUntil, Text(p, "usersCursor"), Text(p, "groupsCursor"));
    }

    private static string? Cursor(string? value) => value is { Length: > 0 and <= 500 } && !value.Any(char.IsControl) ? value : null;

    private async Task<JsonElement> RequireConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        var settings = await session.QuerySingleOrDefaultAsync("select settings::text from integrations.connection where id = @connectionId and kind = @kind",
            new { connectionId, kind = OnPremDirectoryPack.KindName }, r => r.GetString(0), ct);
        return settings is null ? throw AppException.NotFound() : Parse(settings);
    }

    private async Task RequireEntitlementAsync(CancellationToken ct)
    {
        if (!await EntitledAsync(ct))
        {
            throw AppException.NotInPackage(Capabilities.IntegrationOnPremAgent);
        }
    }

    private async Task<bool> EntitledAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) && await entitlements.IsEnabledAsync(Capabilities.IntegrationOnPremAgent, ct);

    private async Task RequireHeldAsync(AuthenticatedAgent agent, Guid jobId, CancellationToken ct)
    {
        var held = await session.ScalarAsync<bool>(
            "select exists (select 1 from integrations.agent_job where id = @jobId and connection_id = @connection and taken_by = @agent and status = 'taken' and lease_until >= @now)",
            new { jobId, connection = agent.ConnectionId, agent = agent.Id, now = clock.UtcNow }, ct);
        if (!held)
        {
            throw AppException.Conflict("conflict.integrations.agent_job_not_held");
        }
    }

    private Task ExtendAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("update integrations.agent_job set lease_until = @lease where id = @jobId", new { jobId, lease = now + Lease }, ct);

    private Task UpsertAsync(Guid jobId, string kind, string externalId, Jsonb record, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into integrations.agent_directory_record (tenant_id, job_id, kind, external_id, record)
            values (platform.current_tenant_id(), @jobId, @kind, @externalId, @record)
            on conflict (tenant_id, job_id, kind, external_id) do update set record = excluded.record
            """, new { jobId, kind, externalId, record }, ct);

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 200 } && !id.Any(char.IsControl);

    private static string Known(string? errorClass) => errorClass switch
    {
        Runtime.ConnectorErrorClasses.CredentialRefused or Runtime.ConnectorErrorClasses.NotResponding or Runtime.ConnectorErrorClasses.ServerError
            or Runtime.ConnectorErrorClasses.InvalidResponse or Runtime.ConnectorErrorClasses.CredentialMissing => errorClass,
        _ => Runtime.ConnectorErrorClasses.Unexpected,
    };

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string LockKey(Guid connectionId) => $"integrations.agent_job:{connectionId:D}";

    private static string NewCode()
    {
        // 20 characters from an alphabet without look-alike letters, about 100 bits.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}-{new string(chars, 10, 5)}-{new string(chars, 15, 5)}";
    }

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));

    /// <summary>The key as SubjectPublicKeyInfo bytes when it is an ECDSA P-256 public key, otherwise null.</summary>
    private static byte[]? PublicKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 400)
        {
            return null;
        }
        try
        {
            var spki = Convert.FromBase64String(base64.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                return null;
            }
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static bool Verify(string storedKey, string signature, string method, string pathAndQuery, long timestamp, ReadOnlySpan<byte> body)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(storedKey), out _);
            var data = Encoding.UTF8.GetBytes(AgentProtocol.Canonical(method, pathAndQuery, timestamp, body));
            return key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
