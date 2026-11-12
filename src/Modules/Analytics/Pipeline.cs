using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>One event from a client batch. Fields are JSON values; only the declared ones of the event are kept.</summary>
public sealed record AnalyticsEventInput(string? Name, DateTimeOffset? OccurredAt, string? Channel, Dictionary<string, JsonElement>? Fields);

public sealed record AnalyticsBatch(IReadOnlyList<AnalyticsEventInput>? Events);

/// <summary>How many events of a batch were kept and how many were dropped (undeclared, malformed, too late, or switched off).</summary>
public sealed record AnalyticsAccepted(int Accepted, int Dropped);

/// <summary>An event checked against its schema, ready to store.</summary>
internal sealed record CheckedEvent(AnalyticsSchema Schema, DateTimeOffset OccurredAt, string Channel, IReadOnlyDictionary<string, object> Fields);

/// <summary>
/// The declared schema check (spec 021 FR-003, FR-017): events and fields outside the schema are dropped and counted,
/// values must have their declared type and allowed value, and required fields must be present. Free text never passes,
/// because no field type takes it.
/// </summary>
internal static partial class AnalyticsSchemaCheck
{
    public const int MaxNumber = 1_000_000_000;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_]*)+@\d{1,3}$")]
    private static partial Regex ComponentRefPattern();

    /// <summary>The value in its stored form, or null when it does not fit the field.</summary>
    public static object? Coerce(AnalyticsField field, object? raw)
    {
        if (raw is JsonElement json)
        {
            raw = json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number when json.TryGetInt64(out var whole) => whole,
                JsonValueKind.Number => json.GetDouble(),
                _ => null,
            };
        }
        object? value = field.Type switch
        {
            AnalyticsFieldType.Id => raw switch
            {
                Guid g when g != Guid.Empty => g.ToString("D"),
                string s when Guid.TryParse(s, out var g) && g != Guid.Empty => g.ToString("D"),
                _ => null,
            },
            AnalyticsFieldType.Key => raw is string s && KeyPattern().IsMatch(s) ? s : null,
            AnalyticsFieldType.ComponentRef => raw is string s && ComponentRefPattern().IsMatch(s) ? s : null,
            AnalyticsFieldType.Count => Whole(raw) is { } n and >= 0 and <= MaxNumber ? n : null,
            AnalyticsFieldType.Number => Real(raw) is { } d && double.IsFinite(d) && d >= 0 && d <= MaxNumber ? d : null,
            _ => null,
        };
        if (value is string text && field.Allowed is { } allowed && !allowed.Contains(text))
        {
            return null;
        }
        return value;
    }

    private static long? Whole(object? raw) => raw switch
    {
        long l => l,
        int i => i,
        short s => s,
        string s when long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var l) => l,
        _ => null,
    };

    private static double? Real(object? raw) => raw switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// Checks one event. Returns null when the event is undeclared or a declared field is missing or malformed; an
    /// undeclared field is left out and counted in <paramref name="droppedFields"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? Fields(AnalyticsSchema schema, IEnumerable<KeyValuePair<string, object?>> raw, out int droppedFields)
    {
        droppedFields = 0;
        var kept = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (!schema.Fields.TryGetValue(key, out var field))
            {
                droppedFields++;
                continue;
            }
            if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null })
            {
                continue;
            }
            if (Coerce(field, value) is not { } coerced)
            {
                return null;
            }
            kept[key] = coerced;
        }
        return schema.Fields.Any(f => f.Value.Required && !kept.ContainsKey(f.Key)) ? null : kept;
    }

    /// <summary>Fields as invariant strings, for the outbox message.</summary>
    public static IReadOnlyDictionary<string, string> AsStrings(IReadOnlyDictionary<string, object> fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? string.Empty,
        }, StringComparer.Ordinal);
}

/// <summary>
/// Pseudonymous user keys (spec 021 FR-003): the first 16 bytes of an HMAC-SHA256 of the user ID with the tenant's own
/// random salt, as hex. The same person gets a different key in every tenant, and a key cannot be turned back into a
/// user ID without the salt, which no API returns.
/// </summary>
public sealed class Pseudonyms(IDbSession session, IClock clock)
{
    private byte[]? _salt;

    public static string Key(byte[] salt, Guid userId)
    {
        var hash = HMACSHA256.HashData(salt, userId.ToByteArray());
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    public async Task<string> KeyAsync(Guid userId, CancellationToken ct = default) => Key(await SaltAsync(ct), userId);

    private async Task<byte[]> SaltAsync(CancellationToken ct)
    {
        if (_salt is not null)
        {
            return _salt;
        }
        await session.ExecAsync(
            "insert into analytics.tenant_key (tenant_id, salt, created_at) values (platform.current_tenant_id(), @salt, @now) on conflict do nothing",
            new { salt = RandomNumberGenerator.GetBytes(32), now = clock.UtcNow }, ct);
        _salt = await session.ScalarAsync<byte[]>("select salt from analytics.tenant_key", null, ct)
            ?? throw new InvalidOperationException("The tenant analytics key is missing.");
        return _salt;
    }
}

/// <summary>
/// Writes checked events (spec 021 T004) with the tenant from the session, the person's pseudonymous key, their org unit
/// (primary, else the first) and location at event time, the channel and the locale. Essential events carry no key, unit
/// or location (FR-018). Nonessential events are dropped while the tenant has them off. Events more than 7 days old
/// (edge case: offline mobile) or more than 5 minutes ahead are dropped.
/// </summary>
public sealed class AnalyticsWriter(IDbSession session, IClock clock, AnalyticsPolicies policies, Pseudonyms pseudonyms,
    IOrganizationHierarchy organization, IProfiles profiles)
{
    public static readonly TimeSpan LateLimit = TimeSpan.FromDays(7);
    public static readonly TimeSpan AheadLimit = TimeSpan.FromMinutes(5);

    private static readonly Meter Meter = new("TeamBeats.Analytics");
    private static readonly Counter<long> Stored = Meter.CreateCounter<long>("analytics.events.stored");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("analytics.events.dropped");

    internal static void CountDropped(int events) => Dropped.Add(events);

    /// <returns>How many events were written; the rest were dropped.</returns>
    private static DateTimeOffset Day(DateTimeOffset at) => new(at.UtcDateTime.Date, TimeSpan.Zero);

    internal async Task<int> WriteAsync(IReadOnlyList<CheckedEvent> events, Guid? userId, string? locale, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return 0;
        }
        var policy = await policies.GetAsync(ct);
        var now = clock.UtcNow;
        var kept = events.Where(e => (e.Schema.Essential || policy.Nonessential) && e.OccurredAt >= now - LateLimit && e.OccurredAt <= now + AheadLimit).ToList();
        CountDropped(events.Count - kept.Count);
        if (kept.Count == 0)
        {
            return 0;
        }
        string? key = null;
        Guid? unit = null;
        string? location = null;
        if (userId is { } user && kept.Any(e => e.Schema.UserKeyed))
        {
            key = await pseudonyms.KeyAsync(user, ct);
            var memberships = await organization.GetActiveMembershipsAsync(user, ct);
            unit = (memberships.FirstOrDefault(m => m.IsPrimary) ?? memberships.FirstOrDefault())?.OrgUnitId;
            location = (await profiles.PlacementsAsync([user], ct)).GetValueOrDefault(user)?.LocationKey;
        }
        foreach (var e in kept)
        {
            var keyed = e.Schema.UserKeyed && key is not null;
            await session.ExecAsync(
                """
                insert into analytics.event (tenant_id, name, occurred_at, received_at, user_key, org_unit_id, location, channel, locale, fields)
                values (platform.current_tenant_id(), @name, @occurred, @now, @key, @unit, @location, @channel, @locale, @fields)
                """,
                new
                {
                    name = e.Schema.Name, occurred = e.Schema.Unkeyed ? Day(e.OccurredAt) : e.OccurredAt, now = e.Schema.Unkeyed ? Day(now) : now,
                    key = keyed ? key : null, unit = keyed ? unit : null, location = keyed ? location : null, channel = e.Channel,
                    locale = e.Schema.Unkeyed ? null : locale, fields = new Jsonb(JsonSerializer.Serialize(e.Fields)),
                }, ct);
        }
        Stored.Add(kept.Count);
        return kept.Count;
    }
}

/// <summary>
/// The web collection endpoint (spec 021 plan): a batch of up to 20 declared events from the signed-in person, tagged
/// with the session's tenant and user. Clients never name a tenant, user, unit or key.
/// </summary>
public sealed class AnalyticsCollector(ITenantContext context, RequestLocale locale, AnalyticsWriter writer, IClock clock)
{
    public const int MaxEvents = 20;

    public async Task<AnalyticsAccepted> CollectAsync(AnalyticsBatch batch, CancellationToken ct)
    {
        var events = batch.Events ?? [];
        if (events.Count > MaxEvents)
        {
            throw AppException.Validation("events", "validation.analytics.too_many");
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var checkedEvents = new List<CheckedEvent>();
        var dropped = 0;
        foreach (var e in events)
        {
            var channel = e.Channel ?? AnalyticsChannels.Web;
            if (e.Name is null || !AnalyticsEvents.Schemas.TryGetValue(e.Name, out var schema) || channel == AnalyticsChannels.Server
                || AnalyticsEvents.ServerOnly.Contains(e.Name)
                || !AnalyticsChannels.All.Contains(channel)
                || AnalyticsSchemaCheck.Fields(schema, (e.Fields ?? []).Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)), out _) is not { } fields)
            {
                dropped++;
                continue;
            }
            checkedEvents.Add(new CheckedEvent(schema, e.OccurredAt ?? now, channel, fields));
        }
        AnalyticsWriter.CountDropped(dropped);
        var written = await writer.WriteAsync(checkedEvents, user, locale.Locale, ct);
        return new AnalyticsAccepted(written, events.Count - written);
    }
}

/// <summary>
/// The module side of the pipeline (<see cref="IAnalyticsEvents"/>): checks the event now, so a module learns of a bad
/// event in its own tests, and queues it through the outbox, so the action that caused it is never slowed (section 15).
/// </summary>
public sealed class AnalyticsEventsEmitter(ITenantContext context, RequestLocale locale, IOutbox outbox, AnalyticsWriter writer, IClock clock) : IAnalyticsEvents
{
    public async Task EmitAsync(string name, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        if (!AnalyticsEvents.Schemas.TryGetValue(name, out var schema))
        {
            throw new ArgumentException($"Analytics event {name} is not declared.", nameof(name));
        }
        if (AnalyticsSchemaCheck.Fields(schema, fields, out var droppedFields) is not { } kept || droppedFields > 0)
        {
            throw new ArgumentException($"Analytics event {name} does not match its schema.", nameof(fields));
        }
        if (schema.Unkeyed)
        {
            // A count of an anonymous action is written at once in the same transaction, never queued: a queued message
            // keeps the request's correlation and exact time. The row holds no person, locale or time finer than the day.
            await writer.WriteAsync([new CheckedEvent(schema, clock.UtcNow, AnalyticsChannels.Server, kept)], null, null, ct);
            return;
        }
        var user = context.ActorType == ActorType.User ? context.ActorId : null;
        await outbox.EnqueueAsync(new AnalyticsEventEmitted(name, user, AnalyticsChannels.Server, locale.Locale, clock.UtcNow,
            AnalyticsSchemaCheck.AsStrings(kept)), ct);
    }
}

/// <summary>Writes events queued by modules. A message that no longer fits the schema is dropped and counted.</summary>
public sealed class AnalyticsEventHandler(AnalyticsWriter writer) : IMessageHandler<AnalyticsEventEmitted>
{
    public async Task HandleAsync(AnalyticsEventEmitted message, CancellationToken ct)
    {
        if (!AnalyticsEvents.Schemas.TryGetValue(message.Name, out var schema) || !AnalyticsChannels.All.Contains(message.Channel)
            || AnalyticsSchemaCheck.Fields(schema, message.Fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)), out _) is not { } fields)
        {
            AnalyticsWriter.CountDropped(1);
            return;
        }
        await writer.WriteAsync([new CheckedEvent(schema, message.OccurredAt, message.Channel, fields)], message.UserId, message.Locale, ct);
    }
}
