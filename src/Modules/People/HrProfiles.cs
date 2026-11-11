using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.People;

/// <summary>
/// Applies what HR sync matched (spec 019 FR-003, US1 scenario 4): the HR values of each matched person, read through
/// the canonical HR contract, and HR as the source of every profile field the tenant's HR connection maps, so those
/// fields are read-only and labelled "from HR". People who are no longer matched lose their HR values. Field names
/// only, never values, go into the events (spec 010 FR-012).
/// </summary>
public sealed class HrProfiles(IDbSession session, IHrRecords hr, ProfileStore store, IAuditWriter audit, IOutbox outbox)
{
    public async Task ApplyAsync(CancellationToken ct)
    {
        await store.EnsureFreshAsync(ct);
        var mapped = (await hr.MappedFieldsAsync(ct)).Where(ProfileFields.FromHr.ContainsKey).Select(f => ProfileFields.FromHr[f]).ToHashSet(StringComparer.Ordinal);
        await OwnAsync(mapped, ct);
        var policies = await store.PoliciesAsync(ct);
        var records = (await hr.MatchedAsync(null, ct)).ToDictionary(r => r.UserId);
        var rows = await store.QueryAsync("hr_values <> '{}'::jsonb or user_id = any(@ids)", new { ids = records.Keys.ToArray() }, ct);
        var hireFromHr = policies.First(p => p.Key == ProfileFields.HireDate).Source == FieldSources.Hr;
        var updates = new List<object>();
        foreach (var row in rows)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (records.TryGetValue(row.UserId, out var record))
            {
                foreach (var (canonical, field) in ProfileFields.FromHr)
                {
                    if (mapped.Contains(field) && record.Fields.TryGetValue(canonical, out var v) && !string.IsNullOrWhiteSpace(v))
                    {
                        values[field] = v.Trim();
                    }
                }
            }
            var changed = values.Keys.Union(row.HrValues.Keys)
                .Where(k => values.GetValueOrDefault(k) != row.HrValues.GetValueOrDefault(k)).Order(StringComparer.Ordinal).ToList();
            var hire = hireFromHr && values.TryGetValue(ProfileFields.HireDate, out var h) && DateOnly.TryParseExact(h, "yyyy-MM-dd", out var d) ? d : row.HireDate;
            if (changed.Count == 0 && hire == row.HireDate)
            {
                continue;
            }
            var updated = row with { HrValues = values, HireDate = hire };
            updates.Add(new { userId = row.UserId, hrValues = values, hireDate = hire, searchFields = ProfileStore.SearchFields(updated, policies) });
            if (changed.Count > 0)
            {
                await outbox.EnqueueAsync(new ProfileUpdated(row.UserId, changed), ct);
            }
        }
        await session.ExecAsync(
            """
            update people.profile p set hr_values = r."hrValues", hire_date = r."hireDate", search_fields = r."searchFields", version = p.version + 1, updated_at = now()
            from jsonb_to_recordset(@rows) as r("userId" uuid, "hrValues" jsonb, "hireDate" date, "searchFields" jsonb)
            where p.user_id = r."userId"
            """, new { rows = Jsonb.From(updates) }, ct);
    }

    /// <summary>HR owns the fields it maps: their source becomes HR (audited like an administrator's change).</summary>
    private async Task OwnAsync(IReadOnlySet<string> mapped, CancellationToken ct)
    {
        var policies = await store.PoliciesAsync(ct);
        var take = policies.Where(p => mapped.Contains(p.Key) && p.Source != FieldSources.Hr
            && ProfileFields.ByKey.TryGetValue(p.Key, out var def) && def.Sources.Contains(FieldSources.Hr)).ToList();
        if (take.Count == 0)
        {
            return;
        }
        foreach (var p in take)
        {
            await session.ExecAsync(
                """
                insert into people.field_policy (tenant_id, field_key, source, visibility, is_custom, names, position)
                values (platform.current_tenant_id(), @key, 'hr', @visibility, false, '{}'::jsonb, @position)
                on conflict (tenant_id, field_key) do update set source = 'hr'
                """, new { key = p.Key, visibility = p.Visibility, position = p.Position }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleFieldSourceChanged, "profile_field", p.Key,
                Changes: new { field = p.Key, from = p.Source, to = FieldSources.Hr, by = "hr_sync" }), ct);
        }
        store.ForgetPolicies();
    }
}

/// <summary>Runs after every applied HR sync and every link made or removed by hand.</summary>
public sealed class ApplyHrProfiles(HrProfiles profiles) : IMessageHandler<HrSyncCompleted>
{
    public Task HandleAsync(HrSyncCompleted message, CancellationToken ct) => profiles.ApplyAsync(ct);
}
