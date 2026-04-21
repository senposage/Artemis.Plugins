namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;

internal interface ICaptureBackendStatus
{
    string CaptureBackendName { get; }
    string CaptureBackendDetails { get; }
}
