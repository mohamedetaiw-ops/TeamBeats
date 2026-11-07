using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

public sealed record RenderedText(string Title, string Body);

public sealed record TemplateView(string TemplateKey, string Locale, string Title, string Body, bool Overridden, IReadOnlyList<string> Parameters);

public sealed record TemplateOverrideRequest(string Title, string Body);

/// <summary>
/// Notification text (spec 015 FR-006, FR-016). Platform wording is in the resource catalogues under
/// <c>notifications.template.{key}.title</c> and <c>.body</c>; a tenant may override the wording per locale, keeping
/// every parameter the platform text uses. Parameters are plain text and are never interpreted as markup.
/// </summary>
public sealed partial class NotificationTemplates(IDbSession session, ILocalizer localizer, ITenantContext context, IClock clock, IAuditWriter audit)
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 2000;
    private readonly Dictionary<(string, string), RenderedText?> _overrides = [];

    public static string TitleKey(string templateKey) => $"notifications.template.{templateKey}.title";

    public static string BodyKey(string templateKey) => $"notifications.template.{templateKey}.body";

    public bool Exists(string templateKey) => localizer.Has(TitleKey(templateKey), Locales.Fallback);

    public async Task<RenderedText> RenderAsync(string templateKey, string locale, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var text = await OverrideAsync(templateKey, locale, ct)
            ?? new RenderedText(localizer.Get(TitleKey(templateKey), locale), localizer.Get(BodyKey(templateKey), locale));
        return new RenderedText(Fill(text.Title, parameters), Fill(text.Body, parameters));
    }

    public async Task<TemplateView> GetAsync(string templateKey, string locale, CancellationToken ct)
    {
        RequireKnown(templateKey, locale);
        var custom = await OverrideAsync(templateKey, locale, ct);
        var platform = new RenderedText(localizer.Get(TitleKey(templateKey), locale), localizer.Get(BodyKey(templateKey), locale));
        var text = custom ?? platform;
        return new TemplateView(templateKey, locale, text.Title, text.Body, custom is not null, ParametersOf(platform));
    }

    public async Task<TemplateView> SetOverrideAsync(string templateKey, string locale, TemplateOverrideRequest request, CancellationToken ct)
    {
        RequireKnown(templateKey, locale);
        var platform = new RenderedText(localizer.Get(TitleKey(templateKey), locale), localizer.Get(BodyKey(templateKey), locale));
        var required = ParametersOf(platform).ToHashSet(StringComparer.Ordinal);
        var used = ParametersOf(new RenderedText(request.Title, request.Body)).ToHashSet(StringComparer.Ordinal);
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
        {
            errors.Add("title", "validation.notification.template_title_invalid");
        }
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > MaxBodyLength)
        {
            errors.Add("body", "validation.notification.template_body_invalid");
        }
        if (!required.IsSubsetOf(used))
        {
            errors.Add("body", "validation.notification.template_parameter_missing");
        }
        if (!used.IsSubsetOf(required))
        {
            errors.Add("body", "validation.notification.template_parameter_unknown");
        }
        errors.ThrowIfAny();

        await session.ExecAsync(
            """
            insert into notifications.template_override (tenant_id, template_key, locale, title, body, updated_at, updated_by)
            values (platform.current_tenant_id(), @templateKey, @locale, @title, @body, @now, @actor)
            on conflict (tenant_id, template_key, locale) do update set title = excluded.title, body = excluded.body,
              updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """,
            new { templateKey, locale, title = request.Title.Trim(), body = request.Body.Trim(), now = clock.UtcNow, actor = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NotificationTemplateOverridden, "notification_template", $"{templateKey}/{locale}",
            Changes: new { templateKey, locale, reset = false }), ct);
        _overrides.Remove((templateKey, locale));
        return await GetAsync(templateKey, locale, ct);
    }

    public async Task<TemplateView> ResetAsync(string templateKey, string locale, CancellationToken ct)
    {
        RequireKnown(templateKey, locale);
        var removed = await session.ExecAsync(
            "delete from notifications.template_override where template_key = @templateKey and locale = @locale", new { templateKey, locale }, ct);
        if (removed > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.NotificationTemplateOverridden, "notification_template", $"{templateKey}/{locale}",
                Changes: new { templateKey, locale, reset = true }), ct);
        }
        _overrides.Remove((templateKey, locale));
        return await GetAsync(templateKey, locale, ct);
    }

    /// <summary>Fills <c>{name}</c> placeholders. A placeholder without a value is left out rather than shown raw.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> parameters) =>
        Placeholder().Replace(text, m => parameters.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);

    public static IReadOnlyList<string> ParametersOf(RenderedText text) =>
        Placeholder().Matches(text.Title + "\n" + text.Body).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    private void RequireKnown(string templateKey, string locale)
    {
        if (!Exists(templateKey) || !Locales.IsSupported(locale))
        {
            throw AppException.NotFound();
        }
    }

    private async Task<RenderedText?> OverrideAsync(string templateKey, string locale, CancellationToken ct)
    {
        if (_overrides.TryGetValue((templateKey, locale), out var cached))
        {
            return cached;
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select title, body from notifications.template_override where template_key = @templateKey and locale = @locale",
            new { templateKey, locale }, r => new RenderedText(r.GetString(0), r.GetString(1)), ct);
        _overrides[(templateKey, locale)] = row;
        return row;
    }

    [GeneratedRegex(@"\{([a-zA-Z][a-zA-Z0-9_]{0,40})\}")]
    private static partial Regex Placeholder();
}
