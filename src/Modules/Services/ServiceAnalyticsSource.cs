using System.Text.Json;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Services for service analytics (spec 021 US4, spec 016 FR-012). The same rights as service administration: an owner
/// with <c>service.manage</c> at the owning unit. No person, comment or free text leaves this module; a request's
/// structured reasons are the values of its reportable choice fields only.
/// </summary>
internal sealed class ServiceAnalyticsSource(ServiceAdmin admin, IDbSession session) : IAnalyticsServiceSource
{
    /// <summary>The most requests one answer reads; a longer period is cut at the oldest.</summary>
    public const int MaxRequests = 20000;

    /// <summary>Step decisions and how they map: the outcome, and whether automation decided it.</summary>
    private static readonly IReadOnlyDictionary<string, (string Outcome, bool Automated)> Decisions = new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
    {
        ["approved"] = ("approved", false),
        ["rejected"] = ("rejected", false),
        ["returned"] = ("returned", false),
        ["task_done"] = ("done", false),
        ["task_rejected"] = ("rejected", false),
        ["auto_approved"] = ("approved", true),
        ["automation_done"] = ("done", true),
        ["automation_failed"] = ("failed", true),
        ["skipped"] = ("skipped", true),
    };

    /// <summary>Events that start the wait for the next decision.</summary>
    private static readonly IReadOnlySet<string> Starts = new HashSet<string>(StringComparer.Ordinal) { "submitted", "resubmitted", "triggered" };

    private static AnalyticsService Map(ServiceRow row) => new(row.Id, row.Names, ServiceAdmin.ProcessKey(row.Id), row.OwnerOrgUnitId);

    public async Task<IReadOnlyList<AnalyticsService>> ListAsync(CancellationToken ct = default) =>
        (await admin.ListAsync(ct)).Select(v => Map(v.Service)).ToList();

    public async Task<AnalyticsServiceFacts> FactsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var view = await admin.GetAsync(serviceId, ct);
        var fields = (view.Latest?.Form.Fields ?? []).Where(f => f.Reportable && f.Type == FieldTypes.Choice && f.Options is { Count: > 0 }).ToList();
        var options = fields.ToDictionary(f => f.Key, f => f.Options!.Select(o => o.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var requests = await session.QueryAsync(
            """
            select r.id, r.submitted_at, r.closed_at, r.status, r.requester_org_unit_id, r.service_level = 'breached', r.data::text
            from services.request r
            where r.service_id = @serviceId and r.submitted_at >= @from and r.submitted_at < @to
            order by r.submitted_at desc
            limit @max
            """, new { serviceId, from, to, max = MaxRequests },
            r => (Id: r.GetGuid(0), Submitted: r.GetFieldValue<DateTimeOffset>(1), Closed: r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(2),
                Status: r.GetString(3), Unit: r.IsDBNull(4) ? (Guid?)null : r.GetGuid(4), Breached: !r.IsDBNull(5) && r.GetBoolean(5), Data: r.GetString(6)), ct);
        var ids = requests.Select(r => r.Id).ToArray();
        var events = ids.Length == 0
            ? []
            : await session.QueryAsync(
                "select request_id, event, step, at from services.request_event where request_id = any(@ids) order by request_id, id",
                new { ids }, r => (Request: r.GetGuid(0), Event: r.GetString(1), Step: r.IsDBNull(2) ? null : r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var byRequest = events.GroupBy(e => e.Request).ToDictionary(g => g.Key, g => g.ToList());
        var facts = new List<AnalyticsRequestFact>(requests.Count);
        foreach (var r in requests)
        {
            var steps = new List<AnalyticsStepFact>();
            DateTimeOffset? since = r.Submitted;
            foreach (var e in byRequest.GetValueOrDefault(r.Id) ?? [])
            {
                if (Starts.Contains(e.Event))
                {
                    since = e.At;
                }
                else if (Decisions.TryGetValue(e.Event, out var d))
                {
                    steps.Add(new AnalyticsStepFact(e.Step ?? "", d.Outcome, since is { } s ? Math.Max(0, (e.At - s).TotalSeconds) : 0, d.Automated));
                    since = e.At;
                }
            }
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fields.Count > 0)
            {
                using var doc = JsonDocument.Parse(r.Data);
                foreach (var f in fields)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(f.Key, out var value) && value.ValueKind == JsonValueKind.String
                        && options[f.Key].Contains(value.GetString()!))
                    {
                        reasons[f.Key] = value.GetString()!;
                    }
                }
            }
            facts.Add(new AnalyticsRequestFact(r.Submitted, r.Closed, r.Status, r.Unit, r.Breached, steps, reasons));
        }
        var reasonFields = fields.Select(f => new AnalyticsReasonField(f.Key, f.Labels, f.Options!.Select(o => new AnalyticsReasonOption(o.Value, o.Labels)).ToList())).ToList();
        return new AnalyticsServiceFacts(Map(view.Service), facts, reasonFields);
    }
}
