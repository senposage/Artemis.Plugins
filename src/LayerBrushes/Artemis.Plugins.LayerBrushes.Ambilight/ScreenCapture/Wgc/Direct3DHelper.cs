using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Direct3DHelper
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    /// <summary>
    /// Creates a D3D11 device suitable for WGC capture.
    /// </summary>
    public static ID3D11Device CreateD3D11Device()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device device).CheckError();
        return device;
    }

    /// <summary>
    /// Wraps a Vortice D3D11 device into a WinRT IDirect3DDevice for use with WGC APIs.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using IDXGIDevice dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pUnk);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(pUnk);
        }
        finally
        {
            Marshal.Release(pUnk);
        }
    }

    /// <summary>
    /// Extracts the underlying ID3D11Texture2D from a WinRT IDirect3DSurface.
    /// </summary>
    public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
        Guid iid = typeof(ID3D11Texture2D).GUID;
        IntPtr ptr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(ptr);
    }

    #region HMONITOR lookup

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorProc lpfnEnum, IntPtr dwData);

    private delegate bool EnumMonitorProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>
    /// Finds the HMONITOR handle for a given display device name (e.g. \\.\DISPLAY1).
    /// </summary>
    public static IntPtr GetMonitorHandle(string deviceName)
    {
        IntPtr result = IntPtr.Zero;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, ref _, _) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMonitor, ref info) &&
                info.szDevice.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                result = hMonitor;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    #endregion
}
