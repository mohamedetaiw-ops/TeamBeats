using System.Globalization;
using System.Text;
using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Api.DevHost.Demo;

/// <summary>Turns the pack's short text forms into what the modules take: body documents and small files.</summary>
public static class DemoText
{
    /// <summary>
    /// A body from lines: <c>## </c> starts a heading, <c>- </c> a bullet (consecutive bullets form one list),
    /// <c>&gt; </c> a quote, anything else a paragraph.
    /// </summary>
    public static BodyDocument Body(IEnumerable<string> lines)
    {
        var blocks = new List<Block>();
        List<IReadOnlyList<Inline>>? items = null;
        void Flush()
        {
            if (items is { Count: > 0 })
            {
                blocks.Add(new Block(BlockTypes.List, Ordered: false, Items: items));
            }
            items = null;
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                (items ??= []).Add([new Inline(line[2..].Trim())]);
                continue;
            }
            Flush();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(new Block(BlockTypes.Heading, [new Inline(line[3..].Trim())], Level: 2));
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                blocks.Add(new Block(BlockTypes.Quote, [new Inline(line[2..].Trim())]));
            }
            else
            {
                blocks.Add(new Block(BlockTypes.Paragraph, [new Inline(line)]));
            }
        }
        Flush();
        return new BodyDocument(blocks);
    }

    /// <summary>A body that is one paragraph per line, for policy clauses.</summary>
    public static BodyDocument Paragraphs(string text) =>
        new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => new Block(BlockTypes.Paragraph, [new Inline(l)])).ToList());

    /// <summary>The bytes of a seeded document: a one-page PDF for <c>.pdf</c> names, UTF-8 text otherwise.</summary>
    public static byte[] File(string name, string title, IReadOnlyList<string> lines) =>
        name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? Pdf(title, lines)
            : Encoding.UTF8.GetBytes((name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "# " : string.Empty) + title + "\n\n" + string.Join("\n", lines) + "\n");

    /// <summary>
    /// A minimal valid PDF 1.4 with one A4 page of Helvetica text: the title large, then each line, wrapped. Latin
    /// text only (the standard fonts carry no Arabic), which is why Arabic documents in the pack are Markdown.
    /// </summary>
    public static byte[] Pdf(string title, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder();
        content.Append("BT\n/F1 18 Tf\n56 780 Td\n(").Append(Escape(title)).Append(") Tj\n/F1 11 Tf\n0 -30 Td\n");
        foreach (var line in lines.SelectMany(l => Wrap(l, 90)).Take(56))
        {
            content.Append('(').Append(Escape(line)).Append(") Tj\n0 -16 Td\n");
        }
        content.Append("ET\n");
        var stream = content.ToString();

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Escape(string text)
    {
        var latin = new StringBuilder();
        foreach (var c in text)
        {
            latin.Append(c switch
            {
                '\\' => "\\\\",
                '(' => "\\(",
                ')' => "\\)",
                '’' or '‘' => "'",
                '“' or '”' => "\"",
                '–' or '—' => "-",
                _ when c < 256 => c.ToString(),
                _ => "?",
            });
        }
        return latin.ToString();
    }

    private static IEnumerable<string> Wrap(string line, int width)
    {
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }
        var current = new StringBuilder();
        foreach (var word in line.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(word);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
