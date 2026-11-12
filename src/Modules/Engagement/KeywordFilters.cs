using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record KeywordFilterRequest(IReadOnlyList<string>? Keywords);

public sealed record KeywordFilterView(IReadOnlyList<string> Keywords, int Version);

/// <summary>
/// The tenant's keyword filter (spec 023 FR-019): words and short phrases that hold a post or comment for review. It is
/// versioned, schema-checked configuration (constitution XXIX) that engagement administrators change with If-Match. A
/// keyword matches as a whole word or phrase, ignoring case, so "ass" does not hold "class".
/// </summary>
public sealed class KeywordFilters(IDbSession session, ITenantContext context, IAuthorizationService authz, Communities communities, IAuditWriter audit, IClock clock)
{
    public const int MaxKeywords = 500;
    public const int MinLength = 2;
    public const int MaxLength = 60;

    private IReadOnlyList<string>? loaded;

    public static string ETag(int version) => $"\"k{version}\"";

    private async Task<KeywordFilterView> ReadAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select keywords::text, version from engagement.keyword_filter", null,
            r => (Keywords: EngagementJson.Read<List<string>>(r.GetString(0)), Version: r.GetInt32(1)), ct);
        return row == default ? new KeywordFilterView([], 0) : new KeywordFilterView(row.Keywords, row.Version);
    }

    /// <summary>Whether the text holds a word on the filter. Read once per request.</summary>
    public async Task<bool> MatchesAsync(string text, CancellationToken ct)
    {
        loaded ??= (await ReadAsync(ct)).Keywords;
        return Matches(loaded, text);
    }

    public static bool Matches(IReadOnlyList<string> keywords, string text) =>
        keywords.Any(k => Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(k)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200)));

    public async Task<KeywordFilterView> GetAsync(CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        return await ReadAsync(ct);
    }

    public async Task<KeywordFilterView> SetAsync(KeywordFilterRequest request, string? ifMatch, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var current = await ReadAsync(ct);
        if (ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.keywords_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }
        var errors = new ValidationErrors();
        var keywords = (request.Keywords ?? []).Select(k => (k ?? "").Trim().ToLowerInvariant()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (keywords.Count > MaxKeywords)
        {
            errors.Add("keywords", "validation.engagement.keywords_count");
        }
        if (keywords.Any(k => k.Length is < MinLength or > MaxLength || k.Any(char.IsControl)))
        {
            errors.Add("keywords", "validation.engagement.keyword");
        }
        errors.ThrowIfAny();
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into engagement.keyword_filter (tenant_id, keywords, version, updated_by, updated_at) values (platform.current_tenant_id(), @keywords, @version, @user, @now)
            on conflict (tenant_id) do update set keywords = excluded.keywords, version = excluded.version, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { keywords = new Jsonb(JsonSerializer.Serialize(keywords)), version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        // The words themselves stay out of the audit trail; the count and version say what changed.
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementKeywordsChanged, "keyword_filter", null,
            Changes: new { version, count = keywords.Count, added = keywords.Except(current.Keywords).Count(), removed = current.Keywords.Except(keywords).Count() }), ct);
        loaded = keywords;
        return new KeywordFilterView(keywords, version);
    }
}
