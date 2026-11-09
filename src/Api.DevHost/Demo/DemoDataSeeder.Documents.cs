using TeamBeats.Primitives;
using TeamBeats.Modules.Documents;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// Document libraries for the whole tenant with their folders and files, uploaded and published by the named author
    /// (or the administrator when the author may not add files there). A file the library already has by name is kept;
    /// when its stored content is gone (staging keeps files on the machine's disk, which a deploy replaces), it is
    /// deleted and uploaded again so it still opens.
    /// </summary>
    private async Task SeedDocumentsAsync(Run run, CancellationToken ct)
    {
        foreach (var library in run.Pack.Libraries)
        {
            await ItemAsync(run, $"library {library.Key}", async () =>
            {
                var (libraryId, folders, files) = await AsAsync(run, AdminPersona, 20, async sp =>
                {
                    var libraries = sp.GetRequiredService<Libraries>();
                    var en = library.Names.GetValueOrDefault("en");
                    var id = (await libraries.ListAsync(ct)).FirstOrDefault(l => l.Names.GetValueOrDefault("en") == en)?.Id;
                    if (id is null)
                    {
                        id = (await libraries.CreateAsync(new LibraryRequest(library.Names, LibraryScopes.Tenant, null, null, null, "internal", null), ct)).Id;
                        run.Report.Add("libraries");
                    }
                    var known = (await libraries.FoldersAsync(id.Value, ct)).ToDictionary(f => f.Name, f => f.Id, StringComparer.Ordinal);
                    foreach (var folder in library.Folders ?? [])
                    {
                        if (!known.ContainsKey(folder))
                        {
                            known[folder] = (await libraries.CreateFolderAsync(id.Value, new FolderRequest(null, folder), ct)).Id;
                        }
                    }
                    var items = await sp.GetRequiredService<DocumentFiles>().ItemsAsync(id.Value, new ItemQuery(Recursive: true), null, 500, 0, ct);
                    return (id.Value, known, items.Items.ToDictionary(i => i.Name, i => i.Id, StringComparer.Ordinal));
                }, ct);

                var daysAgo = 18.0;
                foreach (var document in library.Documents)
                {
                    daysAgo = Math.Max(0, daysAgo - 1.3);
                    var when = daysAgo;
                    await ItemAsync(run, $"document {document.Name}", async () =>
                    {
                        if (files.TryGetValue(document.Name, out var existing))
                        {
                            if (await OpensAsync(run, existing, ct))
                            {
                                return;
                            }
                            await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<DocumentVersions>().DeleteAsync(existing, ct), ct);
                            run.Report.Add("documents restored");
                        }
                        var folder = document.Folder is { } f && folders.TryGetValue(f, out var folderId) ? folderId : (Guid?)null;
                        var title = document.Title.GetValueOrDefault("en") ?? document.Title.Values.First();
                        var bytes = DemoText.File(document.Name, title, document.Lines);
                        var request = new UploadRequest(document.Name, folder, null, null, document.Classification, true);
                        async Task<DocumentView> Upload(IServiceProvider sp)
                        {
                            using var body = new MemoryStream(bytes);
                            return await sp.GetRequiredService<DocumentFiles>().UploadAsync(libraryId, request, body, bytes.Length, ct);
                        }
                        if (await AsAsync(run, document.Author, when, sp => TryAsync(() => Upload(sp)), ct) is null)
                        {
                            await AsAsync(run, AdminPersona, when, Upload, ct);
                        }
                        run.Report.Add("documents");
                    });
                }
            });
        }
    }

    /// <summary>True when the administrator can download the document's current version.</summary>
    private async Task<bool> OpensAsync(Run run, Guid documentId, CancellationToken ct)
    {
        try
        {
            return await AsAsync(run, AdminPersona, 0, async sp =>
            {
                var files = sp.GetRequiredService<DocumentFiles>();
                var token = await files.DownloadTokenAsync(documentId, null, ct);
                var (content, _, _) = await files.OpenAsync(token.Token, ct);
                await content.DisposeAsync();
                return true;
            }, ct);
        }
        catch (Exception e) when (e is IOException or AppException)
        {
            return false;
        }
    }
}
