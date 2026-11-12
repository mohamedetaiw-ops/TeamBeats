using System.Text.Json;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// The checks every image and file shared in engagement passes before it is stored (spec 023 FR-017, spec 026): the
/// tenant's malware scanner, and for images a content safety check through the AI Gateway with a structured yes or no.
/// Galleries and post attachments both use them, so a photo is judged the same way wherever it is shared.
/// </summary>
public sealed class MediaChecks(IMalwareScanner scanner, IAiGateway gateway)
{
    public async Task<ScanVerdict> ScanAsync(byte[] data, CancellationToken ct)
    {
        using var scan = new MemoryStream(data, writable: false);
        return await scanner.ScanAsync(scan, ct);
    }

    /// <summary>True when the check finds the photo fit for a workplace, false when it does not, null when it could not tell.</summary>
    public async Task<bool?> SafeAsync(CleanPhoto photo, CancellationToken ct)
    {
        if (photo.Data.Length > AiAttachment.MaxBytes)
        {
            return null;
        }
        AiResult result;
        try
        {
            result = await gateway.ChatAsync(new AiRequest(AiUseCases.Summarize,
                [new AiMessage(AiRoles.System, SafetyPrompt), new AiMessage(AiRoles.User, "Check this photo.", Attachments: [new AiAttachment(photo.ContentType, photo.Data)])],
                DataClassification.Internal, ResponseSchema: SafetySchema, MaxTokens: 50, Temperature: 0), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        if (result.Completion is not { } completion)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(completion.Content);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("safe", out var safe) && safe.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? safe.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string SafetyPrompt =
        "You check photos shared on a company intranet. Answer only with JSON. Set safe to false when the photo shows nudity, sexual content, " +
        "graphic violence or gore, hate symbols, weapons used to threaten, drugs, or anything clearly unfit for a workplace; otherwise set safe to true.";

    private static readonly JsonElement SafetySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { safe = new { type = "boolean" } },
        required = new[] { "safe" },
        additionalProperties = false,
    });
}
