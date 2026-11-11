using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// Stamps a payslip PDF with who downloaded it and when (spec 019 FR-011). The page content is kept as payroll made
/// it (section 11): the stamp is a new content stream on top of each page, in a standard PDF font, so no font is
/// embedded. Text that is not plain ASCII is replaced, because the standard fonts cannot draw it. A PDF that cannot
/// be opened or changed (one with a password, for example) gets null and is not handed out.
/// </summary>
public static class PdfWatermark
{
    private const string FontName = "/TbWatermark";

    public static byte[]? Apply(byte[] pdf, string text)
    {
        try
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            if (document.PageCount == 0)
            {
                return null;
            }
            var font = new PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Helvetica");
            font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            document.Internals.AddObject(font);
            var line = Literal(text);
            foreach (var page in document.Pages)
            {
                var fonts = page.Resources.Elements.GetDictionary("/Font");
                if (fonts is null)
                {
                    fonts = new PdfDictionary(document);
                    page.Resources.Elements["/Font"] = fonts;
                }
                fonts.Elements[FontName] = font.Reference;
                var width = page.Width.Point;
                var height = page.Height.Point;
                var diagonal = Math.Atan2(height, width);
                var (cos, sin) = (Math.Cos(diagonal), Math.Sin(diagonal));
                var ops = new StringBuilder()
                    .Append("Q q 0.55 g BT ").Append(FontName).Append(" 8 Tf 1 0 0 1 24 14 Tm ").Append(line).Append(" Tj ET Q\n")
                    .Append("q 0.85 g BT ").Append(FontName).Append(" 22 Tf ")
                    .Append(Number(cos)).Append(' ').Append(Number(sin)).Append(' ').Append(Number(-sin)).Append(' ').Append(Number(cos)).Append(' ')
                    .Append(Number(width * 0.12)).Append(' ').Append(Number(height * 0.18)).Append(" Tm ").Append(line).Append(" Tj ET Q\n")
                    .ToString();
                // Whatever state the payroll content leaves, the stamp starts clean.
                page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
                page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
            }
            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfReaderException or InvalidOperationException or NotImplementedException or ArgumentException or FormatException
            or IndexOutOfRangeException or NullReferenceException or InvalidCastException or IOException)
        {
            return null;
        }
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A PDF literal string of printable ASCII, with the characters PDF treats as special escaped.</summary>
    public static string Literal(string text)
    {
        var b = new StringBuilder("(");
        foreach (var c in text)
        {
            if (c is '(' or ')' or '\\')
            {
                b.Append('\\').Append(c);
            }
            else
            {
                b.Append(c is >= ' ' and <= '~' ? c : '?');
            }
        }
        return b.Append(')').ToString();
    }
}
