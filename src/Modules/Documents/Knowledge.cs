using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>The article templates of FR-011 and the sections each one starts with.</summary>
public static class KnowledgeTemplates
{
    public const string Sop = "sop";
    public const string HowTo = "howto";
    public const string Faq = "faq";
    public const string Troubleshooting = "troubleshooting";

    /// <summary>
    /// Section keys per template (US4 scenario 1). An FAQ starts empty: each heading the editor adds is a question and the
    /// text under it the answer, so every pair is its own unit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Sections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Sop] = ["purpose", "scope", "responsibilities", "steps", "related_documents"],
        [HowTo] = ["overview", "before_you_start", "steps", "result"],
        [Faq] = [],
        [Troubleshooting] = ["symptoms", "causes", "resolution", "escalation"],
    };

    public static string HeadingKey(string section) => $"kb.section.{section}";
}

public sealed record KnowledgeTemplateView(string Key, IReadOnlyList<string> Sections);

public sealed record ArticleRequest(Guid? SiteId, string? Template, IReadOnlyDictionary<string, string>? Titles, Guid? OwnerId, string? Classification);

public sealed record ArticleCreated(Guid Id, string Template, string EditLink);

public sealed record RelatedRequest(IReadOnlyList<Guid>? ArticleIds, IReadOnlyList<Guid>? DocumentIds);

public sealed record FeedbackRequest(bool? Helpful, string? Comment);

public sealed record ArticleSummary(Guid Id, string? Template, IReadOnlyDictionary<string, string> Titles, Guid? OwnerId, int Version, DateTimeOffset PublishedAt,
    DateTimeOffset? ReviewDueAt);

public sealed record ArticleSection(string Anchor, int Level, string Heading, string Text, string Link);

public sealed record RelatedArticle(Guid Id, IReadOnlyDictionary<string, string> Titles);

public sealed record RelatedDocument(Guid Id, string Name, string? Number);

public sealed record MyFeedback(bool Helpful, string? Comment, int Version, DateTimeOffset At);

public sealed record FeedbackScore(int Helpful, int NotHelpful, int? Percent);

public sealed record ArticleView(
    Guid Id,
    string? Template,
    string Locale,
    IReadOnlyDictionary<string, string> Titles,
    Guid? OwnerId,
    int Version,
    DateTimeOffset PublishedAt,
    DateTimeOffset? ReviewDueAt,
    string Link,
    IReadOnlyList<ArticleSection> Sections,
    IReadOnlyList<RelatedArticle> RelatedArticles,
    IReadOnlyList<RelatedDocument> RelatedDocuments,
    MyFeedback? MyFeedback,
    bool CanManage,
    FeedbackScore? Score);

/// <summary>A comment as the owner sees it: the answer and the text, never who gave it.</summary>
public sealed record FeedbackComment(bool Helpful, string Comment, int Version, DateTimeOffset At);

public sealed record FeedbackReport(FeedbackScore Score, IReadOnlyList<FeedbackComment> Comments);

/// <summary>
/// The knowledge base of spec 011 US4 (FR-011). Articles are content items of type <c>knowledge</c> created and read
/// through the spec 009 contract, so drafts, review, publishing and versions are the content lifecycle. This class
/// keeps the template, related articles and documents, and the helpfulness feedback, and cuts FAQ articles into
/// question and answer units for search and the assistant.
/// </summary>
public sealed class KnowledgeBase(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IKnowledgeContent content,
    ITenantSettingsReader tenantSettings,
    ILocalizer localizer,
    RequestLocale requestLocale,
    DocumentFiles files,
    IOutbox outbox,
    IClock clock) : IKnowledgeIndex
{
    public const int MaxRelated = 20;
    public const int CommentMax = 1000;

    public static string ReaderLink(Guid id, string? anchor = null) => $"/content/read/{id}" + (anchor is null ? string.Empty : "#" + anchor);

    public static IReadOnlyList<KnowledgeTemplateView> Templates() =>
        KnowledgeTemplates.Sections.Select(t => new KnowledgeTemplateView(t.Key, t.Value)).ToList();

    private async Task<Guid> DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        return context.RequireUser();
    }

    /// <summary>Creates the draft from a template (US4 scenario 1); the content contract checks authoring rights and <c>kb.edit</c>.</summary>
    public async Task<ArticleCreated> CreateAsync(ArticleRequest request, CancellationToken ct)
    {
        var user = await DemandAsync(ct);
        var errors = new ValidationErrors();
        if (request.Template is not { } template || !KnowledgeTemplates.Sections.TryGetValue(template, out var sections))
        {
            errors.Add("template", "validation.kb.template");
            template = string.Empty;
            sections = [];
        }
        var titles = request.Titles?.Where(t => !string.IsNullOrWhiteSpace(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal) ?? [];
        if (titles.Count == 0)
        {
            errors.Add("titles", "validation.kb.titles");
        }
        errors.ThrowIfAny();
        var headings = titles.Keys.ToDictionary(locale => locale,
            locale => (IReadOnlyList<string>)sections.Select(s => localizer.Get(KnowledgeTemplates.HeadingKey(s), locale)).ToList(), StringComparer.Ordinal);
        var id = await content.CreateDraftAsync(new KnowledgeDraft(request.SiteId, titles, request.OwnerId ?? user, headings, request.Classification), ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.kb_article (tenant_id, item_id, template, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @template, @user, @now, @now)
            """, new { id, template, user, now }, ct);
        return new ArticleCreated(id, template, $"/content/items/{id}");
    }

    private Task<(string Template, Guid[] Items, Guid[] Documents)?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync<(string, Guid[], Guid[])?>(
            "select template, related_item_ids, related_document_ids from documents.kb_article where item_id = @id", new { id },
            r => (r.GetString(0), r.GetFieldValue<Guid[]>(1), r.GetFieldValue<Guid[]>(2)), ct);

    /// <summary>Published articles the caller may read, the most recent first, with their template.</summary>
    public async Task<IReadOnlyList<ArticleSummary>> ListAsync(int limit, CancellationToken ct)
    {
        await DemandAsync(ct);
        var articles = await content.ReadableListAsync(limit, ct);
        var ids = articles.Select(a => a.ItemId).ToArray();
        var templates = (await session.QueryAsync("select item_id, template from documents.kb_article where item_id = any(@ids)", new { ids },
            r => (r.GetGuid(0), r.GetString(1)), ct)).ToDictionary(t => t.Item1, t => t.Item2);
        return articles.Select(a => new ArticleSummary(a.ItemId, templates.GetValueOrDefault(a.ItemId), a.Titles, a.OwnerId, a.Version, a.PublishedAt, a.ReviewDueAt))
            .ToList();
    }

    private async Task<KnowledgeArticle> ReadableAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        return await content.ReadableAsync(id, ct) ?? throw AppException.NotFound();
    }

    /// <summary>The reader's locale, else the tenant default, else the first one the article has.</summary>
    private async Task<string> LocaleAsync(IReadOnlyDictionary<string, string> titles, CancellationToken ct)
    {
        if (titles.ContainsKey(requestLocale.Locale))
        {
            return requestLocale.Locale;
        }
        var fallback = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        return titles.ContainsKey(fallback) ? fallback : titles.Keys.Order(StringComparer.Ordinal).First();
    }

    public async Task<ArticleView> GetAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var article = await ReadableAsync(id, ct);
        var row = await RowAsync(id, ct);
        var locale = await LocaleAsync(article.Titles, ct);
        var sections = (await content.SectionsAsync(id, ct)).Where(s => s.Locale == locale)
            .Select(s => new ArticleSection(s.Anchor, s.Level, s.Heading, s.Text, ReaderLink(id, s.Anchor))).ToList();

        var relatedArticles = new List<RelatedArticle>();
        var relatedDocuments = new List<RelatedDocument>();
        if (row is { } r)
        {
            foreach (var other in r.Items)
            {
                if (await content.ReadableAsync(other, ct) is { } found)
                {
                    relatedArticles.Add(new RelatedArticle(found.ItemId, found.Titles));
                }
            }
            foreach (var doc in r.Documents)
            {
                if (await VisibleDocumentAsync(doc, ct) is { } view)
                {
                    relatedDocuments.Add(new RelatedDocument(view.Id, view.Name, view.Number));
                }
            }
        }
        var mine = await session.QuerySingleOrDefaultAsync(
            "select helpful, comment, version, updated_at from documents.kb_feedback where item_id = @id and user_id = @user", new { id, user },
            f => new MyFeedback(f.GetBoolean(0), f.Get<string?>("comment"), f.GetInt32(2), f.GetFieldValue<DateTimeOffset>(3)), ct);
        var manage = await content.MayManageAsync(id, ct);
        return new ArticleView(id, row?.Template, locale, article.Titles, article.OwnerId, article.Version, article.PublishedAt, article.ReviewDueAt,
            ReaderLink(id), sections, relatedArticles, relatedDocuments, mine, manage, manage ? await ScoreAsync(id, ct) : null);
    }

    /// <summary>The document as the caller sees it, or null when it is gone or hidden from them, so a link never reveals it.</summary>
    private async Task<DocumentView?> VisibleDocumentAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return await files.GetAsync(id, ct);
        }
        catch (AppException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner or a knowledge editor links related articles and documents (FR-011), also while the article is a draft.
    /// Each must be one the editor can read; readers get only those they can read themselves.
    /// </summary>
    public async Task<RelatedRequest> SetRelatedAsync(Guid id, RelatedRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        if (!await content.MayManageAsync(id, ct))
        {
            throw await content.ReadableAsync(id, ct) is null ? AppException.NotFound() : AppException.Denied();
        }
        var articles = (request.ArticleIds ?? []).Distinct().Where(a => a != id).ToArray();
        var documents = (request.DocumentIds ?? []).Distinct().ToArray();
        var errors = new ValidationErrors();
        if (articles.Length > MaxRelated || documents.Length > MaxRelated)
        {
            errors.Add("related", "validation.kb.related_count");
        }
        foreach (var other in articles.Take(MaxRelated + 1))
        {
            if (await content.ReadableAsync(other, ct) is null)
            {
                errors.Add("articleIds", "validation.kb.related_article");
                break;
            }
        }
        foreach (var doc in documents.Take(MaxRelated + 1))
        {
            if (await VisibleDocumentAsync(doc, ct) is null)
            {
                errors.Add("documentIds", "validation.kb.related_document");
                break;
            }
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        var updated = await session.ExecAsync(
            "update documents.kb_article set related_item_ids = @articles, related_document_ids = @documents, updated_at = @now where item_id = @id",
            new { id, articles, documents, now }, ct);
        if (updated == 0)
        {
            // An article written in content before the knowledge base knew it has no template.
            throw AppException.Conflict("conflict.kb.not_article");
        }
        return new RelatedRequest(articles, documents);
    }

    /// <summary>A reader marks the published article helpful or not, with an optional comment (US4 scenario 2). A new answer replaces the earlier one.</summary>
    public async Task<MyFeedback> FeedbackAsync(Guid id, FeedbackRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var article = await ReadableAsync(id, ct);
        var errors = new ValidationErrors();
        if (request.Helpful is not { } helpful)
        {
            errors.Add("helpful", "validation.kb.helpful");
            helpful = false;
        }
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > CommentMax })
        {
            errors.Add("comment", "validation.kb.comment");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.kb_feedback (tenant_id, item_id, user_id, helpful, comment, version, updated_at)
            values (platform.current_tenant_id(), @id, @user, @helpful, @comment, @version, @now)
            on conflict (tenant_id, item_id, user_id) do update set helpful = excluded.helpful, comment = excluded.comment, version = excluded.version,
              updated_at = excluded.updated_at
            """, new { id, user, helpful, comment, version = article.Version, now }, ct);
        await outbox.EnqueueAsync(new KnowledgeFeedbackGiven(id, helpful, comment is not null), ct);
        return new MyFeedback(helpful, comment, article.Version, now);
    }

    private Task<FeedbackScore> ScoreAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            "select count(*) filter (where helpful)::int, count(*) filter (where not helpful)::int from documents.kb_feedback where item_id = @id", new { id },
            r =>
            {
                var yes = r.GetInt32(0);
                var no = r.GetInt32(1);
                return new FeedbackScore(yes, no, yes + no == 0 ? null : (int)Math.Round(100.0 * yes / (yes + no)));
            }, ct)!;

    /// <summary>The score and the comments for the owner and knowledge editors; readers are refused.</summary>
    public async Task<FeedbackReport> ReportAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        if (!await content.MayManageAsync(id, ct))
        {
            throw await content.ReadableAsync(id, ct) is null ? AppException.NotFound() : AppException.Denied();
        }
        var comments = await session.QueryAsync(
            "select helpful, comment, version, updated_at from documents.kb_feedback where item_id = @id and comment is not null order by updated_at desc limit 200",
            new { id }, r => new FeedbackComment(r.GetBoolean(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3)), ct);
        return new FeedbackReport(await ScoreAsync(id, ct), comments);
    }

    /// <summary>The units of an article the caller may read; not found otherwise, so the route never tells whether an ID exists.</summary>
    public async Task<IReadOnlyList<KnowledgeUnit>> ReaderUnitsAsync(Guid articleId, CancellationToken ct)
    {
        await ReadableAsync(articleId, ct);
        return await UnitsAsync(articleId, ct);
    }

    /// <summary>
    /// Units for the index (US4 scenario 3): an FAQ gives one unit per question and answer, linked to its anchor; other
    /// articles give one unit per locale. Read as the caller, so a hidden article gives none.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeUnit>> UnitsAsync(Guid articleId, CancellationToken ct = default)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        if (await content.ReadableAsync(articleId, ct) is not { } article)
        {
            return [];
        }
        var row = await RowAsync(articleId, ct);
        var sections = await content.SectionsAsync(articleId, ct);
        if (row?.Template == KnowledgeTemplates.Faq)
        {
            return sections.Where(s => s.Heading.Length > 0)
                .Select(s => new KnowledgeUnit(articleId, s.Locale, s.Anchor, s.Heading, s.Text, ReaderLink(articleId, s.Anchor))).ToList();
        }
        return article.Titles.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new KnowledgeUnit(articleId, t.Key, null, t.Value,
            string.Join("\n\n", sections.Where(s => s.Locale == t.Key).Select(s => s.Text.Length == 0 ? s.Heading : s.Heading + "\n" + s.Text)),
            ReaderLink(articleId))).ToList();
    }
}
