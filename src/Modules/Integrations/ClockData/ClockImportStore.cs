using System.Globalization;
using System.Security.Cryptography;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>The result of importing one file.</summary>
public sealed record FileImport(Guid FileId, int Imported, int Duplicates, int Unmatched);

/// <summary>An uploaded file waiting to be imported.</summary>
public sealed record PendingUpload(Guid Id, byte[] Content);

/// <summary>One clock data file as the administrator sees it (spec 017 US7 scenario 1).</summary>
public sealed record ClockFileView(
    Guid Id,
    string Source,
    string Name,
    long SizeBytes,
    string Status,
    string? ErrorKey,
    int RowCount,
    int Imported,
    int Duplicates,
    int Unmatched,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ImportedAt);

public sealed record ClockFilePage(IReadOnlyList<ClockFileView> Items, string? NextCursor);

/// <summary>A row that was not imported: its line, the employee number as written, and the reason (<see cref="ClockRowReasons"/>).</summary>
public sealed record ClockProblemView(int Line, string? EmployeeNumber, string Reason);

/// <summary>
/// Clock data records (spec 017 FR-020): files, canonical punches and the rows not imported. Rows map to people by
/// employee number through the People contract, inside the current tenant only. A punch is stored once per source
/// punch ID, so a file imported twice, or a punch repeated in a later file, adds nothing (spec 022 edge cases).
/// </summary>
public sealed class ClockImportStore(IDbSession session, IEmployeeNumbers employees, IOutbox outbox, IClock clock) : IClockData
{
    /// <summary>Rows listed per file; counts always cover every row.</summary>
    public const int ProblemsKept = 1000;

    private const int Batch = 5000;

    /// <summary>Punches are kept 400 days for attendance history; files and their problem rows 90 days, SFTP file records as long as punches so a file is never read twice.</summary>
    public static readonly TimeSpan PunchRetention = TimeSpan.FromDays(400);
    public static readonly TimeSpan FileRetention = TimeSpan.FromDays(90);

    public static string SftpFingerprint(RemoteFile f) =>
        $"sftp:{f.Name}:{f.Size.ToString(CultureInfo.InvariantCulture)}:{f.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture)}";

    public static string UploadFingerprint(byte[] content) => "upload:" + Convert.ToHexStringLower(SHA256.HashData(content));

    public async Task<HashSet<string>> FingerprintsAsync(Guid connectionId, CancellationToken ct) =>
        (await session.QueryAsync("select fingerprint from integrations.clock_file where connection_id = @connectionId and source = 'sftp'",
            new { connectionId }, r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PendingUpload>> PendingUploadsAsync(Guid connectionId, int limit, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, content from integrations.clock_file
            where connection_id = @connectionId and status = 'pending' and content is not null
            order by received_at, id limit @limit for update skip locked
            """, new { connectionId, limit }, r => new PendingUpload(r.GetGuid(0), r.GetFieldValue<byte[]>(1)), ct);

    /// <summary>
    /// Imports a file read over SFTP. Punches are written first and the file record last, so a run that stops halfway
    /// reads the file again next time and the punches already written are kept once.
    /// </summary>
    public async Task<FileImport> ImportSftpAsync(Guid connectionId, ClockCsvSettings csv, FetchedFile file, CancellationToken ct)
    {
        var id = Ids.New();
        var now = clock.UtcNow;
        if (file.Content.Length == 0 && file.File.Size > 0)
        {
            await InsertFileAsync(id, connectionId, "sftp", file.File.Name, SftpFingerprint(file.File), file.File.Size, now, null, ct);
            await RefuseAsync(id, "integrations.clock.file.too_large", ct);
            return new FileImport(id, 0, 0, 0);
        }
        var parse = ClockCsv.Parse(file.Content, csv);
        var result = await WritePunchesAsync(connectionId, id, parse, now, ct);
        await InsertFileAsync(id, connectionId, "sftp", file.File.Name, SftpFingerprint(file.File), file.File.Size, now, null, ct);
        return await FinishAsync(connectionId, id, parse, result, now, ct);
    }

    public async Task<FileImport> ImportUploadAsync(Guid connectionId, ClockCsvSettings csv, PendingUpload upload, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var parse = ClockCsv.Parse(upload.Content, csv);
        var result = await WritePunchesAsync(connectionId, upload.Id, parse, now, ct);
        return await FinishAsync(connectionId, upload.Id, parse, result, now, ct);
    }

    /// <summary>Stores an uploaded file to be imported; the same content uploaded again is refused.</summary>
    public async Task<Guid> AddUploadAsync(Guid connectionId, string name, byte[] content, Guid userId, CancellationToken ct)
    {
        var fingerprint = UploadFingerprint(content);
        if (await session.ScalarAsync<bool>("select exists (select 1 from integrations.clock_file where connection_id = @connectionId and fingerprint = @fingerprint)",
            new { connectionId, fingerprint }, ct))
        {
            throw AppException.Conflict("conflict.clock.file_seen");
        }
        var id = Ids.New();
        await InsertFileAsync(id, connectionId, "upload", name, fingerprint, content.Length, clock.UtcNow, userId, ct);
        await session.ExecAsync("update integrations.clock_file set content = @content where id = @id", new { id, content }, ct);
        return id;
    }

    private Task InsertFileAsync(Guid id, Guid connectionId, string source, string name, string fingerprint, long size, DateTimeOffset now, Guid? userId,
        CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into integrations.clock_file (tenant_id, id, connection_id, source, name, fingerprint, size_bytes, status, received_by, received_at)
            values (platform.current_tenant_id(), @id, @connectionId, @source, @name, @fingerprint, @size, 'pending', @userId, @now)
            """, new { id, connectionId, source, name, fingerprint, size, userId, now }, ct);

    private Task RefuseAsync(Guid id, string key, CancellationToken ct) =>
        session.ExecAsync("update integrations.clock_file set status = 'refused', error_key = @key, content = null, imported_at = @now where id = @id",
            new { id, key, now = clock.UtcNow }, ct);

    private sealed record Written(int Imported, int Duplicates, List<ClockRowProblem> Problems);

    private async Task<Written> WritePunchesAsync(Guid connectionId, Guid fileId, ClockParse parse, DateTimeOffset now, CancellationToken ct)
    {
        var problems = parse.Problems.ToList();
        if (parse.FileError is not null || parse.Rows.Count == 0)
        {
            return new Written(0, 0, problems);
        }
        var people = await employees.ResolveAsync(parse.Rows.Select(r => r.EmployeeNumber).ToList(), ct);
        var matched = new List<(ClockRow Row, Guid User)>(parse.Rows.Count);
        foreach (var row in parse.Rows)
        {
            if (people.TryGetValue(row.EmployeeNumber, out var user))
            {
                matched.Add((row, user));
            }
            else
            {
                problems.Add(new ClockRowProblem(row.Line, row.EmployeeNumber, ClockRowReasons.UnknownEmployee));
            }
        }
        var imported = 0;
        foreach (var chunk in matched.Chunk(Batch))
        {
            imported += await session.ExecAsync(
                """
                insert into integrations.clock_punch (tenant_id, connection_id, punch_id, user_id, at, direction, device, file_id, imported_at)
                select platform.current_tenant_id(), @connectionId, p.punch_id, p.user_id, p.at, p.direction, p.device, @fileId, @now
                from unnest(@ids::text[], @users::uuid[], @ats::timestamptz[], @directions::text[], @devices::text[]) as p(punch_id, user_id, at, direction, device)
                on conflict (tenant_id, connection_id, punch_id) do nothing
                """,
                new
                {
                    connectionId, fileId, now,
                    ids = chunk.Select(m => m.Row.PunchId).ToArray(),
                    users = chunk.Select(m => m.User).ToArray(),
                    ats = chunk.Select(m => m.Row.At).ToArray(),
                    directions = chunk.Select(m => m.Row.Direction).ToArray(),
                    devices = chunk.Select(m => m.Row.Device).ToArray(),
                }, ct);
        }
        return new Written(imported, matched.Count - imported, problems);
    }

    private async Task<FileImport> FinishAsync(Guid connectionId, Guid id, ClockParse parse, Written w, DateTimeOffset now, CancellationToken ct)
    {
        if (parse.FileError is { } error)
        {
            await RefuseAsync(id, error, ct);
            return new FileImport(id, 0, 0, 0);
        }
        var first = parse.Rows.Count == 0 ? (DateTimeOffset?)null : parse.Rows.Min(r => r.At);
        var last = parse.Rows.Count == 0 ? (DateTimeOffset?)null : parse.Rows.Max(r => r.At);
        await session.ExecAsync(
            """
            update integrations.clock_file set status = 'imported', content = null, row_count = @rows, imported = @imported, duplicates = @duplicates,
              unmatched = @unmatched, first_at = @first, last_at = @last, imported_at = @now
            where id = @id
            """, new { id, rows = parse.RowCount, imported = w.Imported, duplicates = w.Duplicates, unmatched = w.Problems.Count, first, last, now }, ct);
        var kept = w.Problems.OrderBy(p => p.Line).Take(ProblemsKept).ToList();
        if (kept.Count > 0)
        {
            await session.ExecAsync(
                """
                insert into integrations.clock_problem (tenant_id, file_id, line, employee_number, reason)
                select platform.current_tenant_id(), @id, p.line, p.employee_number, p.reason
                from unnest(@lines::int[], @numbers::text[], @reasons::text[]) as p(line, employee_number, reason)
                on conflict do nothing
                """, new { id, lines = kept.Select(p => p.Line).ToArray(), numbers = kept.Select(p => p.EmployeeNumber).ToArray(), reasons = kept.Select(p => p.Reason).ToArray() }, ct);
        }
        await outbox.EnqueueAsync(new ClockDataImported(connectionId, id, w.Imported, w.Duplicates, w.Problems.Count, first, last), ct);
        return new FileImport(id, w.Imported, w.Duplicates, w.Problems.Count);
    }

    public async Task<ClockFilePage> FilesAsync(Guid connectionId, string? cursor, int limit, CancellationToken ct)
    {
        var (at, after) = Cursor(cursor);
        var rows = await session.QueryAsync(
            """
            select id, source, name, size_bytes, status, error_key, row_count, imported, duplicates, unmatched, first_at, last_at, received_at, imported_at
            from integrations.clock_file
            where connection_id = @connectionId and (@at::timestamptz is null or (received_at, id) < (@at, @after))
            order by received_at desc, id desc limit @take
            """, new { connectionId, at, after, take = limit + 1 },
            r => new ClockFileView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.Get<string>("error_key"), r.GetInt32(6),
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.Get<DateTimeOffset?>("first_at"), r.Get<DateTimeOffset?>("last_at"),
                r.GetFieldValue<DateTimeOffset>(12), r.Get<DateTimeOffset?>("imported_at")), ct);
        var more = rows.Count > limit;
        var items = rows.Take(limit).ToList();
        return new ClockFilePage(items, more ? $"{items[^1].ReceivedAt.UtcTicks}.{items[^1].Id:N}" : null);
    }

    private static (DateTimeOffset? At, Guid After) Cursor(string? cursor)
    {
        if (cursor is null)
        {
            return (null, Guid.Empty);
        }
        var parts = cursor.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw AppException.Validation("cursor", "validation.cursor.invalid");
        }
        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }

    public async Task<IReadOnlyList<ClockProblemView>?> ProblemsAsync(Guid connectionId, Guid fileId, CancellationToken ct)
    {
        if (!await session.ScalarAsync<bool>("select exists (select 1 from integrations.clock_file where id = @fileId and connection_id = @connectionId)",
            new { fileId, connectionId }, ct))
        {
            return null;
        }
        return await session.QueryAsync("select line, employee_number, reason from integrations.clock_problem where file_id = @fileId order by line",
            new { fileId }, r => new ClockProblemView(r.GetInt32(0), r.Get<string>("employee_number"), r.GetString(2)), ct);
    }

    public async Task<IReadOnlyList<ClockPunch>> ReadAsync(long afterSequence, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        return await session.QueryAsync(
            """
            select seq, connection_id, punch_id, user_id, at, direction, device from integrations.clock_punch
            where seq > @afterSequence order by seq limit @limit
            """, new { afterSequence, limit },
            r => new ClockPunch(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.Get<string>("device")), ct);
    }

    public async Task PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync("delete from integrations.clock_punch where imported_at < @before", new { before = now - PunchRetention }, ct);
        await session.ExecAsync("delete from integrations.clock_file where source = 'upload' and received_at < @before", new { before = now - FileRetention }, ct);
        await session.ExecAsync("delete from integrations.clock_file where source = 'sftp' and received_at < @before", new { before = now - PunchRetention }, ct);
        await session.ExecAsync(
            "delete from integrations.clock_problem p using integrations.clock_file f where f.id = p.file_id and f.received_at < @before",
            new { before = now - FileRetention }, ct);
    }
}
