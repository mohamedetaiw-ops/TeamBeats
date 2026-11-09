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
            case "/forms/libreoffice/convert":
                return Pdf($"%PDF-1.7 converted {name}");
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
