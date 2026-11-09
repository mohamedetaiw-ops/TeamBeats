using System.Net.Http.Headers;

namespace TeamBeats.Persistence;

/// <summary>
/// Converts Office documents to PDF for preview (spec 011 FR-009, plan "Preview"). Conversion runs in a separate
/// sandboxed service, so macros and scripts in a file never run inside the product (spec 011 section 14).
/// </summary>
public interface IDocumentConverter
{
    /// <summary>False when no conversion service is configured; previews of Office files are then not available.</summary>
    bool Available { get; }

    /// <summary>The PDF, or null when this file could not be converted. The caller disposes the stream.</summary>
    Task<Stream?> ToPdfAsync(Stream content, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Gotenberg's LibreOffice route over HTTP. Gotenberg (MIT) runs as its own container; nothing of it is linked into the
/// product. A network error, a timeout or a refusal gives null, never a partial file.
/// </summary>
public sealed class GotenbergConverter(IHttpClientFactory clients, Uri baseUrl) : IDocumentConverter
{
    public const string UrlKey = "Conversion:Gotenberg:Url";
    public const string ClientName = "gotenberg";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public bool Available => true;

    public async Task<Stream?> ToPdfAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            // Gotenberg picks the converter from the extension; the name itself is not needed and is not sent.
            form.Add(file, "files", "document" + Path.GetExtension(fileName).ToLowerInvariant());
            using var response = await clients.CreateClient(ClientName).PostAsync(new Uri(baseUrl, "forms/libreoffice/convert"), form, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var pdf = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await response.Content.CopyToAsync(pdf, ct);
            pdf.Position = 0;
            return pdf;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>No conversion service: Office files show "preview not available" with a download where allowed.</summary>
public sealed class UnavailableConverter : IDocumentConverter
{
    public bool Available => false;

    public Task<Stream?> ToPdfAsync(Stream content, string fileName, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
}
