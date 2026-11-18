using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>A file in a conversation as its owner sees it; <paramref name="ConversationId"/> is null until a turn uses it.</summary>
public sealed record ConversationFileView(Guid Id, Guid? ConversationId, string Name, string MediaType, long Size, string Classification, DateTimeOffset CreatedAt);

/// <summary>
/// Files a person brings to a conversation (spec 014 US5, AST-015). The upload is scanned before anything else, its
/// text is read (Office and text formats in process, PDFs and pictures through the text recognition service), and the
/// person's classification, Internal unless they choose, is checked against what the AI policy allows for the
/// assistant now. Only the text is kept, never the file. It is offered to the model as untrusted passages in its own
/// conversation only, raises the turn's classification, and is deleted with the conversation.
/// </summary>
public sealed class AssistantFiles(IDbSession session, ITenantContext context, IEntitlementService entitlements, IMalwareScanner scanner,
    ITextRecognizer recognizer, IAiClassificationPolicy classification, IAuditWriter audit, IClock clock, AssistantGuests guests)
{
    public const string Source = "assistant_file";
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int MaxFilesPerConversation = 5;
    public const int ChunkChars = 1500;
    public const int MaxPassagesPerTurn = 4;
    public static readonly TimeSpan PendingFor = TimeSpan.FromDays(1);

    public static readonly IReadOnlyDictionary<string, string> MediaTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["odt"] = "application/vnd.oasis.opendocument.text",
        ["txt"] = "text/plain",
        ["md"] = "text/markdown",
        ["csv"] = "text/csv",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
    };

    private const string Columns = "id, conversation_id, name, media_type, size_bytes, classification, created_at";

    private static ConversationFileView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.Get<Guid?>("conversation_id"), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAssistant, ct);
        await entitlements.DemandAsync(Capabilities.AiAssistantFileUpload, ct);
        var user = context.RequireUser();
        await guests.DemandAsync(ct);
        return user;
    }

    public static string? Extension(string? name)
    {
        var dot = name?.LastIndexOf('.') ?? -1;
        return dot < 0 ? null : name![(dot + 1)..].ToLowerInvariant();
    }

    public async Task<ConversationFileView> UploadAsync(Guid? conversationId, string? fileName, string? requestedClassification, byte[] data, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var name = (fileName ?? string.Empty).Trim();
        var errors = new ValidationErrors();
        if (name.Length is 0 or > 255 || Extension(name) is not { } extension || !MediaTypes.ContainsKey(extension))
        {
            errors.Add("file", "validation.assistant.file_type");
        }
        if (data.Length == 0 || data.LongLength > MaxBytes)
        {
            errors.Add("file", "validation.assistant.file_size");
        }
        var level = requestedClassification ?? "internal";
        if (Classifications.Rank(level) < 0)
        {
            errors.Add("classification", "validation.assistant.file_classification");
        }
        errors.ThrowIfAny();
        var mediaType = MediaTypes[Extension(name)!];

        if (conversationId is { } existing)
        {
            var found = await session.ScalarAsync<bool>(
                "select exists (select 1 from agents.conversation where id = @existing and user_id = @user and kind = 'chat')", new { existing, user }, ct);
            if (!found)
            {
                throw AppException.NotFound();
            }
            if (await session.ScalarAsync<long>("select count(*) from agents.conversation_file where conversation_id = @existing", new { existing }, ct) >= MaxFilesPerConversation)
            {
                throw AppException.Conflict("conflict.assistant.file_limit");
            }
        }

        // The AI policy decides before the file is read: a classification above the assistant's ceiling is refused with the reason.
        var ceiling = await classification.CeilingAsync(AiUseCases.Assistant, ct);
        if (ceiling is not { } max || Classifications.Rank(level) > (int)max)
        {
            throw AppException.Conflict("conflict.assistant.file_above_policy");
        }

        var verdict = await scanner.ScanAsync(new MemoryStream(data, writable: false), ct);
        if (verdict == ScanVerdict.Infected)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AssistantFileRefused, "assistant_file", user.ToString(),
                Changes: new { reason = "infected", size = data.LongLength }), ct);
            // The refusal is kept for the security officer although the request fails.
            await session.CommitAsync(ct);
            throw AppException.Conflict("conflict.assistant.file_infected");
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Conflict("conflict.assistant.file_scan_failed");
        }

        var text = await ReadTextAsync(data, Extension(name)!, mediaType, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw AppException.Validation("file", "validation.assistant.file_no_text");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.conversation_file (tenant_id, id, conversation_id, user_id, name, media_type, size_bytes, classification, text, created_at)
            values (platform.current_tenant_id(), @id, @conversationId, @user, @name, @mediaType, @size, @level, @text, @now)
            """, new { id, conversationId, user, name, mediaType, size = data.LongLength, level, text, now }, ct);
        // The audit keeps the file's size and classification, never its name or text.
        await audit.WriteAsync(new AuditEntry(AuditActions.AssistantFileAdded, "assistant_file", id.ToString(),
            Changes: new { conversation = conversationId, size = data.LongLength, classification = level, mediaType }), ct);
        return new ConversationFileView(id, conversationId, name, mediaType, data.LongLength, level, now);
    }

    private async Task<string?> ReadTextAsync(byte[] data, string extension, string mediaType, CancellationToken ct)
    {
        if (TextExtraction.Supports(extension))
        {
            return await TextExtraction.ReadAsync(new MemoryStream(data, writable: false), extension, ct);
        }
        if (!recognizer.Available)
        {
            return null;
        }
        var text = await recognizer.ReadAsync(new MemoryStream(data, writable: false), mediaType, recognize: true, ct);
        return text is { Length: > TextExtraction.MaxChars } ? text[..TextExtraction.MaxChars] : text;
    }

    /// <summary>The person's files of a conversation, or their files waiting for a first turn when <paramref name="conversationId"/> is null.</summary>
    public async Task<IReadOnlyList<ConversationFileView>> ListAsync(Guid? conversationId, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        return await session.QueryAsync(
            $"""
            select {Columns} from agents.conversation_file
            where user_id = @user and (conversation_id = @conversationId or @conversationId::uuid is null and conversation_id is null)
            order by created_at
            """, new { user, conversationId }, Map, ct);
    }

    /// <summary>Removes one of the person's files. Deleting stays open even when the capability or guest access is gone.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAssistant, ct);
        var user = context.RequireUser();
        if (await session.ExecAsync("delete from agents.conversation_file where id = @id and user_id = @user", new { id, user }, ct) == 0)
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>
    /// The files a turn may use: the conversation's own and the person's waiting files the turn names. A named file
    /// that is not the person's, or belongs to another conversation, is not found.
    /// </summary>
    internal async Task<IReadOnlyList<FileText>> ForTurnAsync(Guid? conversationId, IReadOnlyList<Guid>? named, CancellationToken ct)
    {
        var ids = named?.Distinct().ToArray() ?? [];
        if (conversationId is null && ids.Length == 0)
        {
            return [];
        }
        if (ids.Length > 0)
        {
            await entitlements.DemandAsync(Capabilities.AiAssistantFileUpload, ct);
        }
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            """
            select id, conversation_id, name, classification, text, created_at from agents.conversation_file
            where user_id = @user and (conversation_id = @conversationId or (id = any(@ids) and (conversation_id is null or conversation_id = @conversationId)))
            order by created_at
            """, new { user, conversationId, ids },
            r => new FileText(r.GetGuid(0), r.Get<Guid?>("conversation_id"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)), ct);
        if (ids.Any(i => rows.All(r => r.Id != i)))
        {
            throw AppException.NotFound();
        }
        if (rows.Count > MaxFilesPerConversation)
        {
            throw AppException.Conflict("conflict.assistant.file_limit");
        }
        return rows;
    }

    /// <summary>Binds the waiting files a turn used to its conversation, once the conversation exists.</summary>
    internal Task BindAsync(Guid conversationId, IReadOnlyList<FileText> files, CancellationToken ct)
    {
        var waiting = files.Where(f => f.ConversationId is null).Select(f => f.Id).ToArray();
        return waiting.Length == 0
            ? Task.CompletedTask
            : session.ExecAsync("update agents.conversation_file set conversation_id = @conversationId where id = any(@waiting) and conversation_id is null",
                new { conversationId, waiting }, ct);
    }

    /// <summary>
    /// The passages of the files for a question: the file's text in chunks, the chunks sharing most words with the
    /// question first, at most <see cref="MaxPassagesPerTurn"/>. A file above the assistant's ceiling now (the policy
    /// may have changed since the upload) is withheld, never sent.
    /// </summary>
    internal async Task<(IReadOnlyList<RetrievedPassage> Passages, int Withheld)> PassagesAsync(Guid conversationId, IReadOnlyList<FileText> files, string question,
        string language, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return ([], 0);
        }
        var ceiling = await classification.CeilingAsync(AiUseCases.Assistant, ct);
        var allowed = files.Where(f => ceiling is { } max && Classifications.Rank(f.Classification) <= (int)max).ToList();
        var withheld = files.Count - allowed.Count;
        var words = Words(question);
        var chunks = allowed.SelectMany(f => Chunks(f.Text).Select((text, i) => (File: f, Text: text, Index: i, Score: Words(text).Intersect(words).Count())))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Index).Take(MaxPassagesPerTurn).ToList();
        var passages = chunks.Select(c => new RetrievedPassage(Source, c.File.Id, "file", c.File.Name, c.Text, $"/assistant?conversation={conversationId}",
            language, c.File.Classification, null, c.File.CreatedAt, c.Score)).ToList();
        return (passages, withheld);
    }

    private static HashSet<string> Words(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '?', '!', '"', '\'', '(', ')').ToLowerInvariant()).Where(w => w.Length > 2)];

    internal static IEnumerable<string> Chunks(string text)
    {
        for (var i = 0; i < text.Length; i += ChunkChars)
        {
            var chunk = text.Substring(i, Math.Min(ChunkChars, text.Length - i)).Trim();
            if (chunk.Length > 0)
            {
                yield return chunk;
            }
        }
    }
}

internal sealed record FileText(Guid Id, Guid? ConversationId, string Name, string Classification, string Text, DateTimeOffset CreatedAt);
