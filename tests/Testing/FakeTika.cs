using System.Net;
using System.Text;

namespace TeamBeats.Testing;

/// <summary>
/// In-memory Apache Tika Server for tests (spec 011 FR-020). A file's lines that start with <c>text:</c> stand for a
/// PDF's text layer; lines that start with <c>scan:</c> stand for scanned pages or a picture, and come back only when
/// OCR is asked for. All content is fictional.
/// </summary>
public sealed class FakeTika : HttpMessageHandler
{
    public const string BaseUrl = "http://tika.test/";

    private int _reads;
    private int _recognitions;

    /// <summary>Requests received.</summary>
    public int Reads => _reads;

    /// <summary>Requests that asked for OCR.</summary>
    public int Recognitions => _recognitions;

    /// <summary>Makes the service answer 503, as when it is down.</summary>
    public bool Down { get; set; }

    /// <summary>The OCR languages of the last request that asked for OCR.</summary>
    public string? LastLanguages { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Put || request.RequestUri?.AbsolutePath != "/tika" || request.Content is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        Interlocked.Increment(ref _reads);
        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var skipOcr = request.Headers.TryGetValues("X-Tika-OCRskipOcr", out var skip) && skip.Contains("true");
        if (!skipOcr)
        {
            Interlocked.Increment(ref _recognitions);
            LastLanguages = request.Headers.TryGetValues("X-Tika-OCRLanguage", out var l) ? l.First() : null;
        }
        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var text = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("text:", StringComparison.Ordinal))
            {
                text.Append(line["text:".Length..].Trim()).Append("\n\n\n");
            }
            else if (line.StartsWith("scan:", StringComparison.Ordinal) && !skipOcr)
            {
                text.Append(line["scan:".Length..].Trim()).Append("\n\n\n");
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/plain") };
    }
}
