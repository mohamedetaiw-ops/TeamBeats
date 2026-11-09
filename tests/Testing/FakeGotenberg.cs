using System.Net;
using System.Text;

namespace TeamBeats.Testing;

/// <summary>
/// Stands in for Gotenberg's LibreOffice route: answers a small PDF for any file, and an error for a file containing
/// <see cref="Broken"/>. Counts conversions so tests can see that a version is converted only once.
/// </summary>
public sealed class FakeGotenberg : HttpMessageHandler
{
    public const string BaseUrl = "http://gotenberg.test/";
    public const string Broken = "BROKEN-OFFICE-FILE";
    private int _conversions;

    public int Conversions => _conversions;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/forms/libreoffice/convert" || request.Content is not MultipartFormDataContent form)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        Interlocked.Increment(ref _conversions);
        var part = form.Single();
        var name = part.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty;
        var body = await part.ReadAsStringAsync(cancellationToken);
        if (body.Contains(Broken, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"%PDF-1.7 converted {name}")) { Headers = { ContentType = new("application/pdf") } },
        };
    }
}
