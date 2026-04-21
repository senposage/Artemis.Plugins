using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Artemis.Plugins.LayerBrushes.Ambilight.Screens;

public partial class CaptureScreenView : ReactiveUserControl<CaptureScreenViewModel>
{
    public CaptureScreenView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            if (ViewModel == null)
                return;

            ViewModel.PreviewImage = DisplayPreviewImage;
            Disposable.Create(() => ViewModel.PreviewImage = null).DisposeWith(d);
        });
    }
}
