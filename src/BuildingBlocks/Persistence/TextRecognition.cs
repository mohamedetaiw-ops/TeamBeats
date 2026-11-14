using System.Net.Http.Headers;
using System.Text;

namespace TeamBeats.Persistence;

/// <summary>
/// Reads the text of PDFs and images for search (spec 011 FR-020): the text layer of a PDF, and, when recognition is
/// asked for, text recognized by OCR from scanned pages and pictures. It runs in a separate service, so a crafted file
/// is never parsed inside the product.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>False when no service is configured; PDFs and images then give no text.</summary>
    bool Available { get; }

    /// <summary>
    /// The text of the file, or null when it could not be read. Without <paramref name="recognize"/> only text already
    /// in the file is read (a PDF's text layer) and images give nothing.
    /// </summary>
    Task<string?> ReadAsync(Stream content, string contentType, bool recognize, CancellationToken ct = default);
}

/// <summary>
/// Apache Tika Server (Apache-2.0) over HTTP, with Tesseract for OCR in the same container. Nothing of it is linked into
/// the product. Languages are Tesseract's codes joined by <c>+</c>, English and Arabic unless configured. A network
/// error, a timeout or a refusal gives null.
/// </summary>
public sealed class TikaTextRecognizer(IHttpClientFactory clients, Uri baseUrl, string languages) : ITextRecognizer
{
    public const string UrlKey = "Ocr:Tika:Url";
    public const string LanguagesKey = "Ocr:Tika:Languages";
    public const string DefaultLanguages = "eng+ara";
    public const string ClientName = "tika";
    public const int MaxChars = 1_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public bool Available => true;

    public async Task<string?> ReadAsync(Stream content, string contentType, bool recognize, CancellationToken ct = default)
    {
        var image = contentType.StartsWith("image/", StringComparison.Ordinal);
        if (image && !recognize)
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(baseUrl, "tika"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (recognize)
        {
            request.Headers.Add("X-Tika-OCRLanguage", languages);
            // Pages with a text layer are read as they are; pages without one are recognized.
            request.Headers.Add("X-Tika-PDFOcrStrategy", "auto");
        }
        else
        {
            request.Headers.Add("X-Tika-PDFOcrStrategy", "no_ocr");
            request.Headers.Add("X-Tika-OCRskipOcr", "true");
        }
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            var text = Normalize(new string(buffer, 0, read));
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Tika separates pages and blocks with runs of blank lines; one blank line is enough for search.</summary>
    public static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        var output = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank = output.Length > 0;
                continue;
            }
            if (blank)
            {
                output.Append('\n');
                blank = false;
            }
            if (output.Length > 0)
            {
                output.Append('\n');
            }
            output.Append(line);
        }
        return output.ToString();
    }
}

/// <summary>No text service: PDFs and images give no text for search.</summary>
public sealed class UnavailableTextRecognizer : ITextRecognizer
{
    public bool Available => false;

    public Task<string?> ReadAsync(Stream content, string contentType, bool recognize, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
