using TeamBeats.Primitives;
using TeamBeats.Modules.Content;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// News, announcements and knowledge articles: written by the named author on the day given, published (by the author,
    /// or by the administrator when the author may only write), then read, reacted to and commented on by colleagues
    /// over the following days. An item whose English title the tenant already has is left as it is.
    /// </summary>
    private async Task SeedArticlesAsync(Run run, IReadOnlyList<DemoArticle> articles, string refPrefix, CancellationToken ct)
    {
        if (articles.Count == 0)
        {
            return;
        }
        var existing = await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<Items>().ListAsync(null, null, null, 1000, ct), ct) ?? [];
        // Oldest first, so each later step of an item happens after the item exists.
        foreach (var article in articles.OrderByDescending(a => a.DaysAgo))
        {
            var title = article.Title.GetValueOrDefault("en") ?? article.Title.Values.First();
            if (existing.FirstOrDefault(i => i.Type == article.Type && i.Variants.Values.Any(v => v.Title == title)) is { } found)
            {
                run.Refs[$"{refPrefix}:{article.Key}"] = ("content", found.Id);
                continue;
            }
            await ItemAsync(run, $"{article.Type} {article.Key}", async () =>
            {
                var id = await CreateArticleAsync(run, article, ct);
                if (id is not { } itemId)
                {
                    return;
                }
                run.Refs[$"{refPrefix}:{article.Key}"] = ("content", itemId);
                run.Report.Add(article.Type);
                await SocialAsync(run, article, itemId, ct);
            });
        }
    }

    private async Task<Guid?> CreateArticleAsync(Run run, DemoArticle article, CancellationToken ct)
    {
        var variants = article.Title.Keys.ToDictionary(locale => locale, locale => new VariantInput(
            article.Title[locale],
            article.Summary?.GetValueOrDefault(locale),
            DemoText.Body(article.Body.GetValueOrDefault(locale) ?? [])), StringComparer.Ordinal);
        var author = run.Person(article.Author) is null ? AdminPersona : article.Author;
        var owner = article.Owner is { } o ? run.Person(o) ?? run.Person(author) : article.Type == ContentTypes.News ? null : run.Person(author);
        var request = new ItemRequest(null, article.Type, null, owner, null, null, "internal", article.Pinned, article.Featured, variants, null, null);

        var created = await AsAsync(run, author, article.DaysAgo, sp => TryAsync(() => sp.GetRequiredService<Items>().CreateAsync(request, ct)), ct);
        if (created is null)
        {
            // The author may not write here (for example a reader persona): the administrator writes it instead.
            author = AdminPersona;
            created = await AsAsync(run, author, article.DaysAgo, sp => sp.GetRequiredService<Items>().CreateAsync(request, ct), ct);
        }
        var publish = new PublishRequest(null, null, true);
        var published = await AsAsync(run, author, article.DaysAgo, sp => TryAsync(() => sp.GetRequiredService<Items>().PublishAsync(created!.Id, publish, ct)), ct);
        if (published is null)
        {
            await AsAsync(run, AdminPersona, article.DaysAgo, sp => sp.GetRequiredService<Items>().PublishAsync(created!.Id, publish, ct), ct);
        }
        return created!.Id;
    }

    /// <summary>Reactions and comments, spread over the days after publishing (never in the future).</summary>
    private async Task SocialAsync(Run run, DemoArticle article, Guid itemId, CancellationToken ct)
    {
        var step = 0;
        foreach (var reaction in article.Reactions ?? [])
        {
            var daysAgo = Math.Max(0, article.DaysAgo - 0.05 * ++step);
            await ItemAsync(run, $"reaction on {article.Key}", async () =>
            {
                if (await AsAsync(run, reaction.Person, daysAgo, sp => sp.GetRequiredService<ContentSocial>().ReactAsync(itemId, new ReactionRequest(reaction.Kind), ct), ct) is not null)
                {
                    run.Report.Add("reactions");
                }
            });
        }
        foreach (var comment in article.Comments ?? [])
        {
            var daysAgo = Math.Max(0, article.DaysAgo - 0.1 * ++step);
            await ItemAsync(run, $"comment on {article.Key}", async () =>
            {
                if (await AsAsync(run, comment.Person, daysAgo, sp => sp.GetRequiredService<ContentSocial>().CommentAsync(itemId, new CommentRequest(comment.Text), ct), ct) is not null)
                {
                    run.Report.Add("comments");
                }
            });
        }
    }

    /// <summary>The result, or default when the module refuses (denied or not found) so the caller can try another way.</summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T>> work) where T : class
    {
        try
        {
            return await work();
        }
        catch (AppException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }
}
