using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// The home calendar card (spec 037 FR-007, spec 008 FR-051): the week with layer markers, today's next items and
/// alerts, opening the calendar workspace. It needs no capability; each layer is offered by the API.
/// </summary>
public sealed class CalendarComponents : IComponentRegistration
{
    public const string Overview = "calendar.overview";

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        new(Overview, "1.0.0", "TeamBeats", "^1",
            new ComponentDisplay("experience.component.calendar_overview.name", "experience.component.calendar_overview.description", "personal", "calendar"),
            new Dictionary<string, PropertySchema>
            {
                ["items"] = new("integer", Default: JsonSerializer.SerializeToElement(3), Minimum: 1, Maximum: 5),
            },
            new PlacementRules(MinSpan: 4), ["locale", "direction"], ComponentRequirements.None, ComponentEvents.None,
            ["/api/v1/calendar/summary", "/api/v1/calendar/events"]),
    ];

    public IEnumerable<string> PersonalHomeDefaults() => [$"{Overview}@1"];
}

/// <summary>
/// The employee's merged calendar for the assistant (spec 037 FR-022, US7): the same service and checks as the page,
/// for up to 31 days. Item text written by others goes to the model inside the untrusted envelope like any tool output.
/// </summary>
public sealed class MyCalendarTool(UnifiedCalendar calendar) : IAgentTool
{
    public const string Name = "calendar.my_calendar";
    public const int MaxDays = 31;

    public ToolDefinition Definition { get; } = new(
        Name, "calendar", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        CalendarToolJson.Schema("""{"type":"object","additionalProperties":false,"properties":{"days":{"type":"integer","minimum":1,"maximum":31},"layers":{"type":"array","items":{"type":"string","enum":["meeting","company","department","important","holiday","my_leave","team_leave"]}}}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 15,
        "List the employee's own unified calendar from today for the given number of days (default 1): meetings, company and department events, public holidays, their leave and, for managers, how many team members are away each day. Each item has its layer, title, dates or times, status and place.");

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var days = 1;
        string? layers = null;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("days", out var d) && (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out days) || days is < 1 or > MaxDays))
            {
                return ToolResult.Failed("tool.invalid_arguments");
            }
            if (arguments.TryGetProperty("layers", out var l))
            {
                if (l.ValueKind != JsonValueKind.Array || l.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !CalendarLayers.All.Contains(x.GetString())))
                {
                    return ToolResult.Failed("tool.invalid_arguments");
                }
                layers = string.Join(',', l.EnumerateArray().Select(x => x.GetString()));
            }
        }
        var today = await calendar.TodayAsync(ct);
        var (items, sources, _) = await calendar.CollectAsync(today, today.AddDays(days), layers ?? string.Join(',', await calendar.AvailableAsync(ct)), false, ct);
        var rows = items.Take(100).Select(i => new
        {
            i.Layer, i.Title, i.AllDay, i.Start, i.End, i.StartDate, i.EndDate, i.Status, i.Location, online = i.OnlineUrl is not null, teamAway = i.Count,
        });
        return new ToolResult(true, JsonSerializer.Serialize(new { timeZone = (await calendar.ZoneAsync(ct)).Id, sources, items = rows }, CalendarToolJson.Options),
            "internal", []);
    }
}

/// <summary>Team availability aggregates for a manager's assistant (spec 037 FR-022): shares per day and leave they may see.</summary>
public sealed class TeamAvailabilityTool(UnifiedCalendar calendar) : IAgentTool
{
    public const string Name = "calendar.team_availability";
    public const int MaxDays = 62;

    public ToolDefinition Definition { get; } = new(
        Name, "calendar", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        CalendarToolJson.Schema("""{"type":"object","additionalProperties":false,"properties":{"days":{"type":"integer","minimum":1,"maximum":62}}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 15,
        "For a manager: the share of their team available on each day from today for the given number of days (default 31), days under the staffing threshold, and upcoming leave with status. Answers that there is no team for people without reports.");

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var days = 31;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("days", out var d)
            && (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out days) || days is < 1 or > MaxDays))
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        if ((await calendar.TeamAsync(ct)).Count == 0 || !(await calendar.AvailableAsync(ct)).Contains(CalendarLayers.TeamLeave))
        {
            return new ToolResult(true, JsonSerializer.Serialize(new { team = false }, CalendarToolJson.Options), "internal", []);
        }
        var today = await calendar.TodayAsync(ct);
        try
        {
            var view = await calendar.TeamAvailabilityAsync(today, today.AddDays(days), ct);
            return new ToolResult(true, JsonSerializer.Serialize(new
            {
                team = true, view.TeamSize, view.Threshold, view.TodayCounts,
                days = view.Days.Where(x => x.Working).Select(x => new { x.Date, x.Share, x.Breach }),
                upcoming = view.Upcoming.Select(u => new { u.DisplayName, u.Start, end = u.End.AddDays(-1), u.Status, type = u.TypeName }),
            }, CalendarToolJson.Options), "internal", []);
        }
        catch (AppException)
        {
            return ToolResult.Failed("tool.calendar_unavailable");
        }
    }
}

internal static class CalendarToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
