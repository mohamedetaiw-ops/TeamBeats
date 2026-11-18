using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>What the person said, for them to edit before it is sent (spec 014 US6 scenario 1).</summary>
public sealed record TranscriptView(string Text);

public sealed record SpeakRequest(Guid? TurnId);

public sealed record VoiceOffer(int MaxSeconds);

/// <summary>An answer read aloud. The audio is made for this response and kept nowhere.</summary>
public sealed record SpokenAnswer(byte[] Audio, string MediaType);

/// <summary>
/// Voice input and read aloud (spec 014 US6, AST-016). Both go through the AI Gateway as the use cases
/// <c>transcribe</c> and <c>speak</c>, need <c>ai.assistant.voice</c>, and follow the guest rule of the assistant. Audio
/// is held in memory for the call only: it is never written to the database, object storage, the audit or a log.
/// Nothing is sent to the assistant from a transcript until the person confirms it in the composer.
/// </summary>
public sealed partial class AssistantVoice(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAiGateway gateway, AssistantGuests guests)
{
    /// <summary>About a minute of compressed speech from a browser (Opus at up to about 256 kbps).</summary>
    public const long MaxAudioBytes = 2L * 1024 * 1024;

    /// <summary>The longest answer read in one request, the limit of the speech models.</summary>
    public const int MaxSpokenChars = 4000;

    public static readonly IReadOnlySet<string> AudioTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "audio/webm", "audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav", "audio/x-wav",
    };

    private static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.Ordinal) { "ar", "en" };

    [GeneratedRegex(@"\s*\[\d+\]")]
    private static partial Regex CitationMarker();

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAssistant, ct);
        await entitlements.DemandAsync(Capabilities.AiAssistantVoice, ct);
        var user = context.RequireUser();
        await guests.DemandAsync(ct);
        return user;
    }

    /// <summary>Whether voice is offered to the caller, so the page shows its controls only when the API would serve them.</summary>
    public async Task<VoiceOffer> OfferAsync(CancellationToken ct)
    {
        await UserAsync(ct);
        return new VoiceOffer(MaxSeconds);
    }

    /// <summary>The longest spoken question the page records (spec 014 plan item 4).</summary>
    public const int MaxSeconds = 60;

    /// <summary>The media type without parameters, such as <c>audio/webm</c> from <c>audio/webm;codecs=opus</c>.</summary>
    public static string BaseType(string? contentType) => (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

    public async Task<TranscriptView> TranscribeAsync(byte[] audio, string? contentType, string? language, CancellationToken ct)
    {
        await UserAsync(ct);
        var mediaType = BaseType(contentType);
        var errors = new ValidationErrors();
        if (!AudioTypes.Contains(mediaType))
        {
            errors.Add("audio", "validation.assistant.audio_type");
        }
        if (audio.Length == 0 || audio.LongLength > MaxAudioBytes)
        {
            errors.Add("audio", "validation.assistant.audio_size");
        }
        if (language is not null && !Languages.Contains(language))
        {
            errors.Add("language", "validation.assistant.language");
        }
        errors.ThrowIfAny();

        // A spoken question is treated as a typed one: Internal, the level every assistant question is sent at.
        var result = await gateway.TranscribeAsync(new TranscriptionRequest(audio, mediaType, language, DataClassification.Internal), ct);
        if (!result.IsOk)
        {
            throw Unavailable(result.Denial);
        }
        var text = result.Text!.Trim();
        if (text.Length == 0)
        {
            throw AppException.Conflict("conflict.assistant.voice_no_speech");
        }
        return new TranscriptView(text.Length > AssistantService.MaxQuestionLength ? text[..AssistantService.MaxQuestionLength] : text);
    }

    /// <summary>Reads an answer of the person's own conversation aloud, at the conversation's classification.</summary>
    public async Task<SpokenAnswer> SpeakAsync(SpeakRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (request.TurnId is not { } turnId)
        {
            throw AppException.Validation("turnId", "validation.required");
        }
        var found = await session.QuerySingleOrDefaultAsync(
            """
            select t.answer, t.language, c.classification from agents.turn t
            join agents.conversation c on c.id = t.conversation_id
            where t.id = @turnId and t.user_id = @user and c.user_id = @user
            """,
            new { turnId, user }, r => new SpokenTurn(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2)), ct);
        if (found is not { } turn)
        {
            throw AppException.NotFound();
        }
        var text = CitationMarker().Replace(turn.Answer ?? string.Empty, string.Empty).Trim();
        if (text.Length == 0)
        {
            throw AppException.Conflict("conflict.assistant.voice_no_answer");
        }
        if (text.Length > MaxSpokenChars)
        {
            text = text[..MaxSpokenChars];
        }
        var level = Classifications.Rank(turn.Classification) is var rank and >= 0 ? (DataClassification)rank : DataClassification.Internal;
        var result = await gateway.SpeakAsync(new SpeechRequest(text, turn.Language, level), ct);
        if (!result.IsOk)
        {
            throw Unavailable(result.Denial);
        }
        return new SpokenAnswer(result.Audio!, result.MediaType ?? "audio/mpeg");
    }

    private sealed record SpokenTurn(string? Answer, string Language, string Classification);

    private static AppException Unavailable(string? denial) => AppException.Conflict(denial switch
    {
        AiDenials.ClassificationBlocked => "conflict.assistant.voice_blocked",
        AiDenials.QuotaExhausted or AiDenials.BudgetExhausted => "conflict.assistant.voice_quota",
        _ => "conflict.assistant.voice_unavailable",
    });
}
