using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using Microsoft.Extensions.Options;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Slack;

/// <summary>
/// Slack for product modules (spec 017 US8). Every call goes through the connector runtime with the Slack pack, so it is
/// recorded in run history and held by the connection's rate limit and circuit breaker. A tenant uses one Slack
/// workspace: the oldest enabled Slack connection.
/// </summary>
public sealed class SlackMessaging(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements, IClock clock, ITenantContext context,
    IOptions<HostResolutionOptions> hosts, ITenantRegistry tenants)
    : ISlackMessaging, IConnectionDependents
{
    /// <summary>The https address of a path on the tenant's own host; anything else is refused.</summary>
    private async Task<string?> AbsoluteAsync(string? path, CancellationToken ct)
    {
        if (path is null)
        {
            return null;
        }
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
        {
            throw new ArgumentException("Links must be paths on the tenant host.", nameof(path));
        }
        var h = hosts.Value;
        if (h.Mode == InstallationMode.Dedicated)
        {
            return $"https://{h.BaseDomain}{path}";
        }
        var tenant = await tenants.FindByIdAsync(context.RequireTenant(), ct) ?? throw AppException.NotFound();
        return $"https://{tenant.Key}.{h.BaseDomain}{path}";
    }

    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return null;
        }
        return await session.ScalarAsync<Guid?>(
            "select id from integrations.connection where kind = @kind and enabled order by created_at, id limit 1", new { kind = SlackPack.KindName }, ct);
    }

    private Task<(string SlackUserId, Guid ConnectionId, DateTimeOffset LinkedAt)?> LinkRowAsync(Guid userId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync<(string, Guid, DateTimeOffset)?>(
            "select slack_user_id, connection_id, linked_at from integrations.slack_link where user_id = @userId", new { userId },
            r => (r.GetString(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2)), ct);

    public async Task<SlackStatus> StatusAsync(Guid userId, CancellationToken ct = default)
    {
        var connection = await ConnectionAsync(ct);
        var link = await LinkRowAsync(userId, ct);
        var linked = connection is { } c && link is { } l && l.ConnectionId == c;
        return new SlackStatus(connection is not null, linked, linked ? link!.Value.LinkedAt : null);
    }

    public async Task<SlackStatus> LinkAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var connection = await ConnectionAsync(ct) ?? throw AppException.Conflict("conflict.slack.unavailable");
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, SlackPack.LookupUser, JsonSerializer.SerializeToElement(new { email }),
            $"slack-link:{userId:N}:{Ids.New():N}", "slack-link"), ct);
        if (result.Outcome == ConnectorOutcomes.BusinessError)
        {
            throw AppException.Validation("email", "validation.slack.not_in_workspace");
        }
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("slackUserId", out var id) != true || id.GetString() is not { } slackUserId
            || !SlackIds.IsMember(slackUserId))
        {
            throw AppException.Conflict("conflict.slack.unreachable");
        }
        await session.ExecAsync(
            """
            insert into integrations.slack_link (tenant_id, user_id, connection_id, slack_user_id, linked_at)
            values (platform.current_tenant_id(), @userId, @connection, @slackUserId, @now)
            on conflict (tenant_id, user_id) do update set connection_id = excluded.connection_id, slack_user_id = excluded.slack_user_id, linked_at = excluded.linked_at
            """, new { userId, connection, slackUserId, now = clock.UtcNow }, ct);
        return await StatusAsync(userId, ct);
    }

    public Task UnlinkAsync(Guid userId, CancellationToken ct = default) =>
        session.ExecAsync("delete from integrations.slack_link where user_id = @userId", new { userId }, ct);

    public async Task<SlackSendOutcome> SendDirectAsync(Guid userId, string text, string? link, string key, CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return SlackSendOutcome.Unavailable;
        }
        if (await LinkRowAsync(userId, ct) is not { } row || row.ConnectionId != connection)
        {
            return SlackSendOutcome.NotLinked;
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, SlackPack.SendDirectMessage,
            JsonSerializer.SerializeToElement(new { slack_user_id = row.SlackUserId, text = Fit(text), link = await AbsoluteAsync(link, ct) }), key, "slack-dm"), ct);
        var outcome = Map(result);
        if (outcome == SlackSendOutcome.UserGone)
        {
            await UnlinkAsync(userId, ct);
        }
        return outcome;
    }

    public async Task<IReadOnlyList<SlackChannel>> ChannelsAsync(CancellationToken ct = default)
    {
        var connection = await ConnectionAsync(ct) ?? throw AppException.Conflict("conflict.slack.unavailable");
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, SlackPack.ListChannels, JsonSerializer.SerializeToElement(new { }),
            $"slack-channels:{Ids.New():N}", "slack-channels"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output is not { } output || !output.TryGetProperty("channels", out var channels))
        {
            throw AppException.Conflict("conflict.slack.unreachable");
        }
        return channels.EnumerateArray()
            .Select(c => new SlackChannel(c.GetProperty("id").GetString()!, c.GetProperty("name").GetString()!, c.GetProperty("member").GetBoolean()))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<SlackSendOutcome> ShareAsync(string channelId, string text, string link, string key, CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return SlackSendOutcome.Unavailable;
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, SlackPack.ShareLink,
            JsonSerializer.SerializeToElement(new { channel_id = channelId, text = Fit(text), link = await AbsoluteAsync(link, ct) }), key, "slack-share"), ct);
        return Map(result);
    }

    private static string Fit(string text) => text.Length > SlackPack.MaxText ? text[..(SlackPack.MaxText - 1)] + "…" : text;

    private static SlackSendOutcome Map(ConnectorCallResult result) => result.Outcome switch
    {
        ConnectorOutcomes.Succeeded => SlackSendOutcome.Sent,
        ConnectorOutcomes.BusinessError when result.ErrorCode == SlackPack.UserGone => SlackSendOutcome.UserGone,
        ConnectorOutcomes.BusinessError => SlackSendOutcome.Failed,
        _ when result.ErrorClass is ConnectorErrorClasses.NotResponding or ConnectorErrorClasses.Throttled or ConnectorErrorClasses.ServerError
            or ConnectorErrorClasses.CircuitOpen or ConnectorErrorClasses.RateLimited or ConnectorErrorClasses.InvalidResponse => SlackSendOutcome.Transient,
        _ when result.ErrorClass is ConnectorErrorClasses.ConnectionDisabled or ConnectorErrorClasses.NotInPackage => SlackSendOutcome.Unavailable,
        _ => SlackSendOutcome.Failed,
    };

    public async Task<IReadOnlyList<ConnectionDependent>> DescribeAsync(Guid connectionId, CancellationToken ct = default)
    {
        var count = await session.ScalarAsync<long>("select count(*) from integrations.slack_link where connection_id = @connectionId", new { connectionId }, ct);
        return count > 0 ? [new ConnectionDependent("integrations.dependent.slack_links", count)] : [];
    }
}
