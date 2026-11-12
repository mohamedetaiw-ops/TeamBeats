using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.DataChanges;

public sealed record DataChangeGroupSetting(bool Enabled, bool Document);

/// <summary>A group as people see it: its fields, whether it is offered and whether a document must come with it.</summary>
public sealed record DataChangeGroupView(string Key, IReadOnlyList<DataChangeFieldView> Fields, bool Sensitive, bool Enabled, bool Document);

public sealed record DataChangeFieldView(string Key, int MaxLength, bool Required, IReadOnlyList<string>? Choices);

public sealed record DataChangeSettingsView(IReadOnlyList<DataChangeGroupView> Groups, int Version, bool CanChange)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

public sealed record DataChangeSettingsRequest(Dictionary<string, DataChangeGroupSetting>? Groups);

public sealed record DataChangeDocumentView(Guid Id, string FileName, long SizeBytes, string ContentType, string ScanState);

/// <summary>
/// The data change settings (spec 022 FR-019: documents per field) and supporting documents. A tenant chooses which
/// groups people may ask to change and which need a document; the defaults offer every group and ask for a document
/// for bank details, marital status and dependants. Only HR officers of the whole tenant change it; versioned.
/// Documents are stored under the tenant's object path, scanned before use and never sent to AI.
/// </summary>
public sealed class DataChangeConfig(IDbSession session, ITenantContext context, IClock clock, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, TenantObjectStorage storage, IMalwareScanner scanner)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxNameLength = 200;

    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/png", "image/jpeg" };

    internal static string ObjectKey(Guid id) => $"hr/data-changes/documents/{id:N}";

    private async Task<(Dictionary<string, DataChangeGroupSetting> Groups, int Version)> RowAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select groups::text, version from hr.data_change_settings", null,
            r => (Groups: r.GetString(0), Version: r.GetInt32(1)), ct);
        var stored = row == default ? [] : JsonSerializer.Deserialize<Dictionary<string, DataChangeGroupSetting>>(row.Groups, JsonDefaults.Options) ?? [];
        var groups = DataChangeCatalogue.Groups.ToDictionary(g => g.Key, g => stored.GetValueOrDefault(g.Key) ?? new DataChangeGroupSetting(true, g.DocumentByDefault));
        return (groups, row == default ? 0 : row.Version);
    }

    internal async Task<DataChangeGroupSetting> GroupAsync(string key, CancellationToken ct) => (await RowAsync(ct)).Groups[key];

    private static DataChangeGroupView View(DataChangeGroup g, DataChangeGroupSetting s) =>
        new(g.Key, g.Fields.Select(f => new DataChangeFieldView(f.Key, f.MaxLength, f.Required, f.Choices)).ToList(), g.Sensitive, s.Enabled, s.Document);

    /// <summary>The groups the signed-in person may ask to change.</summary>
    public async Task<IReadOnlyList<DataChangeGroupView>> GroupsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var (groups, _) = await RowAsync(ct);
        return DataChangeCatalogue.Groups.Where(g => groups[g.Key].Enabled).Select(g => View(g, groups[g.Key])).ToList();
    }

    public async Task<DataChangeSettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var (groups, version) = await RowAsync(ct);
        return new DataChangeSettingsView(DataChangeCatalogue.Groups.Select(g => View(g, groups[g.Key])).ToList(), version, coverage.WholeTenant);
    }

    public async Task<DataChangeSettingsView> UpdateSettingsAsync(string? ifMatch, DataChangeSettingsRequest request, CancellationToken ct)
    {
        var current = await SettingsAsync(ct);
        if (!current.CanChange)
        {
            throw AppException.Denied();
        }
        if (request.Groups is not { } given || given.Keys.Any(k => DataChangeCatalogue.Find(k) is null) || given.Values.Any(v => v is null))
        {
            throw AppException.Validation("groups", "validation.data_change.groups");
        }
        if (ifMatch != current.ETag)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.data_change.changed", null, new Dictionary<string, object?> { ["etag"] = current.ETag });
        }
        var (groups, _) = await RowAsync(ct);
        foreach (var (key, value) in given)
        {
            groups[key] = value;
        }
        await session.ExecAsync(
            """
            insert into hr.data_change_settings (tenant_id, groups, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @groups, 1, @actor, @now)
            on conflict (tenant_id) do update set groups = @groups, version = hr.data_change_settings.version + 1, updated_by = @actor, updated_at = @now
            """, new { groups = Jsonb.From(groups), actor = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrDataChangeSettingsChanged, "data_change_settings", "data_change", Changes: new { groups }), ct);
        return await SettingsAsync(ct);
    }

    // ---- documents ----

    public async Task<DataChangeDocumentView> UploadAsync(string? fileName, string? contentType, byte[] data, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrDataChange, ct);
        var user = context.RequireUser();
        var file = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var name = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation("name", "validation.data_change.document_name");
        }
        name = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        var type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (data.Length == 0 || !Allowed.Contains(type))
        {
            throw AppException.Validation("file", "validation.data_change.document_type");
        }
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.data_change.document_infected");
        }
        var id = Ids.New();
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, type, ct);
        }
        var state = verdict == ScanVerdict.Clean ? "clean" : "failed";
        await session.ExecAsync(
            """
            insert into hr.data_change_document (tenant_id, id, uploaded_by, file_name, size_bytes, content_type, scan_state, created_at)
            values (platform.current_tenant_id(), @id, @user, @name, @size, @type, @state, @now)
            """, new { id, user, name, size = data.LongLength, type, state, now = clock.UtcNow }, ct);
        return new DataChangeDocumentView(id, name, data.LongLength, type, state);
    }

    /// <summary>Why the caller cannot attach the upload, or null: it must be their own, unattached and clean.</summary>
    internal async Task<string?> ProblemAsync(Guid id, CancellationToken ct)
    {
        var state = await session.ScalarAsync<string?>(
            "select scan_state from hr.data_change_document where id = @id and uploaded_by = @user and request_id is null and deleted_at is null",
            new { id, user = context.RequireUser() }, ct);
        return state switch
        {
            null => "validation.data_change.document_unknown",
            "clean" => null,
            _ => "validation.data_change.document_not_clean",
        };
    }

    internal Task LinkAsync(Guid id, Guid requestId, CancellationToken ct) =>
        session.ExecAsync("update hr.data_change_document set request_id = @requestId where id = @id and request_id is null", new { id, requestId }, ct);

    internal async Task<string?> NameAsync(Guid? id, CancellationToken ct) => id is null ? null
        : await session.ScalarAsync<string?>("select file_name from hr.data_change_document where id = @id and deleted_at is null", new { id }, ct);

    /// <summary>Opens a request's document; the caller checked who may read it.</summary>
    internal async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select file_name, content_type from hr.data_change_document where id = @id and deleted_at is null and scan_state = 'clean'",
            new { id }, r => (Name: r.GetString(0), Type: r.GetString(1)), ct);
        if (row.Name is null)
        {
            throw AppException.NotFound();
        }
        var content = await storage.OpenReadAsync(ObjectKey(id), ct) ?? throw AppException.NotFound();
        return (content, row.Type, row.Name);
    }

    /// <summary>Deletes a document and its file (a sensitive group's document once the change is written).</summary>
    internal async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await storage.DeleteAsync(ObjectKey(id), ct);
        await session.ExecAsync("update hr.data_change_document set deleted_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
    }
}
