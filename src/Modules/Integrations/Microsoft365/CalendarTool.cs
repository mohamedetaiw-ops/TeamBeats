using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// The calendar read tool for agents (spec 018 FR-022, US6): the person's own Outlook calendar, read as the person through
/// their delegated session, never anyone else's. It answers only while the tenant has the calendar feature on and the
/// person has connected Outlook; access while the person is signed out (FR-023) comes with scheduled agents. Subjects and
/// locations are event text written by others, so the runtime passes the answer to the model inside the untrusted
/// envelope like any tool output. Private events show as busy without their subject or location.
/// </summary>
public sealed class CalendarEventsTool(ICalendar calendar, IEntitlementService entitlements, IClock clock) : IAgentTool
{
    public const string Name = "calendar.events";
    public const int MaxDays = 14;
    public const int MaxItems = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ToolDefinition Definition { get; } = new(
        Name, "integrations", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        Schema("""{"type":"object","additionalProperties":false,"properties":{"days":{"type":"integer","minimum":1,"maximum":14}}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 15,
        "List the employee's own Outlook meetings from now for the given number of days (default 1), with subject, start, end, whether it is all day, how it shows (busy, tentative, free, out of office), location and join link.",
        Capabilities.IntegrationMicrosoft365);

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var days = 1;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("days", out var d) &&
            (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out days) || days is < 1 or > MaxDays))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var now = clock.UtcNow;
        var result = await calendar.GetEventsAsync(new CalendarQuery(now, now.AddDays(days), false), ct);
        if (result.State != M365States.Ok && !(result.State == M365States.Degraded && result.AsOf is not null))
        {
            // Not connected, turned off, needs consent and the like: a short reason the agent can pass on.
            return ToolResult.Failed("tool.calendar_" + result.State);
        }
        var items = result.Occurrences
            .Where(o => !o.IsCancelled && o.Response != "declined" && o.End > now)
            .OrderBy(o => o.Start)
            .Take(MaxItems)
            .ToList();
        var rows = items.Select(o => new
        {
            subject = o.IsPrivate ? null : o.Subject,
            o.Start,
            o.End,
            allDay = o.IsAllDay,
            showAs = o.ShowAs,
            location = o.IsPrivate ? null : o.Location,
            joinUrl = o.IsPrivate ? null : o.OnlineJoinUrl,
            isPrivate = o.IsPrivate,
        });
        var sources = items.Where(o => !string.IsNullOrEmpty(o.WebLink))
            .Select(o => new ToolSource("calendar_event", ItemId(o.Id), o.IsPrivate ? string.Empty : o.Subject ?? string.Empty, o.WebLink, "internal"))
            .ToList();
        var content = JsonSerializer.Serialize(new { stale = result.State == M365States.Degraded, asOf = result.AsOf, events = rows }, Json);
        return new ToolResult(true, content, "internal", sources);
    }

    /// <summary>Outlook event IDs are strings; sources need a stable GUID, so the ID is hashed.</summary>
    internal static Guid ItemId(string eventId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(eventId)).AsSpan(0, 16));
}
