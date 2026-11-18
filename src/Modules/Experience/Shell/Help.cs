using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Shell;

/// <summary>One step of a tour: a title and a short text in each language the tenant writes.</summary>
public sealed record TourStep(IReadOnlyDictionary<string, string>? Title, IReadOnlyDictionary<string, string>? Body);

/// <summary>
/// A hint or a short tour (spec 008 FR-020). <paramref name="Path"/> is the address it belongs to; it shows on that page
/// and the pages under it. A hint has a body; a tour has steps.
/// </summary>
public sealed record HelpItem(string? Key, string? Kind, string? Path, IReadOnlyDictionary<string, string>? Title, IReadOnlyDictionary<string, string>? Body,
    IReadOnlyList<TourStep>? Steps);

public sealed record HelpDefinition(IReadOnlyList<HelpItem>? Items);

public sealed record HelpAdminView(int Version, HelpDefinition Definition, DateTimeOffset? PublishedAt);

public sealed record HelpStepView(string Title, string Body);

/// <summary>A hint or tour as the viewer reads it, in their language, and whether they dismissed it.</summary>
public sealed record HelpView(string Key, string Kind, string Path, string Title, string? Body, IReadOnlyList<HelpStepView> Steps, bool Dismissed);

/// <summary>
/// In-context help and tours (spec 008 FR-020, T024): versioned, schema-validated tenant configuration that people
/// holding <c>help.edit</c> publish. Hints and tours never block the page: each can be dismissed, and reopened from the
/// help menu. Dismissals are per person.
/// </summary>
public sealed partial class Help(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuditWriter audit, IClock clock)
{
    public const string Hint = "hint";
    public const string Tour = "tour";
    public const int MaxItems = 60;
    public const int MaxSteps = 8;
    public const int MaxTitle = 120;
    public const int MaxBody = 600;

    [GeneratedRegex("^[a-z0-9_-]{1,60}$")]
    private static partial Regex KeyPattern();

    private async Task<(int Version, HelpDefinition Definition, DateTimeOffset? At)> LiveAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select version, definition::text, published_at from experience.help_config order by version desc limit 1", new { },
            r => (Found: true, Version: r.GetInt32(0), Json: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        return row.Found ? (row.Version, System.Text.Json.JsonSerializer.Deserialize<HelpDefinition>(row.Json, JsonDefaults.Options)!, row.At) : (0, new HelpDefinition([]), null);
    }

    public async Task<HelpAdminView> GetAdminAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HelpEdit, AuthzTarget.Tenant, ct);
        var (version, definition, at) = await LiveAsync(ct);
        return new HelpAdminView(version, definition, at);
    }

    private static void Text(ValidationErrors errors, string field, IReadOnlyDictionary<string, string>? text, int max, bool required)
    {
        if (text is null || text.Count == 0)
        {
            if (required)
            {
                errors.Add(field, "validation.help.text_required");
            }
            return;
        }
        foreach (var (locale, value) in text)
        {
            if (!Locales.IsSupported(locale))
            {
                errors.Add($"{field}.{locale}", "validation.help.locale");
            }
            else if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            {
                errors.Add($"{field}.{locale}", "validation.help.text_length");
            }
        }
    }

    /// <summary>Publishes a new version after checking it against the schema; the previous version stays in the history.</summary>
    public async Task<HelpAdminView> PublishAsync(HelpDefinition? request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.HelpEdit, AuthzTarget.Tenant, ct);
        var items = request?.Items ?? [];
        var errors = new ValidationErrors();
        if (items.Count > MaxItems)
        {
            errors.Add("items", "validation.help.too_many");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<HelpItem>();
        foreach (var (item, i) in items.Select((x, i) => (x, i)))
        {
            var field = $"items[{i}]";
            if (item.Key is not { } key || !KeyPattern().IsMatch(key))
            {
                errors.Add($"{field}.key", "validation.help.key");
            }
            else if (!keys.Add(key))
            {
                errors.Add($"{field}.key", "validation.help.key_duplicate");
            }
            if (item.Kind is not (Hint or Tour))
            {
                errors.Add($"{field}.kind", "validation.help.kind");
            }
            var path = Favorites.Normalize(item.Path, lower: true);
            if (path is null)
            {
                errors.Add($"{field}.path", "validation.help.path");
            }
            Text(errors, $"{field}.title", item.Title, MaxTitle, true);
            if (item.Kind == Hint)
            {
                Text(errors, $"{field}.body", item.Body, MaxBody, true);
                if (item.Steps is { Count: > 0 })
                {
                    errors.Add($"{field}.steps", "validation.help.hint_steps");
                }
            }
            else if (item.Kind == Tour)
            {
                var steps = item.Steps ?? [];
                if (steps.Count is 0 or > MaxSteps)
                {
                    errors.Add($"{field}.steps", "validation.help.steps");
                }
                foreach (var (step, s) in steps.Select((x, s) => (x, s)))
                {
                    Text(errors, $"{field}.steps[{s}].title", step.Title, MaxTitle, true);
                    Text(errors, $"{field}.steps[{s}].body", step.Body, MaxBody, true);
                }
            }
            clean.Add(item with { Path = path });
        }
        errors.ThrowIfAny();
        var definition = new HelpDefinition(clean);
        var (current, before, _) = await LiveAsync(ct);
        var version = current + 1;
        await session.ExecAsync(
            "insert into experience.help_config (tenant_id, version, definition, published_by, published_at) values (platform.current_tenant_id(), @v, @d, @a, @now)",
            new { v = version, d = Jsonb.From(definition), a = context.ActorId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HelpPublished, "help", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Changes: new { before = before.Items?.Count ?? 0, after = clean.Count, version }), ct);
        return new HelpAdminView(version, definition, clock.UtcNow);
    }

    private static bool Covers(string itemPath, string path) =>
        itemPath == "/" || path == itemPath || path.StartsWith(itemPath + "/", StringComparison.Ordinal);

    private static string Pick(IReadOnlyDictionary<string, string>? text, string locale) =>
        text is null || text.Count == 0 ? string.Empty
            : text.TryGetValue(locale, out var v) ? v
            : text.TryGetValue("en", out var en) ? en
            : text.OrderBy(x => x.Key, StringComparer.Ordinal).First().Value;

    /// <summary>The hints and tours of an address for the signed-in person, in their language, with their dismissals.</summary>
    public async Task<IReadOnlyList<HelpView>> ForPathAsync(string? path, string locale, CancellationToken ct)
    {
        var user = context.RequireUser();
        var at = Favorites.Normalize(path, lower: true) ?? "/";
        var (_, definition, _) = await LiveAsync(ct);
        var dismissed = (await session.QueryAsync("select item_key from experience.help_dismissal where user_id = @user", new { user }, r => r.GetString(0), ct))
            .ToHashSet(StringComparer.Ordinal);
        return (definition.Items ?? []).Where(i => Covers(i.Path!, at)).Select(i => new HelpView(i.Key!, i.Kind!, i.Path!, Pick(i.Title, locale),
            i.Kind == Hint ? Pick(i.Body, locale) : null,
            (i.Steps ?? []).Select(s => new HelpStepView(Pick(s.Title, locale), Pick(s.Body, locale))).ToList(), dismissed.Contains(i.Key!))).ToList();
    }

    /// <summary>Dismisses a hint or tour for the signed-in person, or reopens it.</summary>
    public async Task SetDismissedAsync(string key, bool dismissed, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (!KeyPattern().IsMatch(key) || !((await LiveAsync(ct)).Definition.Items ?? []).Any(i => i.Key == key))
        {
            throw AppException.NotFound();
        }
        if (dismissed)
        {
            await session.ExecAsync(
                """
                insert into experience.help_dismissal (tenant_id, user_id, item_key, dismissed_at) values (platform.current_tenant_id(), @user, @key, @now)
                on conflict (tenant_id, user_id, item_key) do update set dismissed_at = excluded.dismissed_at
                """, new { user, key, now = clock.UtcNow }, ct);
        }
        else
        {
            await session.ExecAsync("delete from experience.help_dismissal where user_id = @user and item_key = @key", new { user, key }, ct);
        }
    }
}
