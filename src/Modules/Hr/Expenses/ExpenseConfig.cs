using System.Security.Cryptography;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Expenses;

public sealed record ExpensePolicyView(ExpensePolicyDocument Policy, int Version, bool CanChange)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

/// <summary>
/// What a person needs to fill in a claim: the currency, the window, the categories, the cost centres and whether the
/// plan offers AI receipt reading (the reading itself still follows the tenant AI policy).
/// </summary>
public sealed record ExpenseFormView(string? Currency, int WindowDays, IReadOnlyList<ExpenseCategoryView> Categories, IReadOnlyList<string> CostCentres, bool ReceiptReading);

public sealed record ExpenseCategoryView(string Key, IReadOnlyDictionary<string, string> Names, string Kind, bool Receipt, decimal? Rate, decimal? Limit);

public sealed record ExpenseRate(string Currency, DateOnly Date, decimal Rate);

public sealed record ExpenseRatesRequest(List<ExpenseRate>? Rates);

public sealed record ExpenseReceiptView(Guid Id, string FileName, long SizeBytes, string ContentType, string ScanState);

/// <summary>
/// The expense policy, exchange rates and receipts (spec 022 FR-023). Finance officers of the whole tenant set the
/// policy and the rates; the rate source is the table finance keeps here (the smallest safe default; a rate feed
/// through spec 017 can fill the same table later). Receipts are stored under the tenant's object path with their
/// SHA-256, scanned before use and never sent to AI here.
/// </summary>
public sealed class ExpenseConfig(IDbSession session, ITenantContext context, IClock clock, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, TenantObjectStorage storage, IMalwareScanner scanner, IUserDirectory users)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxNameLength = 200;
    public const int MaxRates = 500;

    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/png", "image/jpeg" };

    internal static string ObjectKey(Guid id) => $"hr/expenses/receipts/{id:N}";

    internal async Task<(ExpensePolicyDocument Policy, int Version)> PolicyAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select policy::text, version from hr.expense_policy", null, r => (Policy: r.GetString(0), Version: r.GetInt32(1)), ct);
        return row == default
            ? (ExpenseRules.Default(), 0)
            : (JsonSerializer.Deserialize<ExpensePolicyDocument>(row.Policy, JsonDefaults.Options) ?? ExpenseRules.Default(), row.Version);
    }

    /// <summary>The form for the signed-in person, with each category's limit for their grade.</summary>
    public async Task<ExpenseFormView> FormAsync(string? grade, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var (policy, _) = await PolicyAsync(ct);
        return new ExpenseFormView(policy.Currency, policy.WindowDays,
            policy.Categories.Select(c => new ExpenseCategoryView(c.Key, c.Names, c.Kind, c.Receipt, c.Rate, ExpenseRules.LimitFor(c, grade))).ToList(), policy.CostCentres,
            await entitlements.IsEnabledAsync(Capabilities.AiReceiptReading, ct));
    }

    public async Task<ExpensePolicyView> PolicyViewAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var coverage = await authz.CoverageAsync(Permissions.FinanceProcess, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var (policy, version) = await PolicyAsync(ct);
        return new ExpensePolicyView(policy, version, coverage.WholeTenant);
    }

    public async Task<ExpensePolicyView> UpdatePolicyAsync(string? ifMatch, ExpensePolicyDocument? request, CancellationToken ct)
    {
        var current = await PolicyViewAsync(ct);
        if (!current.CanChange)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        foreach (var (field, key) in ExpenseRules.Validate(request))
        {
            errors.Add(field, key);
        }
        if (request?.Tiers is { } tiers && errors.Count == 0)
        {
            var approvers = tiers.SelectMany(t => t.Approvers).Distinct().ToList();
            var found = await users.GetManyAsync(approvers, ct);
            if (approvers.Any(a => !found.ContainsKey(a)))
            {
                errors.Add("tiers", "validation.expenses.tier_approver");
            }
        }
        errors.ThrowIfAny();
        if (ifMatch != current.ETag)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.expenses.changed", null, new Dictionary<string, object?> { ["etag"] = current.ETag });
        }
        var policy = ExpenseRules.Clean(request!);
        await session.ExecAsync(
            """
            insert into hr.expense_policy (tenant_id, policy, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @policy, 1, @actor, @now)
            on conflict (tenant_id) do update set policy = @policy, version = hr.expense_policy.version + 1, updated_by = @actor, updated_at = @now
            """, new { policy = Jsonb.From(policy), actor = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrExpensePolicyChanged, "expense_policy", "expenses",
            Changes: new { policy.Currency, policy.WindowDays, categories = policy.Categories.Select(c => c.Key), tiers = policy.Tiers.Select(t => t.Above) }), ct);
        return await PolicyViewAsync(ct);
    }

    // ---- rates ----

    public async Task<IReadOnlyList<ExpenseRate>> RatesAsync(CancellationToken ct)
    {
        await PolicyViewAsync(ct);
        return await session.QueryAsync("select currency, rate_date, rate from hr.expense_rate order by currency, rate_date desc limit 500", null,
            r => new ExpenseRate(r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetDecimal(2)), ct);
    }

    /// <summary>Adds or replaces rates (finance of the whole tenant). A rate applies from its date until the next one.</summary>
    public async Task<IReadOnlyList<ExpenseRate>> PutRatesAsync(ExpenseRatesRequest request, CancellationToken ct)
    {
        if (!(await PolicyViewAsync(ct)).CanChange)
        {
            throw AppException.Denied();
        }
        if (request.Rates is not { Count: > 0 and <= MaxRates } rates || rates.Any(r => r is null || !ExpenseRules.IsCurrency(r.Currency) || r.Rate is <= 0 or > 1_000_000m
                || decimal.Round(r.Rate, 8) != r.Rate)
            || rates.Select(r => (r.Currency, r.Date)).Distinct().Count() != rates.Count)
        {
            throw AppException.Validation("rates", "validation.expenses.rates");
        }
        var now = clock.UtcNow;
        var actor = context.RequireUser();
        foreach (var r in rates)
        {
            await session.ExecAsync(
                """
                insert into hr.expense_rate (tenant_id, currency, rate_date, rate, updated_by, updated_at)
                values (platform.current_tenant_id(), @currency, @date, @rate, @actor, @now)
                on conflict (tenant_id, currency, rate_date) do update set rate = @rate, updated_by = @actor, updated_at = @now
                """, new { currency = r.Currency, date = r.Date, rate = r.Rate, actor, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrExpenseRatesChanged, "expense_rates", "expenses",
            Changes: new { rates = rates.Select(r => new { r.Currency, r.Date, r.Rate }) }), ct);
        return await RatesAsync(ct);
    }

    /// <summary>The rate for a currency on a date: the latest one on or before it; 1 for the policy currency itself.</summary>
    internal async Task<decimal?> RateAsync(string currency, string policyCurrency, DateOnly date, CancellationToken ct) =>
        currency == policyCurrency
            ? 1m
            : await session.ScalarAsync<decimal?>(
                "select rate from hr.expense_rate where currency = @currency and rate_date <= @date order by rate_date desc limit 1", new { currency, date }, ct);

    // ---- receipts ----

    public async Task<ExpenseReceiptView> UploadAsync(string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrExpenses, ct);
        var user = context.RequireUser();
        var file = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var name = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation("name", "validation.expenses.receipt_name");
        }
        name = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        var type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (data.Length == 0 || !Allowed.Contains(type))
        {
            throw AppException.Validation("file", "validation.expenses.receipt_type");
        }
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.expenses.receipt_infected");
        }
        var id = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, type, ct);
        }
        var state = verdict == ScanVerdict.Clean ? "clean" : "failed";
        await session.ExecAsync(
            """
            insert into hr.expense_receipt (tenant_id, id, uploaded_by, file_name, size_bytes, content_type, sha256, scan_state, created_at)
            values (platform.current_tenant_id(), @id, @user, @name, @size, @type, @hash, @state, @now)
            """, new { id, user, name, size = data.LongLength, type, hash = Convert.ToHexStringLower(SHA256.HashData(data)), state, now = clock.UtcNow }, ct);
        return new ExpenseReceiptView(id, name, data.LongLength, type, state);
    }

    /// <summary>Why the caller cannot attach the receipt, or null: it must be their own, unattached and clean.</summary>
    internal async Task<string?> ProblemAsync(Guid id, CancellationToken ct)
    {
        var state = await session.ScalarAsync<string?>(
            "select scan_state from hr.expense_receipt where id = @id and uploaded_by = @user and claim_id is null", new { id, user = context.RequireUser() }, ct);
        return state switch
        {
            null => "validation.expenses.receipt_unknown",
            "clean" => null,
            _ => "validation.expenses.receipt_not_clean",
        };
    }

    internal Task LinkAsync(Guid id, Guid claimId, CancellationToken ct) =>
        session.ExecAsync("update hr.expense_receipt set claim_id = @claimId where id = @id and claim_id is null", new { id, claimId }, ct);

    /// <summary>Whether a receipt with the same file is on another live claim of the tenant (section 13: receipt hash).</summary>
    internal Task<bool> SameFileElsewhereAsync(Guid receiptId, CancellationToken ct) => session.ScalarAsync<bool>(
        """
        select exists (
          select 1 from hr.expense_receipt r
          join hr.expense_receipt other on other.sha256 = r.sha256 and other.id <> r.id and other.claim_id is not null
          join hr.expense_claim c on c.id = other.claim_id and c.status not in ('rejected', 'withdrawn')
          where r.id = @receiptId)
        """, new { receiptId }, ct);

    internal async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => ids.Count == 0
        ? new Dictionary<Guid, string>()
        : (await session.QueryAsync("select id, file_name from hr.expense_receipt where id = any(@ids)", new { ids = ids.ToArray() },
            r => (Id: r.GetGuid(0), Name: r.GetString(1)), ct)).ToDictionary(x => x.Id, x => x.Name);

    /// <summary>Opens a claim's receipt; the caller checked who may read the claim.</summary>
    internal async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid id, Guid claimId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select file_name, content_type from hr.expense_receipt where id = @id and claim_id = @claimId and scan_state = 'clean'",
            new { id, claimId }, r => (Name: r.GetString(0), Type: r.GetString(1)), ct);
        if (row.Name is null)
        {
            throw AppException.NotFound();
        }
        var content = await storage.OpenReadAsync(ObjectKey(id), ct) ?? throw AppException.NotFound();
        return (content, row.Type, row.Name);
    }
}
