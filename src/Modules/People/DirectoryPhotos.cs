using Microsoft.Extensions.Logging;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.People;

/// <summary>
/// Directory photos (spec 010 T012, US3 scenario 3). After a directory sync, people who have not uploaded a photo get
/// their directory photo, checked and resized like an upload. Each person is checked at most once a day and at most
/// <see cref="BatchSize"/> per sync, those never checked first, so a large directory fills in over a few syncs. When the
/// directory or the conversion service cannot be reached nothing changes and the next sync tries again.
/// </summary>
public sealed class DirectoryProfilePhotos(
    IDbSession session,
    IClock clock,
    IDirectoryReader directory,
    IDirectoryPhotos photos,
    ProfilePhotos profilePhotos,
    ProfileStore store,
    IDocumentConverter converter,
    IOutbox outbox,
    ILogger<DirectoryProfilePhotos> logger)
{
    public const int BatchSize = 100;
    public static readonly TimeSpan Recheck = TimeSpan.FromDays(1);

    private sealed record Due(Guid UserId, string? Tag);

    /// <summary>Applies the due photos; returns how many people's shown photo changed.</summary>
    public async Task<int> ApplyAsync(CancellationToken ct)
    {
        if (!converter.Available || await directory.ReadAsync(ct) is not { } snapshot || !await photos.SupportsAsync(snapshot.ConnectionId, ct))
        {
            return 0;
        }
        await store.EnsureFreshAsync(ct);
        var people = snapshot.People.ToDictionary(p => p.UserId, p => p.DirectoryId);
        var now = clock.UtcNow;
        var due = await session.QueryAsync(
            """
            select user_id, directory_photo_tag from people.profile
            where user_id = any(@users) and photo_source is distinct from 'own'
              and (directory_photo_checked_at is null or directory_photo_checked_at <= @before)
            order by directory_photo_checked_at nulls first, user_id
            limit @limit
            """, new { users = people.Keys.ToArray(), before = now - Recheck, limit = BatchSize },
            r => new Due(r.GetGuid(0), r.Get<string?>("directory_photo_tag")), ct);
        var changed = 0;
        foreach (var person in due)
        {
            DirectoryPhoto photo;
            try
            {
                photo = await photos.ReadAsync(snapshot.ConnectionId, people[person.UserId], person.Tag, ct);
            }
            catch (DirectoryUnavailableException ex)
            {
                logger.LogWarning("Directory photos could not be read ({Reason}); the next sync tries again.", ex.Reason);
                break;
            }
            try
            {
                if (await profilePhotos.ApplyDirectoryAsync(person.UserId, photo, ct))
                {
                    changed++;
                    await outbox.EnqueueAsync(new ProfileUpdated(person.UserId, [ProfilePhotos.Field]), ct);
                }
            }
            catch (AppException ex) when (ex.Status == 409)
            {
                logger.LogWarning("The conversion service did not resize a directory photo; the next sync tries again.");
                break;
            }
            await session.ExecAsync("update people.profile set directory_photo_checked_at = @now where user_id = @u", new { now, u = person.UserId }, ct);
        }
        return changed;
    }
}

/// <summary>Runs after every successful directory sync run.</summary>
public sealed class ApplyDirectoryPhotos(DirectoryProfilePhotos photos) : IMessageHandler<DirectorySynced>
{
    public Task HandleAsync(DirectorySynced message, CancellationToken ct) => photos.ApplyAsync(ct);
}
