using System.Globalization;
using System.Text.Json;
using TeamBeats.Localization;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Notifications;

public sealed record NotificationItem(
    Guid Id,
    string Category,
    bool Mandatory,
    bool Urgent,
    string Title,
    string Body,
    string Link,
    int Count,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor, int Unread);

public enum NotificationFilter
{
    All,
    Unread,
    Mandatory,
}

/// <summary>
/// The employee's own notification centre (spec 015 US1, FR-003). Every query is the signed-in user's rows only;
/// row-level security keeps it inside the tenant. Text is rendered in the request locale at read time.
/// </summary>
public sealed class NotificationCentre(IDbSession session, NotificationTemplates templates, RequestLocale locale, IClock clock)
{
    public const int MaxPageSize = 50;

    public async Task<NotificationPage> ListAsync(Guid userId, NotificationFilter filter, string? category, string? cursor, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var (beforeAt, beforeId) = ParseCursor(cursor);
        var rows = await session.QueryAsync(
            $"""
            select id, category, mandatory, urgent, template_key, parameters::text, link, group_count, created_at, updated_at, read_at
            from notifications.notification
            where user_id = @userId
              {(filter == NotificationFilter.Unread ? "and read_at is null" : "")}
              {(filter == NotificationFilter.Mandatory ? "and mandatory" : "")}
              and (@category::text is null or category = @category)
              and (@beforeAt::timestamptz is null or (updated_at, id) < (@beforeAt, @beforeId))
            order by updated_at desc, id desc
            limit @take
            """,
            new { userId, category, beforeAt, beforeId = beforeId ?? Guid.Empty, take = limit + 1 },
            r => (
                Id: r.GetGuid(0), Category: r.GetString(1), Mandatory: r.GetBoolean(2), Urgent: r.GetBoolean(3), Template: r.GetString(4),
                Parameters: JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5), JsonDefaults.Options) ?? [],
                Link: r.GetString(6), Count: r.GetInt32(7), CreatedAt: r.GetFieldValue<DateTimeOffset>(8), UpdatedAt: r.GetFieldValue<DateTimeOffset>(9),
                ReadAt: r.Get<DateTimeOffset?>("read_at")),
            ct);

        var items = new List<NotificationItem>();
        foreach (var row in rows.Take(limit))
        {
            var parameters = new Dictionary<string, string>(row.Parameters) { ["count"] = row.Count.ToString(CultureInfo.InvariantCulture) };
            var text = await templates.RenderAsync(row.Template, locale.Locale, parameters, ct);
            items.Add(new NotificationItem(row.Id, row.Category, row.Mandatory, row.Urgent, text.Title, text.Body, row.Link, row.Count,
                row.CreatedAt, row.UpdatedAt, row.ReadAt));
        }
        var next = rows.Count > limit ? $"{items[^1].UpdatedAt.UtcTicks}_{items[^1].Id:N}" : null;
        return new NotificationPage(items, next, await UnreadAsync(userId, ct));
    }

    public async Task<int> UnreadAsync(Guid userId, CancellationToken ct) =>
        (int)(await session.ScalarAsync<long>(
            "select count(*) from notifications.notification where user_id = @userId and read_at is null", new { userId }, ct));

    /// <summary>Marks one notification read and returns its link; the target page still checks access (FR-015).</summary>
    public async Task<string> MarkReadAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var link = await session.ScalarAsync<string>(
            """
            update notifications.notification set read_at = coalesce(read_at, @now)
            where user_id = @userId and id = @id
            returning link
            """,
            new { userId, id, now = clock.UtcNow }, ct);
        return link ?? throw AppException.NotFound();
    }

    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) =>
        session.ExecAsync(
            "update notifications.notification set read_at = @now where user_id = @userId and read_at is null",
            new { userId, now = clock.UtcNow }, ct);

    private static (DateTimeOffset? At, Guid? Id) ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return (null, null);
        }
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw AppException.Validation("cursor", "validation.cursor.invalid");
        }
        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
