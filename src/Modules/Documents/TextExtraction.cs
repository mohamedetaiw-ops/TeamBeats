using System.IO.Compression;
using System.Text;
using System.Xml;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Text of an uploaded file for sensitive data detection (spec 026 US6) and search: plain text formats as they are, and
/// the text runs of Office Open XML and OpenDocument files read from their XML parts. PDF, images and older binary
/// formats give no text. Reading stops at the limits, so a crafted archive cannot exhaust memory.
/// </summary>
public static class TextExtraction
{
    public const int MaxChars = 1_000_000;
    public const long MaxXmlBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal) { "txt", "csv", "md" };

    public static bool Supports(string extension) => Plain.Contains(extension) || PartsOf(extension) is not null;

    private static Func<string, bool>? PartsOf(string extension) => extension switch
    {
        "docx" => n => n is "word/document.xml" || n.StartsWith("word/header", StringComparison.Ordinal) || n.StartsWith("word/footer", StringComparison.Ordinal),
        "xlsx" => n => n is "xl/sharedStrings.xml" || n.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal),
        "pptx" => n => n.StartsWith("ppt/slides/slide", StringComparison.Ordinal),
        "odt" or "ods" or "odp" => n => n is "content.xml",
        _ => null,
    };

    /// <summary>The text of the file, or null when its format gives none or it cannot be read.</summary>
    public static async Task<string?> ReadAsync(Stream stream, string extension, CancellationToken ct)
    {
        try
        {
            if (Plain.Contains(extension))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaxChars];
                var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
                return new string(buffer, 0, read);
            }
            if (PartsOf(extension) is not { } wanted)
            {
                return null;
            }
            // ZipArchive needs a seekable stream; object storage streams are not.
            await using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            copy.Position = 0;
            using var zip = new ZipArchive(copy, ZipArchiveMode.Read);
            var text = new StringBuilder();
            long budget = MaxXmlBytes;
            foreach (var entry in zip.Entries.Where(e => wanted(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (entry.Length > budget || text.Length >= MaxChars)
                {
                    break;
                }
                budget -= entry.Length;
                await using var part = entry.Open();
                AppendText(part, text);
            }
            return text.Length > MaxChars ? text.ToString(0, MaxChars) : text.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Text nodes of one XML part; paragraphs, cells and line breaks become line ends so values never run together.</summary>
    private static void AppendText(Stream part, StringBuilder text)
    {
        using var xml = XmlReader.Create(part, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
        while (xml.Read() && text.Length < MaxChars)
        {
            switch (xml.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    text.Append(xml.Value);
                    break;
                case XmlNodeType.EndElement when xml.LocalName is "p" or "c" or "v" or "si" or "tr" or "br" or "tab" or "h":
                    text.Append('\n');
                    break;
            }
        }
    }
}
