using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ScreenCapture.NET;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;

/// <summary>
/// IScreenCapture implementation using the Windows Graphics Capture API.
///
/// Uses on-demand capture: the WGC session is started only when a frame is needed
/// and stopped immediately after, so DWM only does composition work during the
/// brief active window.  Combined with GPU-side mip downscaling, this keeps both
/// DWM and Artemis GPU usage minimal.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcScreenCapture : IScreenCapture
{
    #region Interop

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    private static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    #endregion

    private static readonly ILogger Logger = Log.ForContext<WgcScreenCapture>();

    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly List<WgcCaptureZone> _zones = [];
    private readonly object _sessionLock = new();

    // Persistent — expensive to create, reused across capture cycles
    private GraphicsCaptureItem? _captureItem;
    private volatile bool _closed;

    // Per-capture-cycle — created/disposed each time to minimise DWM overhead
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private volatile bool _disposed;

    // GPU mip-chain texture for hardware downscaling
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipSRV;
    private int _mipTexWidth;
    private int _mipTexHeight;

    // Staging texture for CPU readback (sized to the downscaled mip level)
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;

    public Display Display { get; }
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    public WgcScreenCapture(Display display, ID3D11Device device, IDirect3DDevice winrtDevice)
    {
        Display = display;
        _device = device;
        _winrtDevice = winrtDevice;
    }

    #region Capture item creation

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hMonitor)
    {
        Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
        RoGetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ref interopIid, out IntPtr factoryPtr);
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Guid itemIid = IGraphicsCaptureItemIid;
            int hr = interop.CreateForMonitor(hMonitor, ref itemIid, out IntPtr itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            if (itemPtr == IntPtr.Zero)
                throw new InvalidOperationException("CreateForMonitor returned null");
            try
            {
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    internal static void SelfTest(IntPtr hMonitor)
    {
        var item = CreateCaptureItemForMonitor(hMonitor);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
            throw new InvalidOperationException("WGC capture item has invalid size");
    }

    #endregion

    #region Session management

    /// <summary>
    /// Ensures the persistent capture item exists.
    /// </summary>
    private void EnsureCaptureItem()
    {
        if (_captureItem != null && !_closed) return;

        lock (_sessionLock)
        {
            if (_captureItem != null && !_closed) return;
            if (_disposed) throw new ObjectDisposedException(nameof(WgcScreenCapture));

            if (_closed)
            {
                Logger.Debug("WGC capture item was closed, recreating for {Display}", Display.DeviceName);
                StopSessionUnlocked();
                _captureItem = null;
                _closed = false;
            }

            IntPtr hMonitor = Direct3DHelper.GetMonitorHandle(Display.DeviceName);
            if (hMonitor == IntPtr.Zero)
                throw new InvalidOperationException($"Could not find HMONITOR for '{Display.DeviceName}'");

            _captureItem = CreateCaptureItemForMonitor(hMonitor);
            _captureItem.Closed += (_, _) =>
            {
                Logger.Debug("WGC capture item closed for {Display}", Display.DeviceName);
                _closed = true;
            };
            Logger.Debug("WGC capture item created for {Display}, size={W}x{H}",
                Display.DeviceName, _captureItem.Size.Width, _captureItem.Size.Height);
        }
    }

    /// <summary>
    /// Starts a WGC capture session. Lightweight — capture item is reused.
    /// </summary>
    private void StartSession()
    {
        lock (_sessionLock)
        {
            if (_session != null) return;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                _captureItem!.Size);

            _session = _framePool.CreateCaptureSession(_captureItem);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }
    }

    /// <summary>
    /// Stops the capture session to minimise DWM overhead between frames.
    /// The capture item is kept alive for fast restart.
    /// </summary>
    private void PauseSession()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
        }
    }

    private void StopSessionUnlocked()
    {
        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
    }

    #endregion

    #region GPU resources

    private void EnsureMipTexture(int width, int height)
    {
        if (_mipTexture != null && _mipTexWidth == width && _mipTexHeight == height)
            return;

        _mipSRV?.Dispose();
        _mipTexture?.Dispose();

        _mipTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 0, // full mip chain
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipSRV = _device.CreateShaderResourceView(_mipTexture);
        _mipTexWidth = width;
        _mipTexHeight = height;
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != null && _stagingWidth == width && _stagingHeight == height)
            return;

        _stagingTexture?.Dispose();
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void DisposeGpuResources()
    {
        _mipSRV?.Dispose();
        _mipSRV = null;
        _mipTexture?.Dispose();
        _mipTexture = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
    }

    #endregion

    #region Capture

    /// <summary>
    /// Waits briefly for the first frame after session start.
    /// Returns null if no frame arrives within the timeout.
    /// </summary>
    private Direct3D11CaptureFrame? WaitForFrame(int timeoutMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Direct3D11CaptureFrame? frame = _framePool?.TryGetNextFrame();
            if (frame != null)
                return frame;
            Thread.Sleep(1);
        }
        return null;
    }

    public unsafe bool CaptureScreen()
    {
        if (_disposed) return false;

        if (_device.DeviceRemovedReason.Failure)
            throw new InvalidOperationException("D3D11 device lost: " + _device.DeviceRemovedReason);

        // Determine the minimum downscale level across zones that need data.
        int minDownscale = int.MaxValue;
        lock (_zones)
        {
            foreach (WgcCaptureZone zone in _zones)
            {
                if (zone.NeedsUpdate)
                    minDownscale = Math.Min(minDownscale, zone.DownscaleLevel);
            }
        }

        if (minDownscale == int.MaxValue)
            return false; // no zone needs data

        // On-demand: start session → grab frame → stop session.
        // DWM only composes WGC frames while the session is active.
        EnsureCaptureItem();
        StartSession();

        Direct3D11CaptureFrame? frame = WaitForFrame();
        if (frame == null)
        {
            PauseSession();
            return false;
        }

        try
        {
            SizeInt32 contentSize = frame.ContentSize;
            int width = contentSize.Width;
            int height = contentSize.Height;
            if (width <= 0 || height <= 0)
                return false;

            using ID3D11Texture2D frameTexture = Direct3DHelper.GetTextureFromSurface(frame.Surface);
            ID3D11DeviceContext context = _device.ImmediateContext;

            int mipLevel = Math.Max(0, minDownscale);
            int mapWidth, mapHeight;

            if (mipLevel > 0)
            {
                // GPU-side downscale via mip chain
                EnsureMipTexture(width, height);
                context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frameTexture, 0);
                context.GenerateMips(_mipSRV!);

                mapWidth = Math.Max(1, width >> mipLevel);
                mapHeight = Math.Max(1, height >> mipLevel);
                EnsureStagingTexture(mapWidth, mapHeight);
                context.CopySubresourceRegion(_stagingTexture!, 0, 0, 0, 0, _mipTexture!, mipLevel);
            }
            else
            {
                mapWidth = width;
                mapHeight = height;
                EnsureStagingTexture(mapWidth, mapHeight);
                context.CopyResource(_stagingTexture!, frameTexture);
            }

            var mapped = context.Map(_stagingTexture!, 0, MapMode.Read);
            try
            {
                byte* srcPtr = (byte*)mapped.DataPointer;
                int srcStride = mapped.RowPitch;

                lock (_zones)
                {
                    foreach (WgcCaptureZone zone in _zones)
                        zone.CopyFromFrame(srcPtr, srcStride, mapWidth, mapHeight, mipLevel);
                }
            }
            finally
            {
                context.Unmap(_stagingTexture!, 0);
            }

            Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(true));
            return true;
        }
        finally
        {
            frame.Dispose();
            PauseSession();
        }
    }

    #endregion

    #region IScreenCapture

    public ICaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        var zone = new WgcCaptureZone(Display, x, y, width, height, downscaleLevel);
        lock (_zones)
            _zones.Add(zone);
        return zone;
    }

    public bool UnregisterCaptureZone(ICaptureZone captureZone)
    {
        if (captureZone is not WgcCaptureZone wgcZone) return false;
        lock (_zones)
            return _zones.Remove(wgcZone);
    }

    public void UpdateCaptureZone(ICaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        if (captureZone is WgcCaptureZone wgcZone)
            wgcZone.Update(x ?? wgcZone.X, y, width, height, downscaleLevel);
    }

    public void Restart()
    {
        lock (_sessionLock)
        {
            StopSessionUnlocked();
            _captureItem = null;
            DisposeGpuResources();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_sessionLock)
        {
            StopSessionUnlocked();
            _captureItem = null;
            DisposeGpuResources();
        }
    }

    #endregion
}
