using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed record PortalPipeWireOutput(Display Display, uint NodeId, string StableId, int X, int Y);
