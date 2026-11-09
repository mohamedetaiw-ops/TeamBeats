using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    [GeneratedRegex(@"^([+-]\d+)d$")]
    private static partial Regex RelativeDay();

    /// <summary>
    /// The catalogue: categories, form services with their one approval step, and linked applications, published by
    /// the administrator. Services the tenant already has (by English name) keep their settings; one without a category
    /// is put in the pack's category. Favourites are marked for the people the pack names.
    /// </summary>
    private async Task SeedServicesAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.Services.Count == 0)
        {
            return;
        }
        var categories = await AsAsync(run, AdminPersona, 0, async sp =>
        {
            var admin = sp.GetRequiredService<ServiceAdmin>();
            var existing = await sp.GetRequiredService<Catalogue>().CategoriesAsync(ct);
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var position = existing.Count;
            foreach (var category in run.Pack.ServiceCategories)
            {
                var en = category.Names.GetValueOrDefault("en");
                var found = existing.FirstOrDefault(c => c.Names.GetValueOrDefault("en") == en);
                if (found is null)
                {
                    found = await admin.CreateCategoryAsync(new CategoryRequest(category.Names, position++), ct);
                    run.Report.Add("service categories");
                }
                ids[category.Key] = found.Id;
            }
            return ids;
        }, ct) ?? [];

        var services = await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<ServiceAdmin>().ListAsync(ct), ct) ?? [];
        foreach (var service in run.Pack.Services)
        {
            var name = service.Names.GetValueOrDefault("en");
            var category = categories.TryGetValue(service.Category, out var c) ? c : (Guid?)null;
            if (services.FirstOrDefault(s => s.Service.Kind != ServiceKinds.Module && s.Service.Names.GetValueOrDefault("en") == name) is { } found)
            {
                run.Refs[$"service:{service.Key}"] = ("services", found.Service.Id);
                if (found.Service.CategoryId is null && category is not null && found.Service.Status != "retired")
                {
                    var row = found.Service;
                    await ItemAsync(run, $"category of {name}", () => AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<ServiceAdmin>().UpdateAsync(row.Id,
                        new ServiceSettingsRequest(row.Kind, row.Names, row.Descriptions, row.Synonyms, category, row.Audience, row.OwnerOrgUnitId, row.Classification,
                            row.Url, row.Sso), ct), ct));
                }
                continue;
            }
            await ItemAsync(run, $"service {service.Key}", async () =>
            {
                var id = await AsAsync(run, AdminPersona, 30, async sp =>
                {
                    var admin = sp.GetRequiredService<ServiceAdmin>();
                    var kind = service.Kind == "link" ? ServiceKinds.Linked : ServiceKinds.Form;
                    var created = await admin.CreateAsync(new ServiceSettingsRequest(kind, service.Names, service.Descriptions,
                        service.Synonyms?.ToDictionary(s => s.Key, s => (IReadOnlyList<string>)s.Value), category, null, null, "internal",
                        service.Url, false), ct);
                    if (kind == ServiceKinds.Form)
                    {
                        await admin.SaveDraftAsync(created.Service.Id, new ServiceDraftRequest(service.Form ?? FormDefinition.Empty, Route(run, service.Approval), null,
                            service.AllowOnBehalf, 3), ct);
                    }
                    await admin.PublishAsync(created.Service.Id, ct);
                    return created.Service.Id;
                }, ct);
                run.Refs[$"service:{service.Key}"] = ("services", id);
                run.Report.Add("services");
            });
        }

        // Favourites, so My services shows what each persona uses most.
        foreach (var service in run.Pack.Services.Where(s => s.FavouriteOf is { Count: > 0 }))
        {
            if (!run.Refs.TryGetValue($"service:{service.Key}", out var target))
            {
                continue;
            }
            foreach (var persona in service.FavouriteOf!)
            {
                await ItemAsync(run, $"favourite {service.Key} of {persona}", () => AsAsync(run, persona, 0,
                    sp => sp.GetRequiredService<Catalogue>().SetFavouriteAsync(target.Id, true, ct), ct));
            }
        }
    }

    private static ServiceRoute? Route(Run run, DemoApproval? approval)
    {
        if (approval is null)
        {
            return ServiceRoute.None;
        }
        var approvers = approval.LineManager
            ? new ApproverRule(ApproverKinds.LineManager)
            : new ApproverRule(ApproverKinds.Users, (approval.Approvers ?? [AdminPersona]).Select(run.Person).OfType<Guid>().Distinct().DefaultIfEmpty(run.People[AdminPersona]).ToList());
        return new ServiceRoute(new ApprovalStep(approval.Names, approvers, approval.DueWorkingDays));
    }

    /// <summary>
    /// Requests submitted by the named people on the day given, each once by its key, then approved, rejected or returned
    /// by the named approvers. Requests left open fill the approvers' task inbox and Your day; older ones are overdue.
    /// </summary>
    private async Task SeedRequestsAsync(Run run, CancellationToken ct)
    {
        foreach (var request in run.Pack.Requests.OrderByDescending(r => r.DaysAgo))
        {
            if (!run.Refs.TryGetValue($"service:{request.Service}", out var service))
            {
                run.Report.Skipped.Add($"request {request.Key}: service {request.Service} missing");
                continue;
            }
            await ItemAsync(run, $"request {request.Key}", async () =>
            {
                var submitted = await AsAsync(run, request.Requester, request.DaysAgo, async sp =>
                {
                    var today = DateOnly.FromDateTime(sp.GetRequiredService<IClock>().UtcNow.UtcDateTime);
                    var data = request.Data.ToDictionary(d => d.Key, d => Resolve(d.Value, today, run), StringComparer.Ordinal);
                    return await sp.GetRequiredService<Requests>().SubmitAsync(new RequestSubmit(service.Id, data, null, "demo:" + request.Key), ct);
                }, ct);
                if (submitted is not { Created: true } done)
                {
                    return;
                }
                run.Report.Add("requests");
                // The approval task is created from the request's event: deliver it before anyone decides.
                await DispatchAsync(ct);
                foreach (var decision in request.Decisions ?? [])
                {
                    await AsAsync(run, decision.By, decision.DaysAgo, async sp =>
                    {
                        var tasks = sp.GetRequiredService<Tasks>();
                        var task = (await tasks.InboxAsync(service.Id, ct)).FirstOrDefault(t => t.RequestId == done.Request.Request.Id)
                            ?? throw new InvalidOperationException($"{decision.By} has no task for request {request.Key}");
                        await tasks.DecideAsync(task.TaskId, new TaskDecision(decision.Outcome, decision.Comment), ct);
                    }, ct);
                    run.Report.Add("decisions");
                }
            });
        }
    }

    /// <summary>
    /// Request values as the form takes them: <c>+5d</c> becomes a date that many days after submission, inside objects
    /// too (date ranges), and <c>@persona</c> in an array becomes that person's id (person fields).
    /// </summary>
    private static JsonElement Resolve(JsonElement value, DateOnly today, Run run)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when RelativeDay().Match(value.GetString()!) is { Success: true } m:
                return JsonSerializer.SerializeToElement(today.AddDays(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case JsonValueKind.String when value.GetString() is ['@', .. var persona] && run.Person(persona) is { } id:
                return JsonSerializer.SerializeToElement(id);
            case JsonValueKind.Object:
                return JsonSerializer.SerializeToElement(value.EnumerateObject().ToDictionary(p => p.Name, p => Resolve(p.Value, today, run)));
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(v => Resolve(v, today, run)).ToList());
            default:
                return value;
        }
    }
}
