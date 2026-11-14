using System.Net;
using System.Text;

namespace TeamBeats.Testing;

/// <summary>
/// Stands in for Gotenberg's LibreOffice route: answers a small PDF for any file and the PDF with the stamp text appended for a
/// stamp, and an error for a file containing
/// <see cref="Broken"/>. Counts conversions so tests can see that a version is converted only once.
/// </summary>
public sealed class FakeGotenberg : HttpMessageHandler
{
    public const string BaseUrl = "http://gotenberg.test/";
    public const string Broken = "BROKEN-OFFICE-FILE";
    private int _conversions;
    private int _stamps;

    public int Conversions => _conversions;

    public int Stamps => _stamps;

    private int _screenshots;

    /// <summary>PNG renditions made through the Chromium screenshot route.</summary>
    public int Screenshots => _screenshots;

    /// <summary>Makes the Chromium route fail, as when the conversion service is down.</summary>
    public bool HtmlDown { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.Content is not MultipartFormDataContent form)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var file = form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "files");
        var name = file.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty;
        var body = await file.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri?.AbsolutePath == "/forms/libreoffice/convert")
        {
            Interlocked.Increment(ref _conversions);
        }
        if (body.Contains(Broken, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        switch (request.RequestUri?.AbsolutePath)
        {
            case "/forms/chromium/convert/html":
                // The printed page is the HTML itself after a PDF header, so tests can read what a letter says.
                return HtmlDown || name != "index.html" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Pdf($"%PDF-1.7 html\n{body}");
            case "/forms/libreoffice/convert":
                return Pdf($"%PDF-1.7 converted {name}");
            case "/forms/chromium/screenshot/html":
                {
                    // A PNG signature and the size asked for, so tests can see which rendition was made.
                    string Size(string field) => form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == field).ReadAsStringAsync(cancellationToken).Result;
                    if (HtmlDown || name != "index.html")
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    Interlocked.Increment(ref _screenshots);
                    var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes($" fake {Size("width")}x{Size("height")}")).ToArray();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
                }
            case "/forms/pdfengines/stamp":
                string Field(string field) => form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == field).ReadAsStringAsync(cancellationToken).Result;
                if (Field("stampSource") != "text")
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                Interlocked.Increment(ref _stamps);
                return Pdf($"{body}\n% stamped: {Field("stampExpression")}");
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Pdf(string text) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) { Headers = { ContentType = new("application/pdf") } },
    };
}
