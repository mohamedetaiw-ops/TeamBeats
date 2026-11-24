using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Sms;

/// <summary>
/// SMS for notification delivery (spec 015 US6 scenario 2). Every message goes through the connector runtime with the
/// SMS pack, so it is in run history (without the number or the text), held by the rate limit and the circuit breaker,
/// and idempotent per key. A tenant uses one gateway: the oldest enabled SMS connection.
/// </summary>
public sealed class SmsMessaging(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements, TenantLinks links) : ISmsMessaging
{
    private async Task<Guid?> ConnectionAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.NotificationsSms, ct))
        {
            return null;
        }
        return await session.ScalarAsync<Guid?>(
            "select id from integrations.connection where kind = @kind and enabled order by created_at, id limit 1", new { kind = SmsPack.KindName }, ct);
    }

    public async Task<bool> AvailableAsync(CancellationToken ct = default) => await ConnectionAsync(ct) is not null;

    public async Task<SmsSendOutcome> SendAsync(string phone, string text, string link, string key, CancellationToken ct = default)
    {
        if (await ConnectionAsync(ct) is not { } connection)
        {
            return SmsSendOutcome.Unavailable;
        }
        if (PhoneNumbers.Normalize(phone) is not { } number)
        {
            return SmsSendOutcome.NumberRefused;
        }
        var url = await links.AbsoluteAsync(link, ct);
        var room = SmsPack.MaxText - url.Length - 1;
        var fitted = (room <= 1 ? "" : text.Length > room ? text[..(room - 1)] + "…" : text) + " " + url;
        fitted = fitted.Trim();
        if (fitted.Length > SmsPack.MaxText)
        {
            return SmsSendOutcome.Failed;
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connection, SmsPack.SendSms, JsonSerializer.SerializeToElement(new { to = number, text = fitted }), key, "sms"), ct);
        return result.Outcome switch
        {
            ConnectorOutcomes.Succeeded => SmsSendOutcome.Sent,
            ConnectorOutcomes.BusinessError when result.ErrorCode == SmsPack.NumberRefused => SmsSendOutcome.NumberRefused,
            _ when result.ErrorClass is ConnectorErrorClasses.NotResponding or ConnectorErrorClasses.Throttled or ConnectorErrorClasses.ServerError
                or ConnectorErrorClasses.CircuitOpen or ConnectorErrorClasses.RateLimited or ConnectorErrorClasses.InvalidResponse => SmsSendOutcome.Transient,
            _ when result.ErrorClass is ConnectorErrorClasses.ConnectionDisabled or ConnectorErrorClasses.NotInPackage => SmsSendOutcome.Unavailable,
            _ => SmsSendOutcome.Failed,
        };
    }
}
