using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Finance;

/// <summary>One hand-off as administrators see it on the finance connection.</summary>
public sealed record FinanceHandOffRow(
    Guid Id,
    string SourceType,
    Guid SourceId,
    string State,
    string? ExternalReference,
    string? PaymentStatus,
    string? ErrorClass,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CheckedAt);

/// <summary>
/// The finance hand-off contract (spec 017 FR-021, US7 scenario 2). A claim is sent once through the tenant's finance
/// connection (the oldest enabled one) as a durable connector job, keyed by the claim, so a retry or a second approval
/// never sends it twice. When the job finishes, the reference is stored; the payment status is then read every 6 hours
/// until it is paid or rejected, and each change is published as <see cref="FinanceHandOffChanged"/>.
/// </summary>
public sealed class FinanceHandOffs(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements, IOutbox outbox, IClock clock) : IFinanceHandOff
{
    public const string CorrelationPrefix = "finance-handoff:";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private const string Columns =
        "id, source_type, source_id, state, external_reference, payment_status, error_class, error_code, created_at, sent_at, checked_at";

    private static FinanceHandOffRow Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.Get<string>("external_reference"),
        r.Get<string>("payment_status"), r.Get<string>("error_class"), r.Get<string>("error_code"), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("sent_at"),
        r.Get<DateTimeOffset?>("checked_at"));

    private static FinanceHandOffView View(FinanceHandOffRow r) =>
        new(r.SourceType, r.SourceId, r.State, r.ExternalReference, r.PaymentStatus, r.ErrorClass, r.SentAt, r.CheckedAt);

    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return null;
        }
        return await session.ScalarAsync<Guid?>(
            "select id from integrations.connection where kind = @kind and enabled order by created_at, id limit 1", new { kind = FinancePack.KindName }, ct);
    }

    private Task<FinanceHandOffRow?> RowAsync(string sourceType, Guid sourceId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from integrations.finance_handoff where source_type = @sourceType and source_id = @sourceId",
            new { sourceType, sourceId }, Map, ct);

    public async Task<FinanceHandOffView?> GetAsync(string sourceType, Guid sourceId, CancellationToken ct = default) =>
        await RowAsync(sourceType, sourceId, ct) is { } row ? View(row) : null;

    public async Task<FinanceHandOffView> SubmitAsync(FinanceHandOffRequest request, CancellationToken ct = default)
    {
        Validate(request);
        if (await RowAsync(request.SourceType, request.SourceId, ct) is { } existing && existing.State != FinanceHandOffStates.Failed)
        {
            return View(existing);
        }
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return new FinanceHandOffView(request.SourceType, request.SourceId, FinanceHandOffStates.Unavailable, null, null, null, null, null);
        }
        var id = await session.ScalarAsync<Guid>(
            """
            insert into integrations.finance_handoff (tenant_id, id, connection_id, source_type, source_id, state, created_at)
            values (platform.current_tenant_id(), @id, @connection, @sourceType, @sourceId, 'queued', @now)
            on conflict (tenant_id, source_type, source_id) do update set connection_id = excluded.connection_id, state = 'queued',
              error_class = null, error_code = null, attempt = integrations.finance_handoff.attempt + 1
            returning id
            """, new { id = Ids.New(), connection, sourceType = request.SourceType, sourceId = request.SourceId, now = clock.UtcNow }, ct);
        var attempt = await session.ScalarAsync<int>("select attempt from integrations.finance_handoff where id = @id", new { id }, ct);
        var input = JsonSerializer.SerializeToElement(new
        {
            source_id = request.SourceId.ToString(),
            employee_number = request.EmployeeNumber,
            currency = request.Currency,
            amount = request.Amount,
            claim_date = request.ClaimDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            description = request.Description,
            cost_centre = request.CostCentre,
            project = request.Project,
            lines = request.Lines.Select(l => new
            {
                category = l.Category,
                date = l.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                amount = l.Amount,
                description = l.Description,
                original_amount = l.OriginalAmount,
                original_currency = l.OriginalCurrency,
            }),
        });
        // A resubmit after a refusal is a new request to finance, so it gets a new key.
        var key = $"finance:{request.SourceType}:{request.SourceId:N}:{attempt}";
        var result = await runtime.EnqueueAsync(new ConnectorCall(connection, FinancePack.Submit, input, key, $"{CorrelationPrefix}{id:N}:{attempt}"), ct);
        if (result.Outcome == ConnectorOutcomes.Refused)
        {
            await session.ExecAsync("update integrations.finance_handoff set state = 'failed', error_class = @e where id = @id", new { id, e = result.ErrorClass }, ct);
        }
        return View((await RowAsync(request.SourceType, request.SourceId, ct))!);
    }

    private static void Validate(FinanceHandOffRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.SourceType) || r.SourceType.Length > 50 || !r.SourceType.All(c => char.IsAsciiLetterLower(c) || c == '_'))
        {
            errors.Add("sourceType", "validation.finance.source_type");
        }
        if (string.IsNullOrWhiteSpace(r.EmployeeNumber) || r.EmployeeNumber.Length > 100)
        {
            errors.Add("employeeNumber", "validation.finance.employee_number");
        }
        if (r.Currency is not { Length: 3 } || !r.Currency.All(char.IsAsciiLetterUpper))
        {
            errors.Add("currency", "validation.finance.currency");
        }
        if (r.Amount <= 0 || r.Lines.Count is 0 or > 200 || r.Lines.Sum(l => l.Amount) != r.Amount || r.Lines.Any(l => l.Amount <= 0 || string.IsNullOrWhiteSpace(l.Category)))
        {
            errors.Add("lines", "validation.finance.lines");
        }
        errors.ThrowIfAny();
    }

    /// <summary>A submit job finished: store the reference, or why it failed, and tell the caller. An answer for an earlier attempt is ignored.</summary>
    public async Task OnCompletedAsync(ConnectorActionCompleted done, CancellationToken ct)
    {
        if (done.Action != FinancePack.Submit || done.CorrelationId is not { } correlation || !correlation.StartsWith(CorrelationPrefix, StringComparison.Ordinal)
            || correlation[CorrelationPrefix.Length..].Split(':') is not [var idText, var attemptText]
            || !Guid.TryParseExact(idText, "N", out var id) || !int.TryParse(attemptText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var attempt))
        {
            return;
        }
        var now = clock.UtcNow;
        var state = done.Outcome switch
        {
            ConnectorOutcomes.Succeeded => FinanceHandOffStates.Sent,
            ConnectorOutcomes.Unknown => FinanceHandOffStates.Unknown,
            _ => FinanceHandOffStates.Failed,
        };
        var row = await session.QuerySingleOrDefaultAsync(
            $"""
            update integrations.finance_handoff set state = @state, external_reference = @reference, error_class = @errorClass, error_code = @errorCode,
              payment_status = case when @state = 'sent' then coalesce(payment_status, 'submitted') else payment_status end,
              sent_at = case when @state = 'sent' then @now else sent_at end, next_check_at = case when @state = 'sent' then @next else null end
            where id = @id and attempt = @attempt and state in ('queued', 'unknown')
            returning {Columns}
            """, new { id, attempt, state, reference = done.ExternalReference, errorClass = done.ErrorClass, errorCode = done.ErrorCode, now, next = now + CheckEvery }, Map, ct);
        if (row is not null)
        {
            await outbox.EnqueueAsync(new FinanceHandOffChanged(row.SourceType, row.SourceId, row.State, row.ExternalReference, row.PaymentStatus), ct);
        }
    }

    /// <summary>Reads the payment status of sent hand-offs that are due, until they are paid or rejected (US7 scenario 2).</summary>
    public async Task<int> CheckDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select id, connection_id, external_reference, payment_status from integrations.finance_handoff
            where state = 'sent' and next_check_at <= @now and coalesce(payment_status, '') not in ('paid', 'rejected')
            order by next_check_at, id limit @limit for update skip locked
            """, new { now, limit }, r => (Id: r.GetGuid(0), Connection: r.GetGuid(1), Reference: r.GetString(2), Status: r.Get<string>("payment_status")), ct);
        foreach (var d in due)
        {
            var result = await runtime.InvokeAsync(new ConnectorCall(d.Connection, FinancePack.Status, JsonSerializer.SerializeToElement(new { reference = d.Reference }),
                $"finance-status:{d.Id:N}:{now.UtcTicks}", $"finance-status:{d.Id:N}"), ct);
            string? status = null;
            if (result.Outcome == ConnectorOutcomes.Succeeded && result.Output?.TryGetProperty("status", out var s) == true && s.GetString() is { } value
                && FinancePaymentStatuses.All.Contains(value))
            {
                status = value;
            }
            var row = await session.QuerySingleOrDefaultAsync(
                $"""
                update integrations.finance_handoff set checked_at = @now, next_check_at = @next, payment_status = coalesce(@status, payment_status)
                where id = @id
                returning {Columns}
                """, new { id = d.Id, now, next = now + CheckEvery, status }, Map, ct);
            if (row is not null && status is not null && status != d.Status)
            {
                await outbox.EnqueueAsync(new FinanceHandOffChanged(row.SourceType, row.SourceId, row.State, row.ExternalReference, row.PaymentStatus), ct);
            }
        }
        return due.Count;
    }

    public async Task<IReadOnlyList<FinanceHandOffRow>> ListAsync(Guid connectionId, CancellationToken ct) =>
        await session.QueryAsync($"select {Columns} from integrations.finance_handoff where connection_id = @connectionId order by created_at desc, id desc limit 100",
            new { connectionId }, Map, ct);
}
