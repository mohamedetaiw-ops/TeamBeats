using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Services;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// The policy register: categories, then each policy created by the administrator for its owner, written in both
    /// languages (English binding), submitted and approved by its approver through the task inbox, so version 1 is in
    /// force. A policy with a campaign asks everyone to attest; the people the pack names have already done so. A policy
    /// the tenant already has (by code) is left as it is.
    /// </summary>
    private async Task SeedPoliciesAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.Policies.Count == 0)
        {
            return;
        }
        var (categories, existing) = await AsAsync(run, AdminPersona, 0, async sp =>
        {
            var register = sp.GetRequiredService<PolicyRegister>();
            var known = await register.CategoriesAsync(ct);
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var category in run.Pack.PolicyCategories)
            {
                var en = category.Names.GetValueOrDefault("en");
                var found = known.FirstOrDefault(c => c.Names.GetValueOrDefault("en") == en)
                    ?? await register.CreateCategoryAsync(new TeamBeats.Modules.Policies.CategoryRequest(category.Names), ct);
                ids[category.Key] = found.Id;
            }
            return (ids, (await register.ListAsync(null, null, null, ct)).ToDictionary(p => p.Code, p => p.Id, StringComparer.Ordinal));
        }, ct);

        foreach (var policy in run.Pack.Policies.OrderByDescending(p => p.DaysAgo))
        {
            if (existing.TryGetValue(policy.Code, out var known))
            {
                run.Refs[$"policy:{policy.Code}"] = ("policies", known);
                continue;
            }
            await ItemAsync(run, $"policy {policy.Code}", async () =>
            {
                var approver = run.Person(policy.Approver) is null ? AdminPersona : policy.Approver;
                var id = await AsAsync(run, AdminPersona, policy.DaysAgo + 1, async sp =>
                {
                    var created = await sp.GetRequiredService<PolicyRegister>().CreateAsync(new PolicyRequest(policy.Code, policy.Names,
                        categories.TryGetValue(policy.Category, out var c) ? c : null, run.Person(policy.Owner) ?? run.People[AdminPersona], null, null, "internal", "en",
                        [new RouteStepSetting(null, [run.People[approver]])], 12, null), ct);
                    var approvals = sp.GetRequiredService<PolicyApprovals>();
                    var clauses = policy.Clauses.Select(c => new ClauseInput(null, c.Level, c.Title,
                        c.Body.ToDictionary(b => b.Key, b => DemoText.Paragraphs(b.Value), StringComparer.Ordinal))).ToList();
                    await approvals.SaveDraftAsync(created.Id, new DraftRequest(policy.Names, clauses, true, null), ct);
                    return created.Id;
                }, ct);
                // Submitted by its owner when the owner may, otherwise by the administrator.
                if (await AsAsync(run, policy.Owner, policy.DaysAgo + 1, sp => TryAsync(() => sp.GetRequiredService<PolicyApprovals>().SubmitAsync(id, new SubmitRequest(null), ct)), ct) is null)
                {
                    await AsAsync(run, AdminPersona, policy.DaysAgo + 1, sp => sp.GetRequiredService<PolicyApprovals>().SubmitAsync(id, new SubmitRequest(null), ct), ct);
                }
                await DispatchAsync(ct);
                await AsAsync(run, approver, policy.DaysAgo, async sp =>
                {
                    var tasks = sp.GetRequiredService<Tasks>();
                    var task = (await tasks.InboxAsync(null, ct)).FirstOrDefault(t => t.Source == "policy_approval" && t.Link?.Contains(id.ToString(), StringComparison.Ordinal) == true)
                        ?? (await tasks.InboxAsync(null, ct)).First(t => t.Source == "policy_approval");
                    await tasks.DecideAsync(task.TaskId, new TaskDecision("approve", null), ct);
                }, ct);
                run.Refs[$"policy:{policy.Code}"] = ("policies", id);
                run.Report.Add("policies");
                if (policy.Campaign is { } campaign)
                {
                    await CampaignAsync(run, policy, id, campaign, ct);
                }
            });
        }
    }

    private async Task CampaignAsync(Run run, DemoPolicy policy, Guid id, DemoCampaign campaign, CancellationToken ct)
    {
        await DispatchAsync(ct);
        var launched = await AsAsync(run, AdminPersona, Math.Max(0, policy.DaysAgo - 1), sp => sp.GetRequiredService<PolicyCampaigns>().LaunchAsync(id,
            new CampaignRequest(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(campaign.DeadlineDays), campaign.Text, null, null, null, null), ct), ct);
        run.Report.Add("campaigns");
        await DispatchAsync(ct);
        var step = 0;
        foreach (var persona in campaign.AttestedBy ?? [])
        {
            var daysAgo = Math.Max(0, policy.DaysAgo - 1 - 0.3 * ++step);
            await ItemAsync(run, $"attestation of {persona}", async () =>
            {
                if (await AsAsync(run, persona, daysAgo, sp => sp.GetRequiredService<PolicyCampaigns>().AttestAsync(id, new AttestRequest(launched!.Id, "en", null), ct), ct) is not null)
                {
                    run.Report.Add("attestations");
                }
            });
        }
    }
}
