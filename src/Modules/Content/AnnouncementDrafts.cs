using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>Announcement drafts started by other modules (spec 023 FR-014). The item lifecycle stays in <see cref="Items"/>.</summary>
public sealed class AnnouncementDrafts(Items items) : IAnnouncementDrafts
{
    public async Task<Guid> CreateAsync(AnnouncementDraft draft, CancellationToken ct = default)
    {
        var variants = draft.Titles.ToDictionary(t => t.Key,
            t => new VariantInput(t.Value, null, draft.Bodies.TryGetValue(t.Key, out var body) ? body : BodyDocument.Empty), StringComparer.Ordinal);
        var view = await items.CreateAsync(new ItemRequest(null, ContentTypes.Announcement, null, null, null, null, null, null, null, variants, null, null),
            handOff: true, ct);
        return view.Id;
    }
}
