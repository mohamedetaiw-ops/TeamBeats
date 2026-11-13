using System.IO.Compression;
using System.Text;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Security;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 026 T026 (US6, FR-015): the built-in patterns find the launch countries' identity numbers, IBANs, card numbers
/// and passports only when their check digits, dates or codes are right, and report counts, never values. Every number
/// here is made up for the test.
/// </summary>
public sealed class SensitivePatternsTests
{
    private static Dictionary<string, int> Found(string text, params TenantPattern[] tenant) =>
        SensitivePatterns.Find(text, tenant).ToDictionary(f => f.Name ?? f.Type, f => f.Count);

    [Theory]
    [InlineData("National ID 1087654321 on file.", SensitiveTypes.SaudiId)]
    [InlineData("Iqama: 2123456788", SensitiveTypes.SaudiId)]
    [InlineData("Emirates ID 784-1990-1234567-6", SensitiveTypes.EmiratesId)]
    [InlineData("Emirates ID 784199012345676", SensitiveTypes.EmiratesId)]
    [InlineData("الرقم القومي 29001150123456", SensitiveTypes.EgyptId)]
    [InlineData("QID 28763400123", SensitiveTypes.QatarId)]
    [InlineData("Pay to SA03 8000 0000 6080 1016 7519 please", SensitiveTypes.Iban)]
    [InlineData("IBAN GB82WEST12345698765432", SensitiveTypes.Iban)]
    [InlineData("Card 4111 1111 1111 1111 exp 12/29", SensitiveTypes.PaymentCard)]
    [InlineData("Passport number: A1234567", SensitiveTypes.Passport)]
    [InlineData("رقم جواز السفر: P12345678", SensitiveTypes.Passport)]
    public void Each_built_in_type_is_found(string text, string type)
    {
        var found = Found(text);
        Assert.Equal(1, Assert.Contains(type, (IDictionary<string, int>)found));
        Assert.Single(found);
    }

    [Theory]
    [InlineData("Order 1234567890 ships today.")] // ten digits that fail the check digit
    [InlineData("Ref 29013150123456")] // month 13
    [InlineData("Ref 29001159923456")] // governorate 99
    [InlineData("IBAN SA03 8000 0000 6080 1016 7518")] // wrong check
    [InlineData("Card 4111 1111 1111 1112")] // fails Luhn
    [InlineData("Model A1234567 is in stock.")] // a passport form with no mention of a passport
    [InlineData("Call +971 4 123 4567 or visit room 1204.")]
    public void Ordinary_numbers_are_not_reported(string text) => Assert.Empty(Found(text));

    [Fact]
    public void An_Emirates_ID_is_not_also_counted_as_a_card_and_counts_add_up()
    {
        var found = Found("784-1990-1234567-6 and 784199012345676, cards 4111111111111111 and 5500 0000 0000 0004.");
        Assert.Equal(2, found[SensitiveTypes.EmiratesId]);
        Assert.Equal(2, found[SensitiveTypes.PaymentCard]);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Passports_never_block_and_tenant_patterns_do()
    {
        var employee = new TenantPattern("Employee number", SensitivePatterns.Compile(@"\bEMP-\d{6}\b")!);
        var found = SensitivePatterns.Find("Passport: A1234567, staff EMP-004211 and EMP-004212.", [employee]);
        Assert.False(Assert.Single(found, f => f.Type == SensitiveTypes.Passport).Blocking);
        var custom = Assert.Single(found, f => f.Type == SensitiveTypes.Custom);
        Assert.Equal(("Employee number", 2, true), (custom.Name, custom.Count, custom.Blocking));
    }

    [Fact]
    public void Tenant_patterns_are_compiled_with_a_time_limit_and_bad_ones_are_refused()
    {
        Assert.Null(SensitivePatterns.Compile("(unclosed"));
        var slow = SensitivePatterns.Compile("(a+)+$")!;
        Assert.Equal(SensitivePatterns.TenantTimeout, slow.MatchTimeout);
        // A pattern that runs too long stops; it does not hang the publish.
        Assert.Equal(0, SensitivePatterns.Count(slow, new string('a', 40) + "!"));
    }

    private static MemoryStream Docx(string body)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write($"""<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Text_comes_out_of_plain_and_Office_files_with_paragraphs_kept_apart()
    {
        var text = await TextExtraction.ReadAsync(Docx("<w:p><w:r><w:t>IBAN</w:t></w:r></w:p><w:p><w:r><w:t>GB82WEST12345698765432</w:t></w:r></w:p>"), "docx", default);
        Assert.Equal("IBAN\nGB82WEST12345698765432\n", text);
        Assert.Equal("Plain notes.", await TextExtraction.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("Plain notes.")), "txt", default));
        // Formats with no text, and broken archives, give nothing.
        Assert.Null(await TextExtraction.ReadAsync(new MemoryStream([1, 2, 3]), "pdf", default));
        Assert.Null(await TextExtraction.ReadAsync(new MemoryStream([1, 2, 3]), "docx", default));
    }
}
