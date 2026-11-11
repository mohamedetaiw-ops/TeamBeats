using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>
/// The SFTP server clock data files are read from. <paramref name="SecretRef"/> is a secret manager reference to the
/// password or the private key (PEM or OpenSSH format). <paramref name="HostKeySha256"/> is the server's host key
/// fingerprint as <c>ssh-keygen -lf</c> prints it, with or without the <c>SHA256:</c> prefix. Files whose names match
/// <paramref name="FilePattern"/> (<c>*</c> and <c>?</c>) in <paramref name="Directory"/> are imported once each.
/// </summary>
public sealed record ClockSftpSettings(
    string? Host,
    int? Port,
    string? Username,
    string? AuthType,
    string? SecretRef,
    string? HostKeySha256,
    string? Directory,
    string? FilePattern);

/// <summary>
/// Settings of a clock data connection (spec 017 US7, FR-020). <paramref name="Source"/> is <c>sftp</c> (read daily
/// at <paramref name="ImportAt"/>, local time in <paramref name="TimeZone"/>) or <c>upload</c> (an administrator
/// uploads CSV files). The CSV shape and column mapping apply to both.
/// </summary>
public sealed record ClockImportSettings(
    string? Source,
    ClockSftpSettings? Sftp,
    string? Delimiter,
    bool? HasHeader,
    ClockColumns? Columns,
    string? TimestampFormat,
    string? DateFormat,
    string? TimeFormat,
    string? TimeZone,
    IReadOnlyList<string>? InValues,
    IReadOnlyList<string>? OutValues,
    string? ImportAt)
{
    public ClockCsvSettings Csv => new(Delimiter ?? ",", HasHeader ?? true, Columns!, TimestampFormat, DateFormat, TimeFormat, TimeZone ?? "UTC",
        InValues ?? [], OutValues ?? []);

    public TimeOnly ImportTime => TimeOnly.ParseExact(ImportAt ?? "02:00", "HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The clock data pack. Its one action imports what is waiting: new files on the SFTP server, then files uploaded for
/// the connection. It reads from the customer's server and writes only TeamBeats' own records, so a retry is always
/// safe; the action declares no inputs, so it is never a route step or an agent tool.
/// </summary>
public sealed partial class ClockImportPack(ITenantContext context, ISecretStore secrets, HostAllowlist allowlist, IClockFileSource files, ClockImportStore store)
    : IConnectorPack
{
    public const string KindName = "clock_import";
    public const string Import = "import_clock_data";

    /// <summary>Files read in one run, and the largest file read.</summary>
    public const int FilesPerRun = 20;
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] AuthTypes = ["password", "private_key"];

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationConnectors;

    public IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(Import, ConnectorSideEffects.Read, true, [])];

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))+$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^[A-Za-z0-9+/]{43}$")]
    private static partial Regex Fingerprint();

    [GeneratedRegex(@"^[A-Za-z0-9._*?-]{1,100}$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^(/[A-Za-z0-9._ -]+)*/?$|^[A-Za-z0-9._ -]+(/[A-Za-z0-9._ -]+)*/?$")]
    private static partial Regex RemotePath();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex Clock();

    public static ClockImportSettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<ClockImportSettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Regex FileMatcher(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct)
    {
        if (call.Action != Import)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        if (Parse(call.Connection.Settings) is not { Columns: not null } settings)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        var totals = new ImportTotals();
        if (settings.Source == "sftp")
        {
            if (await TargetAsync(call.Connection, settings, ct) is not { } target)
            {
                return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
            }
            var seen = await store.FingerprintsAsync(call.Connection.Id, ct);
            var matcher = FileMatcher(settings.Sftp!.FilePattern ?? "*.csv");
            var fetch = await files.FetchAsync(target, f => matcher.IsMatch(f.Name) && !seen.Contains(ClockImportStore.SftpFingerprint(f)), FilesPerRun, MaxFileBytes, ct);
            if (fetch.ErrorClass is { } error)
            {
                return error is ConnectorErrorClasses.NotResponding or ConnectorErrorClasses.InvalidResponse
                    ? PackCallResult.Technical(error)
                    : PackCallResult.Fatal(error);
            }
            foreach (var file in fetch.Files)
            {
                totals.Add(await store.ImportSftpAsync(call.Connection.Id, settings.Csv, file, ct));
            }
        }
        foreach (var pending in await store.PendingUploadsAsync(call.Connection.Id, FilesPerRun, ct))
        {
            totals.Add(await store.ImportUploadAsync(call.Connection.Id, settings.Csv, pending, ct));
        }
        var output = JsonSerializer.SerializeToElement(new { files = totals.Files, imported = totals.Imported, duplicates = totals.Duplicates, unmatched = totals.Unmatched });
        return PackCallResult.Ok(output, recordCount: totals.Imported);
    }

    private async Task<SftpTarget?> TargetAsync(PackConnection connection, ClockImportSettings settings, CancellationToken ct)
    {
        var s = settings.Sftp!;
        var secret = s.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, s.SecretRef, ct);
        return string.IsNullOrEmpty(secret)
            ? null
            : new SftpTarget(s.Host!, s.Port ?? 22, s.Username!, s.AuthType ?? "password", secret, s.HostKeySha256!, s.Directory ?? ".");
    }

    public async Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.clock.settings");
        var tenantId = context.RequireTenant();
        var errors = new ValidationErrors();
        ClockSftpSettings? sftp = null;
        if (s.Source is not ("sftp" or "upload"))
        {
            errors.Add("source", "validation.clock.source");
        }
        if (s.Source == "sftp")
        {
            sftp = await CheckSftpAsync(s.Sftp, tenantId, allowlist, errors, ct);
            if (s.ImportAt is not null && !Clock().IsMatch(s.ImportAt))
            {
                errors.Add("importAt", "validation.clock.import_at");
            }
        }
        var delimiter = s.Delimiter ?? ",";
        if (!ClockCsv.Delimiters.Contains(delimiter))
        {
            errors.Add("delimiter", "validation.clock.delimiter");
        }
        var header = s.HasHeader ?? true;
        var c = s.Columns;
        string? Column(string? value, string field, bool required)
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is null)
            {
                if (required)
                {
                    errors.Add($"columns.{field}", "validation.clock.column_required");
                }
                return null;
            }
            var ok = header ? v.Length <= 100 && !v.Any(char.IsControl) : int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 200;
            if (!ok)
            {
                errors.Add($"columns.{field}", header ? "validation.clock.column_name" : "validation.clock.column_number");
            }
            return v;
        }
        var stampSet = !string.IsNullOrWhiteSpace(c?.Timestamp);
        var columns = new ClockColumns(
            Column(c?.EmployeeNumber, "employeeNumber", true),
            Column(c?.PunchId, "punchId", false),
            Column(c?.Timestamp, "timestamp", !stampSet && (string.IsNullOrWhiteSpace(c?.Date) || string.IsNullOrWhiteSpace(c?.Time))),
            stampSet ? null : Column(c?.Date, "date", false),
            stampSet ? null : Column(c?.Time, "time", false),
            Column(c?.Direction, "direction", false),
            Column(c?.Device, "device", false));
        var named = new[] { columns.EmployeeNumber, columns.PunchId, columns.Timestamp, columns.Date, columns.Time, columns.Direction, columns.Device }
            .Where(v => v is not null).ToList();
        if (named.Distinct(StringComparer.OrdinalIgnoreCase).Count() != named.Count)
        {
            errors.Add("columns", "validation.clock.column_twice");
        }
        foreach (var (format, field) in new[] { (s.TimestampFormat, "timestampFormat"), (s.DateFormat, "dateFormat"), (s.TimeFormat, "timeFormat") })
        {
            if (format is not null && (format.Length > 50 || !FormatWorks(format)))
            {
                errors.Add(field, "validation.clock.format");
            }
        }
        var zone = s.TimeZone ?? "UTC";
        if (zone.Length > 64 || ClockCsv.FindZone(zone) is null)
        {
            errors.Add("timeZone", "validation.clock.time_zone");
        }
        var ins = Words(s.InValues);
        var outs = Words(s.OutValues);
        if (columns.Direction is not null && (ins.Count == 0 || outs.Count == 0 || ins.Intersect(outs, StringComparer.OrdinalIgnoreCase).Any()))
        {
            errors.Add("inValues", "validation.clock.direction_values");
        }
        if (ins.Concat(outs).Any(v => v.Length > 30) || ins.Count + outs.Count > 20)
        {
            errors.Add("inValues", "validation.clock.direction_values");
        }
        errors.ThrowIfAny();
        var stored = new ClockImportSettings(s.Source, sftp, delimiter, header, columns, Blank(s.TimestampFormat), stampSet ? null : Blank(s.DateFormat),
            stampSet ? null : Blank(s.TimeFormat), zone, columns.Direction is null ? [] : ins, columns.Direction is null ? [] : outs,
            s.Source == "sftp" ? s.ImportAt ?? "02:00" : null);
        return JsonSerializer.SerializeToElement(stored, Stored);
    }

    /// <summary>Checks SFTP settings shared by clock data and HR imports and returns them in stored form.</summary>
    public static async Task<ClockSftpSettings> CheckSftpAsync(ClockSftpSettings? value, Guid tenantId, HostAllowlist allowlist, ValidationErrors errors, CancellationToken ct)
    {
        var f = value ?? new ClockSftpSettings(null, null, null, null, null, null, null, null);
        var host = f.Host?.Trim().ToLowerInvariant();
        if (host is null || host.Length > 253 || !HostName().IsMatch(host))
        {
            errors.Add("sftp.host", "validation.clock.host");
        }
        else if (!await allowlist.IsAllowedAsync(new Uri("https://" + host), ct))
        {
            errors.Add("sftp.host", "validation.custom_rest.host_not_allowed");
        }
        if (f.Port is { } p && p is < 1 or > 65535)
        {
            errors.Add("sftp.port", "validation.clock.port");
        }
        if (string.IsNullOrWhiteSpace(f.Username) || f.Username.Length > 100 || f.Username.Any(char.IsControl))
        {
            errors.Add("sftp.username", "validation.clock.username");
        }
        if (!AuthTypes.Contains(f.AuthType ?? "password"))
        {
            errors.Add("sftp.authType", "validation.custom_rest.auth_type");
        }
        if (SecretReferences.NameFor(tenantId, f.SecretRef) is null)
        {
            errors.Add("sftp.secretRef", "validation.custom_rest.secret_ref");
        }
        var key = f.HostKeySha256?.Trim();
        key = key is not null && key.StartsWith("SHA256:", StringComparison.Ordinal) ? key[7..] : key;
        key = key?.TrimEnd('=');
        if (key is null || !Fingerprint().IsMatch(key))
        {
            errors.Add("sftp.hostKeySha256", "validation.clock.host_key");
        }
        var directory = string.IsNullOrWhiteSpace(f.Directory) ? "." : f.Directory.Trim();
        if (directory.Length > 300 || (directory != "." && !RemotePath().IsMatch(directory)) || directory.Split('/').Contains(".."))
        {
            errors.Add("sftp.directory", "validation.clock.directory");
        }
        var pattern = string.IsNullOrWhiteSpace(f.FilePattern) ? "*.csv" : f.FilePattern.Trim();
        if (!Pattern().IsMatch(pattern))
        {
            errors.Add("sftp.filePattern", "validation.clock.file_pattern");
        }
        return new ClockSftpSettings(host, f.Port ?? 22, f.Username?.Trim(), f.AuthType ?? "password", f.SecretRef, key, directory, pattern);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> Words(IReadOnlyList<string>? values) =>
        (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A format is accepted when a sample time written with it reads back.</summary>
    private static bool FormatWorks(string format)
    {
        try
        {
            var sample = new DateTime(2026, 3, 15, 14, 5, 9);
            var text = sample.ToString(format, CultureInfo.InvariantCulture);
            return DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Test connection: for SFTP, that the server is reached with the pinned host key, the credential works and the directory can be listed.</summary>
    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        if (Parse(connection.Settings) is not { } settings || settings.Source != "sftp")
        {
            return [new PackCheck("source", true, "integrations.test.clock_upload")];
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
        var matcher = FileMatcher(settings.Sftp!.FilePattern ?? "*.csv");
        var waiting = fetch.Listed.Count(f => matcher.IsMatch(f.Name));
        return [new PackCheck("sftp", true, waiting > 0 ? "integrations.test.clock_files" : "integrations.test.clock_no_files")];
    }
}

/// <summary>What one run imported.</summary>
public sealed class ImportTotals
{
    public int Files { get; private set; }
    public int Imported { get; private set; }
    public int Duplicates { get; private set; }
    public int Unmatched { get; private set; }

    public void Add(FileImport f)
    {
        Files++;
        Imported += f.Imported;
        Duplicates += f.Duplicates;
        Unmatched += f.Unmatched;
    }
}
