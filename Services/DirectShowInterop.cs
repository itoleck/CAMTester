using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CAMTester.Services;

internal sealed record DirectShowDeviceEntry(
    string FriendlyName,
    string DevicePath,
    string Clsid,
    string MonikerDisplayName);

internal static class DirectShowInterop
{
    public static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    public static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    public static readonly Guid ClsidFilterGraph = new("E436EBB3-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid ClsidCaptureGraphBuilder2 = new("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
    public static readonly Guid ClsidSampleGrabber = new("C1F400A0-3F08-11D3-9F0B-006008039E37");
    public static readonly Guid ClsidNullRenderer = new("C1F400A4-3F08-11D3-9F0B-006008039E37");

    public static readonly Guid IidIBaseFilter = new("56A86895-0AD4-11CE-B03A-0020AF0BA770");
    public static readonly Guid IidIPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
    public static readonly Guid IidIAMStreamConfig = new("C6E13340-30AC-11D0-A18C-00A0C9118956");

    public static readonly Guid PinCategoryCapture = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");
    public static readonly Guid PinCategoryPreview = new("FB6C4282-0353-11D1-905F-0000C0CC16BA");

    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatVideoInfo = new("05589F80-C356-11CE-BF01-00AA0055595A");
    public static readonly Guid FormatVideoInfo2 = new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");

    public static readonly Guid MediaSubTypeRgb32 = new("E436EB7E-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid MediaSubTypeArgb32 = new("773C9AC0-3274-11D0-B724-00AA006C1A01");
    public static readonly Guid MediaSubTypeRgb24 = new("E436EB7D-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid MediaSubTypeNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaSubTypeYuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaSubTypeI420 = new("30323449-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaSubTypeIyuv = new("56555949-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaSubTypeMjpg = new("47504A4D-0000-0010-8000-00AA00389B71");

    public static List<DirectShowDeviceEntry> EnumerateVideoCaptureDevices()
    {
        var results = new List<DirectShowDeviceEntry>();
        object? devEnumObj = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            var devEnumType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
            if (devEnumType is null)
            {
                return results;
            }

            devEnumObj = Activator.CreateInstance(devEnumType);
            if (devEnumObj is not ICreateDevEnum devEnum)
            {
                return results;
            }

            var category = ClsidVideoInputDeviceCategory;
            var hr = devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null)
            {
                return results;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null)
                {
                    continue;
                }

                object? bagObj = null;
                try
                {
                    var iidBag = IidIPropertyBag;
                    moniker.BindToStorage(null!, null!, ref iidBag, out bagObj);
                    var bag = bagObj as IPropertyBag;

                    var friendlyName = ReadProperty(bag, "FriendlyName");
                    var devicePath = ReadProperty(bag, "DevicePath");
                    var clsid = ReadProperty(bag, "CLSID");

                    moniker.GetDisplayName(null!, null!, out var displayName);
                    if (string.IsNullOrWhiteSpace(friendlyName))
                    {
                        friendlyName = "DirectShow Camera";
                    }

                    results.Add(new DirectShowDeviceEntry(
                        FriendlyName: friendlyName.Trim(),
                        DevicePath: devicePath.Trim(),
                        Clsid: clsid.Trim(),
                        MonikerDisplayName: displayName ?? string.Empty));
                }
                catch
                {
                    // Skip unreadable device monikers
                }
                finally
                {
                    ReleaseCom(bagObj);
                    ReleaseCom(moniker);
                }
            }
        }
        catch
        {
            // DirectShow enumeration unavailable
        }
        finally
        {
            ReleaseCom(enumMoniker);
            ReleaseCom(devEnumObj);
        }

        return results;
    }

    public static IBaseFilter? BindFilterByMonikerDisplayName(string monikerDisplayName)
    {
        object? devEnumObj = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            var devEnumType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
            if (devEnumType is null)
            {
                return null;
            }

            devEnumObj = Activator.CreateInstance(devEnumType);
            if (devEnumObj is not ICreateDevEnum devEnum)
            {
                return null;
            }

            var category = ClsidVideoInputDeviceCategory;
            var hr = devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null)
            {
                return null;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null)
                {
                    continue;
                }

                try
                {
                    moniker.GetDisplayName(null!, null!, out var displayName);
                    if (string.Equals(displayName, monikerDisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        var iidFilter = IidIBaseFilter;
                        moniker.BindToObject(null!, null!, ref iidFilter, out var filterObj);
                        return filterObj as IBaseFilter;
                    }
                }
                finally
                {
                    ReleaseCom(moniker);
                }
            }
        }
        finally
        {
            ReleaseCom(enumMoniker);
            ReleaseCom(devEnumObj);
        }

        return null;
    }

    public static string FormatSubTypeName(Guid subType)
    {
        if (subType == MediaSubTypeRgb32) return "RGB32";
        if (subType == MediaSubTypeArgb32) return "ARGB32";
        if (subType == MediaSubTypeRgb24) return "RGB24";
        if (subType == MediaSubTypeNv12) return "NV12";
        if (subType == MediaSubTypeYuy2) return "YUY2";
        if (subType == MediaSubTypeI420) return "I420";
        if (subType == MediaSubTypeIyuv) return "IYUV";
        if (subType == MediaSubTypeMjpg) return "MJPG";

        // Decode FourCC from the first 4 bytes of standard DirectShow MEDIASUBTYPE GUIDs
        var bytes = subType.ToByteArray();
        if (bytes[4] == 0x00 && bytes[5] == 0x00 && bytes[6] == 0x10 && bytes[7] == 0x00 &&
            bytes[8] == 0x80 && bytes[9] == 0x00 && bytes[10] == 0x00 && bytes[11] == 0xAA)
        {
            var chars = Encoding.ASCII.GetString(bytes, 0, 4);
            if (chars.All(c => c >= 32 && c <= 126))
            {
                return chars.Trim().ToUpperInvariant();
            }
        }

        return subType.ToString("B");
    }

    public static void FreeMediaType(AMMediaType? mediaType)
    {
        if (mediaType is null)
        {
            return;
        }

        if (mediaType.FormatSize != 0 && mediaType.FormatPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(mediaType.FormatPtr);
            mediaType.FormatSize = 0;
            mediaType.FormatPtr = IntPtr.Zero;
        }

        if (mediaType.UnkPtr != IntPtr.Zero)
        {
            Marshal.Release(mediaType.UnkPtr);
            mediaType.UnkPtr = IntPtr.Zero;
        }
    }

    public static void DeleteMediaType(IntPtr pmt)
    {
        if (pmt == IntPtr.Zero)
        {
            return;
        }

        var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
        FreeMediaType(mt);
        Marshal.FreeCoTaskMem(pmt);
    }

    public static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch
            {
                // Ignore COM release errors during shutdown
            }
        }
    }

    private static string ReadProperty(IPropertyBag? bag, string propName)
    {
        if (bag is null)
        {
            return string.Empty;
        }

        object? value = null;
        return bag.Read(propName, ref value, IntPtr.Zero) == 0 && value is string s
            ? s
            : string.Empty;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker? ppEnumMoniker, [In] int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? pVar, [In] IntPtr pErrorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    [ComImport, Guid("56A86899-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMediaFilter
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long tStart);
        [PreserveSig] int GetState(int dwMilliSecsTimeout, out int filtState);
        [PreserveSig] int SetSyncSource(IntPtr pClock);
        [PreserveSig] int GetSyncSource(out IntPtr pClock);
    }

    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IBaseFilter
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long tStart);
        [PreserveSig] int GetState(int dwMilliSecsTimeout, out int filtState);
        [PreserveSig] int SetSyncSource(IntPtr pClock);
        [PreserveSig] int GetSyncSource(out IntPtr pClock);
        [PreserveSig] int EnumPins(out IntPtr ppEnum);
        [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr ppPin);
        [PreserveSig] int QueryFilterInfo(out IntPtr pInfo);
        [PreserveSig] int JoinFilterGraph(IntPtr pGraph, [MarshalAs(UnmanagedType.LPWStr)] string pName);
        [PreserveSig] int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string pVendorInfo);
    }

    [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphBuilder
    {
        [PreserveSig] int AddFilter([In] IBaseFilter pFilter, [In, MarshalAs(UnmanagedType.LPWStr)] string pName);
        [PreserveSig] int RemoveFilter([In] IBaseFilter pFilter);
        [PreserveSig] int EnumFilters(out IntPtr ppEnum);
        [PreserveSig] int FindFilterByName([In, MarshalAs(UnmanagedType.LPWStr)] string pName, out IBaseFilter ppFilter);
        [PreserveSig] int ConnectDirect(IntPtr ppinOut, IntPtr ppinIn, [In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType? pmt);
        [PreserveSig] int Reconnect(IntPtr ppin);
        [PreserveSig] int Disconnect(IntPtr ppin);
        [PreserveSig] int SetDefaultSyncSource();
        [PreserveSig] int Connect(IntPtr ppinOut, IntPtr ppinIn);
        [PreserveSig] int Render(IntPtr ppinOut);
        [PreserveSig] int RenderFile([In, MarshalAs(UnmanagedType.LPWStr)] string lpcwstrFile, [In, MarshalAs(UnmanagedType.LPWStr)] string? lpcwstrPlayList);
        [PreserveSig] int AddSourceFilter([In, MarshalAs(UnmanagedType.LPWStr)] string lpcwstrFileName, [In, MarshalAs(UnmanagedType.LPWStr)] string lpcwstrFilterName, out IBaseFilter ppFilter);
        [PreserveSig] int SetLogFile(IntPtr hFile);
        [PreserveSig] int Abort();
        [PreserveSig] int ShouldOperationContinue();
    }

    [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureGraphBuilder2
    {
        [PreserveSig] int SetFiltergraph([In] IGraphBuilder pfg);
        [PreserveSig] int GetFiltergraph(out IGraphBuilder ppfg);
        [PreserveSig] int SetOutputFileName([In] ref Guid pType, [In, MarshalAs(UnmanagedType.LPWStr)] string lpstrFile, out IBaseFilter ppbf, out IntPtr ppSink);
        [PreserveSig] int FindInterface([In] IntPtr pCategory, [In] IntPtr pType, [In] IBaseFilter pbf, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppint);
        [PreserveSig] int RenderStream([In] IntPtr pCategory, [In] IntPtr pType, [In, MarshalAs(UnmanagedType.IUnknown)] object pSource, [In] IBaseFilter? pfCompressor, [In] IBaseFilter? pfRenderer);
        [PreserveSig] int ControlStream([In] IntPtr pCategory, [In] IntPtr pType, [In] IBaseFilter pFilter, IntPtr pstart, IntPtr pstop, short wStartCookie, short wStopCookie);
        [PreserveSig] int AllocCapFile([In, MarshalAs(UnmanagedType.LPWStr)] string lpstrFile, long dwlSize);
        [PreserveSig] int CopyCaptureFile([In, MarshalAs(UnmanagedType.LPWStr)] string lpwstrOld, [In, MarshalAs(UnmanagedType.LPWStr)] string lpwstrNew, int fAllowEscAbort, IntPtr pFilter);
        [PreserveSig] int FindPin([In, MarshalAs(UnmanagedType.IUnknown)] object pSource, int pindir, [In] IntPtr pCategory, [In] IntPtr pType, [MarshalAs(UnmanagedType.Bool)] bool fUnconnected, int num, out IntPtr ppPin);
    }

    [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int GetState(int msTimeout, out int pfs);
        [PreserveSig] int RenderFile([In, MarshalAs(UnmanagedType.BStr)] string strFilename);
        [PreserveSig] int AddSourceFilter([In, MarshalAs(UnmanagedType.BStr)] string strFilename, [MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
        [PreserveSig] int get_FilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
        [PreserveSig] int get_RegFilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
        [PreserveSig] int StopWhenReady();
    }

    [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat([In] IntPtr pmt);
        [PreserveSig] int GetFormat(out IntPtr pmt);
        [PreserveSig] int GetNumberOfCapabilities(out int piCount, out int piSize);
        [PreserveSig] int GetStreamCaps(int iIndex, out IntPtr ppmt, [In] IntPtr pSCC);
    }

    [ComImport, Guid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISampleGrabber
    {
        [PreserveSig] int SetOneShot([In, MarshalAs(UnmanagedType.Bool)] bool oneShot);
        [PreserveSig] int SetMediaType([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt);
        [PreserveSig] int GetConnectedMediaType([Out, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt);
        [PreserveSig] int SetBufferSamples([In, MarshalAs(UnmanagedType.Bool)] bool bufferThem);
        [PreserveSig] int GetCurrentBuffer(ref int pBufferSize, IntPtr pBuffer);
        [PreserveSig] int GetCurrentSample(out IntPtr ppSample);
        [PreserveSig] int SetCallback(ISampleGrabberCB? pCallback, int whichMethodToCallback);
    }

    [ComImport, Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISampleGrabberCB
    {
        [PreserveSig] int SampleCB(double sampleTime, IntPtr pSample);
        [PreserveSig] int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class AMMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        [MarshalAs(UnmanagedType.Bool)] public bool FixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr UnkPtr;
        public int FormatSize;
        public IntPtr FormatPtr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoInfoHeader
    {
        public Rect SrcRect;
        public Rect TargetRect;
        public int BitRate;
        public int BitErrorRate;
        public long AvgTimePerFrame;
        public BitmapInfoHeader BmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoInfoHeader2
    {
        public Rect SrcRect;
        public Rect TargetRect;
        public int BitRate;
        public int BitErrorRate;
        public long AvgTimePerFrame;
        public int InterlaceFlags;
        public int CopyProtectFlags;
        public int PictAspectRatioX;
        public int PictAspectRatioY;
        public int ControlFlags;
        public int Reserved2;
        public BitmapInfoHeader BmiHeader;
    }
}
