using System.Xml;
namespace TeamBeats.Primitives;

/// <summary>
/// Image rules shared by the tenant logo (spec 001 US2 scenario 4) and brand assets (spec 033 US4): PNG, JPEG or SVG up
/// to 1 MB. SVG must be static artwork: scripts, event handlers, foreign objects, external references and DTDs are
/// rejected rather than silently rewritten. Errors use the keys <c>validation.{field}.size</c> and <c>validation.{field}.type</c>.
/// </summary>
public static class ImageValidator
{
    public const int MaxBytes = 1024 * 1024;

    private static readonly HashSet<string> ForbiddenSvgElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "embed", "object", "audio", "video", "handler", "listener", "set", "animate",
    };

    public static string Validate(string field, string? contentType, byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
        {
            throw AppException.Validation(field, $"validation.{field}.size");
        }

        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        switch (type)
        {
            case "image/png" when data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }):
                return "png";
            case "image/jpeg" when data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF:
                return "jpg";
            case "image/svg+xml":
                ValidateSvg(field, data);
                return "svg";
            default:
                throw AppException.Validation(field, $"validation.{field}.type");
        }
    }

    private static void ValidateSvg(string field, byte[] data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        };

        try
        {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream, settings);
            var sawRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                {
                    Reject(field);
                }
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (!sawRoot)
                {
                    if (reader.LocalName != "svg")
                    {
                        Reject(field);
                    }
                    sawRoot = true;
                }

                if (ForbiddenSvgElements.Contains(reader.LocalName))
                {
                    Reject(field);
                }

                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        var name = reader.LocalName;
                        var value = reader.Value.Trim();
                        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                        {
                            Reject(field);
                        }
                        if ((name is "href" or "src") && !value.StartsWith('#'))
                        {
                            Reject(field);
                        }
                        if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("url(http", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                        {
                            Reject(field);
                        }
                    }
                    reader.MoveToElement();
                }

                if (reader.LocalName == "style")
                {
                    var css = reader.ReadElementContentAsString();
                    if (css.Contains("@import", StringComparison.OrdinalIgnoreCase) || css.Contains("url(", StringComparison.OrdinalIgnoreCase)
                        || css.Contains("expression(", StringComparison.OrdinalIgnoreCase))
                    {
                        Reject(field);
                    }
                }
            }

            if (!sawRoot)
            {
                Reject(field);
            }
        }
        catch (XmlException)
        {
            Reject(field);
        }
    }

    private static void Reject(string field) => throw AppException.Validation(field, $"validation.{field}.unsafe_svg");
}
