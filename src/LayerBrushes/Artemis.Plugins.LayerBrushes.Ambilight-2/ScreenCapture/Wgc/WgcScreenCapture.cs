#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
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
/// The WGC session is kept alive between frames to avoid the heavy DWM overhead of
/// repeatedly creating and tearing down the capture pipeline.  Frame holding (pool
/// size 2, previous frame kept alive) prevents DWM from doing capture copies while
/// we sleep between grabs.  MinUpdateInterval (24H2+) further tells DWM it can skip
/// capture work entirely between our desired intervals.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcScreenCapture : IScreenCapture, ICaptureBackendStatus
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

    private static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    #endregion

    private static readonly ILogger Logger = Log.ForContext<WgcScreenCapture>();

    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly List<WgcCaptureZone> _zones = [];
    private readonly HashSet<string> _unsupportedSessionProperties = [];
    private readonly HashSet<string> _loggedSessionPropertyApplications = [];
    private readonly object _sessionLock = new();
    private readonly object _frameLock = new();

    // Persistent — expensive to create, reused across capture cycles
    private GraphicsCaptureItem? _captureItem;
    private volatile bool _closed;

    // Session + pool — kept alive between frames, only torn down on Restart/Dispose
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    // Frame holding: keep the previous frame alive to occupy a pool buffer.
    // With pool size 2 this means DWM has at most 1 free buffer — once it fills
    // that one it stops doing capture copies until we consume, drastically reducing
    // DWM GPU work between our capture intervals.
    private Direct3D11CaptureFrame? _heldFrame;
    private Direct3D11CaptureFrame? _pendingFrame;
    private long _framesArrived;
    private long _framesConsumed;
    private bool _captureAttemptLogged;
    private DateTimeOffset _nextNoFrameLog = DateTimeOffset.MinValue;

    private volatile bool _disposed;
    private volatile int _fpsLimit;

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
    public string CaptureBackendName => "Windows Graphics Capture";
    public string CaptureBackendDetails => $"WGC active; display={Display.DeviceName}, fps={_fpsLimit}";

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
        IntPtr factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ref interopIid);
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

    private static IntPtr GetActivationFactory(string activatableClassId, ref Guid iid)
    {
        int hr = WindowsCreateString(activatableClassId, activatableClassId.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(hstring, ref iid, out IntPtr factoryPtr);
            Marshal.ThrowExceptionForHR(hr);
            return factoryPtr;
        }
        finally
        {
            WindowsDeleteString(hstring);
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
    /// Starts a WGC capture session if not already running.  Pool size 2 allows
    /// double-buffering: we hold the previous frame (occupying 1 buffer) while a
    /// new frame sits pre-delivered in the other.  Once both are occupied DWM has
    /// 0 free buffers and stops capture copies — resuming only after we dispose
    /// the old frame during the next grab.
    /// </summary>
    private void StartSession()
    {
        lock (_sessionLock)
        {
            if (_session != null) return;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2, // double-buffer: 1 held + 1 pre-delivered = 0 free → DWM stops capture copies between grabs
                _captureItem!.Size);
            _framePool.FrameArrived += OnFrameArrived;
            AmbilightWindowsDiagnostics.Write(Logger,
                $"WGC frame pool created for {Display.DeviceName} size={_captureItem.Size.Width}x{_captureItem.Size.Height}");

            _session = _framePool.CreateCaptureSession(_captureItem);
            // Raw-COM QI on the underlying WinRT object — CsWinRT projection setters
            // throw MissingMethodException here because Artemis's assembly-resolution
            // loads an SDK.NET whose metadata lacks these setters.
            ApplyRawComSessionSetting(nameof(GraphicsCaptureSession.IsCursorCaptureEnabled),
                Direct3DHelper.TrySetIsCursorCaptureEnabled, value: false);
            ApplyRawComSessionSetting(nameof(GraphicsCaptureSession.IsBorderRequired),
                Direct3DHelper.TrySetIsBorderRequired, value: false);

            // MinUpdateInterval (24H2+) tells DWM to skip capture work between intervals.
            // This is the key to reducing DWM GPU usage during high-FPS games.
            ApplyMinUpdateInterval();

            AmbilightWindowsDiagnostics.Write(Logger, $"WGC StartCapture calling for {Display.DeviceName}");
            _session.StartCapture();
            Logger.Debug("WGC session started for {Display}", Display.DeviceName);
            AmbilightWindowsDiagnostics.Write(Logger, $"WGC session started for {Display.DeviceName} size={_captureItem.Size.Width}x{_captureItem.Size.Height}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame == null)
                return;

            lock (_frameLock)
            {
                _pendingFrame?.Dispose();
                _pendingFrame = frame;
                Interlocked.Increment(ref _framesArrived);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "WGC FrameArrived handling failed for {Display}", Display.DeviceName);
            AmbilightWindowsDiagnostics.Write(Logger, $"WGC FrameArrived handling failed for {Display.DeviceName}: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Sets the capture FPS limit. Updates MinUpdateInterval on the active session
    /// so DWM can skip capture work between intervals.
    /// </summary>
    internal void SetFpsLimit(int fps)
    {
        fps = Math.Max(0, fps);
        if (_fpsLimit == fps)
            return;

        _fpsLimit = fps;
        lock (_sessionLock)
            ApplyMinUpdateInterval();
    }

    private void ApplyMinUpdateInterval()
    {
        if (_session == null) return;
        int fps = _fpsLimit;
        TimeSpan interval = fps > 0 ? TimeSpan.FromSeconds(1.0 / fps) : TimeSpan.Zero;
        ApplyRawComSessionSetting(nameof(GraphicsCaptureSession.MinUpdateInterval),
            Direct3DHelper.TrySetMinUpdateInterval, interval);
    }

    private delegate bool RawComSessionSetter(GraphicsCaptureSession session, bool value, out string? error);
    private delegate bool RawComTimeSpanSessionSetter(GraphicsCaptureSession session, TimeSpan value, out string? error);

    private void ApplyRawComSessionSetting(string propertyName, RawComSessionSetter setter, bool value)
    {
        if (_session == null || _unsupportedSessionProperties.Contains(propertyName))
            return;

        if (setter(_session, value, out string? error))
            MarkSessionPropertyApplied(propertyName, value ? "true" : "false");
        else
            MarkSessionPropertyUnsupported(propertyName, error ?? "unknown error");
    }

    private void ApplyRawComSessionSetting(string propertyName, RawComTimeSpanSessionSetter setter, TimeSpan value)
    {
        if (_session == null || _unsupportedSessionProperties.Contains(propertyName))
            return;

        if (setter(_session, value, out string? error))
            MarkSessionPropertyApplied(propertyName, $"{value.TotalMilliseconds:0.###}ms ({value.Ticks} ticks)");
        else
            MarkSessionPropertyUnsupported(propertyName, error ?? "unknown error");
    }

    private void MarkSessionPropertyApplied(string propertyName, string value)
    {
        string key = $"{propertyName}={value}";
        if (!_loggedSessionPropertyApplications.Add(key))
            return;

        Logger.Debug("WGC optional session property {Property} applied for {Display}: {Value}",
            propertyName,
            Display.DeviceName,
            value);
        AmbilightWindowsDiagnostics.Write(Logger,
            $"WGC optional session property {propertyName} applied for {Display.DeviceName}: {value}");
    }

    private void MarkSessionPropertyUnsupported(string propertyName, string reason)
    {
        if (!_unsupportedSessionProperties.Add(propertyName))
            return;

        Logger.Debug("WGC optional session property {Property} is unavailable for {Display}: {Reason}",
            propertyName,
            Display.DeviceName,
            reason);
        AmbilightWindowsDiagnostics.Write(Logger,
            $"WGC optional session property {propertyName} unavailable for {Display.DeviceName}: {reason}");
    }

    /// <summary>
    /// Stops the capture session and releases its resources.
    /// Only called during Restart/Dispose — not between frames.
    /// </summary>
    private void StopSession()
    {
        lock (_sessionLock)
        {
            lock (_frameLock)
            {
                _pendingFrame?.Dispose();
                _pendingFrame = null;
            }
            _heldFrame?.Dispose();
            _heldFrame = null;
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
        }
    }

    private void StopSessionUnlocked()
    {
        // Called from within _sessionLock — direct disposal without re-locking
        lock (_frameLock)
        {
            _pendingFrame?.Dispose();
            _pendingFrame = null;
        }
        _heldFrame?.Dispose();
        _heldFrame = null;
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

        if (!_captureAttemptLogged)
        {
            _captureAttemptLogged = true;
            int zoneCount;
            lock (_zones)
                zoneCount = _zones.Count;
            AmbilightWindowsDiagnostics.Write(Logger,
                $"WGC first capture call for {Display.DeviceName}; minDownscale={minDownscale}; zones={zoneCount}");
        }

        // Session stays alive between frames — avoids costly DWM setup/teardown.
        EnsureCaptureItem();
        StartSession();

        // Pool size 2 + frame holding: one buffer is occupied by _heldFrame, the
        // other was pre-filled by DWM during our sleep.  Grab the pre-filled frame,
        // then release the old one — DWM refills that buffer and stops (0 free).
        Direct3D11CaptureFrame? frame;
        lock (_frameLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        // Keep polling as a fallback, but prefer frames delivered by FrameArrived.
        frame ??= _framePool?.TryGetNextFrame();
        if (frame == null)
        {
#if DEBUG
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now >= _nextNoFrameLog)
            {
                long arrived = Interlocked.Read(ref _framesArrived);
                long consumed = Interlocked.Read(ref _framesConsumed);
                Logger.Debug("WGC has no frame yet for {Display}; arrived={Arrived} consumed={Consumed}",
                    Display.DeviceName,
                    arrived,
                    consumed);
                AmbilightWindowsDiagnostics.Write(Logger,
                    $"WGC has no frame yet for {Display.DeviceName}; arrived={arrived} consumed={consumed}");
                _nextNoFrameLog = now + TimeSpan.FromSeconds(5);
            }
#endif
            return false;
        }

        Interlocked.Increment(ref _framesConsumed);

        // Release previously held frame AFTER grabbing the new one.
        // This is the key ordering: grab → release → DWM fills 1 → stops.
        // (Releasing first would let DWM fill both, wasting a GPU copy.)
        _heldFrame?.Dispose();
        _heldFrame = null;

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

            // Hold this frame — keeps 1 pool buffer occupied so DWM has only 1 free.
            // DWM fills the free one and stops doing capture copies until next grab.
            _heldFrame = frame;
            frame = null; // prevent disposal in finally

            Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(true));
            return true;
        }
        finally
        {
            frame?.Dispose(); // only reached on processing failure
        }
    }

    #endregion

    #region IScreenCapture

    public ICaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        var zone = new WgcCaptureZone(Display, x, y, width, height, downscaleLevel);
        lock (_zones)
            _zones.Add(zone);
        AmbilightWindowsDiagnostics.Write(Logger,
            $"registered WGC capture zone for {Display.DeviceName}: {x},{y} {width}x{height} downscale={downscaleLevel}");
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

#endif
