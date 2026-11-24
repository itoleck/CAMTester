using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CAMTester.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace CAMTester.Services;

public sealed record CameraSessionInfo(
    CameraDeviceInfo Camera,
    IReadOnlyList<CameraFormatInfo> SupportedFormats,
    CameraFormatInfo? ActiveFormat,
    bool IsSharedReadOnly,
    string SourceKind);

public sealed record FrameUpdate(
    int Width,
    int Height,
    int Stride,
    double Fps,
    string Subtype);

public sealed class CameraPreviewService : IAsyncDisposable
{
    private static readonly string?[] PreferredOutputSubtypes =
    [
        MediaEncodingSubtypes.Bgra8,
        MediaEncodingSubtypes.Nv12,
        MediaEncodingSubtypes.Yuy2,
        null // Camera's native subtype
    ];

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _frameLock = new();

    private MediaCapture? _mediaCapture;
    private MediaFrameSource? _activeSource;
    private MediaFrameReader? _frameReader;
    private DirectShowCameraEngine? _directShowEngine;
    private CameraDeviceInfo? _activeCamera;
    private bool _isSharedReadOnly;

    private byte[] _pixelBuffer = [];
    private byte[] _scratchBuffer = [];
    private int _frameWidth;
    private int _frameHeight;
    private string _currentSubtype = "BGRA8";

    private readonly Stopwatch _fpsWatch = new();
    private int _framesSinceLastSample;
    private double _currentFps;
    private int _frameProcessing;
    private bool _disposed;

    public event EventHandler<FrameUpdate>? FrameReady;
    public event EventHandler<string>? StreamError;

    public bool IsRunning => _frameReader is not null || (_directShowEngine?.IsRunning ?? false);

    public async Task<CameraSessionInfo> StartAsync(
        CameraDeviceInfo camera,
        CameraFormatInfo? requestedFormat = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (camera.Backend == CameraBackend.DirectShow)
            {
                return await StartDirectShowAsync(camera, requestedFormat, cancellationToken);
            }

            // Try CPU memory preference first (lowest overhead), then Auto (allows GPU/DXVA MFT decoding for MJPG, etc.)
            MediaCaptureMemoryPreference[] memoryPreferences =
            [
                MediaCaptureMemoryPreference.Cpu,
                MediaCaptureMemoryPreference.Auto
            ];

            Exception? lastException = null;

            foreach (var memPref in memoryPreferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await StopInternalAsync();

                try
                {
                    return await StartWithMemoryPreferenceAsync(camera, requestedFormat, memPref, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (FormatNotSupportedStartException ex)
                {
                    lastException = ex;
                    // Retry with next MediaCaptureMemoryPreference (e.g., Auto)
                }
            }

            await StopInternalAsync();
            throw lastException ?? new InvalidOperationException("Unable to start camera preview.");
        }
        catch
        {
            await StopInternalAsync();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<CameraSessionInfo> StartWithMemoryPreferenceAsync(
        CameraDeviceInfo camera,
        CameraFormatInfo? requestedFormat,
        MediaCaptureMemoryPreference memoryPreference,
        CancellationToken cancellationToken)
    {
        var (capture, isShared) = await InitializeMediaCaptureAsync(camera, memoryPreference, cancellationToken);
        _mediaCapture = capture;
        _isSharedReadOnly = isShared;
        _activeCamera = camera;

        capture.Failed += OnMediaCaptureFailed;

        var source = SelectFrameSource(capture, camera);
        if (source is null)
        {
            throw new InvalidOperationException(
                "This camera device did not expose a readable video frame source.");
        }

        _activeSource = source;

        var formats = EnumerateFormats(source);
        var formatsToTry = BuildFormatCandidates(formats, requestedFormat, isShared);

        MediaFrameReader? startedReader = null;
        CameraFormatInfo? activeFormat = null;
        var lastStatus = MediaFrameReaderStartStatus.OutputFormatNotSupported;

        foreach (var candidateFormat in formatsToTry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!isShared && candidateFormat is not null)
            {
                // Match against this MediaCapture's own SupportedFormats (important if we re-initialized with Auto).
                var matchingFormat = FindMatchingFormat(source.SupportedFormats, candidateFormat);
                if (matchingFormat is not null)
                {
                    try
                    {
                        await source.SetFormatAsync(matchingFormat).AsTask(cancellationToken);
                    }
                    catch
                    {
                        if (requestedFormat is not null)
                        {
                            // User explicitly requested this format; continue trying to open reader anyway.
                        }
                        else
                        {
                            continue;
                        }
                    }
                }
            }

            var currentSourceFormat = source.CurrentFormat;
            activeFormat = currentSourceFormat is not null
                ? ToFormatInfo(currentSourceFormat)
                : candidateFormat;

            _currentSubtype = activeFormat?.Subtype ?? currentSourceFormat?.Subtype ?? "BGRA8";
            _framesSinceLastSample = 0;
            _currentFps = 0;
            _fpsWatch.Restart();

            var (reader, status) = await TryCreateAndStartReaderAsync(capture, source, cancellationToken);
            if (reader is not null && status == MediaFrameReaderStartStatus.Success)
            {
                startedReader = reader;
                break;
            }

            lastStatus = status;

            // Don't keep trying other formats if the camera is unavailable or exclusively locked.
            if (status is MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                or MediaFrameReaderStartStatus.DeviceNotAvailable)
            {
                throw new InvalidOperationException(FormatStartStatusError(status));
            }
        }

        if (startedReader is null)
        {
            if (lastStatus == MediaFrameReaderStartStatus.OutputFormatNotSupported)
            {
                throw new FormatNotSupportedStartException(FormatStartStatusError(lastStatus));
            }

            throw new InvalidOperationException(FormatStartStatusError(lastStatus));
        }

        _frameReader = startedReader;

        return new CameraSessionInfo(
            Camera: camera,
            SupportedFormats: formats,
            ActiveFormat: activeFormat,
            IsSharedReadOnly: isShared,
            SourceKind: source.Info.SourceKind.ToString());
    }

    private async Task<(MediaFrameReader? Reader, MediaFrameReaderStartStatus Status)> TryCreateAndStartReaderAsync(
        MediaCapture capture,
        MediaFrameSource source,
        CancellationToken cancellationToken)
    {
        var lastStatus = MediaFrameReaderStartStatus.OutputFormatNotSupported;

        foreach (var outputSubtype in PreferredOutputSubtypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaFrameReader? reader = null;
            try
            {
                reader = outputSubtype is not null
                    ? await capture.CreateFrameReaderAsync(source, outputSubtype).AsTask(cancellationToken)
                    : await capture.CreateFrameReaderAsync(source).AsTask(cancellationToken);

                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                reader.FrameArrived += OnFrameArrived;

                var status = await reader.StartAsync().AsTask(cancellationToken);
                if (status == MediaFrameReaderStartStatus.Success)
                {
                    return (reader, status);
                }

                lastStatus = status;
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                reader = null;

                if (status is MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    or MediaFrameReaderStartStatus.DeviceNotAvailable)
                {
                    return (null, status);
                }
            }
            catch (OperationCanceledException)
            {
                if (reader is not null)
                {
                    reader.FrameArrived -= OnFrameArrived;
                    reader.Dispose();
                }
                throw;
            }
            catch
            {
                if (reader is not null)
                {
                    reader.FrameArrived -= OnFrameArrived;
                    reader.Dispose();
                }
            }
        }

        return (null, lastStatus);
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Invokes a callback while holding the frame buffer lock so WPF can copy straight into WriteableBitmap without extra allocation.
    /// </summary>
    public bool WithLatestFrame(Action<byte[], int, int, int> copyAction)
    {
        lock (_frameLock)
        {
            if (_frameWidth <= 0 || _frameHeight <= 0 || _pixelBuffer.Length < _frameWidth * _frameHeight * 4)
            {
                return false;
            }

            copyAction(_pixelBuffer, _frameWidth, _frameHeight, _frameWidth * 4);
            return true;
        }
    }

    private async Task<(MediaCapture Capture, bool IsSharedReadOnly)> InitializeMediaCaptureAsync(
        CameraDeviceInfo camera,
        MediaCaptureMemoryPreference memoryPreference,
        CancellationToken cancellationToken)
    {
        // Try ExclusiveControl first so we can start idle cameras and switch resolutions.
        try
        {
            var exclusiveCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = camera.DeviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = memoryPreference,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            };

            await exclusiveCapture.InitializeAsync(settings).AsTask(cancellationToken);
            return (exclusiveCapture, false);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exclusiveEx)
        {
            // If another app holds exclusive access (or a virtual camera requires shared mode),
            // retry with SharedReadOnly so the user can still preview the feed.
            try
            {
                var sharedCapture = new MediaCapture();
                var sharedSettings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = camera.DeviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = memoryPreference,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly
                };

                await sharedCapture.InitializeAsync(sharedSettings).AsTask(cancellationToken);
                return (sharedCapture, true);
            }
            catch
            {
                throw exclusiveEx;
            }
        }
    }

    private static MediaFrameSource? SelectFrameSource(MediaCapture capture, CameraDeviceInfo camera)
    {
        if (capture.FrameSources.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(camera.SourceId) &&
            capture.FrameSources.TryGetValue(camera.SourceId, out var exactSource))
        {
            return exactSource;
        }

        var videoSources = capture.FrameSources.Values
            .Where(s => s.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
            .ToList();

        if (videoSources.Count == 0)
        {
            return capture.FrameSources.Values.FirstOrDefault();
        }

        var kindMatch = videoSources.FirstOrDefault(s =>
            string.Equals(s.Info.SourceKind.ToString(), camera.SourceKind, StringComparison.OrdinalIgnoreCase) &&
            s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? videoSources.FirstOrDefault(s =>
                string.Equals(s.Info.SourceKind.ToString(), camera.SourceKind, StringComparison.OrdinalIgnoreCase));

        if (kindMatch is not null)
        {
            return kindMatch;
        }

        return videoSources.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? videoSources.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? videoSources.First();
    }

    private static List<CameraFormatInfo> EnumerateFormats(MediaFrameSource source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formats = new List<CameraFormatInfo>();

        foreach (var fmt in source.SupportedFormats)
        {
            var info = ToFormatInfo(fmt);
            if (info is null || info.Width == 0 || info.Height == 0)
            {
                continue;
            }

            var key = $"{info.Width}x{info.Height}@{Math.Round(info.FrameRate, 1)}:{info.Subtype}";
            if (seen.Add(key))
            {
                formats.Add(info);
            }
        }

        return formats
            .OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.FrameRate)
            .ThenBy(f => GetSubtypePriority(f.Subtype))
            .ThenBy(f => f.Subtype, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CameraFormatInfo?> BuildFormatCandidates(
        IReadOnlyList<CameraFormatInfo> formats,
        CameraFormatInfo? requestedFormat,
        bool isSharedReadOnly)
    {
        if (isSharedReadOnly)
        {
            return [null];
        }

        if (requestedFormat is not null)
        {
            return [requestedFormat];
        }

        var candidates = new List<CameraFormatInfo?>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(CameraFormatInfo? fmt)
        {
            if (fmt is null)
            {
                return;
            }

            var key = $"{fmt.Width}x{fmt.Height}@{Math.Round(fmt.FrameRate, 1)}:{fmt.Subtype}";
            if (added.Add(key))
            {
                candidates.Add(fmt);
            }
        }

        // 1. Best uncompressed smooth format (most reliable on USB UVC cameras like LifeCam Studio)
        var uncompressed = formats.Where(f => !IsCompressedSubtype(f.Subtype)).ToList();
        AddIfNew(ChoosePreferredFormat(uncompressed));

        // 2. Best overall format (may be 1080p MJPG)
        AddIfNew(ChoosePreferredFormat(formats));

        // 3. Any other smooth uncompressed format (e.g., 720p/480p YUY2/NV12)
        foreach (var fmt in uncompressed.Where(f => f.FrameRate >= 15).Take(3))
        {
            AddIfNew(fmt);
        }

        // 4. Camera's own default format without calling SetFormatAsync
        candidates.Add(null);

        return candidates;
    }

    private static MediaFrameFormat? FindMatchingFormat(
        IReadOnlyList<MediaFrameFormat> supportedFormats,
        CameraFormatInfo target)
    {
        foreach (var fmt in supportedFormats)
        {
            var vf = fmt.VideoFormat;
            if (vf is null)
            {
                continue;
            }

            double fps = fmt.FrameRate.Denominator > 0
                ? (double)fmt.FrameRate.Numerator / fmt.FrameRate.Denominator
                : 0;

            if (vf.Width == target.Width &&
                vf.Height == target.Height &&
                Math.Abs(fps - target.FrameRate) < 0.25 &&
                string.Equals(fmt.Subtype, target.Subtype, StringComparison.OrdinalIgnoreCase))
            {
                return fmt;
            }
        }

        return null;
    }

    private static CameraFormatInfo? ToFormatInfo(MediaFrameFormat fmt)
    {
        var videoFormat = fmt.VideoFormat;
        if (videoFormat is null)
        {
            return null;
        }

        double fps = 0;
        if (fmt.FrameRate.Denominator > 0)
        {
            fps = (double)fmt.FrameRate.Numerator / fmt.FrameRate.Denominator;
        }

        return new CameraFormatInfo
        {
            FrameFormat = fmt,
            Width = videoFormat.Width,
            Height = videoFormat.Height,
            FrameRate = fps,
            Subtype = fmt.Subtype
        };
    }

    private static CameraFormatInfo? ChoosePreferredFormat(IReadOnlyList<CameraFormatInfo> formats)
    {
        if (formats.Count == 0)
        {
            return null;
        }

        var smooth = formats.Where(f => f.FrameRate >= 24).ToList();
        var candidates = smooth.Count > 0 ? smooth : formats;

        var preferred1080 = candidates.FirstOrDefault(f => f.Width == 1920 && f.Height == 1080);
        if (preferred1080 is not null)
        {
            return preferred1080;
        }

        var preferred720 = candidates.FirstOrDefault(f => f.Width == 1280 && f.Height == 720);
        if (preferred720 is not null)
        {
            return preferred720;
        }

        return candidates
            .Where(f => f.Width <= 1920)
            .OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.FrameRate)
            .ThenBy(f => GetSubtypePriority(f.Subtype))
            .FirstOrDefault()
            ?? candidates[0];
    }

    private static int GetSubtypePriority(string subtype)
    {
        var normalized = subtype.ToUpperInvariant();
        return normalized switch
        {
            "BGRA8" or "ARGB32" or "RGB32" => 0,
            "NV12" => 1,
            "YUY2" => 2,
            "RGB24" => 3,
            "MJPG" => 4,
            _ => 5
        };
    }

    private static bool IsCompressedSubtype(string subtype)
    {
        var normalized = subtype.ToUpperInvariant();
        return normalized is "MJPG" or "H264" or "HEVC" or "H265" or "JPEG";
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref _frameProcessing, 1) == 1)
        {
            return;
        }

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var videoFrame = frame?.VideoMediaFrame;
            if (videoFrame is null)
            {
                return;
            }

            SoftwareBitmap? ownedBitmap = null;
            var sourceBitmap = videoFrame.SoftwareBitmap;
            if (sourceBitmap is null && videoFrame.Direct3DSurface is not null)
            {
                ownedBitmap = SoftwareBitmap
                    .CreateCopyFromSurfaceAsync(videoFrame.Direct3DSurface)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                sourceBitmap = ownedBitmap;
            }

            using var disposeSource = ownedBitmap ?? sourceBitmap;
            if (sourceBitmap is null)
            {
                return;
            }

            var width = sourceBitmap.PixelWidth;
            var height = sourceBitmap.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var requiredBytes = width * height * 4;

            lock (_frameLock)
            {
                if (_pixelBuffer.Length < requiredBytes)
                {
                    _pixelBuffer = new byte[requiredBytes];
                }

                CopySoftwareBitmapToBgra8(sourceBitmap, _pixelBuffer, width, height);

                _frameWidth = width;
                _frameHeight = height;
            }

            _framesSinceLastSample++;
            var elapsedMs = _fpsWatch.Elapsed.TotalMilliseconds;
            if (elapsedMs >= 500)
            {
                _currentFps = _framesSinceLastSample / (elapsedMs / 1000.0);
                _framesSinceLastSample = 0;
                _fpsWatch.Restart();
            }

            var subtype = frame?.Format?.Subtype ?? _currentSubtype;
            FrameReady?.Invoke(this, new FrameUpdate(width, height, width * 4, _currentFps, subtype));
        }
        catch
        {
            // Ignore transient malformed frames during camera stop/reconfigure.
        }
        finally
        {
            Interlocked.Exchange(ref _frameProcessing, 0);
        }
    }

    private void CopySoftwareBitmapToBgra8(SoftwareBitmap sourceBitmap, byte[] destinationBgra, int width, int height)
    {
        var requiredBytes = width * height * 4;

        switch (sourceBitmap.BitmapPixelFormat)
        {
            case BitmapPixelFormat.Bgra8:
                sourceBitmap.CopyToBuffer(destinationBgra.AsBuffer(0, requiredBytes));
                return;

            case BitmapPixelFormat.Gray16:
                ConvertGray16ToBgra8(sourceBitmap, destinationBgra, width, height);
                return;

            default:
                try
                {
                    using var converted = SoftwareBitmap.Convert(
                        sourceBitmap,
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore);
                    converted.CopyToBuffer(destinationBgra.AsBuffer(0, requiredBytes));
                    return;
                }
                catch
                {
                    if (sourceBitmap.BitmapPixelFormat == BitmapPixelFormat.Yuy2)
                    {
                        ConvertYuy2ToBgra8(sourceBitmap, destinationBgra, width, height);
                        return;
                    }

                    if (sourceBitmap.BitmapPixelFormat == BitmapPixelFormat.Nv12)
                    {
                        ConvertNv12ToBgra8(sourceBitmap, destinationBgra, width, height);
                        return;
                    }

                    throw;
                }
        }
    }

    private void ConvertGray16ToBgra8(SoftwareBitmap gray16Bitmap, byte[] destinationBgra, int width, int height)
    {
        var requiredBytes = width * height * 2;
        EnsureScratchSize(requiredBytes);

        gray16Bitmap.CopyToBuffer(_scratchBuffer.AsBuffer(0, requiredBytes));

        var shorts = MemoryMarshal.Cast<byte, ushort>(_scratchBuffer.AsSpan(0, requiredBytes));
        ushort maxVal = 1;
        for (var i = 0; i < shorts.Length; i++)
        {
            if (shorts[i] > maxVal)
            {
                maxVal = shorts[i];
            }
        }

        var scale = 255.0 / maxVal;
        var dst = 0;
        for (var i = 0; i < shorts.Length; i++)
        {
            var intensity = (byte)Math.Clamp((int)(shorts[i] * scale), 0, 255);
            destinationBgra[dst++] = intensity;
            destinationBgra[dst++] = intensity;
            destinationBgra[dst++] = intensity;
            destinationBgra[dst++] = 255;
        }
    }

    private Task<CameraSessionInfo> StartDirectShowAsync(
        CameraDeviceInfo camera,
        CameraFormatInfo? requestedFormat,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var engine = new DirectShowCameraEngine(OnDirectShowRawFrame);
            _directShowEngine = engine;
            _activeCamera = camera;
            _isSharedReadOnly = false;
            _framesSinceLastSample = 0;
            _currentFps = 0;
            _fpsWatch.Restart();

            var session = engine.Start(camera, requestedFormat);
            _currentSubtype = session.ActiveFormat?.Subtype ?? "RGB32";
            return session;
        }, cancellationToken);
    }

    private void OnDirectShowRawFrame(
        IntPtr bufferPtr,
        int bufferLen,
        int width,
        int height,
        Guid subType,
        bool isBottomUp)
    {
        if (Interlocked.Exchange(ref _frameProcessing, 1) == 1)
        {
            return;
        }

        try
        {
            var requiredBytes = width * height * 4;

            lock (_frameLock)
            {
                if (_pixelBuffer.Length < requiredBytes)
                {
                    _pixelBuffer = new byte[requiredBytes];
                }

                if (subType == DirectShowInterop.MediaSubTypeRgb32 || subType == DirectShowInterop.MediaSubTypeArgb32)
                {
                    var rowBytes = width * 4;
                    if (bufferLen < rowBytes * height)
                    {
                        return;
                    }

                    if (isBottomUp)
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var srcRowPtr = IntPtr.Add(bufferPtr, (height - 1 - y) * rowBytes);
                            Marshal.Copy(srcRowPtr, _pixelBuffer, y * rowBytes, rowBytes);
                        }
                    }
                    else
                    {
                        Marshal.Copy(bufferPtr, _pixelBuffer, 0, requiredBytes);
                    }
                }
                else if (subType == DirectShowInterop.MediaSubTypeRgb24)
                {
                    var srcStride = ((width * 3) + 3) & ~3;
                    if (bufferLen < srcStride * height)
                    {
                        return;
                    }

                    EnsureScratchSize(srcStride * height);
                    Marshal.Copy(bufferPtr, _scratchBuffer, 0, srcStride * height);

                    var dst = 0;
                    for (var y = 0; y < height; y++)
                    {
                        var srcY = isBottomUp ? (height - 1 - y) : y;
                        var srcRow = srcY * srcStride;
                        for (var x = 0; x < width; x++)
                        {
                            var s = srcRow + (x * 3);
                            _pixelBuffer[dst++] = _scratchBuffer[s];
                            _pixelBuffer[dst++] = _scratchBuffer[s + 1];
                            _pixelBuffer[dst++] = _scratchBuffer[s + 2];
                            _pixelBuffer[dst++] = 255;
                        }
                    }
                }
                else if (subType == DirectShowInterop.MediaSubTypeYuy2)
                {
                    var expected = width * height * 2;
                    if (bufferLen < expected)
                    {
                        return;
                    }

                    EnsureScratchSize(expected);
                    Marshal.Copy(bufferPtr, _scratchBuffer, 0, expected);
                    ConvertYuy2BufferToBgra8(_scratchBuffer, _pixelBuffer, width, height);
                }
                else if (subType == DirectShowInterop.MediaSubTypeNv12)
                {
                    var expected = width * height + (width * height / 2);
                    if (bufferLen < expected)
                    {
                        return;
                    }

                    EnsureScratchSize(expected);
                    Marshal.Copy(bufferPtr, _scratchBuffer, 0, expected);
                    ConvertNv12BufferToBgra8(_scratchBuffer, _pixelBuffer, width, height);
                }
                else if (subType == DirectShowInterop.MediaSubTypeI420 || subType == DirectShowInterop.MediaSubTypeIyuv)
                {
                    var expected = width * height + (width * height / 2);
                    if (bufferLen < expected)
                    {
                        return;
                    }

                    EnsureScratchSize(expected);
                    Marshal.Copy(bufferPtr, _scratchBuffer, 0, expected);
                    ConvertI420BufferToBgra8(_scratchBuffer, _pixelBuffer, width, height);
                }
                else
                {
                    return;
                }

                _frameWidth = width;
                _frameHeight = height;
            }

            _framesSinceLastSample++;
            var elapsedMs = _fpsWatch.Elapsed.TotalMilliseconds;
            if (elapsedMs >= 500)
            {
                _currentFps = _framesSinceLastSample / (elapsedMs / 1000.0);
                _framesSinceLastSample = 0;
                _fpsWatch.Restart();
            }

            var subtypeName = DirectShowInterop.FormatSubTypeName(subType);
            FrameReady?.Invoke(this, new FrameUpdate(width, height, width * 4, _currentFps, subtypeName));
        }
        catch
        {
            // Ignore transient frames during stop
        }
        finally
        {
            Interlocked.Exchange(ref _frameProcessing, 0);
        }
    }

    private void ConvertYuy2ToBgra8(SoftwareBitmap yuy2Bitmap, byte[] destinationBgra, int width, int height)
    {
        var requiredBytes = width * height * 2;
        EnsureScratchSize(requiredBytes);

        yuy2Bitmap.CopyToBuffer(_scratchBuffer.AsBuffer(0, requiredBytes));
        ConvertYuy2BufferToBgra8(_scratchBuffer, destinationBgra, width, height);
    }

    private static void ConvertYuy2BufferToBgra8(byte[] yuy2Bytes, byte[] destinationBgra, int width, int height)
    {
        var src = 0;
        var dst = 0;
        var pixelPairs = (width * height) / 2;

        for (var i = 0; i < pixelPairs; i++)
        {
            int y0 = yuy2Bytes[src++];
            int u = yuy2Bytes[src++] - 128;
            int y1 = yuy2Bytes[src++];
            int v = yuy2Bytes[src++] - 128;

            WriteYuvPixelToBgra(y0, u, v, destinationBgra, ref dst);
            WriteYuvPixelToBgra(y1, u, v, destinationBgra, ref dst);
        }
    }

    private void ConvertNv12ToBgra8(SoftwareBitmap nv12Bitmap, byte[] destinationBgra, int width, int height)
    {
        var yPlaneSize = width * height;
        var requiredBytes = yPlaneSize + (yPlaneSize / 2);
        EnsureScratchSize(requiredBytes);

        nv12Bitmap.CopyToBuffer(_scratchBuffer.AsBuffer(0, requiredBytes));
        ConvertNv12BufferToBgra8(_scratchBuffer, destinationBgra, width, height);
    }

    private static void ConvertNv12BufferToBgra8(byte[] nv12Bytes, byte[] destinationBgra, int width, int height)
    {
        var yPlaneSize = width * height;
        var dstStride = width * 4;

        Parallel.For(0, height, row =>
        {
            var yRowOffset = row * width;
            var uvRowOffset = yPlaneSize + ((row >> 1) * width);
            var dst = row * dstStride;

            for (var col = 0; col < width; col++)
            {
                int y = nv12Bytes[yRowOffset + col];
                var uvIndex = uvRowOffset + (col & ~1);
                int u = nv12Bytes[uvIndex] - 128;
                int v = nv12Bytes[uvIndex + 1] - 128;

                WriteYuvPixelToBgra(y, u, v, destinationBgra, ref dst);
            }
        });
    }

    private static void ConvertI420BufferToBgra8(byte[] i420Bytes, byte[] destinationBgra, int width, int height)
    {
        var yPlaneSize = width * height;
        var chromaWidth = width >> 1;
        var chromaHeight = height >> 1;
        var uPlaneStart = yPlaneSize;
        var vPlaneStart = yPlaneSize + (chromaWidth * chromaHeight);
        var dstStride = width * 4;

        Parallel.For(0, height, row =>
        {
            var yRow = row * width;
            var chromaRow = (row >> 1) * chromaWidth;
            var dst = row * dstStride;

            for (var col = 0; col < width; col++)
            {
                int y = i420Bytes[yRow + col];
                var chromaIndex = chromaRow + (col >> 1);
                int u = i420Bytes[uPlaneStart + chromaIndex] - 128;
                int v = i420Bytes[vPlaneStart + chromaIndex] - 128;

                WriteYuvPixelToBgra(y, u, v, destinationBgra, ref dst);
            }
        });
    }

    private static void WriteYuvPixelToBgra(int y, int u, int v, byte[] dst, ref int offset)
    {
        var c = Math.Max(0, y - 16) * 298;
        var r = (c + 409 * v + 128) >> 8;
        var g = (c - 100 * u - 208 * v + 128) >> 8;
        var b = (c + 516 * u + 128) >> 8;

        dst[offset++] = (byte)Math.Clamp(b, 0, 255);
        dst[offset++] = (byte)Math.Clamp(g, 0, 255);
        dst[offset++] = (byte)Math.Clamp(r, 0, 255);
        dst[offset++] = 255;
    }

    private void EnsureScratchSize(int requiredBytes)
    {
        if (_scratchBuffer.Length < requiredBytes)
        {
            _scratchBuffer = new byte[requiredBytes];
        }
    }

    private void OnMediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
    {
        var message = string.IsNullOrWhiteSpace(errorEventArgs.Message)
            ? $"Camera stream failed (0x{errorEventArgs.Code:X8})."
            : errorEventArgs.Message;

        StreamError?.Invoke(this, message);
    }

    private async Task StopInternalAsync()
    {
        if (_directShowEngine is not null)
        {
            var engine = _directShowEngine;
            _directShowEngine = null;
            await Task.Run(engine.Dispose);
        }

        if (_frameReader is not null)
        {
            _frameReader.FrameArrived -= OnFrameArrived;
            try
            {
                await _frameReader.StopAsync();
            }
            catch
            {
                // Ignore errors while stopping already-faulted readers.
            }

            _frameReader.Dispose();
            _frameReader = null;
        }

        _activeSource = null;
        _activeCamera = null;
        _isSharedReadOnly = false;

        if (_mediaCapture is not null)
        {
            _mediaCapture.Failed -= OnMediaCaptureFailed;
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }

        lock (_frameLock)
        {
            _frameWidth = 0;
            _frameHeight = 0;
        }

        _fpsWatch.Reset();
        _framesSinceLastSample = 0;
        _currentFps = 0;
    }

    private static string FormatStartStatusError(MediaFrameReaderStartStatus status) => status switch
    {
        MediaFrameReaderStartStatus.ExclusiveControlNotAvailable =>
            "Camera is currently in exclusive use by another application.",
        MediaFrameReaderStartStatus.DeviceNotAvailable =>
            "Camera device is not available or was disconnected.",
        MediaFrameReaderStartStatus.OutputFormatNotSupported =>
            "The selected video format is not supported by this camera.",
        _ => $"Unable to start camera preview ({status})."
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _lifecycleLock.Dispose();
    }

    private sealed class FormatNotSupportedStartException(string message) : InvalidOperationException(message);
}
