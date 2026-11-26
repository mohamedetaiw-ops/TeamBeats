using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Finance;

/// <summary>
/// Settings of a named ERP finance pack (spec 017 R3, INT-005 and INT-006). The address and sign-in are the system's own;
/// the rest is what the ERP needs that the canonical claim does not carry. Oracle Fusion uses
/// <paramref name="BusinessUnit"/>, <paramref name="SupplierSite"/> and <paramref name="InvoiceGroup"/>; SAP S/4HANA uses
/// <paramref name="CompanyCode"/>, <paramref name="Client"/> and <paramref name="TaxCode"/>. <paramref name="Accounts"/>
/// maps an expense category to the account its line is posted to (an Oracle distribution combination or an SAP G/L
/// account), with <paramref name="DefaultAccount"/> for any other category. <paramref name="StatusValues"/> overrides the
/// pack's words for each payment status.
/// </summary>
public sealed record ErpFinanceSettings(
    string? BaseUrl,
    RestAuth? Auth,
    string? BusinessUnit = null,
    string? SupplierSite = null,
    string? InvoiceGroup = null,
    string? CompanyCode = null,
    string? Client = null,
    string? TaxCode = null,
    IReadOnlyDictionary<string, string>? Accounts = null,
    string? DefaultAccount = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StatusValues = null)
{
    public RestSettings Rest => new(BaseUrl!, Auth!);
}

/// <summary>
/// What the named ERP finance packs share. Each implements the finance hand-off contract (FR-021) with the same two
/// actions as the generic pack, against the ERP's published API, so the hand-off of spec 022 expenses does not change.
/// The claim goes in as a supplier invoice for the employee's supplier record, one line per expense line. A retried
/// submit first looks for the invoice the earlier attempt may have created, so the submit is idempotent. Behind the
/// pack's own capability key from the spec 005 catalogue.
/// </summary>
public abstract partial class ErpFinancePack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : IConnectorPack
{
    public abstract string Kind { get; }

    public int Version => 1;

    public abstract string CapabilityKey { get; }

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(FinancePack.Submit, ConnectorSideEffects.Write, true, [FinancePack.InvalidRequest]),
        new(FinancePack.Status, ConnectorSideEffects.Read, true, [FinancePack.NotFound]),
    ];

    protected static readonly IReadOnlyDictionary<int, string> SubmitErrors = new Dictionary<int, string> { [400] = FinancePack.InvalidRequest, [409] = FinancePack.InvalidRequest, [422] = FinancePack.InvalidRequest };
    protected static readonly IReadOnlyDictionary<int, string> StatusErrors = new Dictionary<int, string> { [404] = FinancePack.NotFound };

    private static readonly string[] AuthTypes = ["basic", "oauth_client_credentials", "bearer"];

    /// <summary>The pack's own words for each payment status, used when the settings do not override them.</summary>
    protected abstract IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultStatusValues { get; }

    /// <summary>A cheap read that proves the address and credential work, for Test connection.</summary>
    protected abstract string ProbePath(ErpFinanceSettings s);

    protected abstract void Validate(ErpFinanceSettings s, ValidationErrors errors);

    protected abstract ErpFinanceSettings Stored(ErpFinanceSettings s, RestSettings rest, IReadOnlyDictionary<string, string> accounts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? statusValues);

    protected abstract Task<PackCallResult> SubmitAsync(PackConnection connection, ErpFinanceSettings s, ErpClaim claim, bool retry, CancellationToken ct);

    protected abstract Task<PackCallResult> StatusAsync(PackConnection connection, ErpFinanceSettings s, string reference, CancellationToken ct);

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,100}$")]
    protected static partial Regex Reference();

    [GeneratedRegex(@"^[A-Za-z0-9 ._/-]{1,240}$")]
    private static partial Regex Code();

    public static ErpFinanceSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<ErpFinanceSettings>(JsonDefaults.Options) is { BaseUrl: not null, Auth: not null } s ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Auth);

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (Parse(call.Connection.Settings) is not { } s)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        switch (call.Action)
        {
            case FinancePack.Submit:
                return ErpClaim.From(call.Input) is { } claim
                    ? await SubmitAsync(call.Connection, s, claim, call.Attempt > 1, ct)
                    : PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
            case FinancePack.Status:
            {
                var reference = call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return reference is not null && Reference().IsMatch(reference)
                    ? await StatusAsync(call.Connection, s, reference, ct)
                    : PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
            }
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
    }

    /// <summary>The account a line is posted to: the category's, or the default.</summary>
    protected static string Account(ErpFinanceSettings s, string category) =>
        s.Accounts is not null && s.Accounts.TryGetValue(category, out var account) ? account : s.DefaultAccount!;

    /// <summary>The canonical status a value of the ERP means, or null when it means none.</summary>
    protected string? MapStatus(ErpFinanceSettings s, string status, string? value) =>
        value is not null && (s.StatusValues ?? DefaultStatusValues).TryGetValue(status, out var words)
        && words.Any(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase)) ? status : null;

    protected static PackCallResult Done(string reference, string status) =>
        PackCallResult.Ok(JsonSerializer.SerializeToElement(new { reference, status }), reference);

    protected static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    protected static string? Cut(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;

    /// <summary>
    /// One HTTP exchange with the ERP. Returns the failure the status means, or the JSON answer with the response headers.
    /// An answer that is not JSON or over 1 MB is an invalid response that may have acted when <paramref name="writes"/> is true.
    /// </summary>
    protected async Task<(PackCallResult? Failure, JsonElement Body, HttpResponseHeaders? Headers)> SendAsync(PackConnection connection, ErpFinanceSettings s,
        HttpMethod method, string pathAndQuery, object? body, IReadOnlyDictionary<int, string>? business, bool writes, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null, Func<HttpResponseMessage, PackCallResult?>? before = null)
    {
        var baseUri = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
        var uri = new Uri(s.BaseUrl!.TrimEnd('/') + pathAndQuery);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default, null);
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (await signIn.AuthorizeAsync(connection, s.Rest, request, ct) is { } refused)
        {
            return (refused, default, null);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (before?.Invoke(response) is { } early)
        {
            return (early, default, null);
        }
        if (signIn.MapStatus(connection, s.Rest, response, business) is { } failure)
        {
            return (failure, default, null);
        }
        return await RestSignIn.ReadJsonAsync(response, ct) is { ValueKind: JsonValueKind.Object } json
            ? (null, json, response.Headers)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: writes), default, null);
    }

    protected static string? Text(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e))
            {
                return null;
            }
        }
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, AuthTypes, errors, ct);
        Validate(s, errors);
        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (category, account) in s.Accounts ?? new Dictionary<string, string>())
        {
            if (category is not { Length: > 0 and <= 50 } || account is null || !Code().IsMatch(account) || accounts.Count >= 50)
            {
                errors.Add("accounts", "validation.finance.account");
                continue;
            }
            accounts[category] = account;
        }
        if (s.DefaultAccount is null || !Code().IsMatch(s.DefaultAccount))
        {
            errors.Add("defaultAccount", "validation.finance.account");
        }
        Dictionary<string, IReadOnlyList<string>>? values = null;
        if (s.StatusValues is not null)
        {
            values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (status, words) in s.StatusValues)
            {
                var clean = (words ?? []).Select(w => w.Trim()).Where(w => w.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!FinancePaymentStatuses.All.Contains(status) || clean.Count is 0 or > 10 || clean.Any(w => !seen.Add(w)))
                {
                    errors.Add("statusValues", "validation.finance.erp_status_values");
                    continue;
                }
                values[status] = clean;
            }
        }
        errors.ThrowIfAny();
        return JsonSerializer.SerializeToElement(Stored(s, rest!, accounts, values), JsonDefaults.Options);
    }

    /// <summary>A short text setting: optional unless <paramref name="required"/>, at most <paramref name="max"/> characters of a code.</summary>
    protected static void Check(string? value, bool required, int max, string field, ValidationErrors errors)
    {
        if (value is null ? required : value.Length > max || !Code().IsMatch(value))
        {
            errors.Add(field, "validation.finance.erp_setting");
        }
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { } s)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        var checks = await signIn.TestAsync(connection, s.Rest, ct);
        if (checks.Any(c => !c.Ok))
        {
            return checks;
        }
        PackCheck probe;
        try
        {
            var (failure, _, _) = await SendAsync(connection, s, HttpMethod.Get, ProbePath(s), null, null, false, ct);
            probe = failure is null
                ? new PackCheck("system", true, "integrations.test.system_ok")
                : new PackCheck("system", false, failure.ErrorClass == ConnectorErrorClasses.CredentialRefused ? "integrations.test.system_refused" : "integrations.test.system_failed",
                    failure.ErrorClass);
        }
        catch (HttpRequestException)
        {
            probe = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            probe = new PackCheck("system", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        checks.Add(probe);
        return checks;
    }
}

/// <summary>The canonical claim of the finance hand-off contract, as the ERP packs read it.</summary>
public sealed record ErpClaim(string SourceId, string EmployeeNumber, string Currency, decimal Amount, string ClaimDate, string? Description, string? CostCentre,
    IReadOnlyList<ErpClaimLine> Lines)
{
    public static ErpClaim? From(JsonElement input)
    {
        try
        {
            if (input.ValueKind != JsonValueKind.Object || Str(input, "source_id") is not { } source || Str(input, "employee_number") is not { } employee
                || Str(input, "currency") is not { Length: 3 } currency || Str(input, "claim_date") is not { } date
                || !input.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                || !input.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var items = lines.EnumerateArray().Select(l => new ErpClaimLine(Str(l, "category") ?? "", Str(l, "date"), l.GetProperty("amount").GetDecimal(), Str(l, "description"))).ToList();
            return items.Count == 0 ? null : new ErpClaim(source, employee, currency, amount.GetDecimal(), date, Str(input, "description"), Str(input, "cost_centre"), items);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed record ErpClaimLine(string Category, string? Date, decimal Amount, string? Description);

/// <summary>
/// Oracle Fusion Cloud Financials (INT-005) over the Payables Invoices REST resource
/// (<c>/fscmRestApi/resources/11.13.18.05/invoices</c>). A claim is a Standard invoice numbered by the claim, for the
/// employee's supplier number in the configured business unit, with one Item line per expense line. The reference is
/// the <c>InvoiceId</c>. The status is read from the same resource: <c>PaidStatus</c> for paid, <c>CanceledFlag</c> or
/// <c>ApprovalStatus</c> for rejected, and <c>ApprovalStatus</c> for approved.
/// </summary>
public sealed class OracleFusionFinancePack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : ErpFinancePack(context, signIn, httpClients)
{
    public const string KindName = "finance_oracle_fusion";
    public const string Resource = "/fscmRestApi/resources/11.13.18.05/invoices";

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.IntegrationOracleFusion;

    protected override IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultStatusValues { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [FinancePaymentStatuses.Paid] = ["Paid"],
        [FinancePaymentStatuses.Rejected] = ["Rejected", "Canceled"],
        [FinancePaymentStatuses.Approved] = ["Approved", "Manually approved", "Not required"],
    };

    protected override string ProbePath(ErpFinanceSettings s) => $"{Resource}?limit=1&onlyData=true&fields=InvoiceId";

    protected override void Validate(ErpFinanceSettings s, ValidationErrors errors)
    {
        Check(s.BusinessUnit, true, 240, "businessUnit", errors);
        Check(s.SupplierSite, false, 240, "supplierSite", errors);
        Check(s.InvoiceGroup, false, 80, "invoiceGroup", errors);
    }

    protected override ErpFinanceSettings Stored(ErpFinanceSettings s, RestSettings rest, IReadOnlyDictionary<string, string> accounts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? statusValues) =>
        new(rest.BaseUrl, rest.Auth, BusinessUnit: s.BusinessUnit, SupplierSite: s.SupplierSite, InvoiceGroup: s.InvoiceGroup, Accounts: accounts,
            DefaultAccount: s.DefaultAccount, StatusValues: statusValues);

    /// <summary>The invoice number: the claim ID, which Oracle keeps unique per supplier, so a lookup finds an earlier attempt.</summary>
    private static string Number(ErpClaim claim) => "TB-" + claim.SourceId.Replace("-", "", StringComparison.Ordinal);

    protected override async Task<PackCallResult> SubmitAsync(PackConnection connection, ErpFinanceSettings s, ErpClaim claim, bool retry, CancellationToken ct)
    {
        var number = Number(claim);
        if (retry)
        {
            var query = $"{Resource}?onlyData=true&fields=InvoiceId&q={Uri.EscapeDataString($"InvoiceNumber='{number}'")}";
            var (lookupFailure, found, _) = await SendAsync(connection, s, HttpMethod.Get, query, null, null, false, ct);
            if (lookupFailure is not null)
            {
                return lookupFailure;
            }
            if (found.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
                && Text(items[0], "InvoiceId") is { } existing && Reference().IsMatch(existing))
            {
                return Done(existing, FinancePaymentStatuses.Submitted);
            }
        }
        var body = new Dictionary<string, object?>
        {
            ["InvoiceNumber"] = number,
            ["InvoiceCurrency"] = claim.Currency,
            ["InvoiceAmount"] = claim.Amount,
            ["InvoiceDate"] = claim.ClaimDate,
            ["BusinessUnit"] = s.BusinessUnit,
            ["SupplierNumber"] = claim.EmployeeNumber,
            ["InvoiceType"] = "Standard",
            ["Description"] = Cut(claim.Description, 240),
            ["invoiceLines"] = claim.Lines.Select((l, i) => new Dictionary<string, object?>
            {
                ["LineNumber"] = i + 1,
                ["LineType"] = "Item",
                ["LineAmount"] = l.Amount,
                ["Description"] = Cut(l.Description ?? l.Category, 240),
                ["DistributionCombination"] = Account(s, l.Category),
            }).ToList(),
        };
        if (s.SupplierSite is { } site)
        {
            body["SupplierSite"] = site;
        }
        if (s.InvoiceGroup is { } group)
        {
            body["InvoiceGroup"] = group;
        }
        var (failure, answer, _) = await SendAsync(connection, s, HttpMethod.Post, Resource, body, SubmitErrors, true, ct);
        if (failure is not null)
        {
            return failure;
        }
        return Text(answer, "InvoiceId") is { } reference && Reference().IsMatch(reference)
            ? Done(reference, FinancePaymentStatuses.Submitted)
            : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
    }

    protected override async Task<PackCallResult> StatusAsync(PackConnection connection, ErpFinanceSettings s, string reference, CancellationToken ct)
    {
        var path = $"{Resource}/{Uri.EscapeDataString(reference)}?onlyData=true&fields=InvoiceId,PaidStatus,ApprovalStatus,CanceledFlag";
        var (failure, answer, _) = await SendAsync(connection, s, HttpMethod.Get, path, null, StatusErrors, false, ct);
        if (failure is not null)
        {
            return failure;
        }
        var status = MapStatus(s, FinancePaymentStatuses.Paid, Text(answer, "PaidStatus"))
            ?? (Text(answer, "CanceledFlag") == "true" ? FinancePaymentStatuses.Rejected : null)
            ?? MapStatus(s, FinancePaymentStatuses.Rejected, Text(answer, "ApprovalStatus"))
            ?? MapStatus(s, FinancePaymentStatuses.Approved, Text(answer, "ApprovalStatus"))
            ?? FinancePaymentStatuses.Submitted;
        return Done(reference, status);
    }
}

/// <summary>
/// SAP S/4HANA (INT-006) over the Supplier Invoice OData API (<c>API_SUPPLIERINVOICE_PROCESS_SRV</c>, entity
/// <c>A_SupplierInvoice</c>), which S/4HANA Cloud and on-premises releases publish alike. A claim is a supplier invoice
/// for the employee's supplier account in the configured company code, with one G/L account line per expense line and
/// the claim as the invoicing party's reference. Writes fetch a CSRF token first, as the OData service requires. The
/// reference is <c>{SupplierInvoice}-{FiscalYear}</c>. The API carries the invoice status (parked, posted) but not its
/// payment, so a status read reports posted as approved and never paid unless the tenant maps a value to paid.
/// </summary>
public sealed class SapS4FinancePack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : ErpFinancePack(context, signIn, httpClients)
{
    public const string KindName = "finance_sap_s4";
    public const string Service = "/sap/opu/odata/sap/API_SUPPLIERINVOICE_PROCESS_SRV";

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.IntegrationSap;

    protected override IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultStatusValues { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [FinancePaymentStatuses.Approved] = ["5"],
        [FinancePaymentStatuses.Submitted] = ["A", "B", "D", "E"],
    };

    private string Query(ErpFinanceSettings s, string? more = null)
    {
        var parts = new List<string>();
        if (s.Client is { } client)
        {
            parts.Add("sap-client=" + client);
        }
        if (more is not null)
        {
            parts.Add(more);
        }
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    protected override string ProbePath(ErpFinanceSettings s) => Service + "/" + Query(s, "$format=json");

    protected override void Validate(ErpFinanceSettings s, ValidationErrors errors)
    {
        if (s.CompanyCode is not { Length: >= 1 and <= 4 } code || !code.All(char.IsAsciiLetterOrDigit))
        {
            errors.Add("companyCode", "validation.finance.erp_setting");
        }
        if (s.Client is { } client && (client.Length != 3 || !client.All(char.IsAsciiDigit)))
        {
            errors.Add("client", "validation.finance.erp_setting");
        }
        if (s.TaxCode is { } tax && (tax.Length is 0 or > 2 || !tax.All(char.IsAsciiLetterOrDigit)))
        {
            errors.Add("taxCode", "validation.finance.erp_setting");
        }
        if (s.DefaultAccount is { } account && (account.Length > 10 || !account.All(char.IsAsciiDigit))
            || (s.Accounts ?? new Dictionary<string, string>()).Values.Any(a => a is null || a.Length > 10 || !a.All(char.IsAsciiDigit)))
        {
            errors.Add("accounts", "validation.finance.account");
        }
    }

    protected override ErpFinanceSettings Stored(ErpFinanceSettings s, RestSettings rest, IReadOnlyDictionary<string, string> accounts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? statusValues) =>
        new(rest.BaseUrl, rest.Auth, CompanyCode: s.CompanyCode, Client: s.Client, TaxCode: s.TaxCode, Accounts: accounts, DefaultAccount: s.DefaultAccount,
            StatusValues: statusValues);

    /// <summary>The invoicing party's reference (16 characters in SAP): the claim ID, so a lookup finds an earlier attempt.</summary>
    private static string ClaimReference(ErpClaim claim) => claim.SourceId.Replace("-", "", StringComparison.Ordinal)[..Math.Min(16, claim.SourceId.Replace("-", "", StringComparison.Ordinal).Length)].ToUpperInvariant();

    private static string ODataDate(string date) => date + "T00:00:00";

    private static string? Reference(JsonElement answer) =>
        Text(answer, "SupplierInvoice") is { } invoice && Text(answer, "FiscalYear") is { } year ? $"{invoice}-{year}" : null;

    protected override async Task<PackCallResult> SubmitAsync(PackConnection connection, ErpFinanceSettings s, ErpClaim claim, bool retry, CancellationToken ct)
    {
        var claimReference = ClaimReference(claim);
        if (retry)
        {
            var filter = Uri.EscapeDataString($"SupplierInvoiceIDByInvcgParty eq '{claimReference}' and CompanyCode eq '{s.CompanyCode}' and InvoicingParty eq '{claim.EmployeeNumber.Replace("'", "''", StringComparison.Ordinal)}'");
            var (lookupFailure, found, _) = await SendAsync(connection, s, HttpMethod.Get,
                $"{Service}/A_SupplierInvoice{Query(s, $"$filter={filter}&$select=SupplierInvoice,FiscalYear&$top=1&$format=json")}", null, null, false, ct);
            if (lookupFailure is not null)
            {
                return lookupFailure;
            }
            if (found.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                && results.GetArrayLength() > 0 && Reference(results[0]) is { } existing && Reference().IsMatch(existing))
            {
                return Done(existing, FinancePaymentStatuses.Submitted);
            }
        }
        // The OData service asks for a CSRF token, fetched with a read, and the session cookie that goes with it.
        var (tokenFailure, _, headers) = await SendAsync(connection, s, HttpMethod.Get, Service + "/" + Query(s, "$format=json"), null, null, false, ct,
            new Dictionary<string, string> { ["x-csrf-token"] = "Fetch" });
        if (tokenFailure is not null)
        {
            return tokenFailure;
        }
        if (headers is null || !headers.TryGetValues("x-csrf-token", out var tokens) || tokens.FirstOrDefault() is not { Length: > 0 } token)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse);
        }
        var send = new Dictionary<string, string> { ["x-csrf-token"] = token };
        if (headers.TryGetValues("Set-Cookie", out var cookies))
        {
            send["Cookie"] = string.Join("; ", cookies.Select(c => c.Split(';')[0]));
        }
        var body = new Dictionary<string, object?>
        {
            ["CompanyCode"] = s.CompanyCode,
            ["DocumentDate"] = ODataDate(claim.ClaimDate),
            ["PostingDate"] = ODataDate(claim.ClaimDate),
            ["SupplierInvoiceIDByInvcgParty"] = claimReference,
            ["InvoicingParty"] = claim.EmployeeNumber,
            ["DocumentCurrency"] = claim.Currency,
            ["InvoiceGrossAmount"] = Money(claim.Amount),
            ["DocumentHeaderText"] = Cut(claim.Description, 25),
            ["TaxIsCalculatedAutomatically"] = false,
            ["to_SupplierInvoiceItemGLAcct"] = claim.Lines.Select((l, i) =>
            {
                var item = new Dictionary<string, object?>
                {
                    ["SupplierInvoiceItem"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ["CompanyCode"] = s.CompanyCode,
                    ["GLAccount"] = Account(s, l.Category),
                    ["DocumentCurrency"] = claim.Currency,
                    ["SupplierInvoiceItemAmount"] = Money(l.Amount),
                    ["DebitCreditCode"] = "S",
                    ["SupplierInvoiceItemText"] = Cut(l.Description ?? l.Category, 50),
                };
                if (claim.CostCentre is { } centre)
                {
                    item["CostCenter"] = Cut(centre, 10);
                }
                if (s.TaxCode is { } tax)
                {
                    item["TaxCode"] = tax;
                }
                return item;
            }).ToList(),
        };
        var (failure, answer, _) = await SendAsync(connection, s, HttpMethod.Post, $"{Service}/A_SupplierInvoice{Query(s)}", body, SubmitErrors, true, ct, send,
            // A refused CSRF token is not a refused credential: nothing was posted, so the job tries again with a new token.
            r => r.StatusCode == System.Net.HttpStatusCode.Forbidden && r.Headers.TryGetValues("x-csrf-token", out var v) && v.Any(x => x.Equals("Required", StringComparison.OrdinalIgnoreCase))
                ? PackCallResult.Technical(ConnectorErrorClasses.ServerError)
                : null);
        if (failure is not null)
        {
            return failure;
        }
        return answer.TryGetProperty("d", out var created) && Reference(created) is { } reference && Reference().IsMatch(reference)
            ? Done(reference, FinancePaymentStatuses.Submitted)
            : PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
    }

    protected override async Task<PackCallResult> StatusAsync(PackConnection connection, ErpFinanceSettings s, string reference, CancellationToken ct)
    {
        var cut = reference.LastIndexOf('-');
        if (cut <= 0 || cut == reference.Length - 1)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var (invoice, year) = (reference[..cut], reference[(cut + 1)..]);
        var key = $"A_SupplierInvoice(SupplierInvoice='{Uri.EscapeDataString(invoice)}',FiscalYear='{Uri.EscapeDataString(year)}')";
        var (failure, answer, _) = await SendAsync(connection, s, HttpMethod.Get, $"{Service}/{key}{Query(s, "$select=SupplierInvoiceStatus&$format=json")}", null,
            StatusErrors, false, ct);
        if (failure is not null)
        {
            return failure;
        }
        var value = answer.TryGetProperty("d", out var d) ? Text(d, "SupplierInvoiceStatus") : null;
        var status = MapStatus(s, FinancePaymentStatuses.Paid, value) ?? MapStatus(s, FinancePaymentStatuses.Rejected, value)
            ?? MapStatus(s, FinancePaymentStatuses.Approved, value) ?? FinancePaymentStatuses.Submitted;
        return Done(reference, status);
    }
}
