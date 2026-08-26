using System.Runtime.InteropServices;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Services;

internal sealed class Win32ScreenColorSampler : IScreenColorSampler
{
    private const uint InvalidColor = 0xFFFFFFFF;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly IScreenColorNativeApi _nativeApi;

    public Win32ScreenColorSampler()
        : this(new ScreenColorNativeApi())
    {
    }

    internal Win32ScreenColorSampler(IScreenColorNativeApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        _nativeApi = nativeApi;
    }

    public PhysicalScreenBounds GetVirtualScreenBounds()
    {
        var bounds = new PhysicalScreenBounds(
            _nativeApi.GetSystemMetrics(SmXVirtualScreen),
            _nativeApi.GetSystemMetrics(SmYVirtualScreen),
            _nativeApi.GetSystemMetrics(SmCxVirtualScreen),
            _nativeApi.GetSystemMetrics(SmCyVirtualScreen));
        if (bounds.IsValid)
        {
            return bounds;
        }

        return new PhysicalScreenBounds(
            0,
            0,
            Math.Max(1, _nativeApi.GetSystemMetrics(SmCxScreen)),
            Math.Max(1, _nativeApi.GetSystemMetrics(SmCyScreen)));
    }

    public bool TryGetCursorPoint(out PhysicalScreenPoint point)
    {
        if (_nativeApi.GetCursorPosition(out var nativePoint))
        {
            point = new PhysicalScreenPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    public bool TrySample(PhysicalScreenPoint point, out RgbColor color)
    {
        color = default;
        var desktopDeviceContext = _nativeApi.GetDesktopDeviceContext();
        if (desktopDeviceContext == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var colorReference = _nativeApi.GetPixel(desktopDeviceContext, point.X, point.Y);
            if (colorReference == InvalidColor)
            {
                return false;
            }

            // Win32 COLORREF stores the channels as 0x00BBGGRR.
            color = new RgbColor(
                (byte)(colorReference & 0xFF),
                (byte)((colorReference >> 8) & 0xFF),
                (byte)((colorReference >> 16) & 0xFF));
            return true;
        }
        finally
        {
            _nativeApi.ReleaseDesktopDeviceContext(desktopDeviceContext);
        }
    }
}

internal readonly record struct NativeScreenPoint(int X, int Y);

internal interface IScreenColorNativeApi
{
    bool GetCursorPosition(out NativeScreenPoint point);

    IntPtr GetDesktopDeviceContext();

    uint GetPixel(IntPtr deviceContext, int x, int y);

    void ReleaseDesktopDeviceContext(IntPtr deviceContext);

    int GetSystemMetrics(int index);
}

internal sealed class ScreenColorNativeApi : IScreenColorNativeApi
{
    public bool GetCursorPosition(out NativeScreenPoint point)
    {
        var succeeded = NativeMethods.GetCursorPos(out var nativePoint);
        point = new NativeScreenPoint(nativePoint.X, nativePoint.Y);
        return succeeded;
    }

    public IntPtr GetDesktopDeviceContext() => NativeMethods.GetDC(IntPtr.Zero);

    public uint GetPixel(IntPtr deviceContext, int x, int y) =>
        NativeMethods.GetPixel(deviceContext, x, y);

    public void ReleaseDesktopDeviceContext(IntPtr deviceContext) =>
        _ = NativeMethods.ReleaseDC(IntPtr.Zero, deviceContext);

    public int GetSystemMetrics(int index) => NativeMethods.GetSystemMetrics(index);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(IntPtr deviceContext, int x, int y);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }
    }
}
