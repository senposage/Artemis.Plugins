using System;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using Artemis.UI.Shared;
using Avalonia.Controls;
using ReactiveUI;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.Screens;

public class CaptureScreenViewModel : ActivatableViewModelBase
{
    private bool _isSelected;
    private Image? _previewImage;

    public CaptureScreenViewModel(Display display)
    {
        Display = display;
        DisplayName = $"Display {Display.Index + 1}";
        DisplayPreview = new DisplayPreview(Display);

        if (OperatingSystem.IsWindows())
            MonitorDevicePath = MonitorIdentifier.GetMonitorDevicePath(display.DeviceName);

        this.WhenActivated(d => Disposable.Create(() => DisplayPreview.Dispose()).DisposeWith(d));
    }

    public Display Display { get; }
    public DisplayPreview DisplayPreview { get; }
    public string? MonitorDevicePath { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public Image? PreviewImage
    {
        get => _previewImage;
        set => RaiseAndSetIfChanged(ref _previewImage, value);
    }

    public void Update()
    {
        DisplayPreview.Update();
        PreviewImage?.InvalidateVisual();
    }
}