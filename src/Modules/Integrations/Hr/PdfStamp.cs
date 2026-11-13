using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>The PDF stamp other modules use, through Integrations.Contracts.</summary>
public sealed class PdfStamp : IPdfStamp
{
    public byte[]? Apply(byte[] pdf, string text) => PdfWatermark.Apply(pdf, text);
}
