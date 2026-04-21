using System;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal enum PortalPipeWirePixelFormat
{
    Bgrx,
    Bgra,
    Rgbx,
    Rgba,
    Xrgb,
    Argb,
    Xbgr,
    Abgr
}

internal delegate void PortalPipeWireFrameConsumer(ReadOnlySpan<byte> frame, int sourceWidth, int sourceHeight, int sourceDownscaleLevel, int sourceStride, PortalPipeWirePixelFormat pixelFormat);

internal interface IPortalPipeWireFrameReader : IDisposable, ICaptureBackendStatus
{
    void Configure(int sourceDownscaleLevel, int fpsLimit);

    bool TryUseLatestFrame(PortalPipeWireFrameConsumer consumer);

    void Restart();
}
