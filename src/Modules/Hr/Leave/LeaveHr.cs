using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// Leave when the HR system owns balances (spec 019 US7, spec 022 FR-006): the ledger is not written; approved leave,
/// a cancellation and a change of dates or days are handed to HR, which queues them while it is unavailable. The
/// request shows whether HR recorded it.
/// </summary>
public sealed partial class LeaveRequests
{
    internal Task<bool> HrOwnsBalancesAsync(CancellationToken ct) => ledger.HrOwnsAsync(ct);

    /// <summary>
    /// Hands the request as it now stands to HR. A cancellation or change is handed off only for leave HR was told
    /// about, so leave approved while TeamBeats owned balances stays with the ledger.
    /// </summary>
    private async Task HandOffAsync(Guid id, string kind, CancellationToken ct)
    {
        if (await RowAsync(id, false, ct) is not { } row || await config.TypeAsync(row.TypeId, ct) is not { } type
            || (kind != HrLeaveHandOffRequest.Approved && row.HrChanges == 0))
        {
            return;
        }
        var change = await session.ScalarAsync<int>("update hr.leave_request set hr_changes = hr_changes + 1 where id = @id returning hr_changes", new { id }, ct);
        var state = await hrLeave.HandOffAsync(new HrLeaveHandOffRequest(row.Id, row.UserId, kind, type.Key, row.Start, row.End, row.StartHalf, row.EndHalf, row.Days),
            change, ct);
        await session.ExecAsync("update hr.leave_request set hr_state = @state where id = @id", new { id, state }, ct);
    }

    /// <summary>HR recorded or refused a hand-off; a newer change already sent is not overwritten by an older answer.</summary>
    internal async Task HrAnsweredAsync(HrLeaveHandOffChanged changed, CancellationToken ct)
    {
        await session.ExecAsync(
            "update hr.leave_request set hr_state = @state, hr_reference = coalesce(@reference, hr_reference) where id = @id and hr_changes = @change",
            new { id = changed.RequestId, state = changed.State, reference = changed.ExternalReference, change = changed.Change }, ct);
    }
}

/// <summary>Keeps the HR state of a leave request in step with its hand-off.</summary>
public sealed class LeaveHrAnswered(LeaveRequests requests) : IMessageHandler<HrLeaveHandOffChanged>
{
    public Task HandleAsync(HrLeaveHandOffChanged message, CancellationToken ct) => requests.HrAnsweredAsync(message, ct);
}
