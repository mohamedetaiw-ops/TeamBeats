using System.Text.RegularExpressions;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Shell;

/// <summary>A favorite as its owner sees it now. <paramref name="Href"/> is null when it cannot be opened, with <paramref name="Reason"/> saying why.</summary>
public sealed record FavoriteView(Guid Id, string Kind, string Label, string? Href, bool Available, string? Reason, DateTimeOffset CreatedAt);

/// <summary>
/// A new favorite: an address of the shell (<paramref name="Path"/>, an information architecture address or
/// <c>/p/{slug}</c>), or an item another module owns (<paramref name="Source"/> and <paramref name="ItemId"/>, as search
/// lists it). <paramref name="Label"/> is the person's own name for it.
/// </summary>
public sealed record FavoriteRequest(string? Path, string? Source, Guid? ItemId, string? Label);

public sealed record RecentView(string Path, string Label, DateTimeOffset VisitedAt);

public sealed record RecentRequest(string? Path, string? Label);

public static class FavoriteReasons
{
    /// <summary>The person may no longer open it, or it is gone; the two are not told apart.</summary>
    public const string NotAvailable = "not_available";
}

/// <summary>
/// Favorites and recents (spec 008 FR-017, US4 scenario 6, R2). Each person keeps up to 50 favorites and their last 30
/// visits, in their own rows. A favorite is resolved again on every read: a shell address through the same visibility
/// rule as navigation, an item through its owner's permission check (the module's search source), so one whose access
/// was revoked shows disabled with a reason and no link, and is never followed. Recents keep only the path, never a
/// query string or fragment; a recent shell address the person can no longer open is left out, and the person can
/// clear recents at any time. Nothing here is visible to anyone else.
/// </summary>
public sealed partial class Favorites(IDbSession session, ITenantContext context, ShellService shell, IEnumerable<ISearchSource> sources, IClock clock)
{
    public const int MaxFavorites = 50;
    public const int MaxRecents = 30;
    public const int MaxLabel = 200;
    public const int MaxPath = 300;

    [GeneratedRegex("^/[a-z0-9/_-]*$")]
    private static partial Regex ShellPath();

    [GeneratedRegex("^/[A-Za-z0-9/_.-]*$")]
    private static partial Regex AppPath();

    /// <summary>The path alone: no query string or fragment, no trailing slash, at most <see cref="MaxPath"/> characters.</summary>
    internal static string? Normalize(string? path, bool lower)
    {
        var value = (path ?? string.Empty).Trim();
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }
        if (value.Length > 1)
        {
            value = value.TrimEnd('/');
        }
        if (lower)
        {
            value = value.ToLowerInvariant();
        }
        if (value.Length is 0 or > MaxPath || value.Contains("//", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        return (lower ? ShellPath() : AppPath()).IsMatch(value) ? value : null;
    }

    private static string CheckLabel(string? label)
    {
        var value = (label ?? string.Empty).Trim();
        return value.Length is 0 or > MaxLabel ? throw AppException.Validation("label", "validation.experience.favorite_label") : value;
    }

    // ---- Favorites ----

    private sealed record Row(Guid Id, string Kind, string? Path, string? Source, Guid? ItemId, string? Link, string Label, DateTimeOffset CreatedAt);

    private async Task<List<Row>> RowsAsync(Guid user, CancellationToken ct) =>
        await session.QueryAsync(
            "select id, kind, path, source, item_id, link, label, created_at from experience.favorite where user_id = @user order by position, created_at",
            new { user },
            r => new Row(r.GetGuid(0), r.GetString(1), r.Get<string?>("path"), r.Get<string?>("source"), r.Get<Guid?>("item_id"), r.Get<string?>("link"),
                r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)), ct);

    public async Task<IReadOnlyList<FavoriteView>> ListAsync(string locale, CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await RowsAsync(user, ct);
        var openable = await shell.OpenableAsync(rows.Where(r => r.Path is not null).Select(r => r.Path!).ToList(), locale, ct);
        var readable = new HashSet<(string, Guid)>();
        foreach (var group in rows.Where(r => r.Kind == "item").GroupBy(r => r.Source!))
        {
            if (sources.FirstOrDefault(s => s.Source == group.Key) is not { } source)
            {
                continue;
            }
            foreach (var id in await source.ReadableAsync(group.Select(r => r.ItemId!.Value).ToList(), ct))
            {
                readable.Add((group.Key, id));
            }
        }
        return rows.Select(r =>
        {
            var available = r.Kind == "page" ? openable.ContainsKey(r.Path!) : readable.Contains((r.Source!, r.ItemId!.Value));
            var href = !available ? null : r.Kind == "page" ? r.Path : r.Link;
            return new FavoriteView(r.Id, r.Kind, r.Label, href, available, available ? null : FavoriteReasons.NotAvailable, r.CreatedAt);
        }).ToList();
    }

    /// <summary>Adds a favorite the person may open now; adding one that exists returns it.</summary>
    public async Task<FavoriteView> AddAsync(FavoriteRequest request, string locale, CancellationToken ct)
    {
        var user = context.RequireUser();
        var label = CheckLabel(request.Label);
        string kind;
        string? path = null;
        string? link = null;
        if (request.Path is not null)
        {
            path = Normalize(request.Path, lower: true) ?? throw AppException.Validation("path", "validation.experience.favorite_target");
            if (!(await shell.OpenableAsync([path], locale, ct)).ContainsKey(path))
            {
                throw AppException.Validation("path", "validation.experience.favorite_target");
            }
            kind = "page";
        }
        else if (request.Source is { } sourceName && request.ItemId is { } itemId)
        {
            var source = sources.FirstOrDefault(s => s.Source == sourceName) ?? throw AppException.Validation("source", "validation.experience.favorite_target");
            if (!(await source.ReadableAsync([itemId], ct)).Contains(itemId))
            {
                throw AppException.Validation("source", "validation.experience.favorite_target");
            }
            // The link comes from the item's owner, never from the request.
            var unit = (await source.UnitsAsync(itemId, ct)).FirstOrDefault() ?? throw AppException.Validation("source", "validation.experience.favorite_target");
            link = Normalize(unit.Link, lower: false) ?? throw AppException.Validation("source", "validation.experience.favorite_target");
            kind = "item";
        }
        else
        {
            throw AppException.Validation("path", "validation.experience.favorite_target");
        }
        var existing = (await RowsAsync(user, ct)).FirstOrDefault(r => kind == "page" ? r.Path == path : r.Source == request.Source && r.ItemId == request.ItemId);
        if (existing is null)
        {
            var count = await session.ScalarAsync<long>("select count(*) from experience.favorite where user_id = @user", new { user }, ct);
            if (count >= MaxFavorites)
            {
                throw AppException.Conflict("conflict.experience.favorites_full");
            }
            await session.ExecAsync(
                """
                insert into experience.favorite (tenant_id, id, user_id, kind, path, source, item_id, link, label, position, created_at)
                values (platform.current_tenant_id(), @id, @user, @kind, @path, @source, @itemId, @link, @label, @position, @now)
                """, new
                {
                    id = Ids.New(), user, kind, path, source = kind == "item" ? request.Source : null, itemId = kind == "item" ? request.ItemId : null, link, label,
                    position = (int)count, now = clock.UtcNow,
                }, ct);
        }
        var list = await ListAsync(locale, ct);
        return list.First(f => existing is not null ? f.Id == existing.Id : f.Kind == kind && (kind == "page" ? f.Href == path : f.Href == link) && f.Label == label);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (await session.ExecAsync("delete from experience.favorite where id = @id and user_id = @user", new { id, user }, ct) == 0)
        {
            throw AppException.NotFound();
        }
    }

    // ---- Recents ----

    /// <summary>Records a visit: the path alone, as the newest of the person's last 30.</summary>
    public async Task VisitAsync(RecentRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var path = Normalize(request.Path, lower: false) ?? throw AppException.Validation("path", "validation.experience.recent_path");
        var label = CheckLabel(request.Label);
        await session.ExecAsync(
            """
            insert into experience.recent_visit (tenant_id, user_id, path, label, visited_at) values (platform.current_tenant_id(), @user, @path, @label, @now)
            on conflict (tenant_id, user_id, path) do update set label = excluded.label, visited_at = excluded.visited_at;
            delete from experience.recent_visit where user_id = @user and path not in
              (select path from experience.recent_visit where user_id = @user order by visited_at desc limit @max);
            """, new { user, path, label, now = clock.UtcNow, max = MaxRecents }, ct);
    }

    /// <summary>The person's recent visits, newest first; a shell address they can no longer open is left out.</summary>
    public async Task<IReadOnlyList<RecentView>> RecentsAsync(string locale, CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            "select path, label, visited_at from experience.recent_visit where user_id = @user order by visited_at desc limit @max",
            new { user, max = MaxRecents }, r => new RecentView(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
        // Only addresses the shell resolves can be checked here; product pages check the person themselves when opened.
        var shellPaths = rows.Select(r => r.Path).Where(p => Normalize(p, lower: true) == p && IsShellAddress(p)).ToList();
        var openable = await shell.OpenableAsync(shellPaths, locale, ct);
        return rows.Where(r => !shellPaths.Contains(r.Path) || openable.ContainsKey(r.Path)).ToList();
    }

    public async Task ClearRecentsAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        await session.ExecAsync("delete from experience.recent_visit where user_id = @user", new { user }, ct);
    }

    /// <summary>The first path segments the product's own pages use; any other address is one the shell resolves.</summary>
    public static readonly IReadOnlySet<string> ProductSegments = new HashSet<string>(StringComparer.Ordinal)
    {
        "access-reviews", "admin", "agents", "ai-actions", "app-consent", "assets", "assistant", "attendance", "booking", "calendar", "communities",
        "compliance", "content", "dev", "documents", "expenses", "hr", "kb", "kiosk", "learning", "leave", "letters", "my-details", "notifications",
        "offline", "payslips", "people", "policies", "preboarding", "preview", "records", "release", "requests", "search", "services", "settings",
        "signage", "signin", "sites", "spaces", "tasks",
    };

    private static bool IsShellAddress(string path)
    {
        var first = path.TrimStart('/').Split('/')[0];
        return first.Length > 0 && !ProductSegments.Contains(first);
    }
}
