using TeamBeats.Persistence;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 011 FR-020: the text service's output is kept compact for search.</summary>
public sealed class TextRecognitionTests
{
    [Fact]
    public void Runs_of_blank_lines_become_one_and_trailing_space_goes()
    {
        Assert.Equal("Cover\n\nPage two  line\nnext", TikaTextRecognizer.Normalize("\n\r\nCover   \r\n\n\n\n   \nPage two  line\nnext\n\n"));
    }

    [Fact]
    public void Nothing_but_whitespace_is_empty()
    {
        Assert.Equal(string.Empty, TikaTextRecognizer.Normalize(" \n\t\n"));
    }
}
