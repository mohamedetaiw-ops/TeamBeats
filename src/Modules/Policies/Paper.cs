using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>One rejected line of a paper import: its line number in the CSV and why.</summary>
public sealed record PaperRejection(int Line, string Key, string? Message = null);

public sealed record PaperImportView(
    Guid Id,
    Guid PolicyId,
    PolicyPerson ImportedBy,
    DateTimeOffset CreatedAt,
    string ScanName,
    string ScanType,
    long ScanSize,
    string Status,
    DateTimeOffset? CompletedAt,
    int Imported,
    IReadOnlyList<PaperRejection> Rejected);

/// <summary>
/// Paper attestation import (spec 025 section 14, R2). A compliance officer who manages a policy in force first attaches
/// the scanned signed forms (PDF, PNG or JPEG, scanned for malware), then imports a CSV with one line per person:
/// <c>email</c>, <c>attested_on</c> (yyyy-mm-dd) and optionally <c>version</c> (the version in force otherwise). Each
/// accepted line becomes an attestation with source and channel "paper", the importer and the import recorded, and
/// settles what the person owed for that version like an online attestation; it is counted apart from online ones.
/// There is no way to mark people as attested without a line per person and the scan. Needs <c>policy.campaigns</c>.
/// </summary>
public sealed class PaperAttestations(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IEntitlementService entitlements,
    IUserDirectory users,
    IAudiences audiences,
    ITenantSettingsReader tenantSettings,
    IMalwareScanner scanner,
    IObjectStorage storage,
    ILocalizer localizer,
    RequestLocale requestLocale,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxScanBytes = 20 * 1024 * 1024;
    public const int MaxRows = 2000;
    public const int MaxNameLength = 200;
    public const string Channel = "paper";
    public const string Source = "paper";

    private static readonly IReadOnlySet<string> ScanTypes = new HashSet<string>(StringComparer.Ordinal) { "application/pdf", "image/png", "image/jpeg" };

    private const string Columns = "id, policy_id, imported_by, created_at, scan_key, scan_name, scan_type, scan_size, status, completed_at, imported, rejected::text";

    private sealed record ImportRow(Guid Id, Guid PolicyId, Guid ImportedBy, DateTimeOffset CreatedAt, string ScanKey, string ScanName, string ScanType, long ScanSize,
        string Status, DateTimeOffset? CompletedAt, int Imported, string Rejected);

    private static ImportRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetString(8), r.Get<DateTimeOffset?>("completed_at"), r.GetInt32(10), r.GetString(11));

    private string Key(Guid id) => $"policies/paper/{context.RequireTenant():N}/{id:N}";

    private async Task<PaperImportView> ViewAsync(ImportRow row, CancellationToken ct) =>
        new(row.Id, row.PolicyId, await register.PersonAsync(row.ImportedBy, ct), row.CreatedAt, row.ScanName, row.ScanType, row.ScanSize, row.Status, row.CompletedAt,
            row.Imported, (PolicyJson.Read<List<PaperRejection>>(row.Rejected) ?? []).Select(r => r with { Message = localizer.Get(r.Key, requestLocale.Locale) }).ToList());

    /// <summary>The policy, when the caller manages it and it is in force.</summary>
    private async Task<PolicyRow> ManagedAsync(Guid policyId, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCampaigns, ct);
        context.RequireUser();
        var policy = await register.VisibleAsync(policyId, forUpdate, ct);
        if (!await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        return policy;
    }

    private async Task<ImportRow> ImportOfAsync(Guid policyId, Guid importId, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"select {Columns} from policies.paper_import where id = @importId and policy_id = @policyId{(forUpdate ? " for update" : string.Empty)}",
            new { importId, policyId }, Read, ct) ?? throw AppException.NotFound();

    /// <summary>The imports of a policy, newest first, for the people who manage it.</summary>
    public async Task<IReadOnlyList<PaperImportView>> ListAsync(Guid policyId, CancellationToken ct)
    {
        await ManagedAsync(policyId, false, ct);
        var rows = await session.QueryAsync($"select {Columns} from policies.paper_import where policy_id = @policyId order by created_at desc limit 100",
            new { policyId }, Read, ct);
        var list = new List<PaperImportView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>Step one: the scanned forms. Only a clean file is kept; the import waits for its CSV.</summary>
    public async Task<PaperImportView> AttachScanAsync(Guid policyId, string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        var policy = await ManagedAsync(policyId, false, ct);
        var user = context.RequireUser();
        if (policy.Status == PolicyStatuses.Retired || policy.CurrentVersion is null)
        {
            throw AppException.Conflict("conflict.policies.paper_not_in_force");
        }
        var file = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var name = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation("name", "validation.policies.paper_scan_name");
        }
        name = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        var type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (data.Length == 0 || !ScanTypes.Contains(type) || !Looks(type, data))
        {
            throw AppException.Validation("file", "validation.policies.paper_scan_type");
        }
        ScanVerdict verdict;
        using (var stream = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(stream, ct);
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Validation("file", verdict == ScanVerdict.Infected ? "validation.policies.paper_scan_infected" : "validation.policies.paper_scan_unchecked");
        }
        var id = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(Key(id), stream, type, ct);
        }
        await session.ExecAsync(
            """
            insert into policies.paper_import (tenant_id, id, policy_id, imported_by, created_at, scan_key, scan_name, scan_type, scan_size, scan_sha256, status)
            values (platform.current_tenant_id(), @id, @policyId, @user, @now, @key, @name, @type, @size, @hash, 'draft')
            """, new { id, policyId, user, now = clock.UtcNow, key = Key(id), name, type, size = data.LongLength, hash = Convert.ToHexStringLower(SHA256.HashData(data)) }, ct);
        return await ViewAsync(await ImportOfAsync(policyId, id, false, ct), ct);
    }

    /// <summary>The file starts as its type says: a PDF header, or the PNG or JPEG signature.</summary>
    private static bool Looks(string type, byte[] data) => type switch
    {
        "application/pdf" => data.AsSpan().StartsWith("%PDF-"u8),
        "image/png" => data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        "image/jpeg" => data.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        _ => false,
    };

    /// <summary>The scanned forms of an import, for the people who manage the policy.</summary>
    public async Task<(Stream Content, string ContentType, string FileName)> ScanAsync(Guid policyId, Guid importId, CancellationToken ct)
    {
        await ManagedAsync(policyId, false, ct);
        var row = await ImportOfAsync(policyId, importId, false, ct);
        var stream = await storage.OpenReadAsync(row.ScanKey, ct) ?? throw AppException.NotFound();
        return (stream, row.ScanType, row.ScanName);
    }

    /// <summary>
    /// Step two: the CSV. Every line is checked on its own; the good ones become paper attestations and the others are
    /// reported by line with the reason. The import is then completed and cannot run again.
    /// </summary>
    public async Task<PaperImportView> ImportAsync(Guid policyId, Guid importId, string csv, CancellationToken ct)
    {
        var policy = await ManagedAsync(policyId, true, ct);
        var user = context.RequireUser();
        var row = await ImportOfAsync(policyId, importId, true, ct);
        if (row.Status != "draft" || row.ImportedBy != user)
        {
            throw AppException.Conflict("conflict.policies.paper_import_done");
        }
        if (policy.Status == PolicyStatuses.Retired || policy.CurrentVersion is null)
        {
            throw AppException.Conflict("conflict.policies.paper_not_in_force");
        }
        var records = CsvRecords(csv);
        var header = records.Count > 0 ? records[0].Select(h => h.Trim().ToLowerInvariant()).ToList() : [];
        if (!header.Contains("email") || !header.Contains("attested_on") || header.Any(h => h is not ("email" or "attested_on" or "version")))
        {
            throw AppException.Validation("file", "validation.policies.paper_header");
        }
        var lines = records.Skip(1).Select((cells, i) => (Line: i + 2, Cells: cells)).Where(l => !l.Cells.All(string.IsNullOrWhiteSpace)).ToList();
        if (lines.Count == 0 || lines.Count > MaxRows)
        {
            throw AppException.Validation("file", "validation.policies.paper_rows");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, zone));
        var versions = new Dictionary<int, VersionRow?>();
        var rejected = new List<PaperRejection>();
        var seen = new HashSet<(Guid, int)>();
        var imported = new List<object>();
        foreach (var (line, cells) in lines)
        {
            string Cell(string column) => header.IndexOf(column) is var at and >= 0 && at < cells.Count ? cells[at].Trim() : string.Empty;
            var person = Cell("email") is { Length: > 0 } email ? await users.FindByEmailAsync(email, ct) : null;
            if (person is not { Status: "active" })
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_person"));
                continue;
            }
            var number = policy.CurrentVersion.Value;
            if (Cell("version") is { Length: > 0 } given && (!int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1))
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_version"));
                continue;
            }
            if (!versions.TryGetValue(number, out var version))
            {
                versions[number] = version = await register.VersionByNumberAsync(policy.Id, number, ct);
            }
            if (version?.EffectiveFrom is not { } effective)
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_version"));
                continue;
            }
            if (!DateOnly.TryParseExact(Cell("attested_on"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > today
                || day < DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(effective.UtcDateTime, zone)))
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_date"));
                continue;
            }
            if (!await audiences.IncludesAsync(policy.AppliesTo, person.Id, ct))
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_audience"));
                continue;
            }
            if (!seen.Add((person.Id, number)))
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_duplicate"));
                continue;
            }
            var at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), zone), TimeSpan.Zero);
            if (at > now)
            {
                at = now;
            }
            var campaign = await session.QuerySingleOrDefaultAsync(
                """
                select c.id from policies.campaign c join policies.campaign_member m on m.campaign_id = c.id
                where c.policy_id = @policy and c.version = @number and c.status = 'open' and m.user_id = @person and m.status in ('open', 'overdue')
                order by c.launched_at desc limit 1
                """, new { policy = policy.Id, number, person = person.Id }, r => (Guid?)r.GetGuid(0), ct);
            // In a campaign the person still owes it; outside one, a version is attested once (a later campaign asks again).
            if (campaign is null && await session.ScalarAsync<long>(
                    "select count(*) from policies.attestation where user_id = @person and policy_id = @policy and version = @number",
                    new { person = person.Id, policy = policy.Id, number }, ct) > 0)
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_already"));
                continue;
            }
            var locale = policy.BindingLocale is { } binding && version.Locales.Contains(binding) ? binding : version.Locales[0];
            var text = localizer.Get(PolicyCampaigns.DefaultTextKey, locale);
            var shown = string.Join("\n\n", version.Titles.GetValueOrDefault(locale) ?? string.Empty,
                string.Join("\n\n", version.Clauses.Select(c => ClauseRules.Text(c, locale))), text);
            var inserted = await session.ExecAsync(
                """
                insert into policies.attestation (tenant_id, id, user_id, policy_id, version, campaign_id, attestation_text, locale, text_hash, channel, source, attested_at,
                  imported_by, import_id)
                values (platform.current_tenant_id(), @id, @person, @policy, @number, @campaign, @text, @locale, @hash, 'paper', 'paper', @at, @user, @importId)
                on conflict do nothing
                """, new { id = Ids.New(), person = person.Id, policy = policy.Id, number, campaign, text, locale, hash = ClauseRules.Hash(shown), at, user, importId }, ct);
            if (inserted == 0)
            {
                rejected.Add(new PaperRejection(line, "validation.policies.paper_already"));
                continue;
            }
            if (campaign is { } campaignId)
            {
                await session.ExecAsync("update policies.campaign_member set status = 'attested', attested_at = @at where campaign_id = @campaignId and user_id = @person",
                    new { campaignId, person = person.Id, at }, ct);
            }
            if (number == policy.CurrentVersion)
            {
                await session.ExecAsync(
                    "update policies.obligation set status = 'attested', attested_at = @at where user_id = @person and policy_id = @policy and status = 'open'",
                    new { person = person.Id, policy = policy.Id, at }, ct);
            }
            await outbox.EnqueueAsync(new PolicyAttested(policy.Id, number, person.Id, campaign, Channel), ct);
            imported.Add(new { user = person.Id, version = number, on = day });
        }

        await session.ExecAsync(
            "update policies.paper_import set status = 'completed', completed_at = @now, imported = @count, rejected = @rejected where id = @importId",
            new { importId, now, count = imported.Count, rejected = new Jsonb(PolicyJson.Write(rejected)) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyPaperImported, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { import = importId, scan = row.ScanName, imported, rejected = rejected.Count }), ct);
        return await ViewAsync(await ImportOfAsync(policyId, importId, false, ct), ct);
    }

    /// <summary>RFC 4180 records: quoted cells may hold commas, line breaks and doubled quotes; a byte order mark is ignored.</summary>
    internal static List<List<string>> CsvRecords(string content)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var text = content.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }
            switch (ch)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    record.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(cell.ToString());
                    cell.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }
        if (cell.Length > 0 || record.Count > 0)
        {
            record.Add(cell.ToString());
            records.Add(record);
        }
        return records;
    }
}
