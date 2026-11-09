using System.Text;
using TeamBeats.Modules.Documents;
using TeamBeats.Persistence;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 011 T008: which formats preview from the file, which need conversion, and the converter without a service.</summary>
public sealed class PreviewRulesTests
{
    [Theory]
    [InlineData("application/pdf", "a.pdf", "pdf", false)]
    [InlineData("image/webp", "a.webp", "image", false)]
    [InlineData("video/webm", "a.webm", "video", false)]
    [InlineData("text/csv", "a.csv", "text", false)]
    [InlineData("text/markdown", "a.md", "text", false)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "a.DOCX", "pdf", true)]
    [InlineData("application/vnd.ms-powerpoint", "a.ppt", "pdf", true)]
    [InlineData("application/rtf", "a.rtf", "pdf", true)]
    [InlineData("application/octet-stream", "a.bin", "unavailable", false)]
    public void Formats_map_to_a_preview_kind(string contentType, string name, string kind, bool convert)
    {
        Assert.Equal((kind, convert), PreviewKinds.Of(contentType, name));
    }

    [Fact]
    public void Every_allowed_file_type_has_a_preview_kind()
    {
        foreach (var (extension, contentType) in FileTypes.ContentTypes)
        {
            Assert.NotEqual(PreviewKinds.Unavailable, PreviewKinds.Of(contentType, "file." + extension).Kind);
        }
    }

    [Fact]
    public async Task Without_a_conversion_service_nothing_is_converted()
    {
        var converter = new UnavailableConverter();
        Assert.False(converter.Available);
        Assert.Null(await converter.ToPdfAsync(new MemoryStream(Encoding.UTF8.GetBytes("x")), "a.docx"));
    }
}
