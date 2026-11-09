using System.Globalization;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Hr;
using TeamBeats.Modules.Search;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// Working calendars with their public holidays. The key <c>default</c> fills the tenant's default calendar; others
    /// are found by English name or created. Holidays are set only for a year the calendar has none for.
    /// </summary>
    private async Task SeedCalendarsAsync(Run run, CancellationToken ct)
    {
        foreach (var plan in run.Pack.Calendars)
        {
            await ItemAsync(run, $"calendar {plan.Key}", () => AsAsync(run, AdminPersona, 0, async sp =>
            {
                var calendars = sp.GetRequiredService<CalendarService>();
                var list = await calendars.ListAsync(ct);
                var en = plan.Names.GetValueOrDefault("en");
                var calendar = plan.Key == "default"
                    ? list.First(c => c.IsTenantDefault)
                    : list.FirstOrDefault(c => c.Name.GetValueOrDefault("en") == en);
                if (calendar is null)
                {
                    calendar = await calendars.CreateAsync(new CalendarRequest(plan.Names, plan.Country, plan.WeekDays, null), ct);
                    run.Report.Add("calendars");
                }
                else if (plan.Key == "default" && (calendar.Country != plan.Country || !calendar.WeekDays.SequenceEqual(plan.WeekDays)))
                {
                    calendar = await calendars.UpdateAsync(calendar.Id, calendar.ETag, new CalendarRequest(plan.Names, plan.Country, plan.WeekDays, null), ct);
                }
                calendar = await calendars.GetAsync(calendar.Id, ct);
                if ((calendar.Holidays ?? []).Any(h => h.Date.Year == plan.Year))
                {
                    return;
                }
                var holidays = plan.Holidays.Select(h => new HolidayInput(DateOnly.ParseExact(h.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), h.Names, h.HalfDay)).ToList();
                await calendars.SetHolidaysAsync(calendar.Id, calendar.ETag, new HolidaysRequest(plan.Year, holidays), ct);
                run.Report.Add("holidays", holidays.Count);
            }, ct));
        }
    }

    /// <summary>Best bets that point at seeded items, and synonym sets, each added once.</summary>
    private async Task SeedSearchAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.Search is not { } plan)
        {
            return;
        }
        var (bets, synonyms) = await AsAsync(run, AdminPersona, 0, async sp =>
        {
            var tuning = sp.GetRequiredService<SearchTuning>();
            return (await tuning.ListBestBetsAsync(ct), await tuning.ListSynonymsAsync(ct));
        }, ct);
        foreach (var set in plan.Synonyms ?? [])
        {
            // Stored terms are normalized (case, hamza and the like), so compare the normalized forms.
            var wanted = set.Terms.SelectMany(SearchText.Terms).ToHashSet(StringComparer.Ordinal);
            if (synonyms.Any(s => s.Locale == set.Locale && s.Terms.SelectMany(SearchText.Terms).Any(wanted.Contains)))
            {
                continue;
            }
            await ItemAsync(run, $"synonyms {set.Terms[0]}", async () =>
            {
                await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<SearchTuning>().AddSynonymsAsync(new SynonymSetRequest(set.Locale, set.Terms), ct), ct);
                run.Report.Add("synonym sets");
            });
        }
        foreach (var bet in plan.BestBets ?? [])
        {
            if (!run.Refs.TryGetValue(bet.Ref, out var target))
            {
                run.Report.Skipped.Add($"best bet {bet.Query}: {bet.Ref} not seeded");
                continue;
            }
            if (bets.Any(b => b.ItemId == target.Id && b.Query == SearchText.QueryKey(bet.Query)))
            {
                continue;
            }
            await ItemAsync(run, $"best bet {bet.Query}", async () =>
            {
                await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<SearchTuning>().AddBestBetAsync(new BestBetRequest(bet.Query, bet.Locale, target.Source, target.Id), ct), ct);
                run.Report.Add("best bets");
            });
        }
    }

    /// <summary>Assistant conversations: each person asks their questions in one conversation, once (only when they have none yet).</summary>
    private async Task SeedAssistantAsync(Run run, CancellationToken ct)
    {
        foreach (var conversation in run.Pack.Assistant)
        {
            await ItemAsync(run, $"assistant for {conversation.Person}", async () =>
            {
                var has = await AsAsync(run, conversation.Person, 0, async sp => (await sp.GetRequiredService<AssistantHistory>().ListAsync(ct)).Count > 0, ct);
                if (has || run.Person(conversation.Person) is null)
                {
                    return;
                }
                Guid? id = null;
                var hoursAgo = conversation.Questions.Count;
                foreach (var question in conversation.Questions)
                {
                    var turn = await AsAsync(run, conversation.Person, hoursAgo-- / 24.0,
                        sp => sp.GetRequiredService<AssistantService>().AskAsync(new TurnRequest(id, question), ct), ct);
                    id = turn?.ConversationId;
                    run.Report.Add("assistant turns");
                }
            });
        }
    }
}
