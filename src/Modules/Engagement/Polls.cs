using TeamBeats.Messaging;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record PollRequest(string? Question, IReadOnlyList<string>? Options, bool Multiple, bool Anonymous, bool AllowChange, string? Results, DateTimeOffset? ClosesAt);

public sealed record PollOptionsRequest(IReadOnlyList<string>? Options);

public sealed record VoteRequest(IReadOnlyList<Guid>? OptionIds);

/// <summary>An option with its count when results are shown, and in a named poll the voters, for the author and moderators.</summary>
public sealed record PollOptionView(Guid Id, string Label, int? Count, IReadOnlyList<Person>? Voters);

public sealed record PollView(
    Guid Id,
    Guid CommunityId,
    Person Author,
    string Question,
    bool Multiple,
    bool Anonymous,
    bool AllowChange,
    string Results,
    DateTimeOffset ClosesAt,
    bool Closed,
    bool Voted,
    IReadOnlyList<Guid> MyChoices,
    bool ResultsVisible,
    int? Voters,
    IReadOnlyList<PollOptionView> Options,
    bool CanVote,
    bool CanClose,
    bool CanAddOptions,
    DateTimeOffset CreatedAt);

public static class PollResults
{
    public const string AfterVote = "after_vote";
    public const string AfterClose = "after_close";
}

/// <summary>
/// Polls in a community (spec 023 US3 scenario 1, FR-008). Anyone who can post in the community starts one; everyone
/// who reads it and takes part votes once, single or multiple choice, and changes the vote until close if the poll
/// allows it. Results show after voting or only after close; the author and moderators always see them. An anonymous
/// poll counts votes with nobody named and keeps only "has voted" apart, deleted at close; its votes cannot be changed.
/// After the first vote options can only be added. A poll closes at its end time or earlier by its author or a moderator.
/// </summary>
public sealed class Polls(IDbSession session, ITenantContext context, Communities communities, IUserDirectory users, IOutbox outbox, IClock clock)
{
    public const int MaxOptions = 20;
    public const int MaxQuestion = 300;
    public const int MaxLabel = 120;
    public static readonly TimeSpan MaxOpen = TimeSpan.FromDays(365);

    private sealed record PollRow(Guid Id, Guid CommunityId, Guid AuthorId, string Question, bool Multiple, bool Anonymous, bool AllowChange, string Results,
        DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt);

    private const string Columns = "id, community_id, author_id, question, multiple, anonymous, allow_change, results, closes_at, closed_at, created_at";

    private static PollRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
        r.GetBoolean(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("closed_at"), r.GetFieldValue<DateTimeOffset>(10));

    // ---- Reading ----

    public async Task<IReadOnlyList<PollView>> ListAsync(Guid communityId, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        var rows = await session.QueryAsync($"select {Columns} from engagement.poll where community_id = @communityId order by created_at desc limit 50",
            new { communityId }, Read, ct);
        var views = new List<PollView>();
        foreach (var row in rows)
        {
            views.Add(await ViewAsync(await CloseIfDueAsync(row, ct), s, ct));
        }
        // Open polls first, newest first within each group.
        return views.OrderBy(v => v.Closed).ThenByDescending(v => v.CreatedAt).ToList();
    }

    public async Task<PollView> GetAsync(Guid id, CancellationToken ct)
    {
        var (row, s) = await ReadableAsync(id, ct);
        return await ViewAsync(row, s, ct);
    }

    private async Task<(PollRow Row, Standing Standing)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from engagement.poll where id = @id", new { id }, Read, ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var s = await communities.ReadableAsync(rows[0].CommunityId, ct);
        return (await CloseIfDueAsync(rows[0], ct), s);
    }

    /// <summary>A poll past its end time is closed the first time anyone looks at it, which also deletes the anonymous "has voted" marks.</summary>
    private async Task<PollRow> CloseIfDueAsync(PollRow row, CancellationToken ct)
    {
        if (row.ClosedAt is not null || row.ClosesAt > clock.UtcNow)
        {
            return row;
        }
        return await CloseRowAsync(row, row.ClosesAt, ct);
    }

    private async Task<PollRow> CloseRowAsync(PollRow row, DateTimeOffset at, CancellationToken ct)
    {
        var changed = await session.ExecAsync("update engagement.poll set closed_at = @at where id = @id and closed_at is null", new { id = row.Id, at }, ct);
        await session.ExecAsync("delete from engagement.poll_voted where poll_id = @id", new { id = row.Id }, ct);
        if (changed > 0)
        {
            await outbox.EnqueueAsync(new PollClosed(row.Id, row.CommunityId), ct);
        }
        return row with { ClosedAt = at };
    }

    private async Task<PollView> ViewAsync(PollRow row, Standing s, CancellationToken ct)
    {
        var user = context.RequireUser();
        var closed = row.ClosedAt is not null;
        var options = await session.QueryAsync("select id, label from engagement.poll_option where poll_id = @id order by position", new { id = row.Id },
            r => (Id: r.GetGuid(0), Label: r.GetString(1)), ct);
        IReadOnlyList<Guid> mine = row.Anonymous
            ? []
            : await session.QueryAsync("select option_id from engagement.poll_vote where poll_id = @id and user_id = @user", new { id = row.Id, user }, r => r.GetGuid(0), ct);
        var voted = row.Anonymous
            ? await session.ScalarAsync<bool>("select exists (select 1 from engagement.poll_voted where poll_id = @id and user_id = @user)", new { id = row.Id, user }, ct)
            : mine.Count > 0;
        var owner = row.AuthorId == user;
        var insight = owner || s.Moderates;
        var visible = closed || insight || (row.Results == PollResults.AfterVote && voted);
        Dictionary<Guid, int> counts = [];
        ILookup<Guid, Guid> voters = Enumerable.Empty<(Guid, Guid)>().ToLookup(v => v.Item1, v => v.Item2);
        int? total = null;
        if (visible)
        {
            if (row.Anonymous)
            {
                counts = (await session.QueryAsync("select option_id, count from engagement.poll_tally where poll_id = @id", new { id = row.Id },
                    r => (Id: r.GetGuid(0), Count: r.GetInt32(1)), ct)).ToDictionary(c => c.Id, c => c.Count);
                // Each person counts once per option; with one choice each, the largest possible voter count is the sum.
                total = row.Multiple ? null : counts.Values.Sum();
            }
            else
            {
                var votes = await session.QueryAsync("select option_id, user_id from engagement.poll_vote where poll_id = @id", new { id = row.Id },
                    r => (Option: r.GetGuid(0), User: r.GetGuid(1)), ct);
                counts = votes.GroupBy(v => v.Option).ToDictionary(g => g.Key, g => g.Count());
                total = votes.Select(v => v.User).Distinct().Count();
                if (insight)
                {
                    voters = votes.ToLookup(v => v.Option, v => v.User);
                }
            }
        }
        var people = await PeopleAsync([row.AuthorId, .. voters.SelectMany(v => v)], ct);
        var open = !closed && s.Community.Status == "active";
        return new PollView(row.Id, row.CommunityId, people[row.AuthorId], row.Question, row.Multiple, row.Anonymous, row.AllowChange, row.Results, row.ClosesAt, closed,
            voted, mine, visible, total,
            options.Select(o => new PollOptionView(o.Id, o.Label, visible ? counts.GetValueOrDefault(o.Id) : null,
                insight && !row.Anonymous && visible ? voters[o.Id].Select(v => people[v]).ToList() : null)).ToList(),
            CanVote: open && s.Participates && (!voted || row.AllowChange),
            CanClose: open && (owner || s.Moderates),
            CanAddOptions: open && owner && options.Count < MaxOptions,
            row.CreatedAt);
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var found = await users.GetManyAsync(list, ct);
        return list.ToDictionary(id => id, id => found.TryGetValue(id, out var u) ? new Person(id, u.DisplayName, u.Status == "active") : new Person(id, "", false));
    }

    // ---- Writing ----

    private static List<string> Labels(IReadOnlyList<string>? options, ValidationErrors errors)
    {
        var labels = (options ?? []).Select(o => (o ?? "").Trim()).ToList();
        if (labels.Any(l => l.Length is 0 or > MaxLabel || l.Any(char.IsControl)))
        {
            errors.Add("options", "validation.engagement.poll_option");
        }
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count)
        {
            errors.Add("options", "validation.engagement.poll_option_repeated");
        }
        return labels;
    }

    public async Task<PollView> CreateAsync(Guid communityId, PollRequest request, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        if (!s.Posts)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var question = request.Question?.Trim() ?? "";
        if (question.Length is 0 or > MaxQuestion || question.Any(char.IsControl))
        {
            errors.Add("question", "validation.engagement.poll_question");
        }
        var labels = Labels(request.Options, errors);
        if (labels.Count is < 2 or > MaxOptions)
        {
            errors.Add("options", "validation.engagement.poll_options");
        }
        var results = request.Results ?? PollResults.AfterVote;
        if (results is not (PollResults.AfterVote or PollResults.AfterClose))
        {
            errors.Add("results", "validation.engagement.poll_results");
        }
        if (request.Anonymous && request.AllowChange)
        {
            errors.Add("allowChange", "validation.engagement.poll_anonymous_change");
        }
        var now = clock.UtcNow;
        if (request.ClosesAt is not { } closes || closes <= now || closes > now + MaxOpen)
        {
            errors.Add("closesAt", "validation.engagement.poll_closes");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into engagement.poll (tenant_id, id, community_id, author_id, question, multiple, anonymous, allow_change, results, closes_at, created_at)
            values (platform.current_tenant_id(), @id, @communityId, @user, @question, @multiple, @anonymous, @allowChange, @results, @closes, @now)
            """, new
            {
                id, communityId, user = context.RequireUser(), question, multiple = request.Multiple, anonymous = request.Anonymous, allowChange = request.AllowChange,
                results, closes = request.ClosesAt!.Value, now,
            }, ct);
        await AddOptionsAsync(id, labels, 0, ct);
        return await GetAsync(id, ct);
    }

    private async Task AddOptionsAsync(Guid pollId, IReadOnlyList<string> labels, int start, CancellationToken ct)
    {
        for (var i = 0; i < labels.Count; i++)
        {
            await session.ExecAsync("insert into engagement.poll_option (tenant_id, id, poll_id, label, position) values (platform.current_tenant_id(), @id, @pollId, @label, @position)",
                new { id = Ids.New(), pollId, label = labels[i], position = start + i }, ct);
        }
    }

    /// <summary>The author adds options while the poll is open; existing options never change, so votes keep their meaning.</summary>
    public async Task<PollView> AddOptionsAsync(Guid id, PollOptionsRequest request, CancellationToken ct)
    {
        var (row, s) = await ReadableAsync(id, ct);
        var view = await ViewAsync(row, s, ct);
        if (!view.CanAddOptions)
        {
            throw row.AuthorId == context.RequireUser() && row.ClosedAt is not null ? Closed() : AppException.Denied();
        }
        var errors = new ValidationErrors();
        var labels = Labels(request.Options, errors);
        if (labels.Count == 0 || view.Options.Count + labels.Count > MaxOptions)
        {
            errors.Add("options", "validation.engagement.poll_options");
        }
        if (labels.Any(l => view.Options.Any(o => string.Equals(o.Label, l, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add("options", "validation.engagement.poll_option_repeated");
        }
        errors.ThrowIfAny();
        await AddOptionsAsync(id, labels, view.Options.Count, ct);
        return await GetAsync(id, ct);
    }

    private static AppException Closed() => new(ProblemCodes.Conflict, 409, "conflict.engagement.poll_closed", null);

    public async Task<PollView> VoteAsync(Guid id, VoteRequest request, CancellationToken ct)
    {
        var (row, s) = await ReadableAsync(id, ct);
        if (!s.Participates)
        {
            throw AppException.Denied();
        }
        if (row.ClosedAt is not null || s.Community.Status != "active")
        {
            throw Closed();
        }
        var chosen = (request.OptionIds ?? []).Distinct().ToList();
        var valid = (await session.QueryAsync("select id from engagement.poll_option where poll_id = @id", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
        if (chosen.Count == 0 || chosen.Any(o => !valid.Contains(o)) || (!row.Multiple && chosen.Count > 1))
        {
            throw AppException.Validation("optionIds", "validation.engagement.poll_choice");
        }
        var user = context.RequireUser();
        if (row.Anonymous)
        {
            // The mark that this person voted and the counts are written apart; nothing joins them.
            var marked = await session.ExecAsync(
                "insert into engagement.poll_voted (tenant_id, poll_id, user_id) values (platform.current_tenant_id(), @id, @user) on conflict do nothing", new { id, user }, ct);
            if (marked == 0)
            {
                throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.already_voted", null);
            }
            foreach (var option in chosen.Order())
            {
                await session.ExecAsync(
                    """
                    insert into engagement.poll_tally (tenant_id, poll_id, option_id, count) values (platform.current_tenant_id(), @id, @option, 1)
                    on conflict (tenant_id, poll_id, option_id) do update set count = engagement.poll_tally.count + 1
                    """, new { id, option }, ct);
            }
        }
        else
        {
            var had = await session.ScalarAsync<bool>("select exists (select 1 from engagement.poll_vote where poll_id = @id and user_id = @user)", new { id, user }, ct);
            if (had && !row.AllowChange)
            {
                throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.already_voted", null);
            }
            await session.ExecAsync("delete from engagement.poll_vote where poll_id = @id and user_id = @user", new { id, user }, ct);
            foreach (var option in chosen)
            {
                await session.ExecAsync(
                    "insert into engagement.poll_vote (tenant_id, poll_id, option_id, user_id, voted_at) values (platform.current_tenant_id(), @id, @option, @user, @now)",
                    new { id, option, user, now = clock.UtcNow }, ct);
            }
        }
        return await ViewAsync(row, s, ct);
    }

    public async Task<PollView> CloseAsync(Guid id, CancellationToken ct)
    {
        var (row, s) = await ReadableAsync(id, ct);
        if (row.ClosedAt is not null)
        {
            throw Closed();
        }
        if (row.AuthorId != context.RequireUser() && !s.Moderates)
        {
            throw AppException.Denied();
        }
        return await ViewAsync(await CloseRowAsync(row, clock.UtcNow, ct), s, ct);
    }
}
