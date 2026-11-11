using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Expenses;

public static class ExpenseStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string HandedOff = "handed_off";
    public const string Paid = "paid";
    public const string HandOffFailed = "handoff_failed";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";

    public static readonly IReadOnlyList<string> All = [Pending, Approved, HandedOff, Paid, HandOffFailed, Rejected, Withdrawn];
}

public sealed record ExpenseClaimInput(string? Title, string? CostCentre, string? Project, List<ExpenseLineInput>? Lines);

public sealed record ExpenseLineView(
    int N, string Category, string Kind, DateOnly Date, string? Merchant, string? Description, decimal Amount, string Currency, decimal? Tax, int? Days, decimal? Km,
    decimal Rate, decimal BaseAmount, decimal? Limit, bool OverLimit, string? Justification, Guid? ReceiptId, string? ReceiptName, bool Duplicate);

public sealed record ExpenseRights(bool Withdraw, bool Decide);

public sealed record ExpenseClaimView(
    Guid Id,
    string Reference,
    PersonRef Person,
    string Title,
    string? CostCentre,
    string? Project,
    string Currency,
    decimal Total,
    bool Duplicate,
    bool OverLimit,
    string Status,
    string? FinanceState,
    string? ExternalReference,
    string? PaymentStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    IReadOnlyList<ExpenseLineView> Lines,
    ExpenseRights Rights);

internal sealed record ExpenseClaimRow(
    Guid Id, string Reference, Guid UserId, Guid? OrgUnitId, string Title, string? CostCentre, string? Project, string Currency, decimal Total, bool Duplicate,
    bool OverLimit, string Status, string? FinanceState, string? ExternalReference, string? PaymentStatus, Guid? InstanceId, DateTimeOffset? DecidedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Expense claims (spec 022 US8, FR-023, FR-025). The person sends a claim of lines with receipts; each line is checked
/// against the policy window and the limit for their grade and category, and a line over its limit needs a
/// justification. Lines in another currency are converted at finance's rate on the receipt date and keep both amounts.
/// A line with the same merchant, date and amount as another live line of the person, or a receipt file already on
/// another live claim, is flagged for the approver, never refused. The claim runs on the process engine: the line
/// manager, an approver for each amount tier the total is above, then finance; nobody decides their own claim. Once
/// finance approves, the claim goes to the finance or ERP system through spec 017, and its reference and payment status
/// are shown.
/// </summary>
public sealed class ExpenseClaims(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IProcessEngine engine,
    INotificationSender notifications,
    IUserDirectory users,
    IHrRecords hrRecords,
    IFinanceHandOff finance,
    ExpenseConfig config,
    LeaveLedger people)
{
    public const string ProcessPrefix = "hr.expense.";
    public const string SourceType = "expense_claim";
    public const int MaxOpen = 20;

    public const string ApprovedTemplate = "hr.expense_approved";
    public const string RejectedTemplate = "hr.expense_rejected";
    public const string PaidTemplate = "hr.expense_paid";

    public static string Link(Guid id) => $"/expenses/{id}";

    internal static Guid? Parse(string businessKey) => Guid.TryParseExact(businessKey, "N", out var id) ? id : null;

    private const string Columns =
        "id, reference, user_id, org_unit_id, title, cost_centre, project, currency, total, duplicate, over_limit, status, finance_state, external_reference, payment_status, instance_id, decided_at, created_at";

    private static ExpenseClaimRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Get<Guid?>("org_unit_id"), r.GetString(4), r.Get<string?>("cost_centre"), r.Get<string?>("project"), r.GetString(7),
        r.GetDecimal(8), r.GetBoolean(9), r.GetBoolean(10), r.GetString(11), r.Get<string?>("finance_state"), r.Get<string?>("external_reference"),
        r.Get<string?>("payment_status"), r.Get<Guid?>("instance_id"), r.Get<DateTimeOffset?>("decided_at"), r.GetFieldValue<DateTimeOffset>(17));

    internal async Task<ExpenseClaimRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.expense_claim where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    private Task NotifyAsync(Guid to, string template, ExpenseClaimRow row, CancellationToken ct) =>
        notifications.SendAsync(new NotificationRequest([to], NotificationCategories.RequestUpdate, template, Link(row.Id),
            new Dictionary<string, string>
            {
                ["reference"] = row.Reference, ["total"] = row.Total.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = row.Currency,
            }, DedupeKey: $"hr.expense:{row.Id:N}:{template}"), ct);

    /// <summary>The form for the signed-in person: the policy, with each limit for their grade.</summary>
    public async Task<ExpenseFormView> FormAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var person = await people.PersonAsync(context.RequireUser(), ct);
        return await config.FormAsync(person?.Grade, ct);
    }

    // ---- sending ----

    public async Task<ExpenseClaimView> CreateAsync(ExpenseClaimInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var me = context.RequireUser();
        if (await people.PersonAsync(me, ct) is not { Active: true } person)
        {
            throw AppException.Denied();
        }
        var (policy, version) = await config.PolicyAsync(ct);
        if (policy.Currency is null)
        {
            throw AppException.Conflict("conflict.expenses.not_configured");
        }
        var errors = new List<(string Field, string Key)>();
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > ExpenseRules.MaxText || title.Any(char.IsControl))
        {
            errors.Add(("title", "validation.expenses.title"));
        }
        var costCentre = string.IsNullOrWhiteSpace(input.CostCentre) ? null : input.CostCentre.Trim();
        if (costCentre is not null && (policy.CostCentres.Count > 0 ? !policy.CostCentres.Contains(costCentre) : costCentre.Length > 50 || costCentre.Any(char.IsControl)))
        {
            errors.Add(("costCentre", "validation.expenses.cost_centre"));
        }
        var project = string.IsNullOrWhiteSpace(input.Project) ? null : input.Project.Trim();
        if (project is not null && (project.Length > 100 || project.Any(char.IsControl)))
        {
            errors.Add(("project", "validation.expenses.project"));
        }
        if (input.Lines is not { Count: > 0 and <= ExpenseRules.MaxLines } lines)
        {
            errors.Add(("lines", "validation.expenses.lines"));
            Throw(errors);
            return null!;
        }
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var priced = new List<(PricedLine Line, decimal Rate, decimal Base)>();
        var receipts = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var field = $"lines[{i}]";
            if (ExpenseRules.Check(policy, lines[i], person.Grade, today, field, errors) is not { } line)
            {
                continue;
            }
            if (line.ReceiptId is { } receipt)
            {
                if (!receipts.Add(receipt))
                {
                    errors.Add(($"{field}.receiptId", "validation.expenses.receipt_twice"));
                }
                else if (await config.ProblemAsync(receipt, ct) is { } problem)
                {
                    errors.Add(($"{field}.receiptId", problem));
                }
            }
            if (await config.RateAsync(line.Currency, policy.Currency, line.Date, ct) is not { } rate)
            {
                errors.Add(($"{field}.currency", "validation.expenses.no_rate"));
                continue;
            }
            var amount = ExpenseRules.ToPolicyCurrency(line.Amount, rate);
            if (amount <= 0)
            {
                errors.Add(($"{field}.amount", "validation.expenses.amount"));
                continue;
            }
            if (line.Limit is { } limit && amount > limit && line.Justification is null)
            {
                errors.Add(($"{field}.justification", "validation.expenses.justification_required"));
            }
            priced.Add((line, rate, amount));
        }
        Throw(errors);
        var total = priced.Sum(p => p.Base);
        if (total > ExpenseRules.MaxAmount)
        {
            throw AppException.Validation("lines", "validation.expenses.total");
        }

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.expense:' || @me::text))", new { me }, ct);
        if (await session.ScalarAsync<long>("select count(*) from hr.expense_claim where user_id = @me and status = 'pending'", new { me }, ct) >= MaxOpen)
        {
            throw AppException.Conflict("conflict.expenses.too_many");
        }
        var number = await session.ScalarAsync<int>(
            """
            insert into hr.expense_counter as c (tenant_id, next_number) values (platform.current_tenant_id(), 2)
            on conflict (tenant_id) do update set next_number = c.next_number + 1
            returning next_number - 1
            """, null, ct);
        var id = Ids.New();
        var reference = ExpenseRules.Reference(number);
        var now = clock.UtcNow;
        var overLimit = priced.Any(p => p.Line.Limit is { } l && p.Base > l);
        await session.ExecAsync(
            """
            insert into hr.expense_claim (tenant_id, id, reference, user_id, org_unit_id, title, cost_centre, project, currency, total, over_limit, status, policy_version,
              created_at, updated_at)
            values (platform.current_tenant_id(), @id, @reference, @me, @unit, @title, @costCentre, @project, @currency, @total, @overLimit, 'pending', @version, @now, @now)
            """, new { id, reference, me, unit = person.PrimaryUnit, title, costCentre, project, currency = policy.Currency, total, overLimit, version, now }, ct);
        for (var n = 0; n < priced.Count; n++)
        {
            var (line, rate, amount) = priced[n];
            await session.ExecAsync(
                """
                insert into hr.expense_line (tenant_id, claim_id, n, category, kind, expense_date, merchant, description, amount, currency, tax, days, km, rate, base_amount,
                  limit_amount, justification, receipt_id)
                values (platform.current_tenant_id(), @id, @n, @category, @kind, @date, @merchant, @description, @amount, @currency, @tax, @days, @km, @rate, @base, @limit,
                  @justification, @receipt)
                """, new
                {
                    id, n = n + 1, category = line.Category.Key, kind = line.Category.Kind, date = line.Date, merchant = line.Merchant, description = line.Description,
                    amount = line.Amount, currency = line.Currency, tax = line.Tax, days = line.Days, km = line.Km, rate, @base = amount, limit = line.Limit,
                    justification = line.Justification, receipt = line.ReceiptId,
                }, ct);
            if (line.ReceiptId is { } receipt)
            {
                await config.LinkAsync(receipt, id, ct);
            }
        }
        var duplicate = await FlagDuplicatesAsync(id, me, ct);
        var tiers = ExpenseRules.TiersFor(policy, total);
        var instance = await engine.StartAsync(await RouteAsync(tiers, ct), id.ToString("N"), person.PrimaryUnit, me, [me], me, ct: ct);
        await session.ExecAsync("update hr.expense_claim set instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrExpenseSubmitted, "expense_claim", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { reference, total, currency = policy.Currency, lines = priced.Count, duplicate, overLimit }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    private static void Throw(List<(string Field, string Key)> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }
        var all = new ValidationErrors();
        foreach (var (field, key) in errors)
        {
            all.Add(field, key);
        }
        all.ThrowIfAny();
    }

    /// <summary>
    /// Flags lines that match another live line of the person on merchant, date and amount, or whose receipt file is on
    /// another live claim (US8 scenario 4). Returns whether any line of the claim is flagged.
    /// </summary>
    private async Task<bool> FlagDuplicatesAsync(Guid claimId, Guid me, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            update hr.expense_line l set duplicate = true
            where l.claim_id = @claimId and (
              (l.merchant is not null and exists (
                select 1 from hr.expense_line o join hr.expense_claim c on c.id = o.claim_id
                where c.user_id = @me and c.status not in ('rejected', 'withdrawn') and not (o.claim_id = l.claim_id and o.n = l.n)
                  and lower(o.merchant) = lower(l.merchant) and o.expense_date = l.expense_date and o.amount = l.amount and o.currency = l.currency))
              or (l.receipt_id is not null and exists (
                select 1 from hr.expense_receipt r
                join hr.expense_receipt other on other.sha256 = r.sha256 and other.id <> r.id and other.claim_id is not null
                join hr.expense_claim c on c.id = other.claim_id and c.status not in ('rejected', 'withdrawn')
                where r.id = l.receipt_id)))
            """, new { claimId, me }, ct);
        var flagged = await session.ScalarAsync<bool>("select exists (select 1 from hr.expense_line where claim_id = @claimId and duplicate)", new { claimId }, ct);
        await session.ExecAsync("update hr.expense_claim set duplicate = @flagged where id = @claimId", new { claimId, flagged }, ct);
        return flagged;
    }

    /// <summary>
    /// The route for a claim: the line manager (finance when there is none, or the manager is the claimant), one step
    /// per amount tier the total is above with that tier's approvers, then finance covering the person's unit.
    /// </summary>
    private async Task<string> RouteAsync(IReadOnlyList<ExpenseTier> tiers, CancellationToken ct)
    {
        var key = ExpenseRules.RouteKey(tiers);
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            var steps = new List<RouteStep> { new("manager", "Line manager", new CandidateRule(Permission: Permissions.FinanceProcess, LineManager: true)) };
            steps.AddRange(tiers.Select((t, i) => new RouteStep($"tier{i + 1}", $"Amount above {t.Above.ToString("0.##", CultureInfo.InvariantCulture)}",
                new CandidateRule(Users: t.Approvers))));
            steps.Add(new RouteStep("finance", "Finance", new CandidateRule(Permission: Permissions.FinanceProcess)));
            await engine.PublishRouteAsync(key, RouteSources.ExpenseRoute, new RouteSpec("expense approval", steps), ct);
        }
        return key;
    }

    // ---- reading ----

    public async Task<IReadOnlyList<ExpenseClaimView>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var rows = await session.QueryAsync($"select {Columns} from hr.expense_claim where user_id = @me order by created_at desc limit 100",
            new { me = context.RequireUser() }, Read, ct);
        return await ViewsAsync(rows, ct);
    }

    /// <summary>The claims of the people the finance officer covers, newest first.</summary>
    public async Task<IReadOnlyList<ExpenseClaimView>> ListAsync(string? status, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var coverage = await authz.CoverageAsync(Permissions.FinanceProcess, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        if (status is not null && !ExpenseStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.expenses.status");
        }
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.expense_claim where (@status::text is null or status = @status) order by created_at desc limit 500", new { status }, Read, ct);
        return await ViewsAsync(rows.Where(r => r.OrgUnitId is null ? coverage.WholeTenant : coverage.Covers(r.OrgUnitId)).Take(200).ToList(), ct);
    }

    private async Task<IReadOnlyList<ExpenseClaimView>> ViewsAsync(IReadOnlyList<ExpenseClaimRow> rows, CancellationToken ct)
    {
        var list = new List<ExpenseClaimView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    public async Task<ExpenseClaimView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        if (await RowAsync(id, false, ct) is not { } row || !await MayReadAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    /// <summary>The person, the holder of its approval task and finance officers covering the person.</summary>
    private async Task<bool> MayReadAsync(ExpenseClaimRow row, CancellationToken ct) =>
        row.UserId == context.RequireUser() || await DecidingTaskAsync(row, ct) is not null || await FinanceCoversAsync(row, ct);

    private async Task<bool> FinanceCoversAsync(ExpenseClaimRow row, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.FinanceProcess, ct);
        return coverage.WholeTenant || (row.OrgUnitId is { } unit && coverage.Covers(unit));
    }

    internal async Task<UserTaskInfo?> DecidingTaskAsync(ExpenseClaimRow row, CancellationToken ct)
    {
        if (row.InstanceId is not { } instance || row.Status != ExpenseStatuses.Pending || row.UserId == context.RequireUser())
        {
            return null;
        }
        foreach (var task in (await engine.InstanceTasksAsync(instance, ct)).Where(t => t.Status == "open"))
        {
            if (await engine.MyTaskAsync(task.Id, ct) is { } mine)
            {
                return mine;
            }
        }
        return null;
    }

    public async Task<(Stream Content, string ContentType, string FileName)> ReceiptAsync(Guid id, Guid receiptId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        if (await RowAsync(id, false, ct) is not { } row || !await MayReadAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return await config.OpenAsync(receiptId, id, ct);
    }

    public async Task<ExpenseClaimView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        if (await RowAsync(id, true, ct) is not { } row || row.UserId != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (row.Status != ExpenseStatuses.Pending)
        {
            throw AppException.Conflict("conflict.expenses.not_pending");
        }
        if (row.InstanceId is { } instance)
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await session.ExecAsync("update hr.expense_claim set status = 'withdrawn', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    // ---- deciding and paying ----

    /// <summary>The route ended: a refused claim ends; an approved one goes to finance.</summary>
    internal async Task DecidedAsync(ExpenseClaimRow row, ProcessInstanceInfo instance, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (instance.Outcome != InstanceOutcomes.Approved)
        {
            await session.ExecAsync("update hr.expense_claim set status = 'rejected', decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
                new { id = row.Id, decider, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrExpenseRejected, "expense_claim", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Reference, row.Total, row.Currency, approver = decider, outcome = instance.Outcome }), ct);
            await NotifyAsync(row.UserId, RejectedTemplate, row, ct);
            return;
        }
        await session.ExecAsync("update hr.expense_claim set status = 'approved', decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
            new { id = row.Id, decider, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrExpenseApproved, "expense_claim", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Reference, row.Total, row.Currency, approver = decider }), ct);
        await outbox.EnqueueAsync(new ExpenseApproved(row.Id, row.UserId, row.Reference, row.Total, row.Currency), ct);
        await NotifyAsync(row.UserId, ApprovedTemplate, row, ct);
        await HandOffAsync(row, ct);
    }

    /// <summary>Sends an approved claim to finance (FR-025). Without a finance connection or an employee number, finance pays it outside TeamBeats.</summary>
    private async Task HandOffAsync(ExpenseClaimRow row, CancellationToken ct)
    {
        var number = (await hrRecords.MatchedAsync([row.UserId], ct)).FirstOrDefault()?.Fields.GetValueOrDefault("employee_number");
        if (string.IsNullOrWhiteSpace(number))
        {
            await session.ExecAsync("update hr.expense_claim set finance_state = 'unavailable' where id = @id", new { id = row.Id }, ct);
            return;
        }
        var lines = await session.QueryAsync(
            "select category, expense_date, base_amount, description, amount, currency from hr.expense_line where claim_id = @id order by n", new { id = row.Id },
            r => new FinanceLine(r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetDecimal(2), r.Get<string?>("description"),
                r.GetString(5) == row.Currency ? null : r.GetDecimal(4), r.GetString(5) == row.Currency ? null : r.GetString(5)), ct);
        var view = await finance.SubmitAsync(new FinanceHandOffRequest(SourceType, row.Id, number, row.Currency, row.Total,
            DateOnly.FromDateTime(row.CreatedAt.UtcDateTime), row.Title, row.CostCentre, row.Project, lines), ct);
        await ApplyAsync(row.Id, view.State, view.ExternalReference, view.PaymentStatus, ct);
    }

    /// <summary>Finance answered (spec 017): store the state, the reference and the payment status.</summary>
    internal async Task HandOffChangedAsync(FinanceHandOffChanged message, CancellationToken ct)
    {
        if (message.SourceType != SourceType)
        {
            return;
        }
        await ApplyAsync(message.SourceId, message.State, message.ExternalReference, message.PaymentStatus, ct);
    }

    private async Task ApplyAsync(Guid id, string state, string? reference, string? payment, CancellationToken ct)
    {
        if (await RowAsync(id, true, ct) is not { Status: ExpenseStatuses.Approved or ExpenseStatuses.HandedOff or ExpenseStatuses.HandOffFailed } row)
        {
            return;
        }
        var status = state switch
        {
            FinanceHandOffStates.Failed => ExpenseStatuses.HandOffFailed,
            FinanceHandOffStates.Sent when payment == "paid" => ExpenseStatuses.Paid,
            FinanceHandOffStates.Sent => ExpenseStatuses.HandedOff,
            _ => row.Status,
        };
        await session.ExecAsync(
            """
            update hr.expense_claim set status = @status, finance_state = @state, external_reference = coalesce(@reference, external_reference),
              payment_status = coalesce(@payment, payment_status), updated_at = @now
            where id = @id
            """, new { id, status, state, reference, payment, now = clock.UtcNow }, ct);
        if (row.Status != ExpenseStatuses.HandedOff && row.Status != ExpenseStatuses.Paid && status is ExpenseStatuses.HandedOff or ExpenseStatuses.Paid)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrExpenseHandedOff, "expense_claim", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Reference, row.Total, row.Currency, reference }), ct);
        }
        if (status == ExpenseStatuses.Paid && row.Status != ExpenseStatuses.Paid)
        {
            await NotifyAsync(row.UserId, PaidTemplate, row, ct);
        }
    }

    private async Task<ExpenseClaimView> ViewAsync(ExpenseClaimRow row, CancellationToken ct)
    {
        var name = (await users.GetAsync(row.UserId, ct))?.DisplayName ?? row.UserId.ToString();
        var me = context.RequireUser();
        var lines = await session.QueryAsync(
            """
            select n, category, kind, expense_date, merchant, description, amount, currency, tax, days, km, rate, base_amount, limit_amount, justification, receipt_id, duplicate
            from hr.expense_line where claim_id = @id order by n
            """, new { id = row.Id }, r => (
                N: r.GetInt32(0), Category: r.GetString(1), Kind: r.GetString(2), Date: r.GetFieldValue<DateOnly>(3), Merchant: r.Get<string?>("merchant"),
                Description: r.Get<string?>("description"), Amount: r.GetDecimal(6), Currency: r.GetString(7), Tax: r.Get<decimal?>("tax"), Days: r.Get<int?>("days"),
                Km: r.Get<decimal?>("km"), Rate: r.GetDecimal(11), Base: r.GetDecimal(12), Limit: r.Get<decimal?>("limit_amount"), Justification: r.Get<string?>("justification"),
                Receipt: r.Get<Guid?>("receipt_id"), Duplicate: r.GetBoolean(16)), ct);
        var names = await config.NamesAsync(lines.Where(l => l.Receipt is not null).Select(l => l.Receipt!.Value).ToList(), ct);
        var views = lines.Select(l => new ExpenseLineView(l.N, l.Category, l.Kind, l.Date, l.Merchant, l.Description, l.Amount, l.Currency, l.Tax, l.Days, l.Km, l.Rate,
            l.Base, l.Limit, l.Limit is { } limit && l.Base > limit, l.Justification, l.Receipt, l.Receipt is { } rid ? names.GetValueOrDefault(rid) : null, l.Duplicate)).ToList();
        var rights = new ExpenseRights(row.UserId == me && row.Status == ExpenseStatuses.Pending, await DecidingTaskAsync(row, ct) is not null);
        return new ExpenseClaimView(row.Id, row.Reference, new PersonRef(row.UserId, name), row.Title, row.CostCentre, row.Project, row.Currency, row.Total, row.Duplicate,
            row.OverLimit, row.Status, row.FinanceState, row.ExternalReference, row.PaymentStatus, row.CreatedAt, row.DecidedAt, views, rights);
    }
}

/// <summary>
/// Expense claims in the spec 016 task inbox (FR-026). Each step of the route is a task; nobody decides their own claim,
/// directly or as a delegate (section 6). Finance hand-off changes come back here as well.
/// </summary>
public sealed class ExpenseTaskSource(ITenantContext context, IProcessEngine engine, ExpenseClaims claims, ILocalizer localizer)
    : ITaskSource, IMessageHandler<FinanceHandOffChanged>
{
    public bool Handles(string definitionKey) => definitionKey.StartsWith(ExpenseClaims.ProcessPrefix, StringComparison.Ordinal);

    private async Task<ExpenseClaimRow?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct) =>
        ExpenseClaims.Parse(task.BusinessKey) is { } id && await claims.RowAsync(id, forUpdate, ct) is { Status: ExpenseStatuses.Pending } row
            && row.InstanceId == task.InstanceId
            ? row
            : null;

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not { } row)
        {
            return null;
        }
        var total = row.Total.ToString("0.00", CultureInfo.InvariantCulture);
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get("hr.expenses.task", l)
            .Replace("{reference}", row.Reference, StringComparison.Ordinal)
            .Replace("{total}", total, StringComparison.Ordinal)
            .Replace("{currency}", row.Currency, StringComparison.Ordinal));
        return new TaskSubject("expense", title, ExpenseClaims.Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.UserId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is { } row && authority != row.UserId;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not { } row || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        await claims.DecidedAsync(row, instance, done.CompletedBy ?? context.RequireUser(), ct);
    }

    public Task HandleAsync(FinanceHandOffChanged message, CancellationToken ct) => claims.HandOffChangedAsync(message, ct);
}
