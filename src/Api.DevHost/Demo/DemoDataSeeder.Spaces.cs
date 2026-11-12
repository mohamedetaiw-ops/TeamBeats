using TeamBeats.Modules.Spaces;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// Project and team spaces (spec 029), each created by its owner from the standard template with its members, then its
    /// milestones, tasks with assignees and due dates, and a status. A space is found again by its name in the tenant's
    /// default language, and only a new space gets milestones and tasks, so a restart adds nothing.
    /// </summary>
    private async Task SeedSpacesAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.Spaces.Count == 0)
        {
            return;
        }
        var locale = await AsSystemAsync(run, async sp => (await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync(ct)).DefaultLocale, ct);
        string Text(Dictionary<string, string> text) => text.GetValueOrDefault(locale) ?? text.GetValueOrDefault("en") ?? text.Values.First();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var plan in run.Pack.Spaces)
        {
            var name = Text(plan.Names);
            await ItemAsync(run, $"space {name}", async () =>
            {
                var members = (plan.Members ?? []).Where(m => run.Person(m.Key) is not null)
                    .Select(m => new MemberRequest(run.Person(m.Key)!.Value, m.Value)).ToList();
                var created = await AsAsync(run, plan.Owner, 0, async sp =>
                {
                    var spaces = sp.GetRequiredService<SpacesService>();
                    if ((await spaces.ListAsync(true, false, name, ct)).Any(s => s.Name == name))
                    {
                        return (Guid?)null;
                    }
                    var view = await spaces.CreateAsync(new SpaceRequest(name, Text(plan.Descriptions), plan.Kind, plan.Visibility, plan.Kind, plan.Visibility == "public",
                        "internal", null, null, members, null), ct);
                    return view.Id;
                }, ct);
                if (created is not { } spaceId)
                {
                    return;
                }
                run.Report.Add("spaces");

                var milestones = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var milestone in plan.Milestones ?? [])
                {
                    var list = await AsAsync(run, plan.Owner, 0, sp => sp.GetRequiredService<SpaceMilestones>()
                        .CreateAsync(spaceId, new MilestoneRequest(Text(milestone.Names), today.AddDays(milestone.DueInDays), null), ct), ct);
                    milestones[milestone.Key] = list!.Single(m => m.Name == Text(milestone.Names)).Id;
                    run.Report.Add("space milestones");
                }
                foreach (var task in plan.Tasks ?? [])
                {
                    var assignees = (task.Assignees ?? []).Select(run.Person).OfType<Guid>().ToList();
                    await AsAsync(run, plan.Owner, 0, async sp =>
                    {
                        var tasks = sp.GetRequiredService<SpaceTasks>();
                        var detail = await tasks.CreateAsync(spaceId, new TaskRequest(null, null, Text(task.Titles), null, task.Priority, null,
                            task.DueInDays is { } d ? today.AddDays(d) : null, assignees, task.Milestone is { } m ? milestones[m] : null, null), ct);
                        if (task.Done)
                        {
                            await tasks.CompleteAsync(spaceId, detail.Task.Id, true, ct);
                        }
                    }, ct);
                    run.Report.Add("space tasks");
                }
                if (plan.Status is { } status)
                {
                    await AsAsync(run, plan.Owner, 0, sp => sp.GetRequiredService<SpaceMilestones>()
                        .SetStatusAsync(spaceId, new StatusRequest(status, plan.StatusNotes is { } notes ? Text(notes) : null), ct), ct);
                }
            });
        }
    }
}
