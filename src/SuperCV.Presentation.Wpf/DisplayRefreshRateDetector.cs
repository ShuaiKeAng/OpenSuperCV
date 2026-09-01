using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SuperCV;

/// <summary>
/// Detects the nominal refresh rate of the monitor currently owning a WPF window. The render
/// pacer separately measures CompositionTarget callbacks, so this value is only the target and
/// never treated as proof that frames were actually delivered.
/// </summary>
internal sealed class DisplayRefreshRateDetector
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int CurrentDisplaySettings = -1;
    private const int VerticalRefreshDeviceCapability = 116;
    private static readonly long ProbeIntervalTicks = Stopwatch.Frequency / 2;

    private long _nextProbeTimestamp;

    internal double RefreshRateHz { get; private set; } = 60.0;

    internal bool Update(Window window, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        nint windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == 0)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        if (!force && now < _nextProbeTimestamp)
        {
            return false;
        }

        nint monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        _nextProbeTimestamp = now + ProbeIntervalTicks;

        double detectedRate = GetDwmRefreshRate(windowHandle);
        if (detectedRate is < 24 or > 1000)
        {
            detectedRate = GetMonitorRefreshRate(monitor);
        }

        if (detectedRate is < 24 or > 1000)
        {
            detectedRate = GetDeviceContextRefreshRate(windowHandle);
        }

        if (detectedRate is < 24 or > 1000 ||
            Math.Abs(RefreshRateHz - detectedRate) < 0.01)
        {
            return false;
        }

        RefreshRateHz = detectedRate;
        return true;
    }

    private static double GetDwmRefreshRate(nint windowHandle)
    {
        var timing = new DwmTimingInfo
        {
            Size = Marshal.SizeOf<DwmTimingInfo>(),
        };
        int result = DwmGetCompositionTimingInfo(windowHandle, ref timing);
        return result == 0 && timing.RefreshRate.Denominator != 0
            ? timing.RefreshRate.Numerator / (double)timing.RefreshRate.Denominator
            : 0;
    }

    private static double GetMonitorRefreshRate(nint monitor)
    {
        if (monitor == 0)
        {
            return 0;
        }

        var monitorInfo = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return 0;
        }

        var mode = new DevMode
        {
            Size = checked((short)Marshal.SizeOf<DevMode>()),
        };
        return EnumDisplaySettings(
            monitorInfo.DeviceName,
            CurrentDisplaySettings,
            ref mode)
            ? mode.DisplayFrequency
            : 0;
    }

    private static double GetDeviceContextRefreshRate(nint windowHandle)
    {
        nint deviceContext = GetDC(windowHandle);
        if (deviceContext == 0)
        {
            return 0;
        }

        try
        {
            return GetDeviceCaps(deviceContext, VerticalRefreshDeviceCapability);
        }
        finally
        {
            _ = ReleaseDC(windowHandle, deviceContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        internal short SpecVersion;
        internal short DriverVersion;
        internal short Size;
        internal short DriverExtra;
        internal int Fields;
        internal int PositionX;
        internal int PositionY;
        internal int DisplayOrientation;
        internal int DisplayFixedOutput;
        internal short Color;
        internal short Duplex;
        internal short YResolution;
        internal short TrueTypeOption;
        internal short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string FormName;

        internal short LogPixels;
        internal int BitsPerPixel;
        internal int PixelsWidth;
        internal int PixelsHeight;
        internal int DisplayFlags;
        internal int DisplayFrequency;
        internal int IcmMethod;
        internal int IcmIntent;
        internal int MediaType;
        internal int DitherType;
        internal int Reserved1;
        internal int Reserved2;
        internal int PanningWidth;
        internal int PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnsignedRatio
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        internal int Size;
        internal UnsignedRatio RefreshRate;
        internal ulong QpcRefreshPeriod;
        internal UnsignedRatio ComposeRate;
        internal ulong QpcVBlank;
        internal ulong RefreshCount;
        internal uint DxRefreshCount;
        internal ulong QpcCompose;
        internal ulong FrameCount;
        internal uint DxPresentCount;
        internal ulong RefreshFrameCount;
        internal ulong FrameSubmittedCount;
        internal uint DxPresentSubmittedCount;
        internal ulong FrameConfirmedCount;
        internal uint DxPresentConfirmedCount;
        internal ulong RefreshConfirmedCount;
        internal uint DxRefreshConfirmedCount;
        internal ulong LateFrameCount;
        internal uint OutstandingFrameCount;
        internal ulong FrameDisplayedCount;
        internal ulong QpcFrameDisplayed;
        internal ulong RefreshFrameDisplayedCount;
        internal ulong FrameCompleteCount;
        internal ulong QpcFrameComplete;
        internal ulong FramePendingCount;
        internal ulong QpcFramePending;
        internal ulong FramesDisplayedCount;
        internal ulong FramesCompleteCount;
        internal ulong FramesPendingCount;
        internal ulong FramesAvailableCount;
        internal ulong FramesDroppedCount;
        internal ulong FramesMissedCount;
        internal ulong RefreshNextDisplayedCount;
        internal ulong RefreshNextPresentedCount;
        internal ulong RefreshesDisplayedCount;
        internal ulong RefreshesPresentedCount;
        internal ulong RefreshStartedCount;
        internal ulong PixelsReceivedCount;
        internal ulong PixelsDrawnCount;
        internal ulong BuffersEmptyCount;
    }

    [DllImport("User32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string deviceName,
        int modeNumber,
        ref DevMode deviceMode);

    [DllImport("User32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("User32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("Gdi32.dll")]
    private static extern int GetDeviceCaps(nint deviceContext, int index);

    [DllImport("Dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(
        nint windowHandle,
        ref DwmTimingInfo timingInfo);
}
