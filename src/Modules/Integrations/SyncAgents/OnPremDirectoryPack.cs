using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.SyncAgents;

/// <summary>
/// Settings of an on-premises Active Directory connection (spec 017 US6, FR-018): the domain controller the agent reads
/// over LDAPS inside the customer network, the base DN, and optional LDAP filters. The service account and its
/// password stay with the agent on the customer side and never reach TeamBeats.
/// </summary>
public sealed record OnPremDirectorySettings(string? Host, int? Port, string? BaseDn, string? UserFilter, string? GroupFilter);

/// <summary>
/// The on-premises Active Directory pack. Its work runs in the customer's sync agent, which pulls jobs (see
/// <see cref="SyncAgentService"/>), so the pack has no actions the runtime calls from TeamBeats. Test connection checks
/// that an agent is enrolled and has been in touch within the last 5 minutes.
/// </summary>
public sealed partial class OnPremDirectoryPack(IDbSession session, IClock clock) : IConnectorPack
{
    public const string KindName = "onprem_ad";

    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Kind => KindName;

    public int Version => 1;

    public string CapabilityKey => Capabilities.IntegrationOnPremAgent;

    public IReadOnlyList<ConnectorActionDefinition> Actions => [];

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*$")]
    private static partial Regex HostName();

    public Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct) => Task.FromResult(PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction));

    public static OnPremDirectorySettings? Parse(JsonElement settings)
    {
        try
        {
            return settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<OnPremDirectorySettings>(JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct)
    {
        var s = Parse(settings) ?? throw AppException.Validation("settings", "validation.onprem.settings");
        var errors = new ValidationErrors();
        if (s.Host is null || s.Host.Length > 253 || !HostName().IsMatch(s.Host))
        {
            errors.Add("host", "validation.onprem.host");
        }
        if (s.Port is { } p && p is not (636 or 3269))
        {
            errors.Add("port", "validation.onprem.port");
        }
        if (string.IsNullOrWhiteSpace(s.BaseDn) || s.BaseDn.Length > 500 || !s.BaseDn.Contains('='))
        {
            errors.Add("baseDn", "validation.onprem.base_dn");
        }
        if (s.UserFilter is { } u && (u.Length > 1000 || !u.StartsWith('(') || !u.EndsWith(')')))
        {
            errors.Add("userFilter", "validation.onprem.filter");
        }
        if (s.GroupFilter is { } g && (g.Length > 1000 || !g.StartsWith('(') || !g.EndsWith(')')))
        {
            errors.Add("groupFilter", "validation.onprem.filter");
        }
        errors.ThrowIfAny();
        var stored = new OnPremDirectorySettings(s.Host!.ToLowerInvariant(), s.Port ?? 636, s.BaseDn!.Trim(), s.UserFilter, s.GroupFilter);
        return Task.FromResult(JsonSerializer.SerializeToElement(stored, Stored));
    }

    public async Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct)
    {
        var enrolled = await session.ScalarAsync<long>("select count(*) from integrations.sync_agent where connection_id = @id and revoked_at is null",
            new { id = connection.Id }, ct);
        var online = await session.ScalarAsync<bool>(
            "select exists (select 1 from integrations.sync_agent where connection_id = @id and revoked_at is null and last_contact_at > @since)",
            new { id = connection.Id, since = clock.UtcNow - SyncAgentService.OfflineAfter }, ct);
        return
        [
            new PackCheck("agent_enrolled", enrolled > 0, enrolled > 0 ? "integrations.test.agent_enrolled" : "integrations.test.agent_missing",
                enrolled > 0 ? null : ConnectorErrorClasses.ConnectionMissing),
            new PackCheck("agent_online", online, online ? "integrations.test.agent_online" : "integrations.test.agent_offline",
                online ? null : ConnectorErrorClasses.NotResponding),
        ];
    }
}
