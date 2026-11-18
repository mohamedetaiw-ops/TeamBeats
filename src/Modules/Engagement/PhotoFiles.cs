using TeamBeats.Primitives;

namespace TeamBeats.Modules.Engagement;

/// <summary>A gallery photo ready to store: its type, its size in pixels and the bytes with metadata removed.</summary>
public sealed record CleanPhoto(string ContentType, int Width, int Height, byte[] Data);

/// <summary>
/// Gallery photo files (spec 023 US7). Only PNG and JPEG are taken, up to 10 MB and 12000 pixels on a side, with
/// metadata removed by <see cref="PhotoCleaner"/>. Errors use <c>validation.engagement.photo_type</c> and
/// <c>validation.engagement.photo_size</c>.
/// </summary>
public static class PhotoFiles
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxSide = 12000;

    public static CleanPhoto Clean(string? contentType, byte[] data)
    {
        var clean = PhotoCleaner.Clean(contentType, data, MaxBytes, MaxSide, "file", "validation.engagement.photo_size", "validation.engagement.photo_type");
        return new CleanPhoto(clean.ContentType, clean.Width, clean.Height, clean.Data);
    }
}
