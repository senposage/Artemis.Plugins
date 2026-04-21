using System;
using System.Collections.Generic;
using System.Linq;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using ScreenCapture.NET;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class PortalPipeWireScreenCapture : IScreenCapture, IConfigurableCaptureFps, ICaptureBackendStatus
{
    private static readonly ILogger Logger = Log.ForContext<PortalPipeWireScreenCapture>();

    private readonly PortalPipeWireOutput _output;
    private readonly int _pipeWireRemoteFd;
    private readonly List<PortalPipeWireCaptureZone> _zones = [];
    private readonly object _zonesLock = new();
    private IPortalPipeWireFrameReader _frameReader;
    private PortalPipeWireCaptureZone[] _zoneSnapshot = [];
    private int _fpsLimit = 30;
    private bool _forceGStreamer;
    private int _captureMissLogCount;
    private int _captureSuccessLogCount;

    public Display Display => _output.Display;
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    public PortalPipeWireScreenCapture(PortalPipeWireOutput output, int pipeWireRemoteFd)
    {
        _output = output;
        _pipeWireRemoteFd = pipeWireRemoteFd;
        _frameReader = PortalPipeWireFrameReaderFactory.Create(output, pipeWireRemoteFd);
        AmbilightLinuxDiagnostics.Write(Logger, $"created portal PipeWire screen capture for {output.StableId}");
    }

    public string CaptureBackendName => _frameReader.CaptureBackendName;

    public string CaptureBackendDetails => _frameReader.CaptureBackendDetails;

    public void SetForceGStreamer(bool forceGStreamer)
    {
        lock (_zonesLock)
        {
            if (_forceGStreamer == forceGStreamer)
                return;

            _forceGStreamer = forceGStreamer;
            AmbilightLinuxDiagnostics.Write(Logger,
                forceGStreamer
                    ? $"forcing GStreamer PipeWire reader for {_output.StableId}"
                    : $"allowing direct PipeWire reader for {_output.StableId}");

            _frameReader.Dispose();
            _frameReader = PortalPipeWireFrameReaderFactory.Create(_output, _pipeWireRemoteFd, forceGStreamer);
            ReconfigureFrameReader();
        }
    }

    public ICaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel)
    {
        ValidateCaptureZone(x, y, width, height, downscaleLevel);
        Logger.Information("Registering portal capture zone for {Display}: {X},{Y} {Width}x{Height} downscale={Downscale}",
            Display.DeviceName, x, y, width, height, downscaleLevel);
        AmbilightLinuxDiagnostics.Write(Logger,
            $"registering portal capture zone for {Display.DeviceName}: {x},{y} {width}x{height} downscale={downscaleLevel}");
        var zone = new PortalPipeWireCaptureZone(Display, x, y, width, height, downscaleLevel);
        lock (_zonesLock)
        {
            _zones.Add(zone);
            _zoneSnapshot = _zones.ToArray();
            ReconfigureFrameReader();
        }
        return zone;
    }

    public bool UnregisterCaptureZone(ICaptureZone captureZone)
    {
        lock (_zonesLock)
        {
            bool removed = captureZone is PortalPipeWireCaptureZone zone && _zones.Remove(zone);
            if (removed)
            {
                Logger.Information("Unregistered portal capture zone for {Display}", Display.DeviceName);
                _zoneSnapshot = _zones.ToArray();
                ReconfigureFrameReader();
            }
            return removed;
        }
    }

    public void UpdateCaptureZone(ICaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        if (captureZone is not PortalPipeWireCaptureZone zone)
            throw new ArgumentException("Capture zone was not created by this portal PipeWire capture.", nameof(captureZone));

        ValidateCaptureZone(x ?? zone.X, y ?? zone.Y, width ?? zone.UnscaledWidth, height ?? zone.UnscaledHeight, downscaleLevel ?? zone.DownscaleLevel);
        zone.Update(x, y, width, height, downscaleLevel);
        lock (_zonesLock)
            ReconfigureFrameReader();
    }

    public bool CaptureScreen()
    {
        PortalPipeWireCaptureZone[] zones = _zoneSnapshot;
        bool anyUpdated = false;
        if (!_frameReader.TryUseLatestFrame((frame, sourceWidth, sourceHeight, sourceDownscaleLevel, sourceStride, pixelFormat) =>
            {
                foreach (PortalPipeWireCaptureZone zone in zones)
                {
                    if (!zone.NeedsUpdate)
                        continue;

                    zone.CopyFromFrame(frame, sourceWidth, sourceHeight, sourceDownscaleLevel, sourceStride, pixelFormat);
                    anyUpdated = true;
                }
            }))
        {
            if (_captureMissLogCount < 10)
            {
                _captureMissLogCount++;
                AmbilightLinuxDiagnostics.Write(Logger, $"CaptureScreen did not have a PipeWire frame yet for {Display.DeviceName}");
            }
            return false;
        }

        if (_captureSuccessLogCount < 3)
        {
            _captureSuccessLogCount++;
            AmbilightLinuxDiagnostics.Write(Logger, $"CaptureScreen received a PipeWire frame for {Display.DeviceName}");
        }

        Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(anyUpdated));
        return anyUpdated;
    }

    public void Restart()
    {
        _frameReader.Restart();
    }

    public void SetFpsLimit(int fps)
    {
        lock (_zonesLock)
        {
            fps = fps <= 0 ? 0 : Math.Clamp(fps, 1, PortalPipeWireFrameReader.MaxFpsLimit);
            if (_fpsLimit == fps)
                return;

            _fpsLimit = fps;
            ReconfigureFrameReader();
        }
    }

    public void Dispose()
    {
        _frameReader.Dispose();
        lock (_zonesLock)
        {
            _zones.Clear();
            _zoneSnapshot = [];
        }
    }

    private void ReconfigureFrameReader()
    {
        int sourceDownscaleLevel = _zones.Count == 0 ? 0 : _zones.Min(zone => zone.DownscaleLevel);
        _frameReader.Configure(sourceDownscaleLevel, _fpsLimit);
    }

    private void ValidateCaptureZone(int x, int y, int width, int height, int downscaleLevel)
    {
        if (x < 0 || y < 0)
            throw new ArgumentException("Capture zone coordinates must be positive.");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Capture zone size must be positive.");
        if (downscaleLevel < 0)
            throw new ArgumentException("Downscale level must be positive.");
        if (x + width > Display.Width || y + height > Display.Height)
            throw new ArgumentException("Capture zone must fit inside the display.");
    }
}
