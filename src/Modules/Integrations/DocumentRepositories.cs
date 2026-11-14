using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations;

/// <summary>
/// The one <see cref="IDocumentRepository"/> other modules get (ADR-0016): it sends each call to the adapter of the
/// connection's kind, SharePoint Online for Microsoft 365 connections and Google Drive for Google Workspace ones, so
/// <c>Documents</c> never needs to know which system a connected library lives in.
/// </summary>
public sealed class DocumentRepositories(IDbSession session, Microsoft365.SharePointLibraries sharePoint, Google.GoogleDrive drive) : IDocumentRepository
{
    private async Task<IDocumentRepository?> ForAsync(Guid connectionId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select kind from integrations.connection where id = @id", new { id = connectionId }, r => r.GetString(0), ct) switch
        {
            ConnectionKinds.Microsoft365 => sharePoint,
            Google.GooglePack.KindName => drive,
            _ => null,
        };

    public async Task<IReadOnlyList<RepositoryContainer>> ContainersAsync(Guid connectionId, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r ? await r.ContainersAsync(connectionId, ct) : [];

    public async Task<RepositoryPage> ListAsync(Guid connectionId, string containerId, string? folderId, string? cursor, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r
            ? await r.ListAsync(connectionId, containerId, folderId, cursor, ct)
            : throw new RepositoryUnavailableException("connection_unusable");

    public async Task<RepositoryItem?> GetAsync(Guid connectionId, string itemId, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r ? await r.GetAsync(connectionId, itemId, ct) : null;

    public async Task<RepositoryChanges> ChangesAsync(Guid connectionId, string containerId, string? cursor, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r
            ? await r.ChangesAsync(connectionId, containerId, cursor, ct)
            : throw new RepositoryUnavailableException("connection_unusable");

    public async Task<RepositoryContent?> OpenAsync(Guid connectionId, string itemId, string userEmail, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r ? await r.OpenAsync(connectionId, itemId, userEmail, ct) : null;

    public async Task<IReadOnlyList<RepositorySource>> SourcesAsync(CancellationToken ct = default) =>
        [.. await sharePoint.SourcesAsync(ct), .. await drive.SourcesAsync(ct)];

    public async Task<RepositoryUserPage> ListAsUserAsync(Guid connectionId, string containerId, string? folderId, string? cursor, string userEmail,
        CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r
            ? await r.ListAsUserAsync(connectionId, containerId, folderId, cursor, userEmail, ct)
            : RepositoryUserPage.Not("unavailable");

    public async Task<RepositoryWrite> UploadAsUserAsync(Guid connectionId, string containerId, string? folderId, string name, Stream content, long length,
        string userEmail, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r
            ? await r.UploadAsUserAsync(connectionId, containerId, folderId, name, content, length, userEmail, ct)
            : RepositoryWrite.Not("unavailable");

    public async Task<RepositoryWrite> RenameAsUserAsync(Guid connectionId, string itemId, string name, string userEmail, CancellationToken ct = default) =>
        await ForAsync(connectionId, ct) is { } r ? await r.RenameAsUserAsync(connectionId, itemId, name, userEmail, ct) : RepositoryWrite.Not("unavailable");
}
