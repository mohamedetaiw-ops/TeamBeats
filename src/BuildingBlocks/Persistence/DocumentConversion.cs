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

    /// <summary>The PDF with one line of text at the foot of every page, or null when it could not be stamped.</summary>
    Task<Stream?> StampFooterAsync(Stream pdf, string text, CancellationToken ct = default);

    /// <summary>
    /// A PDF printed from a self-contained HTML page (spec 022 FR-021 letters), or null when it could not be printed.
    /// The page must load nothing from outside: the converter is given the HTML alone.
    /// </summary>
    Task<Stream?> HtmlToPdfAsync(string html, CancellationToken ct = default);
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

    public Task<Stream?> ToPdfAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Gotenberg picks the converter from the extension; the name itself is not needed and is not sent.
        form.Add(file, "files", "document" + Path.GetExtension(fileName).ToLowerInvariant());
        return PostAsync("forms/libreoffice/convert", form, ct);
    }

    /// <summary>
    /// Gotenberg 8's stamp route with a text source only: the footer goes over the content at the bottom centre of
    /// each page. No file or path source is ever sent (GHSA-3cv5-q585-h563 concerns those).
    /// </summary>
    public Task<Stream?> StampFooterAsync(Stream pdf, string text, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var file = new StreamContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", "document.pdf");
        form.Add(new StringContent("text"), "stampSource");
        form.Add(new StringContent(text), "stampExpression");
        form.Add(new StringContent("""{"position":"bc","offset":"0 14","points":8,"scale":"1 abs","rotation":0,"opacity":1}"""), "stampOptions");
        return PostAsync("forms/pdfengines/stamp", form, ct);
    }

    /// <summary>
    /// Gotenberg 8's Chromium route with one index.html and nothing else, A4 with 2 cm margins. Chromium shapes Arabic
    /// and lays out right to left, which a PDF library alone does not.
    /// </summary>
    public Task<Stream?> HtmlToPdfAsync(string html, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var file = new StringContent(html, System.Text.Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(file, "files", "index.html");
        form.Add(new StringContent("8.27"), "paperWidth");
        form.Add(new StringContent("11.7"), "paperHeight");
        foreach (var side in new[] { "marginTop", "marginBottom", "marginLeft", "marginRight" })
        {
            form.Add(new StringContent("0.79"), side);
        }
        form.Add(new StringContent("true"), "printBackground");
        return PostAsync("forms/chromium/convert/html", form, ct);
    }

    private async Task<Stream?> PostAsync(string route, MultipartFormDataContent form, CancellationToken ct)
    {
        try
        {
            using (form)
            {
                using var response = await clients.CreateClient(ClientName).PostAsync(new Uri(baseUrl, route), form, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var pdf = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                await response.Content.CopyToAsync(pdf, ct);
                pdf.Position = 0;
                return pdf;
            }
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

    public Task<Stream?> StampFooterAsync(Stream pdf, string text, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

    public Task<Stream?> HtmlToPdfAsync(string html, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
}
