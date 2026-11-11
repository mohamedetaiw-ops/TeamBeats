using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
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
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Letters;

public static class LetterStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Issued = "issued";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
}

public sealed record LetterRequestInput(string? Type, string? Language, string? Addressee);

public sealed record LetterValuesRequest(Dictionary<string, string?>? Values);

/// <summary>
/// One value the letter needs. <see cref="Names"/> is set for a manual field; an HR value is named by its key in the
/// client's messages. <see cref="FromHr"/> says the value came from the HR record, not from HR typing it in.
/// </summary>
public sealed record LetterFieldView(string Key, Dictionary<string, string>? Names, string? Value, bool FromHr);

public sealed record LetterRights(bool Withdraw, bool Decide, bool EditValues, bool Download);

public sealed record LetterView(
    Guid Id,
    string Reference,
    string TypeKey,
    Dictionary<string, string> TypeNames,
    string Language,
    string? Addressee,
    PersonRef Person,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? IssuedAt,
    IReadOnlyList<LetterFieldView>? Fields,
    LetterRights Rights);

/// <summary>What the public verification page shows (FR-022): the reference, the type, the issue date and the company. Nothing else.</summary>
public sealed record LetterVerification(string Reference, Dictionary<string, string> TypeNames, DateOnly IssuedOn, string CompanyName);

internal sealed record LetterRow(
    Guid Id, string Reference, Guid UserId, Guid? OrgUnitId, Guid TypeId, Dictionary<string, string> TypeNames, string Language, string? Addressee,
    Dictionary<string, string> ManualValues, string Status, Guid? InstanceId, DateTimeOffset? DecidedAt, string? ObjectKey, DateTimeOffset? IssuedAt,
    int IssueAttempts, DateTimeOffset CreatedAt);

/// <summary>
/// HR letters and certificates (spec 022 US7, FR-021, FR-022). The person asks for a letter of a type that applies to
/// them, in Arabic or English, optionally addressed to someone. The letter is filled from the type's template with
/// the person's HR record; a type that needs approval goes to HR officers covering the person, who type in what the
/// template needs and HR does not hold, and nobody decides their own letter. Once approved, the letter is printed to a
/// PDF with its reference and a verification code, of which only a hash is kept, and kept as issued: a later template
/// change never changes it. When the conversion service is down the letter waits, approved, and is issued by the
/// worker. Needs <c>hr.letters</c>.
/// </summary>
public sealed class LetterRequests(
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
    IHrRecords hr,
    ITenantSettingsReader tenantSettings,
    ITenantRegistry tenants,
    IOptions<HostResolutionOptions> hosts,
    IDocumentConverter converter,
    TenantObjectStorage storage,
    ILocalizer localizer,
    LetterConfig config,
    LeaveLedger people)
{
    public const string ProcessKey = "hr.letter";
    public const int MaxAddresseeLength = 200;
    public const int MaxValueLength = 200;

    /// <summary>Letters a person may have waiting at once (smallest safe default; the spec is silent).</summary>
    public const int MaxOpen = 5;

    /// <summary>Conversion attempts before the worker stops retrying a letter (about 40 minutes at the worker's pace).</summary>
    public const int MaxIssueAttempts = 20;

    public const string IssuedTemplate = "hr.letter_issued";
    public const string RejectedTemplate = "hr.letter_rejected";
    public const string VerifyPath = "/letters/verify";

    public static string Link(Guid id) => $"/letters/{id}";

    internal static Guid? Parse(string businessKey) => Guid.TryParseExact(businessKey, "N", out var id) ? id : null;

    private static string ObjectKey(Guid id) => $"hr/letters/{id:N}.pdf";

    private const string Columns =
        "id, reference, user_id, org_unit_id, letter_type_id, type_names::text, language, addressee, manual_values::text, status, instance_id, decided_at, object_key, issued_at, issue_attempts, created_at";

    private static LetterRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Get<Guid?>("org_unit_id"), r.GetGuid(4),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5), JsonDefaults.Options) ?? [], r.GetString(6), r.Get<string?>("addressee"),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(8), JsonDefaults.Options) ?? [], r.GetString(9), r.Get<Guid?>("instance_id"),
        r.Get<DateTimeOffset?>("decided_at"), r.Get<string?>("object_key"), r.Get<DateTimeOffset?>("issued_at"), r.GetInt32(14), r.GetFieldValue<DateTimeOffset>(15));

    internal async Task<LetterRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.letter where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    // ---- asking ----

    public async Task<LetterView> CreateAsync(LetterRequestInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var me = context.RequireUser();
        if (await people.PersonAsync(me, ct) is not { Active: true } person)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var type = (await config.AvailableAsync(person, ct)).FirstOrDefault(t => t.Key == input.Type);
        if (type is null)
        {
            errors.Add("type", "validation.letters.type");
        }
        var language = input.Language;
        if (language is null || !LetterConfig.Languages.Contains(language))
        {
            errors.Add("language", "validation.letters.language");
        }
        var addressee = string.IsNullOrWhiteSpace(input.Addressee) ? null : input.Addressee.Trim();
        if (addressee is not null && (addressee.Length > MaxAddresseeLength || addressee.Any(char.IsControl)))
        {
            errors.Add("addressee", "validation.letters.addressee");
        }
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.letter:' || @me::text))", new { me }, ct);
        if (await session.ScalarAsync<long>("select count(*) from hr.letter where user_id = @me and status in ('pending', 'approved')", new { me }, ct) >= MaxOpen)
        {
            throw AppException.Conflict("conflict.letters.too_many_open");
        }
        var number = await session.ScalarAsync<int>(
            """
            insert into hr.letter_counter as c (tenant_id, next_number) values (platform.current_tenant_id(), 2)
            on conflict (tenant_id) do update set next_number = c.next_number + 1
            returning next_number - 1
            """, null, ct);
        var id = Ids.New();
        var reference = "LT-" + number.ToString("000000", CultureInfo.InvariantCulture);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.letter (tenant_id, id, reference, user_id, org_unit_id, letter_type_id, type_version, type_names, language, addressee, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @reference, @me, @unit, @typeId, @version, @names, @language, @addressee, @status, @now, @now)
            """, new
            {
                id, reference, me, unit = person.PrimaryUnit, typeId = type!.Id, version = type.Version, names = Jsonb.From(type.Names), language, addressee,
                status = type.NeedsApproval ? LetterStatuses.Pending : LetterStatuses.Approved, now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterRequested, "letter", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { type = type.Key, reference, language, addressed = addressee is not null }), ct);
        var row = (await RowAsync(id, true, ct))!;
        if (type.NeedsApproval)
        {
            var instance = await engine.StartAsync(await RouteAsync(ct), id.ToString("N"), person.PrimaryUnit, me, [me], me, ct: ct);
            await session.ExecAsync("update hr.letter set instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
        }
        else
        {
            // Without approval nobody can type a missing value in, so the letter cannot be asked for (SC-004 path).
            if ((await FieldsAsync(row, type, ct)).Any(f => f.Value is null))
            {
                throw AppException.Conflict("conflict.letters.data_missing");
            }
            await IssueAsync(row, ct);
        }
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>One HR step: HR officers covering the person's unit decide.</summary>
    private async Task<string> RouteAsync(CancellationToken ct)
    {
        if (await engine.GetDefinitionAsync(ProcessKey, null, ct) is null)
        {
            await engine.PublishRouteAsync(ProcessKey, RouteSources.LetterRoute,
                new RouteSpec("letter approval", [new RouteStep("hr", "HR", new CandidateRule(Permission: Permissions.HrManage))]), ct);
        }
        return ProcessKey;
    }

    public async Task<IReadOnlyList<LetterView>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var rows = await session.QueryAsync($"select {Columns} from hr.letter where user_id = @me order by created_at desc limit 100",
            new { me = context.RequireUser() }, Read, ct);
        var list = new List<LetterView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>The letters of the people the HR officer covers, newest first.</summary>
    public async Task<IReadOnlyList<LetterView>> ListAsync(string? status, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        if (status is not null && status is not (LetterStatuses.Pending or LetterStatuses.Approved or LetterStatuses.Issued or LetterStatuses.Rejected or LetterStatuses.Withdrawn))
        {
            throw AppException.Validation("status", "validation.letters.status");
        }
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.letter where (@status::text is null or status = @status) order by created_at desc limit 500", new { status }, Read, ct);
        var list = new List<LetterView>();
        foreach (var row in rows.Where(r => r.OrgUnitId is null ? coverage.WholeTenant : coverage.Covers(r.OrgUnitId)).Take(200))
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>The person, anyone with its approval task and HR officers covering the person; else not found.</summary>
    public async Task<LetterView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        if (await RowAsync(id, false, ct) is not { } row || !(row.UserId == context.RequireUser() || await DecidingTaskAsync(row, ct) is not null || await HrCoversAsync(row, ct)))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    private async Task<bool> HrCoversAsync(LetterRow row, CancellationToken ct) =>
        await people.PersonAsync(row.UserId, ct) is { } person && await people.HrCoversAsync(person, ct);

    internal async Task<UserTaskInfo?> DecidingTaskAsync(LetterRow row, CancellationToken ct)
    {
        if (row.InstanceId is not { } instance || row.Status != LetterStatuses.Pending || row.UserId == context.RequireUser())
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

    public async Task<LetterView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        if (await RowAsync(id, true, ct) is not { } row || row.UserId != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (row.Status != LetterStatuses.Pending)
        {
            throw AppException.Conflict("conflict.letters.not_pending");
        }
        if (row.InstanceId is { } instance)
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await session.ExecAsync("update hr.letter set status = 'withdrawn', manual_values = '{}', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>
    /// HR, holding the approval task, types in the type's manual fields and any HR value the template needs that HR does
    /// not hold. An empty value clears what was typed. Values are plain text.
    /// </summary>
    public async Task<LetterView> SetValuesAsync(Guid id, LetterValuesRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        if (await RowAsync(id, true, ct) is not { } row || await DecidingTaskAsync(row, ct) is null)
        {
            throw AppException.NotFound();
        }
        var type = await config.TypeAsync(row.TypeId, ct) ?? throw AppException.NotFound();
        var allowed = (await FieldsAsync(row, type, ct)).Where(f => !f.FromHr).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(row.ManualValues, StringComparer.Ordinal);
        foreach (var (key, raw) in request.Values ?? [])
        {
            var value = raw?.Trim();
            if (!allowed.Contains(key) || value is { Length: > MaxValueLength } || value?.Any(char.IsControl) == true)
            {
                throw AppException.Validation("values", "validation.letters.value");
            }
            if (string.IsNullOrEmpty(value))
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }
        await session.ExecAsync("update hr.letter set manual_values = @values, updated_at = @now where id = @id",
            new { id, values = Jsonb.From(values), now = clock.UtcNow }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>The approval ended. An approval with a value still missing is refused, and the decision with it.</summary>
    internal async Task DecidedAsync(LetterRow row, ProcessInstanceInfo instance, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (instance.Outcome != InstanceOutcomes.Approved)
        {
            await session.ExecAsync(
                "update hr.letter set status = 'rejected', manual_values = '{}', decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
                new { id = row.Id, decider, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterRejected, "letter", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Reference, approver = decider, outcome = instance.Outcome }), ct);
            await notifications.SendAsync(new NotificationRequest([row.UserId], NotificationCategories.RequestUpdate, RejectedTemplate, Link(row.Id),
                new Dictionary<string, string> { ["reference"] = row.Reference }, DedupeKey: $"hr.letter:{row.Id:N}:rejected"), ct);
            return;
        }
        var type = await config.TypeAsync(row.TypeId, ct) ?? throw AppException.NotFound();
        if ((await FieldsAsync(row, type, ct)).Any(f => f.Value is null))
        {
            throw AppException.Conflict("conflict.letters.data_missing");
        }
        await session.ExecAsync("update hr.letter set status = 'approved', decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
            new { id = row.Id, decider, now }, ct);
        await IssueAsync((await RowAsync(row.Id, true, ct))!, ct);
    }

    // ---- issuing ----

    /// <summary>Approved letters the conversion service could not print yet; called by the worker.</summary>
    public async Task<int> IssueDueAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.letter where status = 'approved' and issue_attempts < @max order by updated_at limit 20 for update skip locked",
            new { max = MaxIssueAttempts }, Read, ct);
        var issued = 0;
        foreach (var row in rows)
        {
            if (await IssueAsync(row, ct))
            {
                issued++;
            }
        }
        return issued;
    }

    /// <summary>Prints and keeps an approved letter. False when the PDF could not be made now; the letter stays approved.</summary>
    private async Task<bool> IssueAsync(LetterRow row, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var type = await config.TypeAsync(row.TypeId, ct);
        var fields = type is null ? [] : await FieldsAsync(row, type, ct);
        if (type is null || fields.Any(f => f.Value is null))
        {
            await session.ExecAsync("update hr.letter set issue_attempts = issue_attempts + 1, updated_at = @now where id = @id", new { id = row.Id, now }, ct);
            return false;
        }
        var locale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var arabicDigits = row.Language == "ar" && locale.NumberingSystem == "arab";
        var company = (await tenantSettings.GetBrandingAsync(ct)).DisplayName;
        var body = LetterRender.Fill(type.Templates[row.Language], fields.ToDictionary(f => f.Key, f => f.Value!));
        if (arabicDigits)
        {
            body = LetterRender.ArabicDigits(body);
        }
        var code = LetterRender.NewCode();
        var footer = new List<(string Label, string Value)>
        {
            (localizer.Get("hr.letters.pdf.reference", row.Language), row.Reference),
            (localizer.Get("hr.letters.pdf.code", row.Language), code),
        };
        if (await config.VerificationEnabledAsync(ct))
        {
            var origin = await TenantOriginAsync(ct);
            footer.Add((localizer.Get("hr.letters.pdf.verify", row.Language), origin + VerifyPath));
        }
        var html = LetterRender.Html(row.Language, company, body, footer);
        await using var pdf = await converter.HtmlToPdfAsync(html, ct);
        if (pdf is null)
        {
            await session.ExecAsync("update hr.letter set issue_attempts = issue_attempts + 1, updated_at = @now where id = @id", new { id = row.Id, now }, ct);
            return false;
        }
        await storage.PutAsync(ObjectKey(row.Id), pdf, "application/pdf", ct);
        await session.ExecAsync(
            """
            update hr.letter set status = 'issued', code_hash = @hash, company_name = @company, object_key = @key, issued_at = @now, manual_values = '{}',
              type_version = @version, type_names = @names, issue_attempts = issue_attempts + 1, updated_at = @now
            where id = @id
            """, new
            {
                id = row.Id, hash = LetterRender.CodeHash(LetterRender.NormalizeCode(code)!), company, key = ObjectKey(row.Id), now, version = type.Version,
                names = Jsonb.From(type.Names),
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterIssued, "letter", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { type = type.Key, row.Reference, row.Language, version = type.Version }), ct);
        await outbox.EnqueueAsync(new LetterIssued(row.Id, row.UserId, row.Reference), ct);
        await notifications.SendAsync(new NotificationRequest([row.UserId], NotificationCategories.RequestUpdate, IssuedTemplate, Link(row.Id),
            new Dictionary<string, string> { ["reference"] = row.Reference }, DedupeKey: $"hr.letter:{row.Id:N}:issued"), ct);
        return true;
    }

    private async Task<string> TenantOriginAsync(CancellationToken ct)
    {
        var h = hosts.Value;
        if (h.Mode == InstallationMode.Dedicated)
        {
            return $"https://{h.BaseDomain}";
        }
        var tenant = await tenants.FindByIdAsync(context.RequireTenant(), ct) ?? throw new InvalidOperationException("Unknown tenant.");
        return $"https://{tenant.Key}.{h.BaseDomain}";
    }

    /// <summary>
    /// Every value the letter's template needs, in the letter's language: HR record values, the letter's own values and
    /// what HR typed in. A null value is missing.
    /// </summary>
    private async Task<List<LetterFieldView>> FieldsAsync(LetterRow row, LetterTypeRow type, CancellationToken ct)
    {
        var template = type.Templates.GetValueOrDefault(row.Language) ?? string.Empty;
        var needed = LetterRender.Placeholders(template);
        var record = (await hr.MatchedAsync([row.UserId], ct)).FirstOrDefault()?.Fields ?? new Dictionary<string, string>();
        var locale = await tenantSettings.GetLocaleSettingsAsync(ct);
        var arabicDigits = row.Language == "ar" && locale.NumberingSystem == "arab";
        string? Hr(string key)
        {
            string? Field(string f) => record.TryGetValue(f, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            return key switch
            {
                LetterRender.Name => row.Language == "ar" ? Field(HrFields.NameAr) ?? Field(HrFields.NameEn) : Field(HrFields.NameEn) ?? Field(HrFields.NameAr),
                LetterRender.HireDate => Field(HrFields.HireDate) is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? LetterRender.FormatDate(date, row.Language, arabicDigits)
                    : null,
                _ => Field(key),
            };
        }
        var fields = new List<LetterFieldView>();
        foreach (var key in needed)
        {
            var manual = type.ManualFields.FirstOrDefault(f => f.Key == key);
            var typed = row.ManualValues.GetValueOrDefault(key);
            switch (key)
            {
                case LetterRender.Company or LetterRender.Addressee or LetterRender.Date or LetterRender.Reference:
                    break; // Filled below, never missing.
                case var _ when manual is not null:
                    fields.Add(new LetterFieldView(key, manual.Names, typed, false));
                    break;
                case var _ when LetterRender.FromHr.Contains(key):
                    var fromHr = key == LetterRender.Name ? Hr(key) ?? (await users.GetAsync(row.UserId, ct))?.DisplayName : Hr(key);
                    fields.Add(fromHr is not null ? new LetterFieldView(key, null, fromHr, true) : new LetterFieldView(key, null, typed, false));
                    break;
            }
        }
        var today = await people.TodayAsync(ct);
        fields.Add(new LetterFieldView(LetterRender.Company, null, (await tenantSettings.GetBrandingAsync(ct)).DisplayName, true));
        fields.Add(new LetterFieldView(LetterRender.Addressee, null, row.Addressee ?? localizer.Get("hr.letters.addressee_default", row.Language), true));
        fields.Add(new LetterFieldView(LetterRender.Date, null, LetterRender.FormatDate(today, row.Language, arabicDigits), true));
        fields.Add(new LetterFieldView(LetterRender.Reference, null, row.Reference, true));
        return fields;
    }

    // ---- reading ----

    /// <summary>The issued PDF, for the person and HR officers covering them.</summary>
    public async Task<(Stream Content, string FileName)> PdfAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLetters, ct);
        if (await RowAsync(id, false, ct) is not { } row || !(row.UserId == context.RequireUser() || await HrCoversAsync(row, ct)))
        {
            throw AppException.NotFound();
        }
        if (row.Status != LetterStatuses.Issued || row.ObjectKey is null)
        {
            throw AppException.Conflict("conflict.letters.not_issued");
        }
        var content = await storage.OpenReadAsync(row.ObjectKey, ct) ?? throw AppException.NotFound();
        return (content, row.Reference + ".pdf");
    }

    /// <summary>
    /// The public verification page (US7 scenario 2, FR-022), on the tenant's host, without sign-in. An unknown code,
    /// a letter of another tenant, a turned off page or a plan without letters are all not found, alike.
    /// </summary>
    public async Task<LetterVerification> VerifyAsync(string code, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLetters, ct) || !await config.VerificationEnabledAsync(ct)
            || LetterRender.NormalizeCode(code) is not { } normalized)
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select id, reference, type_names::text, issued_at, company_name from hr.letter where code_hash = @hash and status = 'issued'",
            new { hash = LetterRender.CodeHash(normalized) },
            r => (Id: r.GetGuid(0), Reference: r.GetString(1), Names: r.GetString(2), IssuedAt: r.GetFieldValue<DateTimeOffset>(3), Company: r.Get<string?>("company_name")), ct);
        if (row == default)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLetterVerified, "letter", row.Id.ToString(), Changes: new { row.Reference }), ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return new LetterVerification(row.Reference, JsonSerializer.Deserialize<Dictionary<string, string>>(row.Names, JsonDefaults.Options) ?? [],
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.IssuedAt, zone).DateTime), row.Company ?? string.Empty);
    }

    private async Task<LetterView> ViewAsync(LetterRow row, CancellationToken ct)
    {
        var name = (await users.GetAsync(row.UserId, ct))?.DisplayName ?? row.UserId.ToString();
        var type = await config.TypeAsync(row.TypeId, ct);
        var me = context.RequireUser();
        var deciding = await DecidingTaskAsync(row, ct) is not null;
        var hrCovers = row.UserId != me && await HrCoversAsync(row, ct);
        // The values (a salary, say) are shown only to HR while the letter waits; the issued PDF holds them afterwards.
        var fields = type is not null && row.Status is LetterStatuses.Pending or LetterStatuses.Approved && (deciding || hrCovers)
            ? (await FieldsAsync(row, type, ct)).Where(f => f.Key is not (LetterRender.Company or LetterRender.Date or LetterRender.Reference or LetterRender.Addressee)).ToList()
            : null;
        var rights = new LetterRights(row.UserId == me && row.Status == LetterStatuses.Pending, deciding, deciding,
            row.Status == LetterStatuses.Issued && (row.UserId == me || hrCovers));
        return new LetterView(row.Id, row.Reference, type?.Key ?? string.Empty, row.TypeNames, row.Language, row.Addressee, new PersonRef(row.UserId, name), row.Status,
            row.CreatedAt, row.DecidedAt, row.IssuedAt, fields, rights);
    }
}

/// <summary>
/// Letters that need approval in the spec 016 task inbox (FR-026): HR officers covering the person decide; nobody
/// decides their own letter, directly or as a delegate (section 6).
/// </summary>
public sealed class LetterTaskSource(ITenantContext context, IProcessEngine engine, LetterRequests letters, ILocalizer localizer) : ITaskSource
{
    public bool Handles(string definitionKey) => definitionKey == LetterRequests.ProcessKey;

    private async Task<LetterRow?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct) =>
        LetterRequests.Parse(task.BusinessKey) is { } id && await letters.RowAsync(id, forUpdate, ct) is { Status: LetterStatuses.Pending } row && row.InstanceId == task.InstanceId
            ? row
            : null;

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not { } row)
        {
            return null;
        }
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get("hr.letters.task", l)
            .Replace("{type}", row.TypeNames.GetValueOrDefault(l == "ar" ? "ar" : "en") ?? row.Reference, StringComparison.Ordinal)
            .Replace("{reference}", row.Reference, StringComparison.Ordinal));
        return new TaskSubject("letter", title, LetterRequests.Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.UserId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is { } row && authority != row.UserId;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not { } row || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        await letters.DecidedAsync(row, instance, done.CompletedBy ?? context.RequireUser(), ct);
    }
}
