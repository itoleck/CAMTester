using Windows.Media.Capture.Frames;

namespace CAMTester.Models;

public sealed class CameraFormatInfo
{
    public MediaFrameFormat? FrameFormat { get; init; }
    public int? DirectShowCapabilityIndex { get; init; }
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required double FrameRate { get; init; }
    public required string Subtype { get; init; }

    public string DisplayLabel => FrameRate > 0.1
        ? $"{Width} × {Height}  @  {FrameRate:0.#} fps  ({CleanSubtype(Subtype)})"
        : $"{Width} × {Height}  ({CleanSubtype(Subtype)})";

    public string CompactLabel => $"{Width} × {Height}";

    public static string CleanSubtype(string subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype))
        {
            return "Unknown";
        }

        if (subtype.StartsWith('{') && subtype.EndsWith('}'))
        {
            return "Custom";
        }

        return subtype.ToUpperInvariant();
    }

    public override string ToString() => DisplayLabel;
}
