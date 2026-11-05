using System.Collections;
using System.Reflection;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Audit;

/// <summary>Writes audit records inside the caller's transaction, so a rolled-back action leaves no success record.</summary>
public sealed class AuditWriter(IDbSession session, ITenantContext context, IClock clock) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var tenantId = context.RequireTenant();
        await session.ExecAsync(
            """
            insert into audit.audit_event
              (tenant_id, id, occurred_at, actor_id, actor_type, action, target_type, target_id, target_org_unit_id,
               outcome, correlation_id, source_ip, changes)
            values (@tenant, @id, @at, @actor, @actorType, @action, @targetType, @targetId, @unit, @outcome, @corr, @ip, @changes)
            """,
            new
            {
                tenant = tenantId,
                id = Ids.New(),
                at = clock.UtcNow,
                actor = context.ActorId,
                actorType = context.ActorType,
                action = entry.Action,
                targetType = entry.TargetType,
                targetId = entry.TargetId,
                unit = entry.TargetOrgUnitId,
                outcome = entry.Outcome,
                corr = Correlation.Id,
                ip = context.SourceIp,
                changes = AuditChanges.ToJson(entry.Changes),
            },
            ct);
    }

    public async Task WritePlatformAsync(AuditEntry entry, Guid? subjectTenantId, CancellationToken ct = default)
    {
        await session.ExecAsync(
            """
            insert into audit.platform_audit_event
              (id, occurred_at, actor_id, actor_type, action, target_type, target_id, subject_tenant_id, outcome, correlation_id, source_ip, changes)
            values (@id, @at, @actor, @actorType, @action, @targetType, @targetId, @subject, @outcome, @corr, @ip, @changes)
            """,
            new
            {
                id = Ids.New(),
                at = clock.UtcNow,
                actor = context.ActorId,
                actorType = context.ActorType,
                action = entry.Action,
                targetType = entry.TargetType,
                targetId = entry.TargetId,
                subject = subjectTenantId,
                outcome = entry.Outcome,
                corr = Correlation.Id,
                ip = context.SourceIp,
                changes = AuditChanges.ToJson(entry.Changes),
            },
            ct);
    }
}

/// <summary>Builds the change summary, dropping <see cref="SensitiveAttribute"/> properties and secret-looking keys.</summary>
public static class AuditChanges
{
    private static readonly string[] SecretMarkers = ["secret", "password", "token", "credential", "apikey", "api_key"];

    public static Jsonb? ToJson(object? changes)
    {
        if (changes is null)
        {
            return null;
        }

        var map = Flatten(changes);
        return new Jsonb(JsonSerializer.Serialize(map, JsonDefaults.Options));
    }

    public static bool LooksSecret(string name) =>
        SecretMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, object?> Flatten(object changes)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (changes is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                var key = e.Key.ToString()!;
                if (!LooksSecret(key))
                {
                    result[key] = e.Value;
                }
            }
            return result;
        }

        foreach (var prop in changes.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<SensitiveAttribute>() is not null || LooksSecret(prop.Name))
            {
                continue;
            }
            result[JsonNamingPolicy.CamelCase.ConvertName(prop.Name)] = prop.GetValue(changes);
        }
        return result;
    }
}
