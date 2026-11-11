using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// Settings of the HR REST pack (spec 019 FR-016), filled from the HR mapping template without code: the HR system's
/// address and sign-in, the employees list (<paramref name="EmployeesPath"/>, the array of records at
/// <paramref name="RecordsPath"/>, and paging by page number or by a next link), where each canonical field is in a
/// record (<paramref name="Fields"/>, paths such as <c>$.person.email</c>), the HR system's words for each status,
/// and the nightly sync time in the HR system's time zone. <paramref name="EmployeePath"/> is optional: the address of one
/// employee with <c>{record_id}</c> in it (for example <c>/employees/{record_id}</c>), and <paramref name="EmployeeRecordPath"/>
/// is where the record is in its answer. Without it, values that must be current cannot be confirmed (US5 scenario 3).
/// Payslips (US6) are optional too: <paramref name="PayslipsPath"/> lists one employee's payslips (with <c>{record_id}</c>),
/// <paramref name="PayslipFields"/> says where the period, gross, deductions, net and currency are in each, and
/// <paramref name="PayslipPdfPath"/> is the PDF of one period (with <c>{record_id}</c> and <c>{period}</c>).
/// Leave (spec 019 US7) is optional as well: <paramref name="LeaveBalancesPath"/> lists one employee's balances (with
/// <c>{record_id}</c>), <paramref name="LeaveBalanceFields"/> says where the type and the balance are in each, and
/// <paramref name="LeaveSubmitPath"/> takes approved leave, cancellations and changes as a POST, answering with the HR
/// reference at <paramref name="LeaveReferencePath"/>. <paramref name="LeaveTypeCodes"/> maps TeamBeats leave type keys
/// to HR's codes when they differ; <paramref name="LeaveIdempotencyHeader"/> makes a repeated send safe.
/// </summary>
public sealed record HrRestSettings(
    string? BaseUrl,
    RestAuth? Auth,
    string? EmployeesPath,
    string? RecordsPath,
    string? Paging,
    string? PageParam,
    string? PageSizeParam,
    int? PageSize,
    string? NextPath,
    IReadOnlyDictionary<string, string>? Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StatusValues,
    string? DateFormat,
    string? SyncAt,
    string? TimeZone,
    string? EmployeePath = null,
    string? EmployeeRecordPath = null,
    string? PayslipsPath = null,
    string? PayslipRecordsPath = null,
    IReadOnlyDictionary<string, string>? PayslipFields = null,
    string? PayslipPdfPath = null,
    string? LeaveBalancesPath = null,
    string? LeaveBalanceRecordsPath = null,
    IReadOnlyDictionary<string, string>? LeaveBalanceFields = null,
    string? LeaveSubmitPath = null,
    string? LeaveReferencePath = null,
    string? LeaveIdempotencyHeader = null,
    IReadOnlyDictionary<string, string>? LeaveTypeCodes = null)
{
    public RestSettings Rest => new(BaseUrl!, Auth!);
}

/// <summary>
/// The generic HR REST pack (spec 019 US1). Its one action reads every employee page by page and hands the canonical
/// records to <see cref="HrSync"/>; nothing is applied when any page fails, so a broken read never looks like people
/// leaving. The action declares no inputs, so it is never a route step or an agent tool, and HR DTOs never leave the pack.
/// </summary>
public sealed partial class HrRestPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients, HrSync sync) : IConnectorPack
{
    public const string KindName = "hr_rest";
    public const string SyncEmployees = "sync_employees";
    public const string SubmitLeave = "submit_leave";
    public const string LeaveRefused = "leave_refused";
    public const int MaxLeaveBalances = 200;
    public const int MaxPages = 500;
    public const int DefaultPageSize = 200;

    public static readonly IReadOnlyList<string> PagingModes = ["none", "page", "next"];
    private static readonly string[] AuthTypes = ["api_key", "bearer", "basic", "oauth_client_credentials"];

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.HrIntegration;

    /// <summary>A full read of 20,000 employees takes minutes, not seconds (section 15).</summary>
    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
    [
        new(SyncEmployees, ConnectorSideEffects.Read, true, [], JobTimeout: TimeSpan.FromMinutes(5)),
        new(SubmitLeave, ConnectorSideEffects.Write, false, [LeaveRefused]),
    ];

    private static readonly IReadOnlyDictionary<int, string> LeaveErrors = new Dictionary<int, string> { [400] = LeaveRefused, [409] = LeaveRefused, [422] = LeaveRefused };

    /// <summary>Leave goes to HR only when the connection has a submit address; with an idempotency header a retry is safe.</summary>
    public Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct)
    {
        var s = Parse(connection.Settings);
        IReadOnlyList<ConnectorActionDefinition> list = s?.LeaveSubmitPath is null
            ? [Actions[0]]
            : [Actions[0], Actions[1] with { Idempotent = s.LeaveIdempotencyHeader is not null }];
        return Task.FromResult(list);
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,100}$")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex LeaveKey();

    [GeneratedRegex(@"^[A-Za-z0-9._ -]{1,60}$")]
    private static partial Regex LeaveCode();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[[0-9]{1,3}\]){1,8}$")]
    internal static partial Regex RecordPath();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[[0-9]{1,3}\]){0,8}$")]
    private static partial Regex ArrayPath();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]{0,300}$")]
    private static partial Regex PathShape();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]{0,150}\{record_id\}[A-Za-z0-9._~/-]{0,150}$")]
    private static partial Regex OnePathShape();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/{}-]{1,300}$")]
    private static partial Regex TemplateShape();

    /// <summary>A payroll period: year and month.</summary>
    [GeneratedRegex(@"^[0-9]{4}-(0[1-9]|1[0-2])$")]
    public static partial Regex PayslipPeriod();

    [GeneratedRegex(@"^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex ParamName();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    internal static partial Regex ClockTime();

    public static HrRestSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object && settings.Deserialize<HrRestSettings>(JsonDefaults.Options) is { BaseUrl: not null, Auth: not null } s ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateOnly? CredentialExpiry(JsonElement settings) => RestSignIn.ExpiresOn(Parse(settings)?.Auth);

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (call.Action == SubmitLeave)
        {
            return await SubmitLeaveAsync(call, ct);
        }
        if (call.Action != SyncEmployees)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (Parse(call.Connection.Settings) is not { Fields: not null, StatusValues: not null } s)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var employees = new List<HrEmployee>();
        var problems = new List<HrProblem>();
        var size = s.PageSize ?? DefaultPageSize;
        string? next = s.EmployeesPath;
        for (var page = 1; next is not null; page++)
        {
            if (page > MaxPages || employees.Count + problems.Count > HrMapping.MaxRecords)
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse);
            }
            var uri = PageUri(s, next, page, size);
            if (uri is null)
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
            }
            var (failure, answer) = await GetAsync(call.Connection, s, uri, ct);
            if (failure is not null)
            {
                return failure;
            }
            var records = RestDefinitions.Select(answer, s.RecordsPath ?? "$");
            if (records is not { ValueKind: JsonValueKind.Array } list)
            {
                return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse);
            }
            foreach (var record in list.EnumerateArray())
            {
                var raw = s.Fields!.ToDictionary(f => f.Key, f => Text(RestDefinitions.Select(record, f.Value)), StringComparer.Ordinal);
                var (employee, problem) = HrMapping.Map(raw, s.StatusValues!, s.DateFormat);
                if (employee is not null)
                {
                    employees.Add(employee);
                }
                else
                {
                    problems.Add(problem!);
                }
            }
            next = (s.Paging ?? "none") switch
            {
                "page" => list.GetArrayLength() < size || list.GetArrayLength() == 0 ? null : s.EmployeesPath,
                "next" => s.NextPath is { } p && Text(RestDefinitions.Select(answer, p)) is { Length: > 0 } link ? link : null,
                _ => null,
            };
        }
        var run = await sync.RunAsync(call.Connection.Id, HrMapping.Check(employees, problems), ct);
        return PackCallResult.Ok(Output(run), run.Id.ToString(), run.Total);
    }

    public static JsonElement Output(HrRunView run) => JsonSerializer.SerializeToElement(new
    {
        run = run.Id, status = run.Status, total = run.Total, matched = run.Matched, unmatched = run.Unmatched, conflicts = run.Conflicts,
        refused = run.Refused, changed = run.Changed,
    });

    /// <summary>
    /// Reads one employee now (US5 scenario 3). Null with no failure means the connection has no address for one record.
    /// The record ID is escaped into the path, so it cannot change the address.
    /// </summary>
    public async Task<(HrEmployee? Employee, PackCallResult? Failure)> ReadOneAsync(PackConnection connection, string recordId, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { Fields: not null, StatusValues: not null, EmployeePath: { } path } s)
        {
            return (null, null);
        }
        var uri = PageUri(s with { Paging = "none" }, path.Replace("{record_id}", Uri.EscapeDataString(recordId), StringComparison.Ordinal), 1, 1);
        if (uri is null)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        var (failure, answer) = await GetAsync(connection, s, uri, ct);
        if (failure is not null)
        {
            return (null, failure);
        }
        if (RestDefinitions.Select(answer, s.EmployeeRecordPath ?? "$") is not { ValueKind: JsonValueKind.Object } record)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
        var raw = s.Fields!.ToDictionary(f => f.Key, f => Text(RestDefinitions.Select(record, f.Value)), StringComparer.Ordinal);
        var (employee, _) = HrMapping.Map(raw, s.StatusValues!, s.DateFormat);
        return employee is not null && employee.RecordId == recordId
            ? (employee, null)
            : (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
    }

    public static readonly IReadOnlyList<string> PayslipFieldNames = ["period", "gross", "deductions", "net", "currency"];

    /// <summary>The largest payslip PDF passed on; payroll PDFs are a page or two.</summary>
    public const int MaxPdfBytes = 10 * 1024 * 1024;

    /// <summary>
    /// One employee's payslips (US6), newest period first. Null with no failure means the connection has no payslip
    /// address. Values are read live and never stored (section 8).
    /// </summary>
    public async Task<(IReadOnlyList<HrPayslip>? Payslips, PackCallResult? Failure)> ReadPayslipsAsync(PackConnection connection, string recordId, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { PayslipsPath: { } path, PayslipFields: { } fields } s)
        {
            return (null, null);
        }
        var uri = PageUri(s with { Paging = "none" }, path.Replace("{record_id}", Uri.EscapeDataString(recordId), StringComparison.Ordinal), 1, 1);
        if (uri is null)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        var (failure, answer) = await GetAsync(connection, s, uri, ct);
        if (failure is not null)
        {
            return (null, failure);
        }
        if (RestDefinitions.Select(answer, s.PayslipRecordsPath ?? "$") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() > 600)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
        var payslips = new List<HrPayslip>();
        foreach (var item in list.EnumerateArray())
        {
            string? Value(string name) => fields.TryGetValue(name, out var at) ? Text(RestDefinitions.Select(item, at)) : null;
            static decimal? Number(string? v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            var period = Value("period");
            if (period is null || !PayslipPeriod().IsMatch(period) || Number(Value("net")) is not { } net)
            {
                return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
            }
            var currency = Value("currency")?.Trim().ToUpperInvariant();
            payslips.Add(new HrPayslip(period, Number(Value("gross")), Number(Value("deductions")), net, currency is not null && CurrencyCode().IsMatch(currency) ? currency : null));
        }
        return (payslips.OrderByDescending(p => p.Period, StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>The PDF of one period as payroll provides it. Null with no failure means the connection has no PDF address.</summary>
    public async Task<(byte[]? Pdf, PackCallResult? Failure)> ReadPayslipPdfAsync(PackConnection connection, string recordId, string period, CancellationToken ct)
    {
        if (!PayslipPeriod().IsMatch(period) || Parse(connection.Settings) is not { PayslipPdfPath: { } path } s)
        {
            return (null, null);
        }
        var address = path.Replace("{record_id}", Uri.EscapeDataString(recordId), StringComparison.Ordinal).Replace("{period}", period, StringComparison.Ordinal);
        if (PageUri(s with { Paging = "none" }, address, 1, 1) is not { } uri)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        if (!await signIn.IsAllowedAsync(new Uri(s.BaseUrl!.TrimEnd('/') + "/"), ct))
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        if (await signIn.AuthorizeAsync(connection, s.Rest, request, ct) is { } refused)
        {
            return (null, refused);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(connection, s.Rest, response) is { } failure)
        {
            return (null, failure);
        }
        if (response.Content.Headers.ContentLength is > MaxPdfBytes)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + read > MaxPdfBytes)
            {
                return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
            }
            copy.Write(buffer, 0, read);
        }
        var bytes = copy.ToArray();
        return bytes.AsSpan().StartsWith("%PDF-"u8) ? (bytes, null) : (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
    }

    /// <summary>One employee's leave balances by TeamBeats type key. Null with no failure means the connection has no balances address.</summary>
    public async Task<(IReadOnlyList<HrLeaveBalance>? Balances, PackCallResult? Failure)> ReadLeaveBalancesAsync(PackConnection connection, string recordId, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { LeaveBalancesPath: { } path, LeaveBalanceFields: { } fields } s)
        {
            return (null, null);
        }
        var uri = PageUri(s with { Paging = "none" }, path.Replace("{record_id}", Uri.EscapeDataString(recordId), StringComparison.Ordinal), 1, 1);
        if (uri is null)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        var (failure, answer) = await GetAsync(connection, s, uri, ct);
        if (failure is not null)
        {
            return (null, failure);
        }
        if (RestDefinitions.Select(answer, s.LeaveBalanceRecordsPath ?? "$") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() > MaxLeaveBalances)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
        var keys = (s.LeaveTypeCodes ?? new Dictionary<string, string>()).ToDictionary(c => c.Value, c => c.Key, StringComparer.Ordinal);
        var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            var code = Text(RestDefinitions.Select(item, fields["type"]))?.Trim();
            if (code is null || !decimal.TryParse(Text(RestDefinitions.Select(item, fields["balance"])), NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
                || Math.Abs(balance) > 100000)
            {
                return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
            }
            var key = keys.TryGetValue(code, out var mapped) ? mapped : code.ToLowerInvariant();
            if (LeaveKey().IsMatch(key))
            {
                balances[key] = decimal.Round(balance, 2);
            }
        }
        return (balances.Select(b => new HrLeaveBalance(b.Key, b.Value)).ToList(), null);
    }

    /// <summary>
    /// Sends one leave change to HR. The body names the employee by HR record ID and number and the type by HR's code; the
    /// TeamBeats request ID and change number let HR tell a repeat. HR's reference comes back as the external reference.
    /// </summary>
    private async Task<PackCallResult> SubmitLeaveAsync(PackCall call, CancellationToken ct)
    {
        if (Parse(call.Connection.Settings) is not { LeaveSubmitPath: { } path } s)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var input = call.Input;
        string? Field(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) ? Text(v) : null;
        var type = Field("type");
        if (Field("record_id") is not { } recordId || type is null || Field("request_id") is null || Field("kind") is not ("approved" or "cancelled" or "changed"))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidInput);
        }
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["request_id"] = Field("request_id"),
            ["change"] = Field("change"),
            ["kind"] = Field("kind"),
            ["record_id"] = recordId,
            ["employee_number"] = Field("employee_number"),
            ["type"] = s.LeaveTypeCodes is { } codes && codes.TryGetValue(type, out var code) ? code : type,
            ["start"] = Field("start"),
            ["end"] = Field("end"),
            ["start_half"] = Field("start_half") == "true",
            ["end_half"] = Field("end_half") == "true",
            ["days"] = decimal.TryParse(Field("days"), NumberStyles.Number, CultureInfo.InvariantCulture, out var days) ? days : 0m,
        };
        var baseUri = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
        var uri = new Uri(s.BaseUrl!.TrimEnd('/') + path);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || !await signIn.IsAllowedAsync(baseUri, ct))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (s.LeaveIdempotencyHeader is { } header)
        {
            // The same for every attempt at one change, so HR can tell a resend from a new change.
            request.Headers.TryAddWithoutValidation(header, $"teambeats-leave-{Field("request_id")}-{Field("change")}");
        }
        if (await signIn.AuthorizeAsync(call.Connection, s.Rest, request, ct) is { } refused)
        {
            return refused;
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(call.Connection, s.Rest, response, LeaveErrors) is { } failure)
        {
            return failure;
        }
        var answer = await RestSignIn.ReadJsonAsync(response, ct);
        var reference = answer is { ValueKind: JsonValueKind.Object } json ? Text(RestDefinitions.Select(json, s.LeaveReferencePath ?? "$.id")) : null;
        if (reference is null || !Reference().IsMatch(reference))
        {
            // HR may hold the leave without a reference we can use: an operator must look.
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: true);
        }
        return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { reference }), reference);
    }

    private static string? Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    /// <summary>
    /// The address of one page. A next link may be a path or a full address, but only on the HR system's own host over
    /// https, so a crafted answer cannot send the credential elsewhere.
    /// </summary>
    private static Uri? PageUri(HrRestSettings s, string next, int page, int size)
    {
        var root = new Uri(s.BaseUrl!.TrimEnd('/') + "/");
        Uri uri;
        if (next.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var absolute))
            {
                return null;
            }
            uri = absolute;
        }
        else if (next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal))
        {
            uri = new Uri(s.BaseUrl!.TrimEnd('/') + next);
        }
        else
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != root.Port || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        if (s.Paging == "page")
        {
            var join = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            uri = new Uri(uri.AbsoluteUri + join + Uri.EscapeDataString(s.PageParam ?? "page") + "=" + page.ToString(CultureInfo.InvariantCulture)
                + (s.PageSizeParam is { } sp ? "&" + Uri.EscapeDataString(sp) + "=" + size.ToString(CultureInfo.InvariantCulture) : ""));
        }
        return uri;
    }

    private async Task<(PackCallResult? Failure, JsonElement Body)> GetAsync(PackConnection connection, HrRestSettings s, Uri uri, CancellationToken ct)
    {
        if (!await signIn.IsAllowedAsync(new Uri(s.BaseUrl!.TrimEnd('/') + "/"), ct))
        {
            return (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (await signIn.AuthorizeAsync(connection, s.Rest, request, ct) is { } refused)
        {
            return (refused, default);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (signIn.MapStatus(connection, s.Rest, response) is { } failure)
        {
            return (failure, default);
        }
        return await RestSignIn.ReadJsonAsync(response, ct) is { } json and ({ ValueKind: JsonValueKind.Object } or { ValueKind: JsonValueKind.Array })
            ? (null, json)
            : (PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse), default);
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.custom_rest.settings");
        var errors = new ValidationErrors();
        var rest = await signIn.ValidateAsync(tenantId, s.BaseUrl, s.Auth, AuthTypes, errors, ct);
        if (s.EmployeesPath is null || !PathShape().IsMatch(s.EmployeesPath) || s.EmployeesPath.Contains("..", StringComparison.Ordinal))
        {
            errors.Add("employeesPath", "validation.hr.employees_path");
        }
        if (s.RecordsPath is not null && !ArrayPath().IsMatch(s.RecordsPath))
        {
            errors.Add("recordsPath", "validation.hr.record_path");
        }
        var paging = s.Paging ?? "none";
        if (!PagingModes.Contains(paging))
        {
            errors.Add("paging", "validation.hr.paging");
        }
        if (paging == "page")
        {
            if (s.PageParam is not null && !ParamName().IsMatch(s.PageParam))
            {
                errors.Add("pageParam", "validation.hr.param_name");
            }
            if (s.PageSizeParam is not null && !ParamName().IsMatch(s.PageSizeParam))
            {
                errors.Add("pageSizeParam", "validation.hr.param_name");
            }
            if (s.PageSize is { } size && size is < 1 or > 1000)
            {
                errors.Add("pageSize", "validation.hr.page_size");
            }
        }
        if (paging == "next" && (s.NextPath is null || !RecordPath().IsMatch(s.NextPath)))
        {
            errors.Add("nextPath", "validation.hr.record_path");
        }
        if (Blank(s.EmployeePath) is { } one && (!OnePathShape().IsMatch(one) || one.Contains("..", StringComparison.Ordinal)
            || one.Split("{record_id}").Length != 2))
        {
            errors.Add("employeePath", "validation.hr.employee_path");
        }
        if (s.EmployeeRecordPath is not null && !ArrayPath().IsMatch(s.EmployeeRecordPath))
        {
            errors.Add("employeeRecordPath", "validation.hr.record_path");
        }
        var payslipsPath = Blank(s.PayslipsPath);
        var pdfPath = Blank(s.PayslipPdfPath);
        Dictionary<string, string>? payslipFields = null;
        if (payslipsPath is not null || pdfPath is not null || s.PayslipFields is { Count: > 0 })
        {
            if (payslipsPath is null || !Template(payslipsPath, ["{record_id}"]))
            {
                errors.Add("payslipsPath", "validation.hr.payslips_path");
            }
            if (pdfPath is not null && !Template(pdfPath, ["{record_id}", "{period}"]))
            {
                errors.Add("payslipPdfPath", "validation.hr.payslip_pdf_path");
            }
            if (s.PayslipRecordsPath is not null && !ArrayPath().IsMatch(s.PayslipRecordsPath))
            {
                errors.Add("payslipRecordsPath", "validation.hr.record_path");
            }
            payslipFields = [];
            foreach (var (name, at) in s.PayslipFields ?? new Dictionary<string, string>())
            {
                var clean = at?.Trim();
                if (!PayslipFieldNames.Contains(name) || clean is null || !RecordPath().IsMatch(clean))
                {
                    errors.Add($"payslipFields.{name}", "validation.hr.field_source");
                    continue;
                }
                payslipFields[name] = clean;
            }
            if (!payslipFields.ContainsKey("period") || !payslipFields.ContainsKey("net"))
            {
                errors.Add("payslipFields", "validation.hr.payslip_fields_required");
            }
        }
        var balancesPath = Blank(s.LeaveBalancesPath);
        Dictionary<string, string>? balanceFields = null;
        if (balancesPath is not null || s.LeaveBalanceFields is { Count: > 0 })
        {
            if (balancesPath is null || !Template(balancesPath, ["{record_id}"]))
            {
                errors.Add("leaveBalancesPath", "validation.hr.leave_balances_path");
            }
            if (s.LeaveBalanceRecordsPath is not null && !ArrayPath().IsMatch(s.LeaveBalanceRecordsPath))
            {
                errors.Add("leaveBalanceRecordsPath", "validation.hr.record_path");
            }
            balanceFields = [];
            foreach (var (name, at) in s.LeaveBalanceFields ?? new Dictionary<string, string>())
            {
                var clean = at?.Trim();
                if (name is not ("type" or "balance") || clean is null || !RecordPath().IsMatch(clean))
                {
                    errors.Add($"leaveBalanceFields.{name}", "validation.hr.field_source");
                    continue;
                }
                balanceFields[name] = clean;
            }
            if (!balanceFields.ContainsKey("type") || !balanceFields.ContainsKey("balance"))
            {
                errors.Add("leaveBalanceFields", "validation.hr.leave_balance_fields_required");
            }
        }
        var submitPath = Blank(s.LeaveSubmitPath);
        if (submitPath is not null && (!PathShape().IsMatch(submitPath) || submitPath.Contains("..", StringComparison.Ordinal)))
        {
            errors.Add("leaveSubmitPath", "validation.hr.leave_submit_path");
        }
        if (s.LeaveReferencePath is not null && !RecordPath().IsMatch(s.LeaveReferencePath))
        {
            errors.Add("leaveReferencePath", "validation.hr.record_path");
        }
        var idempotency = Blank(s.LeaveIdempotencyHeader);
        if (idempotency is not null && (!HeaderName().IsMatch(idempotency) || idempotency.Equals("authorization", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("leaveIdempotencyHeader", "validation.hr.leave_idempotency_header");
        }
        var typeCodes = (s.LeaveTypeCodes ?? new Dictionary<string, string>()).Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value.Trim(), StringComparer.Ordinal);
        if (typeCodes.Count > 50 || typeCodes.Any(c => !LeaveKey().IsMatch(c.Key) || !LeaveCode().IsMatch(c.Value)))
        {
            errors.Add("leaveTypeCodes", "validation.hr.leave_type_codes");
        }
        var fields = CheckFields(s.Fields, f => RecordPath().IsMatch(f), errors);
        var statuses = HrMapping.CheckStatusValues(s.StatusValues, errors);
        CheckSchedule(s.DateFormat, s.SyncAt, s.TimeZone, errors);
        errors.ThrowIfAny();
        var stored = new HrRestSettings(rest!.BaseUrl, rest.Auth, s.EmployeesPath, s.RecordsPath ?? "$", paging, paging == "page" ? s.PageParam ?? "page" : null,
            paging == "page" ? s.PageSizeParam : null, paging == "page" ? s.PageSize ?? DefaultPageSize : null, paging == "next" ? s.NextPath : null,
            fields, statuses, Blank(s.DateFormat), s.SyncAt ?? "01:00", s.TimeZone ?? "UTC",
            Blank(s.EmployeePath), Blank(s.EmployeePath) is null ? null : s.EmployeeRecordPath ?? "$",
            payslipsPath, payslipsPath is null ? null : s.PayslipRecordsPath ?? "$", payslipsPath is null ? null : payslipFields, payslipsPath is null ? null : pdfPath,
            balancesPath, balancesPath is null ? null : s.LeaveBalanceRecordsPath ?? "$", balancesPath is null ? null : balanceFields,
            submitPath, submitPath is null ? null : s.LeaveReferencePath ?? "$.id", submitPath is null ? null : idempotency, typeCodes.Count == 0 ? null : typeCodes);
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    /// <summary>A path with each placeholder exactly once and nothing else in braces.</summary>
    private static bool Template(string path, IReadOnlyList<string> placeholders)
    {
        if (!TemplateShape().IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || placeholders.Any(p => path.Split(p).Length != 2))
        {
            return false;
        }
        var rest = placeholders.Aggregate(path, (s, p) => s.Replace(p, "x", StringComparison.Ordinal));
        return !rest.Contains('{') && !rest.Contains('}');
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The canonical fields of a mapping: known names only, the record ID and status required, each source used once.</summary>
    internal static Dictionary<string, string> CheckFields(IReadOnlyDictionary<string, string>? map, Func<string, bool> sourceOk, ValidationErrors errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, source) in map ?? new Dictionary<string, string>())
        {
            var clean = source?.Trim();
            if (!HrFields.All.Contains(canonical) || clean is null || clean.Length is 0 or > 100 || !sourceOk(clean))
            {
                errors.Add($"fields.{canonical}", "validation.hr.field_source");
                continue;
            }
            result[canonical] = clean;
        }
        if (HrFields.Required.Any(r => !result.ContainsKey(r)))
        {
            errors.Add("fields", "validation.hr.fields_required");
        }
        if (result.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            errors.Add("fields", "validation.hr.field_twice");
        }
        return result;
    }

    internal static void CheckSchedule(string? dateFormat, string? syncAt, string? timeZone, ValidationErrors errors)
    {
        if (Blank(dateFormat) is { } format && !HrMapping.DateFormatWorks(format))
        {
            errors.Add("dateFormat", "validation.hr.date_format");
        }
        if (syncAt is not null && !ClockTime().IsMatch(syncAt))
        {
            errors.Add("syncAt", "validation.hr.sync_at");
        }
        if (timeZone is not null && (timeZone.Length > 64 || ClockCsv.FindZone(timeZone) is null))
        {
            errors.Add("timeZone", "validation.hr.time_zone");
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
        // One page of employees, read and mapped, without applying anything.
        PackCheck check;
        try
        {
            var (failure, answer) = PageUri(s, s.EmployeesPath!, 1, s.PageSize ?? DefaultPageSize) is { } uri
                ? await GetAsync(connection, s, uri, ct)
                : (PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed), default);
            check = failure is not null
                ? new PackCheck("employees", false, failure.ErrorClass == ConnectorErrorClasses.CredentialRefused ? "integrations.test.system_refused" : "integrations.test.system_failed",
                    failure.ErrorClass)
                : RestDefinitions.Select(answer, s.RecordsPath ?? "$") is { ValueKind: JsonValueKind.Array }
                    ? new PackCheck("employees", true, "integrations.test.hr_employees_read")
                    : new PackCheck("employees", false, "integrations.test.hr_records_missing", ConnectorErrorClasses.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            check = new PackCheck("employees", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            check = new PackCheck("employees", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
        }
        checks.Add(check);
        return checks;
    }
}
