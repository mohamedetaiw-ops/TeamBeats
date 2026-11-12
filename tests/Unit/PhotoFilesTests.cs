using System.Text;
using TeamBeats.Modules.Engagement;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 023 FR-017: gallery photos are PNG or JPEG within the limits, and lose their metadata before they are kept.</summary>
public sealed class PhotoFilesTests
{
    private static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        using var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (var (type, data) in chunks)
        {
            s.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]);
        }
        return s.ToArray();
    }

    private static byte[] Ihdr(int width, int height) =>
        [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 2, 0, 0, 0];

    [Fact]
    public void Png_text_and_exif_chunks_are_dropped_and_the_picture_kept()
    {
        var file = Png(("IHDR", Ihdr(800, 600)), ("tEXt", Encoding.Latin1.GetBytes("Location\0Riyadh")), ("eXIf", Encoding.Latin1.GetBytes("GPS")),
            ("IDAT", Encoding.Latin1.GetBytes("pixels")), ("IEND", []));
        var clean = PhotoFiles.Clean("image/png", file);
        var text = Encoding.Latin1.GetString(clean.Data);
        Assert.Equal((800, 600), (clean.Width, clean.Height));
        Assert.DoesNotContain("Riyadh", text);
        Assert.DoesNotContain("GPS", text);
        Assert.Contains("pixels", text);
        Assert.EndsWith("IEND\0\0\0\0", text);
    }

    [Fact]
    public void Jpeg_app_segments_and_comments_are_dropped_and_the_scan_kept()
    {
        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x07]);
        s.Write(Encoding.Latin1.GetBytes("JFIF\0"));
        s.Write([0xFF, 0xE1, 0x00, 0x08]);
        s.Write(Encoding.Latin1.GetBytes("GPS:1."));
        s.Write([0xFF, 0xFE, 0x00, 0x07]);
        s.Write(Encoding.Latin1.GetBytes("note!"));
        s.Write([0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x02, 0x58, 0x03, 0x20, 0x01, 0x01, 0x11, 0x00]);
        s.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        s.Write(Encoding.Latin1.GetBytes("scan"));
        s.Write([0xFF, 0xD9]);
        var clean = PhotoFiles.Clean("image/jpeg", s.ToArray());
        var text = Encoding.Latin1.GetString(clean.Data);
        Assert.Equal((800, 600), (clean.Width, clean.Height));
        Assert.Contains("JFIF", text);
        Assert.DoesNotContain("GPS", text);
        Assert.DoesNotContain("note", text);
        Assert.Contains("scan", text);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/jpeg")]
    [InlineData(null)]
    public void Other_types_and_mismatched_types_are_refused(string? type)
    {
        var png = Png(("IHDR", Ihdr(10, 10)), ("IDAT", [1]), ("IEND", []));
        var error = Assert.Throws<AppException>(() => PhotoFiles.Clean(type, png));
        Assert.Contains("validation.engagement.photo_type", error.Errors!.SelectMany(e => e.Value));
    }

    [Fact]
    public void Broken_files_and_oversized_pictures_are_refused()
    {
        Assert.Throws<AppException>(() => PhotoFiles.Clean("image/png", Png(("IHDR", Ihdr(10, 10)), ("IDAT", [1]))));
        Assert.Throws<AppException>(() => PhotoFiles.Clean("image/png", Png(("IDAT", [1]), ("IEND", []))));
        Assert.Throws<AppException>(() => PhotoFiles.Clean("image/png", Png(("IHDR", Ihdr(13000, 10)), ("IEND", []))));
        Assert.Throws<AppException>(() => PhotoFiles.Clean("image/png", []));
        Assert.Throws<AppException>(() => PhotoFiles.Clean("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
    }
}
