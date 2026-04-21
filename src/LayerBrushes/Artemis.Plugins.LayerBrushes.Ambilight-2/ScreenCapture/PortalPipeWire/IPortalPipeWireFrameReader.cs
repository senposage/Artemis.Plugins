using System;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal interface IPortalPipeWireFrameReader : IDisposable
{
    void Configure(int sourceDownscaleLevel, int fpsLimit);

    bool TryCopyLatestFrame(ref byte[] destination, out int sourceWidth, out int sourceHeight, out int sourceDownscaleLevel);

    void Restart();
}
