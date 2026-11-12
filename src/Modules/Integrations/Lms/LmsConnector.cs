using System.Globalization;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Lms;

/// <summary>
/// The LMS contract over the tenant's LMS connection (spec 028 FR-015). Reads go through the connector runtime inline
/// (3 attempts within 10 seconds), so a slow LMS answers "unavailable" rather than holding a caller; a push is a durable
/// job keyed by the assignment. The runtime records every call in the connection's run history.
/// </summary>
public sealed class LmsConnector(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements, TeamBeats.Primitives.IClock clock) : ILmsConnector
{
    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return null;
        }
        return await session.ScalarAsync<Guid?>(
            "select id from integrations.connection where kind = @kind and enabled order by created_at, id limit 1", new { kind = LmsPack.KindName }, ct);
    }

    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) => await ConnectionAsync(ct) is not null;

    private static string Failed(ConnectorCallResult result) =>
        result.Outcome == ConnectorOutcomes.Refused || result.ErrorClass is ConnectorErrorClasses.InvalidInput ? LmsResults.Refused : LmsResults.Unavailable;

    public async Task<LmsRead<LmsCourse>> CatalogueAsync(CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return new(LmsResults.NotConfigured, []);
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, LmsPack.ListCourses, JsonSerializer.SerializeToElement(new { }),
            $"lms-courses:{clock.UtcNow.UtcTicks}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("courses", out var list) != true)
        {
            return new(Failed(result), []);
        }
        return new(LmsResults.Ok, list.EnumerateArray().Select(c => new LmsCourse(c.GetProperty("external_id").GetString()!, c.GetProperty("title").GetString()!,
            Optional(c, "summary"), c.GetProperty("launch_url").GetString()!, Optional(c, "language"))).ToList());
    }

    public async Task<LmsRead<LmsCompletion>> CompletionsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return new(LmsResults.NotConfigured, []);
        }
        var input = JsonSerializer.SerializeToElement(new { since = since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) });
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, LmsPack.ListCompletions, input, $"lms-completions:{clock.UtcNow.UtcTicks}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("completions", out var list) != true)
        {
            return new(Failed(result), []);
        }
        return new(LmsResults.Ok, list.EnumerateArray().Select(c => new LmsCompletion(c.GetProperty("course_id").GetString()!, c.GetProperty("learner_email").GetString()!,
            c.GetProperty("completed_at").GetDateTimeOffset(), c.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null)).ToList());
    }

    public async Task<string> AssignAsync(LmsAssignment assignment, CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return LmsResults.NotConfigured;
        }
        var input = JsonSerializer.SerializeToElement(new
        {
            reference = assignment.AssignmentId.ToString(),
            course_id = assignment.ExternalCourseId,
            learner_email = assignment.LearnerEmail,
            due_on = assignment.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
        var result = await runtime.EnqueueAsync(new ConnectorCall(connection, LmsPack.Assign, input, $"lms-assign:{assignment.AssignmentId:N}",
            $"lms-assign:{assignment.AssignmentId:N}"), ct);
        return result.Outcome switch
        {
            ConnectorOutcomes.Queued or ConnectorOutcomes.InProgress or ConnectorOutcomes.Succeeded => LmsResults.Ok,
            ConnectorOutcomes.Refused => LmsResults.Refused,
            _ => LmsResults.Unavailable,
        };
    }

    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
