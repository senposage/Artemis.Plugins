using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using ScreenCapture.NET;
using Serilog;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;

/// <summary>
/// IScreenCaptureService that uses Windows Graphics Capture for frame acquisition
/// and reuses DX11ScreenCaptureService for display/GPU enumeration (which is lightweight).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcScreenCaptureService : IScreenCaptureService
{
    private static readonly ILogger Logger = Log.ForContext<WgcScreenCaptureService>();

    private readonly DX11ScreenCaptureService _enumerationService;
    private readonly Dictionary<Display, WgcScreenCapture> _captures = [];

    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;

    public WgcScreenCaptureService()
    {
        _enumerationService = new DX11ScreenCaptureService();
        _device = Direct3DHelper.CreateD3D11Device();
        _winrtDevice = Direct3DHelper.CreateDirect3DDevice(_device);

        // Self-test: verify WGC can actually create a capture item for the first monitor.
        // If this fails the exception propagates to the bootstrapper, which falls back to DX11.
        foreach (GraphicsCard gc in _enumerationService.GetGraphicsCards())
        {
            foreach (Display display in _enumerationService.GetDisplays(gc))
            {
                IntPtr hMon = Direct3DHelper.GetMonitorHandle(display.DeviceName);
                if (hMon != IntPtr.Zero)
                {
                    Logger.Debug("Running WGC self-test on {Display} (HMONITOR 0x{Mon:X})", display.DeviceName, hMon);
                    WgcScreenCapture.SelfTest(hMon);
                    Logger.Debug("WGC self-test passed");
                    return; // one successful test is enough
                }
            }
        }

        Logger.Warning("WGC self-test skipped — no monitors found via enumeration");
    }

    /// <summary>
    /// Returns true if WGC is available on this system (Windows 10 1903+).
    /// </summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<GraphicsCard> GetGraphicsCards() => _enumerationService.GetGraphicsCards();
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard) => _enumerationService.GetDisplays(graphicsCard);

    public IScreenCapture GetScreenCapture(Display display)
    {
        lock (_captures)
        {
            if (!_captures.TryGetValue(display, out WgcScreenCapture? capture))
                _captures[display] = capture = new WgcScreenCapture(display, _device, _winrtDevice);
            return capture;
        }
    }

    public void Dispose()
    {
        lock (_captures)
        {
            foreach (WgcScreenCapture capture in _captures.Values)
                capture.Dispose();
            _captures.Clear();
        }

        _winrtDevice.Dispose();
        _device.Dispose();
        _enumerationService.Dispose();
    }
}
