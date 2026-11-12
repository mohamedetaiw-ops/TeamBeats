using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Reads the cached Microsoft 365 room mailboxes of the tenant (spec 018 US5). The cache is empty until the Rooms
/// feature refreshes it, so a tenant without Microsoft 365 rooms has none and every room can be a TeamBeats room.
/// </summary>
internal sealed class RoomMailboxes(IDbSession session) : IRoomMailboxes
{
    public Task<bool> IsRoomMailboxAsync(string address, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(address)
            ? Task.FromResult(false)
            : session.ScalarAsync<bool>("select exists (select 1 from integrations.room_mailbox where address = @address)",
                new { address = address.Trim().ToLowerInvariant() }, ct);
}
