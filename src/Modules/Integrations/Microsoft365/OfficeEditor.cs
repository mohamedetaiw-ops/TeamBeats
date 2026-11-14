using System.Text.Json;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Microsoft 365 adapter for <see cref="IOfficeEditor"/> (spec 011 FR-010): the copy goes to a folder in the employee's
/// own OneDrive with delegated <c>Files.ReadWrite</c>, opens in Office for the web from its <c>webUrl</c>, and is read
/// back and removed on check-in. Nothing is cached; every call acts as the signed-in employee.
/// </summary>
public sealed class GraphOfficeEditor(DelegatedAccessService access, IMicrosoftGraph graph) : IOfficeEditor
{
    /// <summary>The OneDrive folder that holds copies while they are edited.</summary>
    public const string Folder = "TeamBeats editing";

    public async Task<string> StateAsync(CancellationToken ct = default) =>
        (await access.AcquireAsync(Microsoft365Features.OfficeEditing, ct)).State;

    public async Task<OfficeEditCopy> PlaceAsync(string fileName, Stream content, long length, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.OfficeEditing, ct);
        if (grant.State != M365States.Ok)
        {
            return new OfficeEditCopy(grant.State, null, null);
        }
        try
        {
            var path = $"me/drive/root:/{Uri.EscapeDataString(Folder)}/{Uri.EscapeDataString(fileName)}:/content?@microsoft.graph.conflictBehavior=rename";
            var item = await graph.DelegatedUploadAsync(grant.AccessToken!, path, content, length, ct);
            var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
            var url = item.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
            return id is null || url is null || !url.StartsWith("https://", StringComparison.Ordinal)
                ? new OfficeEditCopy(M365States.Degraded, null, null)
                : new OfficeEditCopy(M365States.Ok, id, url);
        }
        catch (GraphCallException ex)
        {
            return new OfficeEditCopy(StateOf(ex), null, null);
        }
    }

    public async Task<OfficeEditContent> ReadAsync(string itemId, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.OfficeEditing, ct);
        if (grant.State != M365States.Ok)
        {
            return new OfficeEditContent(grant.State, null, null);
        }
        try
        {
            var stream = await graph.DelegatedDownloadAsync(grant.AccessToken!, $"me/drive/items/{Uri.EscapeDataString(itemId)}/content", ct);
            return new OfficeEditContent(M365States.Ok, stream, stream.Length);
        }
        catch (GraphCallException ex) when (ex.Kind is "not_found" or "no_mailbox")
        {
            return new OfficeEditContent(OfficeEditContent.Gone, null, null);
        }
        catch (GraphCallException ex)
        {
            return new OfficeEditContent(StateOf(ex), null, null);
        }
    }

    public async Task<Func<CancellationToken, Task<bool>>> RemoverAsync(string itemId, CancellationToken ct = default)
    {
        var grant = await access.AcquireAsync(Microsoft365Features.OfficeEditing, ct);
        if (grant.State != M365States.Ok)
        {
            return _ => Task.FromResult(false);
        }
        var token = grant.AccessToken!;
        return async token2 =>
        {
            try
            {
                await graph.DelegatedSendAsync(token, HttpMethod.Delete, $"me/drive/items/{Uri.EscapeDataString(itemId)}", null, token2);
                return true;
            }
            catch (GraphCallException ex)
            {
                // Already gone is what we wanted.
                return ex.Kind is "not_found" or "no_mailbox";
            }
        };
    }

    private static string StateOf(GraphCallException ex) => ex.Kind switch
    {
        "unauthorized" => M365States.Reconnect,
        "unavailable" => M365States.Degraded,
        _ => M365States.NoDrive,
    };
}
