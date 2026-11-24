using CAMTester.Models;
using Windows.Devices.Enumeration;
using Windows.Media.Capture.Frames;

namespace CAMTester.Services;

public sealed class CameraDiscoveryService : IDisposable
{
    private DeviceWatcher? _watcher;
    private bool _enumerationCompleted;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _enumerationCompleted = false;
        _watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Added += OnDeviceTopologyChanged;
        _watcher.Removed += OnDeviceTopologyChanged;
        _watcher.Updated += OnDeviceTopologyChanged;
        _watcher.Start();
    }

    public async Task<IReadOnlyList<CameraDeviceInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CameraDeviceInfo>();
        var seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNormalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEntryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Inspect MediaFrameSourceGroups first to capture multi-sensor streams (Color, Infrared, Depth)
        //    and richer enclosure/stream metadata.
        IReadOnlyList<MediaFrameSourceGroup> sourceGroups = [];
        try
        {
            sourceGroups = await MediaFrameSourceGroup.FindAllAsync().AsTask(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back smoothly to DeviceInformation enumeration below.
        }

        foreach (var group in sourceGroups)
        {
            var videoSources = group.SourceInfos
                .Where(s => s.MediaStreamType is Windows.Media.Capture.MediaStreamType.VideoPreview
                         or Windows.Media.Capture.MediaStreamType.VideoRecord)
                .ToList();

            if (videoSources.Count == 0)
            {
                continue;
            }

            var distinctSensorStreams = videoSources
                .GroupBy(s => new
                {
                    DeviceId = s.DeviceInformation?.Id ?? group.Id,
                    s.SourceKind
                })
                .Select(g => g.FirstOrDefault(s => s.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview)
                             ?? g.First())
                .ToList();

            foreach (var sourceInfo in distinctSensorStreams)
            {
                var devInfo = sourceInfo.DeviceInformation;
                if (devInfo is null || string.IsNullOrWhiteSpace(devInfo.Id))
                {
                    continue;
                }

                var entryKey = $"{devInfo.Id}::{sourceInfo.SourceKind}";
                if (!seenEntryKeys.Add(entryKey))
                {
                    continue;
                }

                seenDeviceIds.Add(devInfo.Id);
                seenNormalizedPaths.Add(NormalizeDevicePath(devInfo.Id));

                var rawName = !string.IsNullOrWhiteSpace(devInfo.Name)
                    ? devInfo.Name.Trim()
                    : !string.IsNullOrWhiteSpace(group.DisplayName)
                        ? group.DisplayName.Trim()
                        : "Unnamed Camera";

                results.Add(new CameraDeviceInfo
                {
                    Id = entryKey,
                    DeviceId = devInfo.Id,
                    Backend = CameraBackend.WinRT,
                    SourceGroupId = group.Id,
                    SourceId = sourceInfo.Id,
                    RawName = rawName,
                    DisplayName = rawName,
                    SourceKind = FormatSourceKind(sourceInfo.SourceKind),
                    Location = FormatLocation(devInfo.EnclosureLocation),
                    IsEnabled = devInfo.IsEnabled,
                    Index = results.Count + 1
                });
            }
        }

        // 2. Also enumerate DeviceClass.VideoCapture directly so hardware/FrameServer cameras
        //    that don't expose a MediaFrameSourceGroup are always included.
        var videoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);

        foreach (var device in videoDevices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || seenDeviceIds.Contains(device.Id))
            {
                continue;
            }

            seenDeviceIds.Add(device.Id);
            seenNormalizedPaths.Add(NormalizeDevicePath(device.Id));

            var rawName = string.IsNullOrWhiteSpace(device.Name) ? "Unnamed Camera" : device.Name.Trim();

            results.Add(new CameraDeviceInfo
            {
                Id = device.Id,
                DeviceId = device.Id,
                Backend = CameraBackend.WinRT,
                RawName = rawName,
                DisplayName = rawName,
                SourceKind = "Color",
                Location = FormatLocation(device.EnclosureLocation),
                IsEnabled = device.IsEnabled,
                Index = results.Count + 1
            });
        }

        // 3. Enumerate DirectShow video input filters (CLSID_VideoInputDeviceCategory) to pick up
        //    software virtual cameras (e.g. OBS Virtual Camera, DroidCam, ManyCam, NDI) and legacy capture filters
        //    that are not exposed through WinRT / Media Foundation on Windows 10.
        var directShowDevices = await Task.Run(DirectShowInterop.EnumerateVideoCaptureDevices, cancellationToken);

        foreach (var ds in directShowDevices)
        {
            var normalizedPath = NormalizeDevicePath(ds.DevicePath);

            // Skip hardware cameras already discovered via WinRT
            if (!string.IsNullOrEmpty(normalizedPath) && seenNormalizedPaths.Contains(normalizedPath))
            {
                continue;
            }

            var entryKey = $"dshow::{ds.MonikerDisplayName}";
            if (!seenEntryKeys.Add(entryKey))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedPath))
            {
                seenNormalizedPaths.Add(normalizedPath);
            }

            var isVirtual = string.IsNullOrEmpty(ds.DevicePath);
            var deviceId = !string.IsNullOrEmpty(ds.DevicePath)
                ? ds.DevicePath
                : ds.MonikerDisplayName;

            results.Add(new CameraDeviceInfo
            {
                Id = entryKey,
                DeviceId = deviceId,
                Backend = CameraBackend.DirectShow,
                DirectShowMonikerName = ds.MonikerDisplayName,
                RawName = ds.FriendlyName,
                DisplayName = ds.FriendlyName,
                SourceKind = isVirtual ? "Virtual" : "Color",
                Location = "DirectShow",
                IsEnabled = true,
                Index = results.Count + 1
            });
        }

        return DisambiguateNames(results);
    }

    private static string NormalizeDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().ToLowerInvariant();
        if (normalized.EndsWith(@"\global", StringComparison.Ordinal))
        {
            normalized = normalized[..^@"\global".Length];
        }

        // WinRT uses KSCATEGORY_VIDEO_CAMERA (#{e5323777-...}) while DirectShow uses
        // KSCATEGORY_CAPTURE (#{65e8773d-...}) for the exact same physical device instance.
        var interfaceGuidIndex = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceGuidIndex > 0)
        {
            normalized = normalized[..interfaceGuidIndex];
        }

        return normalized;
    }

    private static List<CameraDeviceInfo> DisambiguateNames(List<CameraDeviceInfo> cameras)
    {
        var nameCounts = cameras
            .GroupBy(c => c.RawName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var nameSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var final = new List<CameraDeviceInfo>(cameras.Count);

        for (var i = 0; i < cameras.Count; i++)
        {
            var cam = cameras[i];
            var displayName = cam.RawName;

            if (nameCounts[cam.RawName] > 1)
            {
                var sameNameSameKindCount = cameras.Count(c =>
                    string.Equals(c.RawName, cam.RawName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.SourceKind, cam.SourceKind, StringComparison.OrdinalIgnoreCase));

                if (sameNameSameKindCount == 1)
                {
                    displayName = $"{cam.RawName} ({cam.SourceKind})";
                }
                else
                {
                    var key = $"{cam.RawName}::{cam.SourceKind}";
                    nameSeen.TryGetValue(key, out var count);
                    count++;
                    nameSeen[key] = count;
                    displayName = $"{cam.RawName} #{count}";
                }
            }

            final.Add(new CameraDeviceInfo
            {
                Id = cam.Id,
                DeviceId = cam.DeviceId,
                Backend = cam.Backend,
                DirectShowMonikerName = cam.DirectShowMonikerName,
                SourceGroupId = cam.SourceGroupId,
                SourceId = cam.SourceId,
                RawName = cam.RawName,
                DisplayName = displayName,
                SourceKind = cam.SourceKind,
                Location = cam.Location,
                IsEnabled = cam.IsEnabled,
                Index = i + 1
            });
        }

        return final;
    }

    private static string FormatSourceKind(MediaFrameSourceKind kind) => kind switch
    {
        MediaFrameSourceKind.Color => "Color",
        MediaFrameSourceKind.Infrared => "Infrared",
        MediaFrameSourceKind.Depth => "Depth",
        MediaFrameSourceKind.Audio => "Audio",
        MediaFrameSourceKind.Image => "Image",
        _ => "Video"
    };

    private static string FormatLocation(EnclosureLocation? location) => location?.Panel switch
    {
        Panel.Front => "Front",
        Panel.Back => "Back",
        Panel.Top => "Top",
        Panel.Bottom => "Bottom",
        Panel.Left => "Left",
        Panel.Right => "Right",
        _ => "External"
    };

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _enumerationCompleted = true;
    }

    private void OnDeviceTopologyChanged(DeviceWatcher sender, object args)
    {
        if (_enumerationCompleted && !_disposed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Added -= OnDeviceTopologyChanged;
            _watcher.Removed -= OnDeviceTopologyChanged;
            _watcher.Updated -= OnDeviceTopologyChanged;

            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }

            _watcher = null;
        }
    }
}
