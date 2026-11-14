using System.Globalization;
using System.Net;
using System.Text;

namespace TeamBeats.DevFakes;

/// <summary>
/// In-memory stand-in for the Gotenberg conversion service (Gotenberg protocol) (spec 011 FR-009), so Office previews load in development,
/// staging and tests without running LibreOffice. The LibreOffice route answers with a one page PDF that names the file
/// and says it is a fictional preview; the stamp route returns the PDF it was given. <see cref="Unavailable"/> makes
/// every call fail, for the "preview not available" state.
/// </summary>
public sealed class FakeOfficeConverter : HttpMessageHandler
{
    public const string BaseUrl = "https://gotenberg.fake.test/";

    public bool Unavailable { get; set; }

    public int Requests;

    /// <summary>The last HTML page printed, so tests can check what a letter says.</summary>
    public string? LastHtml { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        if (Unavailable)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Post || request.Content is not MultipartFormDataContent form)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var file = form.FirstOrDefault(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "files");
        if (file is null)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
        if (path.EndsWith("/forms/libreoffice/convert", StringComparison.Ordinal))
        {
            var size = (await file.ReadAsByteArrayAsync(cancellationToken)).Length;
            var kind = Path.GetExtension(file.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty).TrimStart('.').ToUpperInvariant();
            return Pdf(OnePage(["TeamBeats test environment", $"Fictional preview of a {(kind.Length > 0 ? kind : "Office")} file",
                $"{size.ToString("N0", CultureInfo.InvariantCulture)} bytes, converted by the fake converter."]));
        }
        if (path.EndsWith("/forms/chromium/convert/html", StringComparison.Ordinal))
        {
            // The letter's own text is kept out of the fake PDF; a hash of the page lets tests tell two pages apart, and
            // the verification code is printed so a test can check the letter on the public page.
            var html = await file.ReadAsStringAsync(cancellationToken);
            LastHtml = html;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..16];
            var code = System.Text.RegularExpressions.Regex.Match(html, "[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}").Value;
            return Pdf(OnePage(["TeamBeats test environment", "Fictional letter printed by the fake converter", $"Page {hash}", $"Code {code}"]));
        }
        if (path.EndsWith("/forms/chromium/screenshot/html", StringComparison.Ordinal))
        {
            // Asset renditions (spec 011 FR-012): a transparent one-pixel PNG stands in for the rendered image.
            var png = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")) };
            png.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return png;
        }
        if (path.EndsWith("/forms/pdfengines/stamp", StringComparison.Ordinal))
        {
            return Pdf(await file.ReadAsByteArrayAsync(cancellationToken));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Pdf(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        return response;
    }

    /// <summary>A valid single page A4 PDF with a few lines of Helvetica text (ASCII only).</summary>
    public static byte[] OnePage(IReadOnlyList<string> lines)
    {
        static string Escape(string s) => new string(s.Where(c => c is >= ' ' and <= '~').ToArray()).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var text = new StringBuilder("BT /F1 18 Tf 72 760 Td 24 TL");
        foreach (var line in lines)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({Escape(line)}) Tj T*");
        }
        text.Append(" ET");
        var stream = text.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        }
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
