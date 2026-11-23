using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Evaluation;

/// <summary>
/// A drift alert (spec 034 US7 scenario 6, FR-024): a metric of an agent's production samples in one language, over
/// the rolling window, against the last gate result of the same version and the metric's threshold. Examples are the
/// samples that scored worst; models are the ones that answered in the window, so a silent change behind one name shows.
/// </summary>
public sealed record DriftAlertView(Guid Id, Guid AgentId, int AgentVersion, string Metric, string Locale, double? Baseline, double? Threshold, double Value,
    int Samples, IReadOnlyList<Guid> Examples, IReadOnlyList<string> Models, string Status, DateTimeOffset DetectedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record DriftView(Guid AgentId, int WindowDays, int MinSamples, double Margin, IReadOnlyList<DriftAlertView> Alerts, string RoutingLink);

/// <summary>The rule a production score drifts by, kept pure so it is tested on its own.</summary>
public static class DriftRules
{
    /// <summary>Samples a metric needs in a language before it can raise or clear an alert.</summary>
    public const int MinSamples = 20;

    /// <summary>A drop of this much below the gate result is drift even when the threshold still holds (spec 034 SC-004: 10 points).</summary>
    public const double Margin = 0.10;

    public const int Examples = 5;
    public const string Open = "open";
    public const string Resolved = "resolved";

    public static bool Drifts(MetricDefinition metric, double value, double? baseline, double? threshold)
    {
        if (metric.HigherIsBetter)
        {
            return (threshold is { } t && value < t) || (baseline is { } b && b - value >= Margin - 1e-9);
        }
        return (threshold is { } max && value > max) || (baseline is { } low && value - low >= Margin - 1e-9);
    }
}

/// <summary>
/// Compares each agent's rolling production scores with the last gate result of the version that produced them (spec
/// 034 US7 scenario 6). A metric that drifts in a language opens one alert, kept up to date while it lasts and resolved
/// when the window recovers; a new alert tells the agent owner and the AI Policy Administrators, with a link to the
/// agent's evaluation where the metrics, the worst samples and the models are, and to model routing to pin or switch.
/// </summary>
public sealed class DriftDetector(IDbSession session, IAgentCatalog agents, IAuthorizationProvisioning roles, IEntitlementService entitlements, INotificationSender notifications,
    IClock clock)
{
    public const string Template = "evaluation.drift_detected";
    public const string RoutingLink = "/admin/ai/routing";

    private sealed record Sample(Guid Id, Guid Agent, int Version, string Locale, string? Model, Dictionary<string, CaseMetric> Metrics);

    public async Task<int> ScanAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct))
        {
            return 0;
        }
        var now = clock.UtcNow;
        var since = now.AddDays(-ProductionSampling.WindowDays);
        var samples = await session.QueryAsync(
            """
            select id, agent_id, agent_version, language, model, metrics::text from evaluation.production_sample
            where status = 'scored' and sampled_at >= @since
            """, new { since },
            r => new Sample(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.Get<string?>("model"),
                JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(r.GetString(5), EvalJson.Options)!), ct);
        var opened = 0;
        foreach (var group in samples.GroupBy(s => (s.Agent, s.Version)))
        {
            var (agentId, version) = group.Key;
            var gate = await GateMetricsAsync(agentId, version, ct);
            var raised = new List<string>();
            foreach (var key in ProductionSampling.ContentMetrics)
            {
                if (MetricCatalog.Find(key) is not { } def)
                {
                    continue;
                }
                foreach (var locale in group.GroupBy(s => s.Locale))
                {
                    var scored = locale.Where(s => s.Metrics.TryGetValue(key, out var m) && m is { State: CaseMetricStates.Scored, Value: not null }).ToList();
                    if (scored.Count < DriftRules.MinSamples)
                    {
                        continue;
                    }
                    var value = Math.Round(scored.Average(s => s.Metrics[key].Value!.Value), 4);
                    var (baseline, gateThreshold) = gate.GetValueOrDefault((key, locale.Key));
                    var threshold = gateThreshold ?? def.DefaultThreshold;
                    var examples = (def.HigherIsBetter ? scored.OrderBy(s => s.Metrics[key].Value) : scored.OrderByDescending(s => s.Metrics[key].Value))
                        .Take(DriftRules.Examples).Select(s => s.Id).ToArray();
                    var models = locale.Select(s => s.Model).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    if (DriftRules.Drifts(def, value, baseline, threshold))
                    {
                        if (await OpenAsync(agentId, version, key, locale.Key, baseline, threshold, value, scored.Count, examples, models, now, ct))
                        {
                            raised.Add(key);
                        }
                    }
                    else
                    {
                        await session.ExecAsync(
                            """
                            update evaluation.drift_alert set status = 'resolved', resolved_at = @now, updated_at = @now, value = @value, samples = @samples
                            where agent_id = @agentId and metric = @key and locale = @locale and status = 'open'
                            """, new { agentId, key, locale = locale.Key, now, value, samples = scored.Count }, ct);
                    }
                }
            }
            if (raised.Count > 0)
            {
                opened += raised.Count;
                await NotifyAsync(agentId, raised, ct);
            }
        }
        return opened;
    }

    /// <summary>Opens an alert, or brings the open one up to date; true when it is new.</summary>
    private async Task<bool> OpenAsync(Guid agentId, int version, string metric, string locale, double? baseline, double? threshold, double value, int samples,
        Guid[] examples, string[] models, DateTimeOffset now, CancellationToken ct)
    {
        var updated = await session.ExecAsync(
            """
            update evaluation.drift_alert set agent_version = @version, baseline = @baseline::float8, threshold = @threshold::float8, value = @value, samples = @samples,
              examples = @examples, models = @models, updated_at = @now
            where agent_id = @agentId and metric = @metric and locale = @locale and status = 'open'
            """, new { agentId, version, metric, locale, baseline, threshold, value, samples, examples, models, now }, ct);
        if (updated > 0)
        {
            return false;
        }
        await session.ExecAsync(
            """
            insert into evaluation.drift_alert (tenant_id, id, agent_id, agent_version, metric, locale, baseline, threshold, value, samples, examples, models, status,
              detected_at, updated_at)
            values (platform.current_tenant_id(), @id, @agentId, @version, @metric, @locale, @baseline::float8, @threshold::float8, @value, @samples, @examples, @models, 'open', @now, @now)
            """, new { id = Ids.New(), agentId, version, metric, locale, baseline, threshold, value, samples, examples, models, now }, ct);
        return true;
    }

    private async Task NotifyAsync(Guid agentId, IReadOnlyList<string> metrics, CancellationToken ct)
    {
        var recipients = new HashSet<Guid>(await roles.TenantHoldersAsync(Permissions.EvalPolicyManage, ct));
        if (await agents.GetAsync(agentId, ct) is { } agent)
        {
            recipients.Add(agent.OwnerUserId);
        }
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.System, Template, $"/admin/evaluation/agents/{agentId}",
            new Dictionary<string, string> { ["count"] = metrics.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            DedupeKey: $"{Template}:{agentId:N}:{string.Join(',', metrics)}"), ct);
    }

    /// <summary>Each metric per locale of the last completed gate run of the version: its value and its threshold.</summary>
    private async Task<Dictionary<(string, string), (double? Value, double? Threshold)>> GateMetricsAsync(Guid agentId, int version, CancellationToken ct)
    {
        var text = await session.ScalarAsync<string?>(
            """
            select result::text from evaluation.run where agent_id = @agentId and agent_version = @version and status = 'completed' and result is not null
              and trigger = 'gate'
            order by finished_at desc limit 1
            """, new { agentId, version }, ct);
        var result = new Dictionary<(string, string), (double?, double?)>();
        if (text is null)
        {
            return result;
        }
        foreach (var m in JsonSerializer.Deserialize<RunResult>(text, EvalJson.Options)!.Metrics ?? [])
        {
            foreach (var l in m.Locales)
            {
                result[(m.Key, l.Locale)] = (l.Value, m.Threshold);
            }
        }
        return result;
    }
}

/// <summary>An agent's drift alerts, open and resolved in the last 30 days, for whoever may read its evaluation.</summary>
public sealed class DriftAlerts(IDbSession session, EvalAccess access, IEntitlementService entitlements, IClock clock)
{
    public async Task<DriftView> ForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await access.ReadableAgentAsync(agentId, ct);
        await entitlements.DemandAsync(Capabilities.AiEvalsProduction, ct);
        var alerts = await session.QueryAsync(
            """
            select id, agent_id, agent_version, metric, locale, baseline, threshold, value, samples, examples, models, status, detected_at, updated_at, resolved_at
            from evaluation.drift_alert where agent_id = @agent and (status = 'open' or resolved_at >= @since) order by status, detected_at desc
            """, new { agent = agent.Id, since = clock.UtcNow.AddDays(-30) },
            r => new DriftAlertView(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Get<double?>("baseline"), r.Get<double?>("threshold"),
                r.GetDouble(7), r.GetInt32(8), r.GetFieldValue<Guid[]>(9), r.GetFieldValue<string[]>(10), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12),
                r.GetFieldValue<DateTimeOffset>(13), r.Get<DateTimeOffset?>("resolved_at")), ct);
        return new DriftView(agent.Id, ProductionSampling.WindowDays, DriftRules.MinSamples, DriftRules.Margin, alerts, DriftDetector.RoutingLink);
    }
}
