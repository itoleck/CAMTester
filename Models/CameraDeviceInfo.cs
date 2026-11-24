namespace CAMTester.Models;

public enum CameraBackend
{
    WinRT,
    DirectShow
}

public sealed class CameraDeviceInfo
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public CameraBackend Backend { get; init; } = CameraBackend.WinRT;
    public string? DirectShowMonikerName { get; init; }
    public string? SourceGroupId { get; init; }
    public string? SourceId { get; init; }
    public required string DisplayName { get; init; }
    public required string RawName { get; init; }
    public string SourceKind { get; init; } = "Video";
    public string Location { get; init; } = "External";
    public bool IsEnabled { get; init; } = true;
    public int Index { get; init; }

    public string Subtitle => string.IsNullOrWhiteSpace(Location)
        ? SourceKind
        : $"{SourceKind} • {Location}";
}
