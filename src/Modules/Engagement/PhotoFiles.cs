using System.Buffers.Binary;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Engagement;

/// <summary>A gallery photo ready to store: its type, its size in pixels and the bytes with metadata removed.</summary>
public sealed record CleanPhoto(string ContentType, int Width, int Height, byte[] Data);

/// <summary>
/// Gallery photo files (spec 023 US7). Only PNG and JPEG are taken, up to 10 MB and 12000 pixels on a side. Metadata
/// that can say where and when a photo was taken (EXIF, XMP, comments and text chunks, which may hold GPS positions) is
/// removed before the file is stored; the picture data itself is copied unchanged. Errors use
/// <c>validation.engagement.photo_type</c> and <c>validation.engagement.photo_size</c>.
/// </summary>
public static class PhotoFiles
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxSide = 12000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG chunks kept: the picture and how to show its colors. Text, time and EXIF chunks are dropped.</summary>
    private static readonly IReadOnlySet<string> KeptPngChunks = new HashSet<string>(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "pHYs", "bKGD",
    };

    public static CleanPhoto Clean(string? contentType, byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
        {
            throw AppException.Validation("file", "validation.engagement.photo_size");
        }
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        var photo = type switch
        {
            "image/png" when data.AsSpan().StartsWith(PngSignature) => Png(data),
            "image/jpeg" when data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF => Jpeg(data),
            _ => null,
        };
        if (photo is null)
        {
            throw AppException.Validation("file", "validation.engagement.photo_type");
        }
        if (photo.Width is < 1 or > MaxSide || photo.Height is < 1 or > MaxSide)
        {
            throw AppException.Validation("file", "validation.engagement.photo_size");
        }
        return photo;
    }

    private static CleanPhoto? Png(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(PngSignature);
        var at = PngSignature.Length;
        int? width = null, height = null;
        var ended = false;
        while (at + 12 <= data.Length && !ended)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
            if (length > int.MaxValue || at + 12 + (long)length > data.Length)
            {
                return null;
            }
            var name = System.Text.Encoding.ASCII.GetString(data, at + 4, 4);
            var total = 12 + (int)length;
            if (name == "IHDR")
            {
                if (width is not null || length < 8)
                {
                    return null;
                }
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 8, 4)), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 12, 4)), int.MaxValue);
            }
            else if (width is null)
            {
                return null;
            }
            if (KeptPngChunks.Contains(name))
            {
                output.Write(data, at, total);
            }
            ended = name == "IEND";
            at += total;
        }
        return ended && width is { } w && height is { } h ? new CleanPhoto("image/png", w, h, output.ToArray()) : null;
    }

    private static CleanPhoto? Jpeg(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var at = 2;
        int? width = null, height = null;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
            {
                return null;
            }
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker == 0xD9)
            {
                output.Write(data, at, 2);
                break;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2, 2));
            if (length < 2 || at + 2 + length > data.Length)
            {
                return null;
            }
            // Start of frame (except DHT C4, JPG C8 and DAC CC): height and width follow the precision byte.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 7, 2));
            }
            if (marker == 0xDA)
            {
                // Start of scan: the compressed picture follows to the end of the file, copied as it is.
                output.Write(data, at, data.Length - at);
                break;
            }
            // APP1 to APP15 (EXIF, XMP, maker notes and the like) and comments are dropped; APP0 (JFIF) and tables are kept.
            var dropped = marker is >= 0xE1 and <= 0xEF or 0xFE;
            if (!dropped)
            {
                output.Write(data, at, 2 + length);
            }
            at += 2 + length;
        }
        return width is { } w && height is { } h ? new CleanPhoto("image/jpeg", w, h, output.ToArray()) : null;
    }
}
