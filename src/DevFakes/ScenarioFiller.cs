using System.Globalization;

namespace TeamBeats.DevFakes;

/// <summary>
/// Gives every enabled person of a scenario directory a realistic Outlook: mail from their manager, team mates and
/// company services, a weekly team meeting, a one to one with their manager and personal time blocks. The texts come
/// from the scenario's <c>fill</c> section in English and Arabic, chosen by the person's preferred language. The
/// result is deterministic for a given start day, so tests and screenshots stay stable. Fictional data only.
/// </summary>
public static class ScenarioFiller
{
    public static void Fill(ScenarioDirectory d, FakeMicrosoftGraph graph, Guid directoryId, DateTimeOffset now, DateTimeOffset today)
    {
        if (d.Fill is not { } fill)
        {
            return;
        }
        var people = d.Users.Where(u => u.Enabled).ToList();
        var byMail = d.Users.ToDictionary(u => u.Mail, StringComparer.OrdinalIgnoreCase);
        var reports = people.Where(u => u.Manager is not null).GroupBy(u => u.Manager!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var top = people.FirstOrDefault(u => u.Manager is null && reports.ContainsKey(u.Mail));
        var n = 0;

        // Mail: each template once per person whose sender resolves; nobody mails themselves.
        foreach (var (person, index) in people.Select((p, i) => (p, i)))
        {
            var mailbox = graph.MailboxOf(directoryId, person.Id);
            foreach (var (t, k) in fill.Messages.Select((t, k) => (t, k)))
            {
                var sender = Sender(t, person, index + k, people, byMail, reports, top);
                if (sender is null || string.Equals(sender.Value.Mail, person.Mail, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var ar = Arabic(person.PreferredLanguage);
                mailbox.Messages.Add(new FakeMicrosoftGraph.Message($"fill-{++n:D5}", Text(t.Subject, ar, person, sender.Value.Name),
                    sender.Value.Name, sender.Value.Mail, now - TimeSpan.FromHours(t.HoursAgo + (index % 3) * 0.25), Text(t.Preview, ar, person, sender.Value.Name),
                    t.IsRead, t.Importance));
            }
        }

        // Calendar: team meetings per manager, one to ones, and personal blocks.
        var rooms = d.Rooms.ToDictionary(r => r.DisplayName, r => r.EmailAddress, StringComparer.OrdinalIgnoreCase);
        foreach (var t in fill.Events)
        {
            switch (t.Pattern)
            {
                case FillPattern.Team:
                    foreach (var (managerMail, team) in reports.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var manager = byMail[managerMail];
                        foreach (var day in t.DayOffsets)
                        {
                            Add(graph, directoryId, manager, $"fill-ev-{++n:D5}", Text(t.Subject, Arabic(manager.PreferredLanguage), manager, manager.DisplayName),
                                At(today, day, t.Start), t.Minutes, t.Location, [.. team.Select(x => x.Mail)], t.Online, t.ShowAs, rooms);
                        }
                    }
                    break;
                case FillPattern.OneToOne:
                    foreach (var (person, index) in people.Where(p => p.Manager is not null).Select((p, i) => (p, i)))
                    {
                        var manager = byMail[person.Manager!];
                        var day = t.DayOffsets.Count == 0 ? 0 : t.DayOffsets[index % t.DayOffsets.Count];
                        var start = At(today, day, t.Start).AddMinutes(index / Math.Max(1, t.DayOffsets.Count) % 4 * 45);
                        var subject = Text(t.Subject, Arabic(manager.PreferredLanguage), person, manager.DisplayName);
                        Add(graph, directoryId, manager, $"fill-ev-{++n:D5}", subject, start, t.Minutes, t.Location, [person.Mail], t.Online, t.ShowAs, rooms);
                    }
                    break;
                case FillPattern.Personal:
                    foreach (var (person, index) in people.Select((p, i) => (p, i)))
                    {
                        foreach (var day in t.DayOffsets)
                        {
                            var start = At(today, day, t.Start).AddMinutes(index % 3 * 30);
                            Add(graph, directoryId, person, $"fill-ev-{++n:D5}", Text(t.Subject, Arabic(person.PreferredLanguage), person, person.DisplayName),
                                start, t.Minutes, t.Location, [], t.Online, t.ShowAs, rooms);
                        }
                    }
                    break;
            }
        }

        FillExtraMailboxes(d, fill, graph, directoryId, now, today, people, byMail, reports, top);
    }

    /// <summary>
    /// UPN mailboxes for people without a directory account. They get the same mail, their own calendar (personal
    /// blocks and the directory meetings they attend) and free/busy of every colleague, as a directory member would.
    /// </summary>
    private static void FillExtraMailboxes(ScenarioDirectory d, ScenarioFill fill, FakeMicrosoftGraph graph, Guid directoryId, DateTimeOffset now,
        DateTimeOffset today, List<ScenarioUser> people, Dictionary<string, ScenarioUser> byMail, Dictionary<string, List<ScenarioUser>> reports, ScenarioUser? top)
    {
        if (d.ExtraMailboxes.Count == 0)
        {
            return;
        }
        var events = graph.DirectoryEvents(directoryId);
        foreach (var colleague in people)
        {
            graph.Schedule(colleague.Mail, [.. events
                .Where(e => e.ShowAs != "free" && (e.OrganizerAddress.Equals(colleague.Mail, StringComparison.OrdinalIgnoreCase)
                    || e.Attendees.Contains(colleague.Mail, StringComparer.OrdinalIgnoreCase)))
                .Select(e => new FakeMicrosoftGraph.FakeScheduleItem(e.ShowAs, e.Start, e.End))]);
        }
        var n = 0;
        foreach (var extra in d.ExtraMailboxes)
        {
            var person = new ScenarioUser
            {
                Id = extra.Upn, Mail = extra.Upn, DisplayName = extra.DisplayName, GivenName = extra.DisplayName.Split(' ')[0],
                PreferredLanguage = extra.PreferredLanguage, Manager = extra.Manager,
            };
            var mailbox = graph.MailboxFor(extra.Upn);
            mailbox.Exists = true;
            var ar = Arabic(person.PreferredLanguage);
            foreach (var (t, k) in fill.Messages.Select((t, k) => (t, k)))
            {
                var sender = Sender(t, person, k, people, byMail, reports, top);
                if (sender is null)
                {
                    continue;
                }
                mailbox.Messages.Add(new FakeMicrosoftGraph.FakeMessage($"xm-{++n:D5}", sender.Value.Name, sender.Value.Mail, Text(t.Subject, ar, person, sender.Value.Name),
                    now - TimeSpan.FromHours(t.HoursAgo), t.IsRead, t.Importance, t.HasAttachments, Text(t.Preview, ar, person, sender.Value.Name)));
            }
            mailbox.UnreadCount = mailbox.Messages.Count(m => !m.IsRead);
            foreach (var e in events.Where(e => e.Attendees.Contains(extra.Upn, StringComparer.OrdinalIgnoreCase)))
            {
                mailbox.Events.Add(new FakeMicrosoftGraph.FakeEvent(e.Id, e.Subject, e.Start, e.End, ShowAs: e.ShowAs, Location: e.Location,
                    JoinUrl: e.IsOnline ? $"https://teams.microsoft.com/l/meetup-join/fake-{e.Id}" : null));
            }
            foreach (var t in fill.Events.Where(t => t.Pattern == FillPattern.Personal))
            {
                foreach (var day in t.DayOffsets)
                {
                    var start = At(today, day, t.Start);
                    mailbox.Events.Add(new FakeMicrosoftGraph.FakeEvent($"xe-{++n:D5}", Text(t.Subject, ar, person, person.DisplayName), start, start.AddMinutes(t.Minutes),
                        ShowAs: t.ShowAs, Location: t.Location));
                }
            }
        }
    }

    private static void Add(FakeMicrosoftGraph graph, Guid directoryId, ScenarioUser organizer, string id, string subject, DateTimeOffset start, int minutes,
        string? location, List<string> attendees, bool online, string showAs, Dictionary<string, string> rooms)
    {
        if (location is not null && rooms.TryGetValue(location, out var room))
        {
            attendees.Add(room);
        }
        graph.MailboxOf(directoryId, organizer.Id).Events.Add(new FakeMicrosoftGraph.Event(id, subject, start, start.AddMinutes(minutes), location,
            organizer.DisplayName, organizer.Mail, attendees, online, showAs));
    }

    private static (string Name, string Mail)? Sender(FillMessage t, ScenarioUser person, int index, List<ScenarioUser> people,
        Dictionary<string, ScenarioUser> byMail, Dictionary<string, List<ScenarioUser>> reports, ScenarioUser? top)
    {
        ScenarioUser? Pick(IReadOnlyList<ScenarioUser> list)
        {
            var others = list.Where(x => !string.Equals(x.Mail, person.Mail, StringComparison.OrdinalIgnoreCase)).ToList();
            return others.Count == 0 ? null : others[index % others.Count];
        }
        var who = t.From switch
        {
            "manager" => person.Manager is not null && byMail.TryGetValue(person.Manager, out var m) ? m : top,
            "colleague" => Pick(person.Manager is not null && reports.TryGetValue(person.Manager, out var peers) ? peers
                : people.Where(p => p.Department == person.Department).ToList()),
            "report" => reports.TryGetValue(person.Mail, out var mine) ? Pick(mine) : null,
            "anyone" => Pick(people),
            _ when t.From.Contains('@') => byMail.GetValueOrDefault(t.From) ?? new ScenarioUser { Mail = t.From, DisplayName = t.FromName ?? t.From },
            _ => null,
        };
        return who is null ? null : (who.DisplayName, who.Mail);
    }

    private static DateTimeOffset At(DateTimeOffset today, int day, string start) =>
        today.AddDays(day) + TimeSpan.Parse(start, CultureInfo.InvariantCulture);

    private static bool Arabic(string? language) => language?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>{first} is the person's given name, {sender} the sender's or organizer's display name, {person} the person's name.</summary>
    private static string Text(LocalizedText text, bool arabic, ScenarioUser person, string sender) =>
        ((arabic ? text.Ar : text.En) ?? text.En ?? text.Ar ?? string.Empty)
            .Replace("{first}", person.GivenName ?? person.DisplayName, StringComparison.Ordinal)
            .Replace("{person}", person.DisplayName, StringComparison.Ordinal)
            .Replace("{sender}", sender, StringComparison.Ordinal);
}

public sealed class ScenarioFill
{
    public List<FillMessage> Messages { get; set; } = [];
    public List<FillEvent> Events { get; set; } = [];
}

public sealed class LocalizedText
{
    public string? En { get; set; }
    public string? Ar { get; set; }
}

public sealed class FillMessage
{
    /// <summary><c>manager</c>, <c>colleague</c> (same manager, else same department), <c>report</c>, <c>anyone</c>, or an address.</summary>
    public string From { get; set; } = "manager";

    /// <summary>Display name for an address that is not a person of the directory (a shared mailbox).</summary>
    public string? FromName { get; set; }

    public LocalizedText Subject { get; set; } = new();
    public LocalizedText Preview { get; set; } = new();
    public double HoursAgo { get; set; }
    public bool IsRead { get; set; }
    public string Importance { get; set; } = "normal";
    public bool HasAttachments { get; set; }
}

public enum FillPattern
{
    /// <summary>Organized by every manager, attended by their direct reports.</summary>
    Team,

    /// <summary>Every person with a manager meets them; spread over the day offsets.</summary>
    OneToOne,

    /// <summary>A block in every person's own calendar (focus time, lunch, out of office).</summary>
    Personal,
}

public sealed class FillEvent
{
    public FillPattern Pattern { get; set; }
    public LocalizedText Subject { get; set; } = new();
    public List<int> DayOffsets { get; set; } = [0];
    public string Start { get; set; } = "09:00";
    public int Minutes { get; set; } = 30;
    public string? Location { get; set; }
    public bool Online { get; set; }
    public string ShowAs { get; set; } = "busy";
}

public sealed class ScenarioMailbox
{
    public string Upn { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PreferredLanguage { get; set; }

    /// <summary>Mail of the directory person who acts as this mailbox's manager in generated mail.</summary>
    public string? Manager { get; set; }
}
