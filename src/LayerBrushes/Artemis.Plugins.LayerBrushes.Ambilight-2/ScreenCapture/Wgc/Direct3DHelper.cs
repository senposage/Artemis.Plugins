#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Direct3DHelper
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

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
    public static unsafe ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr surfaceUnknown = IntPtr.Zero;
        IntPtr accessPtr = IntPtr.Zero;
        IntPtr texturePtr = IntPtr.Zero;
        try
        {
            // frame.Surface is a CsWinRT projection, not a classic RCW —
            // Marshal.GetIUnknownForObject throws InvalidCastException on it.
            surfaceUnknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            Guid accessIid = Direct3DDxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceUnknown, ref accessIid, out accessPtr));

            IntPtr* vtable = *(IntPtr**)accessPtr;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            Guid textureIid = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(getInterface(accessPtr, &textureIid, &texturePtr));

            var texture = new ID3D11Texture2D(texturePtr);
            texturePtr = IntPtr.Zero; // ownership transferred to Vortice wrapper
            return texture;
        }
        finally
        {
            if (texturePtr != IntPtr.Zero)
                Marshal.Release(texturePtr);
            if (accessPtr != IntPtr.Zero)
                Marshal.Release(accessPtr);
            if (surfaceUnknown != IntPtr.Zero)
                Marshal.Release(surfaceUnknown);
        }
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

    #region GraphicsCaptureSession raw-COM interop
    //
    // CsWinRT projects IsCursorCaptureEnabled / IsBorderRequired / MinUpdateInterval via
    // generated code that binds a specific method slot on Microsoft.Windows.SDK.NET.dll.
    // When the plugin loads, Artemis's assembly-resolution brings in an SDK.NET version
    // whose metadata lacks those setters, and the JIT throws MissingMethodException.
    //
    // Bypass the projection entirely: QI the underlying WinRT object for the raw COM
    // interface and invoke the setter through the vtable. This depends only on the OS
    // runtime version, not on which managed projection loaded first.

    // WinRT vtable: [0] QueryInterface [1] AddRef [2] Release
    //               [3] GetIids [4] GetRuntimeClassName [5] GetTrustLevel
    //               [6+] interface-specific methods
    private const int WinRTIInspectableMethodCount = 6;

    private static readonly Guid IGraphicsCaptureSession2Iid = new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
    private static readonly Guid IGraphicsCaptureSession3Iid = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    private static readonly Guid IGraphicsCaptureSession5Iid = new("67c0ea62-1f85-5061-925a-239be0ac09cb");
    private static readonly Guid IGraphicsCaptureAccessStaticsIid = new("743ed370-06ec-5040-a58a-901f0f757095");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    /// <summary>
    /// Sets <see cref="GraphicsCaptureSession.IsCursorCaptureEnabled"/> via raw COM,
    /// avoiding the CsWinRT projection that may not be loaded at runtime.
    /// </summary>
    public static bool TrySetIsCursorCaptureEnabled(GraphicsCaptureSession session, bool value, out string? error)
    {
        // IGraphicsCaptureSession2 layout: [6] get_IsCursorCaptureEnabled, [7] put_IsCursorCaptureEnabled
        return TrySetBoolOnSessionInterface(session, IGraphicsCaptureSession2Iid,
            WinRTIInspectableMethodCount + 1, value, out error);
    }

    /// <summary>
    /// Sets <see cref="GraphicsCaptureSession.IsBorderRequired"/> via raw COM.
    /// For this to actually suppress the yellow capture indicator the process must
    /// have called <see cref="TryRequestBorderlessAccess"/> first and been granted.
    /// </summary>
    public static bool TrySetIsBorderRequired(GraphicsCaptureSession session, bool value, out string? error)
    {
        // IGraphicsCaptureSession3 layout: [6] get_IsBorderRequired, [7] put_IsBorderRequired
        return TrySetBoolOnSessionInterface(session, IGraphicsCaptureSession3Iid,
            WinRTIInspectableMethodCount + 1, value, out error);
    }

    /// <summary>
    /// Sets <see cref="GraphicsCaptureSession.MinUpdateInterval"/> via raw COM.
    /// WinRT TimeSpan is a signed 64-bit 100 ns tick count, same unit as <see cref="TimeSpan.Ticks"/>.
    /// </summary>
    public static bool TrySetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan value, out string? error)
    {
        // IGraphicsCaptureSession5 layout: [6] get_MinUpdateInterval, [7] put_MinUpdateInterval
        return TrySetInt64OnSessionInterface(session, IGraphicsCaptureSession5Iid,
            WinRTIInspectableMethodCount + 1, value.Ticks, out error);
    }

    private static unsafe bool TrySetBoolOnSessionInterface(
        GraphicsCaptureSession session, Guid iid, int vtableSlot, bool value, out string? error)
    {
        return TrySetOnSessionInterface(session, iid, vtableSlot, value ? (byte)1 : (byte)0, out error);
    }

    private static unsafe bool TrySetInt64OnSessionInterface(
        GraphicsCaptureSession session, Guid iid, int vtableSlot, long value, out string? error)
    {
        return TrySetOnSessionInterface(session, iid, vtableSlot, value, out error);
    }

    private static unsafe bool TrySetOnSessionInterface<T>(
        GraphicsCaptureSession session, Guid iid, int vtableSlot, T value, out string? error)
        where T : unmanaged
    {
        IntPtr sessionPtr = IntPtr.Zero;
        IntPtr interfacePtr = IntPtr.Zero;
        try
        {
            sessionPtr = MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            if (sessionPtr == IntPtr.Zero)
            {
                error = "MarshalInspectable returned null";
                return false;
            }

            int hr = Marshal.QueryInterface(sessionPtr, ref iid, out interfacePtr);
            if (hr < 0 || interfacePtr == IntPtr.Zero)
            {
                error = $"QueryInterface({iid}) failed: 0x{hr:X8}";
                return false;
            }

            IntPtr* vtable = *(IntPtr**)interfacePtr;
            var setter = (delegate* unmanaged[Stdcall]<IntPtr, T, int>)vtable[vtableSlot];
            hr = setter(interfacePtr, value);
            if (hr < 0)
            {
                error = $"setter returned 0x{hr:X8}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (interfacePtr != IntPtr.Zero) Marshal.Release(interfacePtr);
            if (sessionPtr != IntPtr.Zero) Marshal.Release(sessionPtr);
        }
    }

    /// <summary>
    /// Fires <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c> to register
    /// the host process with Windows so the "let apps hide the capture border"
    /// consent is applied. The async operation is released without awaiting — the
    /// Windows-side registration is a side effect of the invocation and does not
    /// require the managed caller to observe completion.
    /// </summary>
    public static unsafe bool TryRequestBorderlessAccess(out string? error)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureAccess";
        const int GraphicsCaptureAccessKind_Borderless = 0;

        IntPtr hstring = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;
        IntPtr asyncOp = IntPtr.Zero;
        try
        {
            int hr = WindowsCreateString(className, className.Length, out hstring);
            if (hr < 0) { error = $"WindowsCreateString failed: 0x{hr:X8}"; return false; }

            Guid iid = IGraphicsCaptureAccessStaticsIid;
            hr = RoGetActivationFactory(hstring, ref iid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                // E_NOINTERFACE (0x80004002) here means the OS build predates the
                // Borderless API — nothing to do on those systems.
                error = $"RoGetActivationFactory failed: 0x{hr:X8}";
                return false;
            }

            IntPtr* vtable = *(IntPtr**)factoryPtr;
            // IGraphicsCaptureAccessStatics has exactly one method: RequestAccessAsync at [6]
            var requestAccess = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vtable[WinRTIInspectableMethodCount];
            hr = requestAccess(factoryPtr, GraphicsCaptureAccessKind_Borderless, &asyncOp);
            if (hr < 0)
            {
                error = $"RequestAccessAsync returned 0x{hr:X8}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (asyncOp != IntPtr.Zero) Marshal.Release(asyncOp);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    #endregion
}

#endif
