using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Artemis.Plugins.LayerBrushes.Ambilight.Screens;

public partial class CaptureRegionDisplayView : ReactiveUserControl<CaptureRegionDisplayViewModel>
{
    private readonly Image _displayPreviewImage;

    public CaptureRegionDisplayView()
    {
        InitializeComponent();
        _displayPreviewImage = this.Get<Image>("DisplayPreviewImage");
        this.WhenActivated(d =>
        {
            if (ViewModel == null)
                return;

            ViewModel.PreviewImage = _displayPreviewImage;
            Disposable.Create(() => ViewModel.PreviewImage = null).DisposeWith(d);
        });
    }
}
