using System.Globalization;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// Settings of the HR SFTP pack (spec 019 FR-016): the SFTP server the HR system drops its full employee export on
/// (as for clock data, with a pinned host key), the CSV shape, and which column holds each canonical field
/// (<paramref name="Fields"/>: header names, or column numbers from 1 without a header).
/// </summary>
public sealed record HrSftpSettings(
    ClockSftpSettings? Sftp,
    string? Delimiter,
    bool? HasHeader,
    IReadOnlyDictionary<string, string>? Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StatusValues,
    string? DateFormat,
    string? SyncAt,
    string? TimeZone);

/// <summary>
/// The HR SFTP and CSV pack (spec 019 US1). Each sync reads the newest export file matching the pattern, maps it to
/// the same canonical contract as the REST pack and hands it to <see cref="HrSync"/>. TeamBeats never writes, moves or
/// deletes files on the server.
/// </summary>
public sealed class HrSftpPack(ITenantContext context, ISecretStore secrets, HostAllowlist allowlist, IClockFileSource files, HrSync sync) : IConnectorPack
{
    public const string KindName = "hr_sftp";
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const string NoFile = "no_file";

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.HrIntegration;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } =
        [new(HrRestPack.SyncEmployees, ConnectorSideEffects.Read, true, [NoFile], JobTimeout: TimeSpan.FromMinutes(5))];

    public static HrSftpSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<HrSftpSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<SftpTarget?> TargetAsync(PackConnection connection, HrSftpSettings settings, CancellationToken ct)
    {
        var s = settings.Sftp!;
        var secret = s.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, s.SecretRef, ct);
        return string.IsNullOrEmpty(secret)
            ? null
            : new SftpTarget(s.Host!, s.Port ?? 22, s.Username!, s.AuthType ?? "password", secret, s.HostKeySha256!, s.Directory ?? ".");
    }

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (call.Action != HrRestPack.SyncEmployees)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (Parse(call.Connection.Settings) is not { Sftp: not null, Fields: not null, StatusValues: not null } settings)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        if (await TargetAsync(call.Connection, settings, ct) is not { } target)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
        }
        var matcher = ClockImportPack.FileMatcher(settings.Sftp.FilePattern ?? "*.csv");
        // List first, then read only the newest matching file: one full export is the whole truth.
        var listing = await files.FetchAsync(target, _ => false, 0, 0, ct);
        if (listing.ErrorClass is { } listError)
        {
            return Failure(listError);
        }
        var newest = listing.Listed.Where(f => matcher.IsMatch(f.Name)).OrderByDescending(f => f.Modified).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is null)
        {
            return PackCallResult.Business(NoFile);
        }
        var fetch = await files.FetchAsync(target, f => f.Name == newest.Name, 1, MaxFileBytes, ct);
        if (fetch.ErrorClass is { } error)
        {
            return Failure(error);
        }
        if (fetch.Files.FirstOrDefault() is not { Content.Length: > 0 } file)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse);
        }
        if (Read(file.Content, settings) is not { } read)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse);
        }
        var run = await sync.RunAsync(call.Connection.Id, read, ct);
        return PackCallResult.Ok(HrRestPack.Output(run), run.Id.ToString(), run.Total);
    }

    private static PackCallResult Failure(string error) =>
        error is ConnectorErrorClasses.NotResponding or ConnectorErrorClasses.InvalidResponse ? PackCallResult.Technical(error) : PackCallResult.Fatal(error);

    /// <summary>The canonical records of one export, or null when its header lacks a mapped column.</summary>
    public static HrRead? Read(byte[] content, HrSftpSettings settings)
    {
        var span = content.AsSpan();
        var text = Encoding.UTF8.GetString(span.StartsWith(Encoding.UTF8.Preamble) ? span[3..] : span);
        var header = settings.HasHeader ?? true;
        using var records = ClockCsv.Records(text, (settings.Delimiter ?? ",")[0]).GetEnumerator();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (header)
        {
            if (!records.MoveNext())
            {
                return new HrRead([], []);
            }
            var names = records.Current.Fields.Select((n, i) => (Name: n.Trim(), i)).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
            foreach (var (field, column) in settings.Fields!)
            {
                if (!names.TryGetValue(column, out var i))
                {
                    return null;
                }
                index[field] = i;
            }
        }
        else
        {
            foreach (var (field, column) in settings.Fields!)
            {
                index[field] = int.Parse(column, NumberStyles.None, CultureInfo.InvariantCulture) - 1;
            }
        }
        var employees = new List<HrEmployee>();
        var problems = new List<HrProblem>();
        while (records.MoveNext())
        {
            var row = records.Current.Fields;
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }
            if (employees.Count + problems.Count >= HrMapping.MaxRecords)
            {
                return null;
            }
            var raw = index.ToDictionary(f => f.Key, f => f.Value < row.Count ? row[f.Value] : null, StringComparer.Ordinal);
            var (employee, problem) = HrMapping.Map(raw, settings.StatusValues!, settings.DateFormat);
            if (employee is not null)
            {
                employees.Add(employee);
            }
            else
            {
                problems.Add(problem!);
            }
        }
        return HrMapping.Check(employees, problems);
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.clock.settings");
        var tenantId = context.RequireTenant();
        var errors = new ValidationErrors();
        var sftp = await ClockImportPack.CheckSftpAsync(s.Sftp, tenantId, allowlist, errors, ct);
        var delimiter = s.Delimiter ?? ",";
        if (!ClockCsv.Delimiters.Contains(delimiter))
        {
            errors.Add("delimiter", "validation.clock.delimiter");
        }
        var header = s.HasHeader ?? true;
        var fields = HrRestPack.CheckFields(s.Fields, v => header
            ? !v.Any(char.IsControl)
            : int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 200, errors);
        var statuses = HrMapping.CheckStatusValues(s.StatusValues, errors);
        HrRestPack.CheckSchedule(s.DateFormat, s.SyncAt, s.TimeZone, errors);
        errors.ThrowIfAny();
        var stored = new HrSftpSettings(sftp, delimiter, header, fields, statuses, HrRestPack.Blank(s.DateFormat), s.SyncAt ?? "01:00", s.TimeZone ?? "UTC");
        return JsonSerializer.SerializeToElement(stored, JsonDefaults.Options);
    }

    /// <summary>Test connection: the server is reached with the pinned host key and the credential, and an export file is waiting.</summary>
    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { Sftp: not null } settings)
        {
            return [new PackCheck("settings", false, "integrations.test.settings_invalid", ConnectorErrorClasses.RequestRefused)];
        }
        if (await TargetAsync(connection, settings, ct) is not { } target)
        {
            return [new PackCheck("credential", false, "integrations.test.credential_missing", ConnectorErrorClasses.CredentialMissing)];
        }
        var fetch = await files.FetchAsync(target, _ => false, 0, 0, ct);
        if (fetch.ErrorClass is { } error)
        {
            return
            [
                new PackCheck("sftp", false, error switch
                {
                    ConnectorErrorClasses.HostKeyMismatch => "integrations.test.clock_host_key",
                    ConnectorErrorClasses.CredentialRefused => "integrations.test.clock_credential",
                    ConnectorErrorClasses.RequestRefused => "integrations.test.clock_directory",
                    _ => "integrations.test.clock_unreachable",
                }, error),
            ];
        }
        var matcher = ClockImportPack.FileMatcher(settings.Sftp.FilePattern ?? "*.csv");
        var waiting = fetch.Listed.Any(f => matcher.IsMatch(f.Name));
        return [new PackCheck("sftp", waiting, waiting ? "integrations.test.hr_file_found" : "integrations.test.hr_no_file", waiting ? null : ConnectorErrorClasses.RequestRefused)];
    }
}
