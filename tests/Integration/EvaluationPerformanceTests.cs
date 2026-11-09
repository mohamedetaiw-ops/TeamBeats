using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T017, section 16: a run of an agent's 100-case suite in two languages, with the inherited platform suites,
/// completes well within 15 minutes. The model calls go to the development double, so the time measured is the
/// evaluation's own work: planning, running each case through the real runtime as its persona, judging and scoring. The
/// runner is driven message by message as the workers would. The same run checks the evaluation spans of FR-029.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Eval = "/api/v1/evaluation";
    private const int CasesPerLocale = 50;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(15);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_100_case_suite_in_two_languages_completes_within_15_minutes()
    {
        var t = await world.CreateTenantAsync("evp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        var sara = t["sara"].Client;
        var agent = (await OkAsync(sara.PostAsJsonAsync("/api/v1/agents", new { templateId = "hr_assistant", orgUnitId = hr }), HttpStatusCode.Created))
            .GetProperty("agent").GetProperty("id").GetGuid();
        var dataset = (await OkAsync(sara.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "Load" } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var cases = Enumerable.Range(1, CasesPerLocale).SelectMany(i => new[]
        {
            new { input = $"How many days of annual leave do I get in year {i}?", locale = "en", expect = new { keyFacts = new[] { "leave" } } },
            new { input = $"كم عدد أيام الإجازة السنوية في السنة {i}؟", locale = "ar", expect = new { keyFacts = new[] { "الإجازة" } } },
        }).ToArray();
        await OkAsync(sara.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 0, cases }));
        await OkAsync(sara.PutAsJsonAsync($"{Eval}/agents/{agent}/suite", new { version = 0, metrics = new[] { new { key = "answer_correctness", weight = 1 } }, datasets = new[] { dataset } }));

        // Every case leaves a span with the run, the case and the model attributes (FR-029); the listener sees spans of this run only.
        var spans = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        Guid? watched = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EvalTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (watched is { } id && a.GetTagItem("eval.run_id") as string == id.ToString())
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var clock = Stopwatch.StartNew();
        var run = (await OkAsync(sara.PostAsJsonAsync($"{Eval}/runs", new { agentId = agent }), HttpStatusCode.Created)).GetProperty("run").GetProperty("id").GetGuid();
        watched = run;
        var messages = 0;
        while (await world.Database.TenantScalarAsync<string>(t.Id, "select status from evaluation.run where id = @run", ("run", run)) != RunStatuses.Completed)
        {
            Assert.True(messages++ < 100, "The run did not finish.");
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<EvaluationRunner>().HandleAsync(new EvaluationRunRequested(run), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        clock.Stop();

        var detail = await OkAsync(sara.GetAsync($"{Eval}/runs/{run}"));
        var total = detail.GetProperty("run").GetProperty("total").GetInt32();
        Assert.True(total >= 2 * CasesPerLocale, $"{total} cases");
        Assert.Equal(0, detail.GetProperty("run").GetProperty("errors").GetInt32());
        output.WriteLine($"{total} cases in {messages} messages took {clock.Elapsed.TotalSeconds:F1} s ({clock.Elapsed.TotalMilliseconds / total:F0} ms per case)");
        Assert.True(clock.Elapsed < Budget, $"{clock.Elapsed} for {total} cases");

        var caseSpans = spans.Where(a => a.OperationName == "eval.case").ToList();
        Assert.Equal(total, caseSpans.Count);
        Assert.All(caseSpans, a =>
        {
            Assert.Equal("invoke_agent", a.GetTagItem("gen_ai.operation.name"));
            Assert.NotNull(a.GetTagItem("gen_ai.response.model"));
            Assert.NotNull(a.GetTagItem("eval.locale"));
        });
        Assert.Equal(messages, spans.Count(a => a.OperationName == "eval.batch"));
    }
}
