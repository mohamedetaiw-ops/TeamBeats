using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Workflow;

/// <summary>Instance migration between versions of a process model (spec 024 R3, FR-015, US6).</summary>
public sealed partial class ProcessEngine
{
    /// <summary>
    /// Checks one instance against the target version and, when <paramref name="execute"/> is set and it can move, moves
    /// it: its state, open tasks, service steps, incidents, timers and subscriptions take the target's element IDs, and the
    /// instance runs on the target version from now on. Variables, history and tokens stay. The row lock of the instance
    /// is held for both, so the check is the one the move acts on.
    /// </summary>
    internal async Task<MigrationCheck> MigrateInstanceAsync(Guid instanceId, int fromVersion, int toVersion, IReadOnlyDictionary<string, string> mapping, bool execute,
        CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        if (instance is null)
        {
            return new MigrationCheck(false, "not_active");
        }
        if (instance.Info.DefinitionVersion != fromVersion)
        {
            return new MigrationCheck(false, "other_version");
        }
        var to = await GraphAsync(instance.Info.DefinitionKey, toVersion, ct) ?? throw new InvalidOperationException("Target version is not a graph.");
        var waits = await session.QueryAsync(
            """
            select token_id, element_id from workflow.engine_job where instance_id = @instanceId and kind = @kind and done_at is null and token_id is not null
            union all
            select token_id, element_id from workflow.message_subscription where instance_id = @instanceId and token_id is not null
            """, new { instanceId, kind = EngineJobKinds.Timer }, r => new OpenWait(r.GetGuid(0), r.GetString(1)), ct);
        var check = InstanceMigration.Check(instance.Graph, to, mapping, instance.State, waits);
        if (!check.Migratable || !execute)
        {
            return check;
        }

        var renames = instance.Graph.Elements.Select(e => (From: e.Id, To: InstanceMigration.Target(to, mapping, e.Id)))
            .Where(x => x.To is not null && x.To != x.From).ToList();
        InstanceMigration.Apply(to, mapping, instance.State);
        await session.ExecAsync(
            "update workflow.process_instance set definition_version = @toVersion, state = @state where id = @instanceId",
            new { instanceId, toVersion, state = new Jsonb(StateJson(instance.State)) }, ct);
        if (renames.Count > 0)
        {
            // One statement per table, so a swap of two IDs reads each row's old ID once.
            var args = new { instanceId, froms = renames.Select(r => r.From).ToArray(), tos = renames.Select(r => r.To!).ToArray() };
            foreach (var table in new[] { "user_task", "service_task", "incident", "engine_job", "message_subscription" })
            {
                await session.ExecAsync(
                    $"""
                    update workflow.{table} t set element_id = m.target
                    from unnest(@froms::text[], @tos::text[]) as m(source, target)
                    where t.instance_id = @instanceId and t.element_id = m.source
                    """, args, ct);
            }
        }
        await HistoryAsync(instanceId, "", "migrated", Actor(), null, ct, new JsonObject { ["fromVersion"] = fromVersion, ["toVersion"] = toVersion });
        return check;
    }
}
