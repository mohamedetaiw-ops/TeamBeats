using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Integrations.Ticketing;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Occupancy;

/// <summary>
/// Occupancy sensors over REST (spec 031 R3 US9, FR-026, through spec 017). The connection names an https address on
/// the tenant allowlist and how to sign in. One fixed read, <c>list_sensors</c>, sends <c>GET {address}/sensors</c> and
/// accepts either an array or an object with <c>sensors</c> (or <c>items</c>), each sensor carrying <c>id</c> (or
/// <c>sensor_id</c>), <c>occupied</c> (true or false, or a <c>state</c> of occupied or vacant) and an optional
/// <c>observed_at</c>. Only the ID, the state and the time leave the pack; anything else the system sends, such as who
/// badged in, is dropped.
/// </summary>
public sealed partial class OccupancyRestPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients, IClock clock) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = OccupancyKinds.Rest;
    public const string ListSensors = "list_sensors";
    public const int MaxSensors = 5000;

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.BookingSensors;

    protected override IReadOnlyCollection<string> AuthTypes => ["bearer", "api_key", "basic", "oauth_client_credentials"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(ListSensors, ConnectorSideEffects.Read, true, [])];

    protected override string ProbePath(TicketingSettings settings) => "/sensors";

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,128}$")]
    private static partial Regex SensorId();

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action != ListSensors)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, "/sensors", null, null, false, ct);
        if (failure is not null)
        {
            return failure;
        }
        var list = body.ValueKind == JsonValueKind.Array ? body
            : body.TryGetProperty("sensors", out var s) && s.ValueKind == JsonValueKind.Array ? s
            : body.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i
            : (JsonElement?)null;
        if (list is null)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: false);
        }
        var sensors = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Value.EnumerateArray())
        {
            if (sensors.Count >= MaxSensors)
            {
                break;
            }
            var id = Text(item, "id") ?? Text(item, "sensor_id");
            bool? occupied = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("occupied", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? o.GetBoolean()
                : Text(item, "state")?.ToLowerInvariant() switch { "occupied" => true, "vacant" or "free" => false, _ => null };
            if (id is null || !SensorId().IsMatch(id) || occupied is null || !seen.Add(id))
            {
                continue;
            }
            var observed = Text(item, "observed_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : clock.UtcNow;
            sensors.Add(new { sensor_id = id, occupied = occupied.Value, observed_at = observed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) });
        }
        return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { sensors }, JsonDefaults.Options), recordCount: sensors.Count);
    }
}

/// <summary>The occupancy contract over the occupancy packs. Enabled connections in the package only; answers are canonical.</summary>
public sealed class OccupancyReadings(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements) : IOccupancyReadings
{
    private async Task<bool> InPackageAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) && await entitlements.IsEnabledAsync(Capabilities.BookingSensors, ct);

    public async Task<IReadOnlyList<OccupancyConnection>> ConnectionsAsync(CancellationToken ct = default)
    {
        if (!await InPackageAsync(ct))
        {
            return [];
        }
        return await session.QueryAsync(
            "select id, kind, display_name from integrations.connection where kind = any(@kinds) and enabled order by display_name, id",
            new { kinds = OccupancyKinds.All.ToArray() }, r => new OccupancyConnection(r.GetGuid(0), r.GetString(1), r.GetString(2)), ct);
    }

    public async Task<OccupancyRead> ReadAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (!await InPackageAsync(ct) || !await session.ScalarAsync<bool>(
                "select exists(select 1 from integrations.connection where id = @connectionId and kind = any(@kinds) and enabled)",
                new { connectionId, kinds = OccupancyKinds.All.ToArray() }, ct))
        {
            return new(TrackerResults.NotConfigured, []);
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, OccupancyRestPack.ListSensors, JsonSerializer.SerializeToElement(new { }),
            $"occupancy:{Ids.New():N}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("sensors", out var list) != true)
        {
            return new(result.Outcome == ConnectorOutcomes.Refused ? TrackerResults.Refused : TrackerResults.Unavailable, []);
        }
        return new(TrackerResults.Ok, list.EnumerateArray().Select(s => new SensorReading(s.GetProperty("sensor_id").GetString()!, s.GetProperty("occupied").GetBoolean(),
            DateTimeOffset.Parse(s.GetProperty("observed_at").GetString()!, CultureInfo.InvariantCulture))).ToList());
    }
}
