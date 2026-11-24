using System.Runtime.InteropServices;
using CAMTester.Models;
using static CAMTester.Services.DirectShowInterop;

namespace CAMTester.Services;

/// <summary>
/// Captures live video frames from DirectShow camera sources (such as OBS Virtual Camera and legacy/virtual filters)
/// using a FilterGraph + SampleGrabber + NullRenderer pipeline.
/// </summary>
internal sealed class DirectShowCameraEngine : ISampleGrabberCB, IDisposable
{
    private readonly Action<IntPtr, int, int, int, Guid, bool> _onRawFrame;

    private IGraphBuilder? _graph;
    private ICaptureGraphBuilder2? _captureBuilder;
    private IBaseFilter? _sourceFilter;
    private IBaseFilter? _sampleGrabberFilter;
    private IBaseFilter? _nullRenderer;
    private ISampleGrabber? _sampleGrabber;
    private IMediaControl? _mediaControl;

    private int _connectedWidth;
    private int _connectedHeight;
    private Guid _connectedSubType;
    private bool _isBottomUp;
    private volatile bool _isRunning;
    private bool _disposed;

    public DirectShowCameraEngine(Action<IntPtr, int, int, int, Guid, bool> onRawFrame)
    {
        _onRawFrame = onRawFrame;
    }

    public bool IsRunning => _isRunning;

    public CameraSessionInfo Start(CameraDeviceInfo camera, CameraFormatInfo? requestedFormat)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(camera.DirectShowMonikerName))
        {
            throw new InvalidOperationException("Missing DirectShow moniker for this camera.");
        }

        try
        {
            _sourceFilter = BindFilterByMonikerDisplayName(camera.DirectShowMonikerName)
                ?? throw new InvalidOperationException("DirectShow camera filter could not be opened or is no longer registered.");

            _graph = (IGraphBuilder)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFilterGraph)!)!;
            _captureBuilder = (ICaptureGraphBuilder2)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidCaptureGraphBuilder2)!)!;

            ThrowIfFailed(_captureBuilder.SetFiltergraph(_graph), "SetFiltergraph");
            ThrowIfFailed(_graph.AddFilter(_sourceFilter, "VideoCapture"), "AddFilter(VideoCapture)");

            var formats = EnumerateAndOptionallyConfigureFormat(_captureBuilder, _sourceFilter, requestedFormat, out var selectedFormat);

            // Prefer direct native connection (Guid.Empty) when the source outputs a format we decode directly
            // (NV12/YUY2/I420/RGB32/RGB24) — this avoids legacy DirectShow Color Space Converter stalls on
            // virtual cameras like OBS Virtual Camera. Fall back to RGB32/RGB24 for compressed/other subtypes.
            Guid[] grabberSubTypes =
            [
                Guid.Empty,
                MediaSubTypeRgb32,
                MediaSubTypeRgb24
            ];

            var connected = false;
            int lastHr = 0;

            foreach (var subType in grabberSubTypes)
            {
                if (!TryBuildGrabberChain(subType, out lastHr) || _sampleGrabber is null)
                {
                    continue;
                }

                // If we connected with Guid.Empty, verify we know how to convert the negotiated subtype.
                if (subType == Guid.Empty)
                {
                    var probeMt = new AMMediaType();
                    try
                    {
                        if (_sampleGrabber.GetConnectedMediaType(probeMt) >= 0 && IsSupportedNativeSubType(probeMt.SubType))
                        {
                            connected = true;
                            break;
                        }
                    }
                    finally
                    {
                        FreeMediaType(probeMt);
                    }

                    // Unsupported raw subtype (e.g. MJPG) — tear down and let DirectShow decode to RGB32/RGB24.
                    CleanupGrabberAndRenderer();
                    continue;
                }

                connected = true;
                break;
            }

            if (!connected || _sampleGrabber is null)
            {
                throw new InvalidOperationException(
                    $"Unable to connect video preview pipeline for '{camera.DisplayName}' (0x{lastHr:X8}). " +
                    "Make sure the virtual camera output is started.");
            }

            // Disable graph reference clock synchronization so NullRenderer delivers live frames immediately
            // without stalling on virtual camera timestamp offsets (e.g. OBS Virtual Camera).
            if (_graph is IMediaFilter mediaFilter)
            {
                mediaFilter.SetSyncSource(IntPtr.Zero);
            }

            // Read the negotiated output dimensions and pixel format from SampleGrabber.
            var connectedMt = new AMMediaType();
            try
            {
                ThrowIfFailed(_sampleGrabber.GetConnectedMediaType(connectedMt), "GetConnectedMediaType");
                ExtractConnectedVideoInfo(connectedMt, out _connectedWidth, out _connectedHeight, out var fps, out _isBottomUp);
                _connectedSubType = connectedMt.SubType;

                if (selectedFormat is null && _connectedWidth > 0 && _connectedHeight > 0)
                {
                    selectedFormat = new CameraFormatInfo
                    {
                        Width = (uint)_connectedWidth,
                        Height = (uint)_connectedHeight,
                        FrameRate = fps,
                        Subtype = FormatSubTypeName(_connectedSubType)
                    };
                }
            }
            finally
            {
                FreeMediaType(connectedMt);
            }

            ThrowIfFailed(_sampleGrabber.SetBufferSamples(false), "SetBufferSamples");
            ThrowIfFailed(_sampleGrabber.SetOneShot(false), "SetOneShot");
            ThrowIfFailed(_sampleGrabber.SetCallback(this, 1), "SetCallback");

            _mediaControl = (IMediaControl)_graph;
            _isRunning = true;

            var runHr = _mediaControl.Run();
            if (runHr < 0)
            {
                _isRunning = false;
                throw new InvalidOperationException($"Failed to start DirectShow camera stream (0x{runHr:X8}).");
            }

            return new CameraSessionInfo(
                Camera: camera,
                SupportedFormats: formats,
                ActiveFormat: selectedFormat,
                IsSharedReadOnly: false,
                SourceKind: camera.SourceKind);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private bool TryBuildGrabberChain(Guid desiredSubType, out int hr)
    {
        CleanupGrabberAndRenderer();

        _sampleGrabberFilter = (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidSampleGrabber)!)!;
        _sampleGrabber = (ISampleGrabber)_sampleGrabberFilter;
        _nullRenderer = (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidNullRenderer)!)!;

        var mt = new AMMediaType
        {
            MajorType = MediaTypeVideo,
            SubType = desiredSubType,
            FormatType = Guid.Empty
        };

        hr = _sampleGrabber.SetMediaType(mt);
        if (hr < 0)
        {
            CleanupGrabberAndRenderer();
            return false;
        }

        hr = _graph!.AddFilter(_sampleGrabberFilter, "SampleGrabber");
        if (hr < 0)
        {
            CleanupGrabberAndRenderer();
            return false;
        }

        hr = _graph.AddFilter(_nullRenderer, "NullRenderer");
        if (hr < 0)
        {
            CleanupGrabberAndRenderer();
            return false;
        }

        var catPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        var typePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(PinCategoryCapture, catPtr, false);
            Marshal.StructureToPtr(MediaTypeVideo, typePtr, false);

            // 1. Try standard Capture pin
            hr = _captureBuilder!.RenderStream(catPtr, typePtr, _sourceFilter!, _sampleGrabberFilter, _nullRenderer);
            if (hr >= 0)
            {
                return true;
            }

            // 2. Fall back to Preview pin
            Marshal.StructureToPtr(PinCategoryPreview, catPtr, false);
            hr = _captureBuilder.RenderStream(catPtr, typePtr, _sourceFilter!, _sampleGrabberFilter, _nullRenderer);
            if (hr >= 0)
            {
                return true;
            }

            // 3. Fall back to any video output pin (common for software virtual cameras)
            hr = _captureBuilder.RenderStream(IntPtr.Zero, typePtr, _sourceFilter!, _sampleGrabberFilter, _nullRenderer);
            if (hr >= 0)
            {
                return true;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(catPtr);
            Marshal.FreeCoTaskMem(typePtr);
        }

        CleanupGrabberAndRenderer();
        return false;
    }

    private static List<CameraFormatInfo> EnumerateAndOptionallyConfigureFormat(
        ICaptureGraphBuilder2 captureBuilder,
        IBaseFilter sourceFilter,
        CameraFormatInfo? requestedFormat,
        out CameraFormatInfo? activeFormat)
    {
        activeFormat = null;
        var formats = new List<CameraFormatInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var catPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        var typePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        object? configObj = null;

        try
        {
            Marshal.StructureToPtr(PinCategoryCapture, catPtr, false);
            Marshal.StructureToPtr(MediaTypeVideo, typePtr, false);

            var iidConfig = IidIAMStreamConfig;
            var hr = captureBuilder.FindInterface(catPtr, typePtr, sourceFilter, ref iidConfig, out configObj);
            if (hr < 0 || configObj is null)
            {
                hr = captureBuilder.FindInterface(IntPtr.Zero, typePtr, sourceFilter, ref iidConfig, out configObj);
            }

            if (hr < 0 || configObj is not IAMStreamConfig streamConfig)
            {
                return formats;
            }

            if (streamConfig.GetNumberOfCapabilities(out var count, out var capSize) == 0 && count > 0 && capSize > 0)
            {
                var sccPtr = Marshal.AllocCoTaskMem(capSize);
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (streamConfig.GetStreamCaps(i, out var pmt, sccPtr) != 0 || pmt == IntPtr.Zero)
                        {
                            continue;
                        }

                        try
                        {
                            var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
                            if (mt is null || mt.MajorType != MediaTypeVideo)
                            {
                                continue;
                            }

                            ExtractConnectedVideoInfo(mt, out var w, out var h, out var fps, out _);
                            if (w <= 0 || h <= 0)
                            {
                                continue;
                            }

                            var subTypeName = FormatSubTypeName(mt.SubType);
                            var key = $"{w}x{h}@{Math.Round(fps, 1)}:{subTypeName}";
                            if (seen.Add(key))
                            {
                                formats.Add(new CameraFormatInfo
                                {
                                    DirectShowCapabilityIndex = i,
                                    Width = (uint)w,
                                    Height = (uint)h,
                                    FrameRate = fps,
                                    Subtype = subTypeName
                                });
                            }
                        }
                        finally
                        {
                            DeleteMediaType(pmt);
                        }
                    }

                    // If user requested a specific capability, apply it before building the rest of the graph.
                    if (requestedFormat?.DirectShowCapabilityIndex is int capIndex &&
                        capIndex >= 0 && capIndex < count)
                    {
                        if (streamConfig.GetStreamCaps(capIndex, out var reqPmt, sccPtr) == 0 && reqPmt != IntPtr.Zero)
                        {
                            try
                            {
                                if (streamConfig.SetFormat(reqPmt) >= 0)
                                {
                                    activeFormat = requestedFormat;
                                }
                            }
                            finally
                            {
                                DeleteMediaType(reqPmt);
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(sccPtr);
                }
            }

            // Read actual current format if we didn't explicitly set one.
            if (activeFormat is null && streamConfig.GetFormat(out var currentPmt) == 0 && currentPmt != IntPtr.Zero)
            {
                try
                {
                    var mt = Marshal.PtrToStructure<AMMediaType>(currentPmt);
                    if (mt is not null)
                    {
                        ExtractConnectedVideoInfo(mt, out var w, out var h, out var fps, out _);
                        if (w > 0 && h > 0)
                        {
                            activeFormat = new CameraFormatInfo
                            {
                                Width = (uint)w,
                                Height = (uint)h,
                                FrameRate = fps,
                                Subtype = FormatSubTypeName(mt.SubType)
                            };
                        }
                    }
                }
                finally
                {
                    DeleteMediaType(currentPmt);
                }
            }
        }
        catch
        {
            // Some virtual filters don't support IAMStreamConfig; rely on default output pin format.
        }
        finally
        {
            ReleaseCom(configObj);
            Marshal.FreeCoTaskMem(catPtr);
            Marshal.FreeCoTaskMem(typePtr);
        }

        return formats
            .OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.FrameRate)
            .ThenBy(f => f.Subtype, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ExtractConnectedVideoInfo(
        AMMediaType mt,
        out int width,
        out int height,
        out double fps,
        out bool isBottomUp)
    {
        width = 0;
        height = 0;
        fps = 0;
        isBottomUp = false;

        if (mt.FormatPtr == IntPtr.Zero)
        {
            return;
        }

        BitmapInfoHeader bmi;
        long avgTimePerFrame;

        if (mt.FormatType == FormatVideoInfo && mt.FormatSize >= Marshal.SizeOf<VideoInfoHeader>())
        {
            var vih = Marshal.PtrToStructure<VideoInfoHeader>(mt.FormatPtr);
            bmi = vih.BmiHeader;
            avgTimePerFrame = vih.AvgTimePerFrame;
        }
        else if (mt.FormatType == FormatVideoInfo2 && mt.FormatSize >= Marshal.SizeOf<VideoInfoHeader2>())
        {
            var vih2 = Marshal.PtrToStructure<VideoInfoHeader2>(mt.FormatPtr);
            bmi = vih2.BmiHeader;
            avgTimePerFrame = vih2.AvgTimePerFrame;
        }
        else
        {
            return;
        }

        width = bmi.Width;
        height = Math.Abs(bmi.Height);
        if (avgTimePerFrame > 0)
        {
            fps = 10_000_000.0 / avgTimePerFrame;
        }

        // Standard DirectShow RGB DIBs with positive biHeight are stored bottom-up.
        var isRgb = mt.SubType == MediaSubTypeRgb32 || mt.SubType == MediaSubTypeArgb32 || mt.SubType == MediaSubTypeRgb24;
        isBottomUp = isRgb && bmi.Height > 0;
    }

    public int SampleCB(double sampleTime, IntPtr pSample) => 0;

    public int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
    {
        if (!_isRunning || pBuffer == IntPtr.Zero || bufferLen <= 0 || _connectedWidth <= 0 || _connectedHeight <= 0)
        {
            return 0;
        }

        try
        {
            _onRawFrame(pBuffer, bufferLen, _connectedWidth, _connectedHeight, _connectedSubType, _isBottomUp);
        }
        catch
        {
            // Never throw back into native DirectShow callback thread
        }

        return 0;
    }

    public void Stop()
    {
        _isRunning = false;

        try
        {
            _sampleGrabber?.SetCallback(null, 1);
        }
        catch
        {
        }

        try
        {
            if (_mediaControl is not null)
            {
                _mediaControl.Stop();
                _mediaControl.GetState(250, out _);
            }
        }
        catch
        {
        }

        _mediaControl = null;
        CleanupGrabberAndRenderer();

        if (_graph is not null && _sourceFilter is not null)
        {
            try
            {
                _graph.RemoveFilter(_sourceFilter);
            }
            catch
            {
            }
        }

        ReleaseCom(_sourceFilter);
        _sourceFilter = null;

        ReleaseCom(_captureBuilder);
        _captureBuilder = null;

        ReleaseCom(_graph);
        _graph = null;

        _connectedWidth = 0;
        _connectedHeight = 0;
    }

    private void CleanupGrabberAndRenderer()
    {
        if (_graph is not null && _sampleGrabberFilter is not null)
        {
            try { _graph.RemoveFilter(_sampleGrabberFilter); } catch { }
        }

        if (_graph is not null && _nullRenderer is not null)
        {
            try { _graph.RemoveFilter(_nullRenderer); } catch { }
        }

        _sampleGrabber = null;
        ReleaseCom(_sampleGrabberFilter);
        _sampleGrabberFilter = null;

        ReleaseCom(_nullRenderer);
        _nullRenderer = null;
    }

    private static bool IsSupportedNativeSubType(Guid subType) =>
        subType == MediaSubTypeRgb32 ||
        subType == MediaSubTypeArgb32 ||
        subType == MediaSubTypeRgb24 ||
        subType == MediaSubTypeNv12 ||
        subType == MediaSubTypeYuy2 ||
        subType == MediaSubTypeI420 ||
        subType == MediaSubTypeIyuv;

    private static void ThrowIfFailed(int hr, string step)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"DirectShow {step} failed (0x{hr:X8}).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
