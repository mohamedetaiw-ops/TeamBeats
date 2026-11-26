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

namespace TeamBeats.Modules.Integrations.Regulators;

/// <summary>
/// A regulator's change feed over https (spec 025 R3 US10, FR-024, through spec 017). The connection names an https
/// address on the tenant allowlist and how to sign in, if at all. One fixed read, <c>list_changes</c>, sends
/// <c>GET {address}</c> and accepts a JSON Feed (<c>items</c> with <c>id</c>, <c>title</c>, <c>url</c>,
/// <c>date_published</c>, <c>summary</c> or <c>content_text</c>) or an array, or an object with <c>items</c>, of items
/// with <c>id</c>, <c>title</c>, <c>link</c>, <c>published</c> and <c>summary</c>. At most 200 items; only those five
/// fields leave the pack, as plain text, the summary cut to 2,000 characters and the link kept only when it is https.
/// </summary>
public sealed partial class RegulatorFeedPack(ITenantContext context, RestSignIn signIn, IHttpClientFactory httpClients) : TicketingPack(context, signIn, httpClients)
{
    public const string KindName = RegulatorFeedKinds.Rest;
    public const string ListChanges = "list_changes";
    public const int MaxItems = 200;
    public const int MaxId = 512;
    public const int MaxTitle = 500;
    public const int MaxSummary = 2000;
    public const int MaxLink = 2048;

    public override string Kind => KindName;

    public override string CapabilityKey => Capabilities.PolicyRegulatorFeeds;

    protected override IReadOnlyCollection<string> AuthTypes => ["none", "bearer", "api_key", "basic"];

    public override IReadOnlyList<ConnectorActionDefinition> Actions { get; } = [new(ListChanges, ConnectorSideEffects.Read, true, [])];

    protected override string ProbePath(TicketingSettings settings) => string.Empty;

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Plain text: tags and control characters removed, white space collapsed, cut to the limit.</summary>
    public static string? Plain(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }
        var text = Spaces().Replace(new string(Tags().Replace(value, " ").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()), " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        return text.Length > max ? text[..max] : text;
    }

    public static string? HttpsLink(string? value) =>
        value is { Length: > 0 and <= MaxLink } && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : null;

    private static string? Scalar(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString();
                }
                if (v.ValueKind == JsonValueKind.Number)
                {
                    return v.GetRawText();
                }
            }
        }
        return null;
    }

    protected override async Task<PackCallResult> CallAsync(PackConnection connection, TicketingSettings settings, string action, JsonElement input, string idempotencyKey,
        CancellationToken ct)
    {
        if (action != ListChanges)
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.UnknownAction);
        }
        var (failure, body) = await SendAsync(connection, settings, HttpMethod.Get, string.Empty, null, null, false, ct);
        if (failure is not null)
        {
            return failure;
        }
        var list = body.ValueKind == JsonValueKind.Array ? body
            : body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i
            : (JsonElement?)null;
        if (list is null)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse, mayHaveActed: false);
        }
        var items = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Value.EnumerateArray())
        {
            if (items.Count >= MaxItems)
            {
                break;
            }
            var id = Scalar(item, "id", "guid")?.Trim();
            var title = Plain(Scalar(item, "title"), MaxTitle);
            if (id is not { Length: > 0 and <= MaxId } || title is null || !seen.Add(id))
            {
                continue;
            }
            var published = Scalar(item, "date_published", "published", "date") is { } at
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null;
            items.Add(new
            {
                id, title, link = HttpsLink(Scalar(item, "url", "link")), published,
                summary = Plain(Scalar(item, "summary", "content_text", "description"), MaxSummary),
            });
        }
        return PackCallResult.Ok(JsonSerializer.SerializeToElement(new { items }, JsonDefaults.Options), recordCount: items.Count);
    }
}

/// <summary>The regulator feed contract over the feed pack. Enabled connections in the package only; answers are canonical.</summary>
public sealed class RegulatorFeeds(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements) : IRegulatorFeeds
{
    private async Task<bool> InPackageAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct) && await entitlements.IsEnabledAsync(Capabilities.PolicyRegulatorFeeds, ct);

    public async Task<IReadOnlyList<RegulatorFeedConnection>> ConnectionsAsync(CancellationToken ct = default)
    {
        if (!await InPackageAsync(ct))
        {
            return [];
        }
        return await session.QueryAsync(
            "select id, display_name from integrations.connection where kind = any(@kinds) and enabled order by display_name, id",
            new { kinds = RegulatorFeedKinds.All.ToArray() }, r => new RegulatorFeedConnection(r.GetGuid(0), r.GetString(1)), ct);
    }

    public async Task<RegulatorFeedRead> ReadAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (!await InPackageAsync(ct) || !await session.ScalarAsync<bool>(
                "select exists(select 1 from integrations.connection where id = @connectionId and kind = any(@kinds) and enabled)",
                new { connectionId, kinds = RegulatorFeedKinds.All.ToArray() }, ct))
        {
            return new(TrackerResults.NotConfigured, []);
        }
        var result = await runtime.InvokeAsync(new ConnectorCall(connectionId, RegulatorFeedPack.ListChanges, JsonSerializer.SerializeToElement(new { }),
            $"regulator:{Ids.New():N}"), ct);
        if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output?.TryGetProperty("items", out var list) != true)
        {
            return new(result.Outcome == ConnectorOutcomes.Refused ? TrackerResults.Refused : TrackerResults.Unavailable, []);
        }
        return new(TrackerResults.Ok, list.EnumerateArray().Select(e => new RegulatorItem(
            e.GetProperty("id").GetString()!, e.GetProperty("title").GetString()!,
            e.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
            e.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(p.GetString()!, CultureInfo.InvariantCulture) : null,
            e.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null)).ToList());
    }
}
