using TeamBeats.Messaging;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Closes a request when its route ends (spec 016 FR-005 lifecycle), from a decision or from the engine's completed
/// event: the request takes the status of how the route ended, a step graph route lists the steps it never reached as
/// skipped on Tracking (US5 scenario 1), and the requester is told (completed or returned).
/// </summary>
public sealed class RouteEnds(IDbSession session, IProcessEngine engine, IOutbox outbox, IClock clock, Requests requests)
{
    /// <summary>The request status for the result of the end step a step graph route reached.</summary>
    public static string? StatusFor(string? result) => result switch
    {
        RouteResults.Approved => RequestStatuses.Approved,
        RouteResults.Completed => RequestStatuses.Completed,
        RouteResults.Rejected => RequestStatuses.Rejected,
        RouteResults.Returned => RequestStatuses.Returned,
        _ => null,
    };

    internal async Task CloseAsync(RequestRow request, string status, CancellationToken ct, bool record = false)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("update services.request set status = @status, closed_at = @closed where id = @id",
            new { id = request.Id, status, closed = status == RequestStatuses.Returned ? (DateTimeOffset?)null : now }, ct);
        if (record)
        {
            await requests.EventAsync(request.Id, status, null, null, null, null, ct);
        }
        if (status is RequestStatuses.Approved or RequestStatuses.Completed && request.InstanceId is { } instance
            && (await Versions.GetAsync(session, request.ServiceId, request.ServiceVersion, ct))?.Route.Graph is { } graph)
        {
            var reached = (await engine.HistoryAsync(instance, ct)).Select(h => RouteGraph.StepIdOf(h.ElementId)).ToHashSet(StringComparer.Ordinal);
            foreach (var step in graph.Steps.Where(s => s.Type is not (StepTypes.Condition or StepTypes.Split or StepTypes.Join or StepTypes.End) && !reached.Contains(s.Id)))
            {
                await requests.EventAsync(request.Id, "skipped", step.Names is { Count: > 0 } names ? ServiceAdmin.Preferred(names) ?? step.Id : step.Id, null, null, null, ct);
            }
        }
        if (status == RequestStatuses.Returned)
        {
            await outbox.EnqueueAsync(new RequestReturned(request.Id, request.ServiceId), ct);
        }
        else
        {
            await outbox.EnqueueAsync(new RequestCompleted(request.Id, request.ServiceId, status), ct);
        }
    }
}
