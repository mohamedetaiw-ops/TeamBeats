using System.Text;
using TeamBeats.Modules.Engagement;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 023 FR-003: attachment names keep only the file name, and a file must look like the type it claims.</summary>
public sealed class PostAttachmentRulesTests
{
    [Theory]
    [InlineData("C:\\Users\\someone\\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("  plan\u0007.docx ", "plan.docx")]
    public void A_name_keeps_only_its_last_part_without_control_characters(string given, string expected) =>
        Assert.Equal(expected, PostAttachments.CleanName(given));

    [Fact]
    public void An_empty_name_is_refused_and_a_long_one_is_cut()
    {
        var ex = Assert.Throws<AppException>(() => PostAttachments.CleanName(" / "));
        Assert.Contains("validation.engagement.attachment_name", ex.Errors!.SelectMany(e => e.Value));
        Assert.Equal(PostAttachments.MaxName, PostAttachments.CleanName(new string('a', 300) + ".pdf").Length);
    }

    [Fact]
    public void Files_must_look_like_their_type()
    {
        Assert.True(PostAttachments.Looks("pdf", Encoding.ASCII.GetBytes("%PDF-1.7")));
        Assert.False(PostAttachments.Looks("pdf", Encoding.ASCII.GetBytes("<html>")));
        Assert.True(PostAttachments.Looks("zip", [0x50, 0x4B, 0x03, 0x04, 0x00]));
        Assert.False(PostAttachments.Looks("zip", Encoding.ASCII.GetBytes("%PDF-1.7")));
        Assert.True(PostAttachments.Looks("text", Encoding.UTF8.GetBytes("a,b\n1,2")));
        Assert.False(PostAttachments.Looks("text", [0x41, 0x00, 0x42]));
    }
}
