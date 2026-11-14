using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record ImportView(Guid Id, Guid LibraryId, Guid? FolderId, string FileName, long SizeBytes, string Status, int Total, int Pending, int Imported,
    int Incomplete, int Skipped, int Failed, int Quarantined, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <param name="Problem">The resource key of what went wrong or needs a look; <paramref name="ProblemText"/> is it in the reader's language.</param>
public sealed record ImportRowView(int Row, string Path, string Status, string? Problem, string? ProblemText, Guid? DocumentId, bool? AuthorMatched);

public sealed record ImportRowPage(IReadOnlyList<ImportRowView> Rows, int Total);

/// <summary>Asks the import job to work through the next rows of an import; it asks again until none are left.</summary>
public sealed record DocumentImportRequested(Guid ImportId) : IIntegrationEvent;

public static class ImportRowStates
{
    public const string Pending = "pending";
    public const string Imported = "imported";
    public const string Incomplete = "incomplete";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Quarantined = "quarantined";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pending, Imported, Incomplete, Skipped, Failed, Quarantined };
}

/// <summary>
/// Bulk import (spec 011 FR-022, SC-004): a library manager uploads a ZIP with a manifest; the manifest is checked as a
/// whole, then a background job adds each row's file as a document through the normal upload checks, as the person who
/// started the import, keeping original dates and the author when the author's email matches a user. Every row keeps its
/// result for the report.
/// </summary>
public sealed class DocumentImports(
    IDbSession session,
    ITenantContext context,
    Libraries libraries,
    DocumentFiles files,
    TenantObjectStorage storage,
    IAuditWriter audit,
    IOutbox outbox,
    ILocalizer localizer,
    RequestLocale requestLocale,
    IClock clock)
{
    /// <summary>The largest ZIP accepted; the library's own file size limit still applies to each file inside.</summary>
    public const long MaxZipBytes = 4L * 1024 * 1024 * 1024;

    internal static string ObjectKey(Guid library, Guid import) => $"imports/{library:N}/{import:N}.zip";

    private const string Columns =
        """
        i.id, i.library_id, i.folder_id, i.file_name, i.size_bytes, i.status, i.total, i.created_by, i.created_at, i.finished_at,
        (select count(*) filter (where status = 'pending') from documents.import_row r where r.import_id = i.id)::int,
        (select count(*) filter (where status = 'imported') from documents.import_row r where r.import_id = i.id)::int,
        (select count(*) filter (where status = 'incomplete') from documents.import_row r where r.import_id = i.id)::int,
        (select count(*) filter (where status = 'skipped') from documents.import_row r where r.import_id = i.id)::int,
        (select count(*) filter (where status = 'failed') from documents.import_row r where r.import_id = i.id)::int,
        (select count(*) filter (where status = 'quarantined') from documents.import_row r where r.import_id = i.id)::int
        """;

    private static ImportView Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("folder_id"), r.GetString(3), r.GetInt64(4), r.GetString(5),
        r.GetInt32(6), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetInt32(14), r.GetInt32(15), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8),
        r.Get<DateTimeOffset?>("finished_at"));

    /// <summary>Stores the ZIP, checks its manifest and queues the rows. Only the library's managers import.</summary>
    public async Task<ImportView> StartAsync(Guid libraryId, string? fileName, Guid? folderId, Stream body, long? declared, CancellationToken ct)
    {
        var user = context.RequireUser();
        var library = await libraries.ManagedAsync(libraryId, ct);
        library.RequireOwn();
        if (library.Status != "active")
        {
            throw AppException.Conflict("conflict.documents.library_archived");
        }
        if (folderId is { } folder && !await session.ScalarAsync<bool>("select exists (select 1 from documents.folder where id = @folder and library_id = @libraryId)",
                new { folder, libraryId }, ct))
        {
            throw AppException.Validation("folder", "validation.documents.folder_parent");
        }
        var name = DocumentFiles.CleanName(fileName);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw AppException.Validation("file", "validation.documents.import_zip");
        }
        if (declared > MaxZipBytes)
        {
            throw AppException.Validation("file", "validation.documents.file_size");
        }

        var id = Ids.New();
        var key = ObjectKey(library.Id, id);
        var (_, size) = await files.StoreAsync(key, body, "application/zip", MaxZipBytes, ct);
        IReadOnlyList<ManifestRow> rows;
        try
        {
            rows = await ManifestAsync(key, ct);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.import (tenant_id, id, library_id, folder_id, object_key, file_name, size_bytes, total, created_by, created_at)
            values (platform.current_tenant_id(), @id, @libraryId, @folderId, @key, @name, @size, @total, @user, @now)
            """, new { id, libraryId, folderId, key, name, size, total = rows.Count, user, now }, ct);
        foreach (var chunk in rows.Chunk(500))
        {
            await session.ExecAsync(
                """
                insert into documents.import_row (tenant_id, import_id, row_number, path, "values")
                select platform.current_tenant_id(), @id, n, p, v::jsonb from unnest(@numbers, @paths, @values) as t(n, p, v)
                """,
                new
                {
                    id, numbers = chunk.Select(r => r.Number).ToArray(), paths = chunk.Select(r => r.Path).ToArray(),
                    values = chunk.Select(r => DocJson.Write(r.Values)).ToArray(),
                }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocImportStarted, "doc_import", id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id, folder = folderId, rows = rows.Count, size }), ct);
        await outbox.EnqueueAsync(new DocumentImportRequested(id), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Reads the manifest out of the stored ZIP; a ZIP without one, or with a broken one, is refused.</summary>
    private async Task<IReadOnlyList<ManifestRow>> ManifestAsync(string key, CancellationToken ct)
    {
        await using var copy = await ImportArchive.OpenAsync(storage, key, ct);
        var entry = copy.Archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, ImportManifest.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw AppException.Validation("file", "validation.documents.import_no_manifest");
        if (entry.Length > ImportManifest.MaxBytes)
        {
            throw AppException.Validation("manifest", "validation.documents.import_rows");
        }
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string text;
        try
        {
            text = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw AppException.Validation("manifest", "validation.documents.import_manifest");
        }
        return ImportManifest.Parse(text);
    }

    /// <summary>The import, for the library's managers and the person who started it.</summary>
    private async Task<ImportView> ReadableAsync(Guid id, CancellationToken ct)
    {
        var view = await session.QuerySingleOrDefaultAsync($"select {Columns} from documents.import i where i.id = @id", new { id }, Read, ct)
            ?? throw AppException.NotFound();
        var (_, rights) = await libraries.VisibleAsync(view.LibraryId, ct);
        if (!rights.Manage && view.CreatedBy != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        return view;
    }

    public Task<ImportView> GetAsync(Guid id, CancellationToken ct) => ReadableAsync(id, ct);

    public async Task<IReadOnlyList<ImportView>> ListAsync(Guid libraryId, CancellationToken ct)
    {
        await libraries.ManagedAsync(libraryId, ct);
        return await session.QueryAsync($"select {Columns} from documents.import i where i.library_id = @libraryId order by i.created_at desc, i.id limit 50",
            new { libraryId }, Read, ct);
    }

    public async Task<ImportRowPage> RowsAsync(Guid id, string? status, int limit, int offset, CancellationToken ct)
    {
        await ReadableAsync(id, ct);
        if (status is not null && !ImportRowStates.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.documents.import_status");
        }
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var rows = await session.QueryAsync(
            """
            select row_number, path, status, problem, document_id, author_matched from documents.import_row
            where import_id = @id and (@status::text is null or status = @status) order by row_number limit @limit offset @offset
            """, new { id, status, limit, offset }, ReadRow, ct);
        rows = rows.Select(r => r.Problem is { } key ? r with { ProblemText = localizer.Get(key, requestLocale.Locale) } : r).ToList();
        var total = await session.ScalarAsync<int>("select count(*)::int from documents.import_row where import_id = @id and (@status::text is null or status = @status)",
            new { id, status }, ct);
        return new ImportRowPage(rows, total);
    }

    private static ImportRowView ReadRow(Npgsql.NpgsqlDataReader r) =>
        new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.Get<string>("problem"), null, r.Get<Guid?>("document_id"), r.Get<bool?>("author_matched"));

    /// <summary>The whole report as CSV: row, path, result, problem key and document id, for checking a migration offline.</summary>
    public async Task<string> ReportCsvAsync(Guid id, CancellationToken ct)
    {
        await ReadableAsync(id, ct);
        var rows = await session.QueryAsync(
            "select row_number, path, status, problem, document_id, author_matched from documents.import_row where import_id = @id order by row_number",
            new { id }, ReadRow, ct);
        var csv = new StringBuilder("row,path,result,problem,document,author_matched\n");
        foreach (var row in rows)
        {
            csv.Append(row.Row).Append(',').Append(Quote(row.Path)).Append(',').Append(row.Status).Append(',').Append(Quote(row.Problem ?? string.Empty)).Append(',')
                .Append(row.DocumentId?.ToString() ?? string.Empty).Append(',').Append(row.AuthorMatched switch { true => "yes", false => "no", null => string.Empty }).Append('\n');
        }
        return csv.ToString();
    }

    /// <summary>Quotes a CSV field, and keeps a spreadsheet from reading it as a formula.</summary>
    internal static string Quote(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>A local copy of a stored ZIP: archives need to seek, and object storage streams do not.</summary>
internal sealed class ImportArchive : IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _file;

    private ImportArchive(string path, FileStream file, ZipArchive archive)
    {
        _path = path;
        _file = file;
        Archive = archive;
    }

    public ZipArchive Archive { get; }

    public static async Task<ImportArchive> OpenAsync(TenantObjectStorage storage, string key, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "tb-import-" + Guid.NewGuid().ToString("N") + ".zip");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using (var stored = await storage.OpenReadAsync(key, ct) ?? throw AppException.NotFound())
            {
                await stored.CopyToAsync(file, ct);
            }
            file.Position = 0;
            return new ImportArchive(path, file, new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true));
        }
        catch (InvalidDataException)
        {
            await file.DisposeAsync();
            throw AppException.Validation("file", "validation.documents.import_zip");
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Archive.Dispose();
        await _file.DisposeAsync();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

/// <summary>
/// Works through an import's pending rows, a batch per message. Each row runs in its own transaction as the person who
/// started the import, so a failed row leaves nothing behind and the others carry on; then the job asks for the next
/// batch, or finishes the import, removes the ZIP and tells its starter.
/// </summary>
public sealed class DocumentImportRunner(IServiceScopeFactory scopes, ITenantContext context, IDbSession session, TenantObjectStorage storage,
    INotificationSender notifications, IAuditWriter audit, IOutbox outbox, IClock clock, ILogger<DocumentImportRunner> logger) : IMessageHandler<DocumentImportRequested>
{
    public const int BatchSize = 100;
    public const string FinishedTemplate = "documents.import_finished";

    public static string Link(Guid import) => $"/documents/imports/{import}";

    public async Task HandleAsync(DocumentImportRequested message, CancellationToken ct)
    {
        var import = await session.QuerySingleOrDefaultAsync(
            "select library_id, folder_id, object_key, status, created_by from documents.import where id = @id for update",
            new { id = message.ImportId }, r => (Library: r.GetGuid(0), Folder: r.Get<Guid?>("folder_id"), Key: r.Get<string>("object_key"), Status: r.GetString(3),
                By: r.GetGuid(4)), ct);
        if (import.Status is null or "completed")
        {
            return;
        }
        await session.ExecAsync("update documents.import set status = 'running' where id = @id", new { id = message.ImportId }, ct);
        var rows = await session.QueryAsync(
            """select row_number, path, "values"::text from documents.import_row where import_id = @id and status = 'pending' order by row_number limit @limit""",
            new { id = message.ImportId, limit = BatchSize }, r => new ManifestRow(r.GetInt32(0), r.GetString(1), DocJson.Read<Dictionary<string, string>>(r.GetString(2))), ct);
        if (rows.Count > 0 && import.Key is not null)
        {
            await using var copy = await ImportArchive.OpenAsync(storage, import.Key, ct);
            foreach (var row in rows)
            {
                await RowAsync(message.ImportId, import.Library, import.Folder, import.By, copy.Archive, row, ct);
            }
        }
        var left = await session.ScalarAsync<int>("select count(*)::int from documents.import_row where import_id = @id and status = 'pending'", new { id = message.ImportId }, ct);
        if (left > 0 && rows.Count > 0)
        {
            await outbox.EnqueueAsync(new DocumentImportRequested(message.ImportId), ct);
            return;
        }
        await FinishAsync(message.ImportId, import.Key, import.By, ct);
    }

    private async Task FinishAsync(Guid id, string? key, Guid by, CancellationToken ct)
    {
        // Rows still pending here could not be read at all (the ZIP is gone): they fail rather than wait for ever.
        await session.ExecAsync(
            "update documents.import_row set status = 'failed', problem = 'validation.documents.import_missing', done_at = @now where import_id = @id and status = 'pending'",
            new { id, now = clock.UtcNow }, ct);
        await session.ExecAsync("update documents.import set status = 'completed', finished_at = @now, object_key = null where id = @id", new { id, now = clock.UtcNow }, ct);
        if (key is not null)
        {
            await storage.DeleteAsync(key, ct);
        }
        var counts = await session.QueryAsync("select status, count(*)::int from documents.import_row where import_id = @id group by status", new { id },
            r => (Status: r.GetString(0), Count: r.GetInt32(1)), ct);
        int Count(string status) => counts.Where(c => c.Status == status).Sum(c => c.Count);
        var done = Count(ImportRowStates.Imported) + Count(ImportRowStates.Incomplete);
        var problems = Count(ImportRowStates.Failed) + Count(ImportRowStates.Skipped) + Count(ImportRowStates.Quarantined);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocImportCompleted, "doc_import", id.ToString(),
            Changes: new { imported = Count(ImportRowStates.Imported), incomplete = Count(ImportRowStates.Incomplete), skipped = Count(ImportRowStates.Skipped),
                failed = Count(ImportRowStates.Failed), quarantined = Count(ImportRowStates.Quarantined) }), ct);
        await notifications.SendAsync(new NotificationRequest([by], NotificationCategories.DocumentUpdate, FinishedTemplate, Link(id),
            new Dictionary<string, string>
            {
                ["done"] = done.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["problems"] = problems.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, DedupeKey: $"documents.import_finished:{id:N}"), ct);
    }

    /// <summary>One row in its own scope and transaction; a problem is recorded on the row and the import carries on.</summary>
    private async Task RowAsync(Guid importId, Guid libraryId, Guid? baseFolder, Guid by, ZipArchive archive, ManifestRow row, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var accessor = sp.GetRequiredService<TenantContextAccessor>();
            accessor.SetTenant(tenant);
            accessor.SetActor(ActorType.User, by);
            var rowSession = sp.GetRequiredService<IDbSession>();
            var claimed = await rowSession.ScalarAsync<bool>(
                "select exists (select 1 from documents.import_row where import_id = @importId and row_number = @n and status = 'pending' for update skip locked)",
                new { importId, n = row.Number }, ct);
            if (!claimed)
            {
                return;
            }
            var (status, documentId, matched) = await ImportRowAsync(sp, rowSession, libraryId, baseFolder, archive, row, ct);
            await rowSession.ExecAsync(
                """
                update documents.import_row set status = @status, problem = @problem, document_id = @documentId, author_matched = @matched, done_at = @now
                where import_id = @importId and row_number = @n
                """,
                new
                {
                    importId, n = row.Number, status, documentId, matched, now = clock.UtcNow,
                    problem = status == ImportRowStates.Incomplete ? "documents.import.incomplete" : status == ImportRowStates.Quarantined ? "documents.import.quarantined" : null,
                }, ct);
            await rowSession.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (status, problem) = ex switch
            {
                AppException { Code: ProblemCodes.Conflict } a when a.DetailKey == "conflict.documents.duplicate" => (ImportRowStates.Skipped, a.DetailKey),
                AppException a => (ImportRowStates.Failed, ProblemKey(a)),
                _ => (ImportRowStates.Failed, "documents.import.error"),
            };
            if (ex is not AppException)
            {
                logger.LogWarning(ex, "Import {ImportId} row {Row} failed", importId, row.Number);
            }
            await session.ExecAsync(
                "update documents.import_row set status = @status, problem = @problem, done_at = @now where import_id = @importId and row_number = @n and status = 'pending'",
                new { importId, n = row.Number, status, problem, now = clock.UtcNow }, ct);
        }
    }

    private static string ProblemKey(AppException ex) =>
        ex.Errors?.Values.SelectMany(v => v).FirstOrDefault() ?? ex.DetailKey ?? ex.Code switch
        {
            ProblemCodes.NotFound => "documents.import.not_found",
            ProblemCodes.AuthzDenied => "documents.import.denied",
            _ => "documents.import.error",
        };

    private async Task<(string Status, Guid DocumentId, bool? Matched)> ImportRowAsync(IServiceProvider sp, IDbSession rowSession, Guid libraryId, Guid? baseFolder,
        ZipArchive archive, ManifestRow row, CancellationToken ct)
    {
        var libraries = sp.GetRequiredService<Libraries>();
        var files = sp.GetRequiredService<DocumentFiles>();
        var users = sp.GetRequiredService<IUserDirectory>();
        var now = clock.UtcNow;
        var values = row.Values;

        var entry = archive.GetEntry(row.Path) ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), row.Path, StringComparison.Ordinal))
            ?? throw AppException.Validation("path", "validation.documents.import_missing");
        var created = ImportManifest.Date(values.GetValueOrDefault("created"), now);
        var modified = ImportManifest.Date(values.GetValueOrDefault("modified"), now) ?? created;
        if (created is not null && modified < created)
        {
            throw AppException.Validation("dates", "validation.documents.import_date");
        }
        bool? matched = null;
        Guid? author = null;
        if (values.GetValueOrDefault("author") is { } email)
        {
            author = (await users.FindByEmailAsync(email.Trim(), ct))?.Id;
            matched = author is not null;
        }

        var folder = await FolderAsync(libraries, rowSession, libraryId, baseFolder, ImportManifest.FolderPath(values.GetValueOrDefault("folder"))
            ?? throw AppException.Validation("folder", "validation.documents.folder_name"), ct);
        var types = await libraries.TypesAsync(libraryId, ct);
        DocumentTypeView? type = null;
        if (values.GetValueOrDefault("type") is { } typeName)
        {
            type = types.FirstOrDefault(t => t.Status == "active" && (t.Id.ToString() == typeName.Trim()
                    || t.Names.Values.Any(n => string.Equals(n.Trim(), typeName.Trim(), StringComparison.OrdinalIgnoreCase))))
                ?? throw AppException.Validation("type", "validation.documents.type_unknown");
        }
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (column, value) in values.Where(v => v.Key.StartsWith(ImportManifest.FieldPrefix, StringComparison.Ordinal)))
        {
            var key = column[ImportManifest.FieldPrefix.Length..];
            var field = type?.Fields.FirstOrDefault(f => f.Key == key) ?? throw AppException.Validation($"metadata.{key}", "validation.documents.unknown_field");
            metadata[key] = field.Kind == FieldKinds.Boolean
                ? JsonSerializer.SerializeToElement(value.Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" => (object)true,
                    "false" or "no" or "0" => false,
                    _ => value,
                })
                : JsonSerializer.SerializeToElement(value);
        }
        var controlled = await libraries.ControlledOfAsync(type?.Id, ct) is not null;
        var name = values.GetValueOrDefault("name") ?? Path.GetFileName(row.Path);
        var request = new UploadRequest(name, folder, type?.Id, metadata, values.GetValueOrDefault("classification"), Publish: !controlled);
        DocumentView view;
        await using (var stream = entry.Open())
        {
            view = await files.UploadAsync(libraryId, request, stream, entry.Length, ct);
        }

        // Original dates and the author, where the manifest gives them; the person importing stays the owner.
        await rowSession.ExecAsync(
            """
            update documents.document set created_at = coalesce(@created, created_at), updated_at = coalesce(@modified, updated_at), created_by = coalesce(@author, created_by)
            where id = @id
            """, new { id = view.Id, created, modified, author }, ct);
        await rowSession.ExecAsync(
            "update documents.document_version set created_at = coalesce(@modified, created_at), author_user_id = coalesce(@author, author_user_id) where document_id = @id",
            new { id = view.Id, modified, author }, ct);
        var state = await rowSession.QuerySingleOrDefaultAsync(
            "select d.checked_in, v.scan_state from documents.document d join documents.document_version v on v.document_id = d.id where d.id = @id order by v.created_at desc limit 1",
            new { id = view.Id }, r => (CheckedIn: r.GetBoolean(0), Scan: r.GetString(1)), ct);
        var status = state.Scan == ScanStates.Infected ? ImportRowStates.Quarantined : state.CheckedIn ? ImportRowStates.Imported : ImportRowStates.Incomplete;
        return (status, view.Id, matched);
    }

    /// <summary>The folder for a row: the import's folder, then each named folder below it, made when missing.</summary>
    private static async Task<Guid?> FolderAsync(Libraries libraries, IDbSession rowSession, Guid libraryId, Guid? parent, IReadOnlyList<string> names, CancellationToken ct)
    {
        foreach (var name in names)
        {
            var existing = await rowSession.ScalarAsync<Guid?>(
                """
                select id from documents.folder where library_id = @libraryId and parent_id is not distinct from @parent and lower(name) = lower(@name)
                """, new { libraryId, parent, name }, ct);
            parent = existing ?? (await libraries.CreateFolderAsync(libraryId, new FolderRequest(parent, name), ct)).Id;
        }
        return parent;
    }
}
