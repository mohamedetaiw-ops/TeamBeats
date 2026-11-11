using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Letters;

public sealed record LetterManualField(string Key, Dictionary<string, string> Names);

public sealed record LetterTypeView(
    Guid Id,
    string Key,
    Dictionary<string, string> Names,
    Dictionary<string, string> Templates,
    IReadOnlyList<LetterManualField> ManualFields,
    bool NeedsApproval,
    Guid? OrgUnitId,
    bool Active,
    int Version,
    DateTimeOffset UpdatedAt,
    bool CanEdit)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

/// <summary>What an employee sees of a letter type when asking for one: never the template.</summary>
public sealed record LetterChoice(string Key, Dictionary<string, string> Names, bool NeedsApproval);

public sealed record LetterTypeRequest(
    Dictionary<string, string>? Names,
    Dictionary<string, string>? Templates,
    IReadOnlyList<LetterManualField>? ManualFields,
    bool? NeedsApproval,
    Guid? OrgUnitId,
    bool? Active);

public sealed record LetterSettingsView(bool VerificationEnabled, int Version, bool CanChange)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

public sealed record LetterSettingsRequest(bool? VerificationEnabled);

internal sealed record LetterTypeRow(Guid Id, string Key, Dictionary<string, string> Names, Dictionary<string, string> Templates, List<LetterManualField> ManualFields,
    bool NeedsApproval, Guid? OrgUnitId, bool Active, int Version, DateTimeOffset UpdatedAt);

/// <summary>
/// Letter types and the letter settings (spec 022 FR-021, FR-022). A type has a name and a plain text template in
/// Arabic and English, the values HR types in before issuing, and whether HR approves it. HR officers manage the types
/// of the units they cover (a type for the whole tenant needs one covering it); every change is a new version, and issued
/// letters keep their own PDF. Needs <c>hr.letters</c>.
/// </summary>
public sealed class LetterConfig(IDbSession session, ITenantContext context, IClock clock, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, LeaveLedger people)
{
    public const int MaxTypes = 50;
    public const int MaxNameLength = 120;
    public const int MaxTemplateLength = 5000;
    public const int MaxManualFields = 10;
    public static readonly IReadOnlyList<string> Languages = ["en", "ar"];

    private const string Columns = "id, key, names::text, templates::text, manual_fields::text, needs_approval, org_unit_id, active, version, updated_at";

    private static LetterTypeRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2), JsonDefaults.Options) ?? [],
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), JsonDefaults.Options) ?? [],
        JsonSerializer.Deserialize<List<LetterManualField>>(r.GetString(4), JsonDefaults.Options) ?? [], r.GetBoolean(5), r.Get<Guid?>("org_unit_id"), r.GetBoolean(7),
        r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9));

    internal async Task<LetterTypeRow?> TypeByKeyAsync(string key, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.letter_type where key = @key", new { key }, Read, ct);

    internal async Task<LetterTypeRow?> TypeAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.letter_type where id = @id", new { id }, Read, ct);

    private async Task<OrgCoverage> CoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    private static LetterTypeView View(LetterTypeRow t, OrgCoverage coverage) => new(t.Id, t.Key, t.Names, t.Templates, t.ManualFields, t.NeedsApproval, t.OrgUnitId,
        t.Active, t.Version, t.UpdatedAt, t.OrgUnitId is null ? coverage.WholeTenant : coverage.Covers(t.OrgUnitId));

    /// <summary>The types HR officers see: those for the whole tenant and those of units they cover.</summary>
    public async Task<IReadOnlyList<LetterTypeView>> ListTypesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var coverage = await CoverageAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from hr.letter_type order by key", null, Read, ct);
        return rows.Where(t => t.OrgUnitId is null || coverage.Covers(t.OrgUnitId)).Select(t => View(t, coverage)).ToList();
    }

    /// <summary>The active types the signed-in person may ask for: the tenant's and those of a unit on their path.</summary>
    public async Task<IReadOnlyList<LetterChoice>> ChoicesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var person = await people.PersonAsync(context.RequireUser(), ct);
        return (await AvailableAsync(person, ct)).Select(t => new LetterChoice(t.Key, t.Names, t.NeedsApproval)).ToList();
    }

    internal async Task<List<LetterTypeRow>> AvailableAsync(LeavePerson? person, CancellationToken ct)
    {
        var rows = await session.QueryAsync($"select {Columns} from hr.letter_type where active order by key", null, Read, ct);
        return rows.Where(t => t.OrgUnitId is null || person?.Path.Contains(t.OrgUnitId.Value) == true).ToList();
    }

    public async Task<LetterTypeView> PutTypeAsync(string key, string? ifMatch, LetterTypeRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var coverage = await CoverageAsync(ct);
        var existing = await TypeByKeyAsync(key, ct);
        var unit = request.OrgUnitId;
        bool Allowed(Guid? u) => u is null ? coverage.WholeTenant : coverage.Covers(u);
        if (existing is not null && !Allowed(existing.OrgUnitId))
        {
            throw existing.OrgUnitId is null || coverage.Covers(existing.OrgUnitId) ? AppException.Denied() : AppException.NotFound();
        }
        if (!Allowed(unit))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{1,39}$"))
        {
            errors.Add("key", "validation.letters.key");
        }
        var names = request.Names?.Where(n => Languages.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value.Trim());
        if (names is null || Languages.Any(l => !names.TryGetValue(l, out var n) || n.Length is 0 or > MaxNameLength || n.Any(char.IsControl)))
        {
            errors.Add("names", "validation.letters.names");
        }
        var manual = (request.ManualFields ?? []).ToList();
        var manualKeys = new HashSet<string>(StringComparer.Ordinal);
        if (manual.Count > MaxManualFields || manual.Any(f => f is null || !System.Text.RegularExpressions.Regex.IsMatch(f.Key ?? "", "^[a-z][a-z0-9_]{1,39}$")
                || LetterRender.Known.Contains(f.Key!) || !manualKeys.Add(f.Key!)
                || f.Names is null || Languages.Any(l => !f.Names.TryGetValue(l, out var n) || n.Trim().Length is 0 or > MaxNameLength || n.Any(char.IsControl))))
        {
            errors.Add("manualFields", "validation.letters.manual_fields");
        }
        var templates = request.Templates?.Where(t => Languages.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        if (templates is null || Languages.Any(l => !templates.TryGetValue(l, out var t) || t.Length is 0 or > MaxTemplateLength
                || t.Any(c => char.IsControl(c) && c is not '\n')))
        {
            errors.Add("templates", "validation.letters.templates");
        }
        else if (templates.Values.SelectMany(LetterRender.Placeholders).Any(p => !LetterRender.Known.Contains(p) && !manualKeys.Contains(p)))
        {
            errors.Add("templates", "validation.letters.placeholder");
        }
        var needsApproval = request.NeedsApproval ?? true;
        if (manual.Count > 0 && !needsApproval)
        {
            errors.Add("needsApproval", "validation.letters.manual_needs_approval");
        }
        errors.ThrowIfAny();

        var cleanManual = manual.Select(f => new LetterManualField(f.Key, Languages.ToDictionary(l => l, l => f.Names[l].Trim()))).ToList();
        var now = clock.UtcNow;
        var args = new
        {
            id = existing?.Id ?? Ids.New(), key, names = Jsonb.From(names!), templates = Jsonb.From(templates!), manual = Jsonb.From(cleanManual), needsApproval, unit,
            active = request.Active ?? true, now, actor = context.ActorId,
        };
        if (existing is null)
        {
            if (await session.ScalarAsync<long>("select count(*) from hr.letter_type", null, ct) >= MaxTypes)
            {
                throw AppException.Conflict("conflict.letters.too_many_types");
            }
            await session.ExecAsync(
                """
                insert into hr.letter_type (tenant_id, id, key, names, templates, manual_fields, needs_approval, org_unit_id, active, version, updated_by, updated_at)
                values (platform.current_tenant_id(), @id, @key, @names, @templates, @manual, @needsApproval, @unit, @active, 1, @actor, @now)
                """, args, ct);
        }
        else
        {
            if (ifMatch != JourneyTemplates.ETag(existing.Version))
            {
                throw new AppException(ProblemCodes.Conflict, 409, "conflict.letters.changed", null,
                    new Dictionary<string, object?> { ["etag"] = JourneyTemplates.ETag(existing.Version) });
            }
            await session.ExecAsync(
                """
                update hr.letter_type set names = @names, templates = @templates, manual_fields = @manual, needs_approval = @needsApproval, org_unit_id = @unit,
                  active = @active, version = version + 1, updated_by = @actor, updated_at = @now
                where id = @id
                """, args, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterTypeChanged, "letter_type", key, TargetOrgUnitId: unit,
            Changes: new { operation = existing is null ? "created" : "updated", names, needsApproval, manualFields = cleanManual.Select(f => f.Key), args.active }), ct);
        return View((await TypeByKeyAsync(key, ct))!, coverage);
    }

    // ---- settings ----

    internal async Task<bool> VerificationEnabledAsync(CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select verification_enabled from hr.letter_settings", null, ct) ?? true;

    public async Task<LetterSettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var coverage = await CoverageAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync("select verification_enabled, version from hr.letter_settings", null,
            r => (Enabled: r.GetBoolean(0), Version: r.GetInt32(1)), ct);
        return row == default ? new LetterSettingsView(true, 0, coverage.WholeTenant) : new LetterSettingsView(row.Enabled, row.Version, coverage.WholeTenant);
    }

    /// <summary>Turns the public verification page on or off (US7 scenario 2 default: on). Only HR covering the whole tenant.</summary>
    public async Task<LetterSettingsView> UpdateSettingsAsync(string? ifMatch, LetterSettingsRequest request, CancellationToken ct)
    {
        var current = await SettingsAsync(ct);
        if (!current.CanChange)
        {
            throw AppException.Denied();
        }
        if (request.VerificationEnabled is not { } enabled)
        {
            throw AppException.Validation("verificationEnabled", "validation.required");
        }
        if (ifMatch != current.ETag)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.letters.changed", null, new Dictionary<string, object?> { ["etag"] = current.ETag });
        }
        await session.ExecAsync(
            """
            insert into hr.letter_settings (tenant_id, verification_enabled, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, 1, @actor, @now)
            on conflict (tenant_id) do update set verification_enabled = @enabled, version = hr.letter_settings.version + 1, updated_by = @actor, updated_at = @now
            """, new { enabled, actor = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterSettingsChanged, "letter_settings", "letters", Changes: new { verificationEnabled = enabled }), ct);
        return await SettingsAsync(ct);
    }
}
