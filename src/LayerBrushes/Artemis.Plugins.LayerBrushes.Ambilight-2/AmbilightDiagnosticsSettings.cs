using Artemis.Core;

namespace Artemis.Plugins.LayerBrushes.Ambilight;

public sealed class AmbilightDiagnosticsSettings
{
    private readonly PluginSetting<bool> _verboseCaptureDiagnostics;

    public AmbilightDiagnosticsSettings(PluginSettings settings)
    {
        _verboseCaptureDiagnostics = settings.GetSetting("VerboseCaptureDiagnostics", false);
    }

    public bool VerboseCaptureDiagnostics => _verboseCaptureDiagnostics.Value;

    public void SetVerboseCaptureDiagnostics(bool enabled)
    {
        _verboseCaptureDiagnostics.Value = enabled;
        _verboseCaptureDiagnostics.Save();
    }
}
