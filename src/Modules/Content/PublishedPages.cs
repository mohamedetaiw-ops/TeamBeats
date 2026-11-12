using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>Published items of any type for modules that show them in their own screens (spec 028 lessons), read as the caller.</summary>
public sealed class PublishedPages(ITenantContext context, IEntitlementService entitlements, Items items, Sites sites) : IPublishedPages
{
    public async Task<PublishedPage?> ReadableAsync(Guid itemId, CancellationToken ct = default)
    {
        context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.ContentCore, ct))
        {
            return null;
        }
        var item = await items.RowAsync(itemId, false, ct);
        if (item is null || !ContentStatuses.Readable(item.Status) || item.PublishedVersion is not { } number)
        {
            return null;
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        if (!await items.MayReadAsync(item, site, ct) || await items.VersionAsync(item.Id, number, ct) is not { } version)
        {
            return null;
        }
        return new PublishedPage(item.Id, item.Type, number,
            version.Variants.ToDictionary(v => v.Key, v => v.Value.Title, StringComparer.Ordinal),
            version.Variants.ToDictionary(v => v.Key, v => v.Value.Body, StringComparer.Ordinal));
    }
}
