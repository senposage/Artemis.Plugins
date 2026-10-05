using System.Reactive;
using Artemis.Core;
using Artemis.UI.Shared;
using ReactiveUI;

namespace Artemis.Plugins.LayerBrushes.Ambilight.Screens;

public class AmbilightConfigurationViewModel : PluginConfigurationViewModel
{
    private readonly AmbilightDiagnosticsSettings _diagnosticsSettings;
    private bool _enableVerboseCaptureDiagnostics;

    public AmbilightConfigurationViewModel(Plugin plugin, AmbilightDiagnosticsSettings diagnosticsSettings) : base(plugin)
    {
        _diagnosticsSettings = diagnosticsSettings;
        EnableVerboseCaptureDiagnostics = diagnosticsSettings.VerboseCaptureDiagnostics;
        SaveChanges = ReactiveCommand.Create(Save);
    }

    public bool EnableVerboseCaptureDiagnostics
    {
        get => _enableVerboseCaptureDiagnostics;
        set => RaiseAndSetIfChanged(ref _enableVerboseCaptureDiagnostics, value);
    }

    public ReactiveCommand<Unit, Unit> SaveChanges { get; }

    private void Save()
    {
        _diagnosticsSettings.SetVerboseCaptureDiagnostics(EnableVerboseCaptureDiagnostics);
        AmbilightWindowsDiagnostics.SetVerboseCaptureDiagnostics(EnableVerboseCaptureDiagnostics);
        Close();
    }
}
