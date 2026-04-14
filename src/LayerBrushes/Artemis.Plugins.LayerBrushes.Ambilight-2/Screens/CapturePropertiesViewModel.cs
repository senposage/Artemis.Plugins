using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Threading.Tasks;
using Artemis.Plugins.LayerBrushes.Ambilight.PropertyGroups;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using Artemis.UI.Shared.LayerBrushes;
using Artemis.UI.Shared.Services;
using Artemis.UI.Shared.Services.Builders;
using Avalonia.Threading;
using DynamicData;
using ReactiveUI;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.Screens;

public class CapturePropertiesViewModel : BrushConfigurationViewModel
{
    private readonly ObservableAsPropertyHelper<int> _maxHeight;
    private readonly ObservableAsPropertyHelper<int> _maxWidth;
    private readonly ObservableAsPropertyHelper<int> _maxX;
    private readonly ObservableAsPropertyHelper<int> _maxY;
    private readonly ObservableAsPropertyHelper<bool> _showDownscaleWarning;
    
    private readonly AmbilightCaptureProperties _properties;
    private readonly DispatcherTimer _updateTimer;
    private readonly IWindowService _windowService;
    private bool _blackBarDetectionBottom;
    private bool _blackBarDetectionLeft;
    private bool _blackBarDetectionRight;
    private int _blackBarDetectionThreshold;
    private bool _blackBarDetectionTop;

    private CaptureRegionDisplayViewModel? _captureRegionDisplay;
    private CaptureRegionEditorViewModel? _captureRegionEditor;
    private int _downscaleLevel;
    private bool _enableValidation;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private int _height;
    private CaptureScreenViewModel? _selectedCaptureScreen;
    private int _width;
    private int _x;
    private int _y;
    private bool _saveOnChange;

    // Color controls (stored as percentages in the UI, converted to 0-1 / -1..1 floats when saving)
    private int _brightness;
    private int _contrast;
    private int _saturation;
    private int _colorTemperature;
    private int _blackPoint;
    private int _whitePoint;
    private int _autoExposureStrength;
    private int _highlightCompression;

    // Smoothing & performance
    private int _smoothingFactor;
    private int _frameSkip;
    private int _captureFpsLimit;

    public CapturePropertiesViewModel(AmbilightLayerBrush layerBrush, IWindowService windowService) : base(layerBrush)
    {
        _windowService = windowService;
        _properties = layerBrush.Properties.Capture;

        AmbilightLayerBrush = layerBrush;
        CaptureScreens = new ObservableCollection<CaptureScreenViewModel>();
        ResetRegion = ReactiveCommand.Create(ExecuteResetRegion);

        _maxX = this.WhenAnyValue(vm => vm.Width, vm => vm.SelectedCaptureScreen, (width, screen) => (screen?.Display.Width ?? 0) - width).ToProperty(this, vm => vm.MaxX);
        _maxY = this.WhenAnyValue(vm => vm.Height, vm => vm.SelectedCaptureScreen, (height, screen) => (screen?.Display.Height ?? 0) - height).ToProperty(this, vm => vm.MaxY);
        _maxWidth = this.WhenAnyValue(vm => vm.X, vm => vm.SelectedCaptureScreen, (x, screen) => (screen?.Display.Width ?? 0) - x).ToProperty(this, vm => vm.MaxWidth);
        _maxHeight = this.WhenAnyValue(vm => vm.Y, vm => vm.SelectedCaptureScreen, (y, screen) => (screen?.Display.Height ?? 0) - y).ToProperty(this, vm => vm.MaxHeight);
        _showDownscaleWarning = this.WhenAnyValue(vm => vm.DownscaleLevel, s => s < 3).ToProperty(this, vm => vm.ShowDownscaleWarning);
        _updateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, (_, _) => Update());
        _updateTimer.Start();

        ViewForMixins.WhenActivated((IActivatableViewModel) this, (CompositeDisposable d) =>
        {
            Initialize(d);
            this.WhenAnyValue(vm => vm.SelectedCaptureScreen).Subscribe(OnSelectedCaptureScreenChanged);
        });
    }
    
    public AmbilightLayerBrush AmbilightLayerBrush { get; }
    public ObservableCollection<CaptureScreenViewModel> CaptureScreens { get; }

    public int MaxX => _maxX.Value;
    public int MaxY => _maxY.Value;
    public int MaxWidth => _maxWidth.Value;
    public int MaxHeight => _maxHeight.Value;
    public bool ShowDownscaleWarning => _showDownscaleWarning.Value;
    
    public CaptureRegionEditorViewModel? CaptureRegionEditor
    {
        get => _captureRegionEditor;
        set => RaiseAndSetIfChanged(ref _captureRegionEditor, value);
    }

    public CaptureRegionDisplayViewModel? CaptureRegionDisplay
    {
        get => _captureRegionDisplay;
        set => RaiseAndSetIfChanged(ref _captureRegionDisplay, value);
    }

    public CaptureScreenViewModel? SelectedCaptureScreen
    {
        get => _selectedCaptureScreen;
        set => RaiseAndSetIfChanged(ref _selectedCaptureScreen, value);
    }

    public ReactiveCommand<Unit, Unit> ResetRegion { get; }

    public int X
    {
        get => _x;
        set => RaiseAndSetIfChanged(ref _x, value);
    }

    public int Y
    {
        get => _y;
        set => RaiseAndSetIfChanged(ref _y, value);
    }

    public int Width
    {
        get => _width;
        set => RaiseAndSetIfChanged(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => RaiseAndSetIfChanged(ref _height, value);
    }

    public bool FlipHorizontal
    {
        get => _flipHorizontal;
        set => RaiseAndSetIfChanged(ref _flipHorizontal, value);
    }

    public bool FlipVertical
    {
        get => _flipVertical;
        set => RaiseAndSetIfChanged(ref _flipVertical, value);
    }

    public int DownscaleLevel
    {
        get => _downscaleLevel;
        set => RaiseAndSetIfChanged(ref _downscaleLevel, value);
    }

    public bool BlackBarDetectionTop
    {
        get => _blackBarDetectionTop;
        set => RaiseAndSetIfChanged(ref _blackBarDetectionTop, value);
    }

    public bool BlackBarDetectionBottom
    {
        get => _blackBarDetectionBottom;
        set => RaiseAndSetIfChanged(ref _blackBarDetectionBottom, value);
    }

    public bool BlackBarDetectionLeft
    {
        get => _blackBarDetectionLeft;
        set => RaiseAndSetIfChanged(ref _blackBarDetectionLeft, value);
    }

    public bool BlackBarDetectionRight
    {
        get => _blackBarDetectionRight;
        set => RaiseAndSetIfChanged(ref _blackBarDetectionRight, value);
    }

    public int BlackBarDetectionThreshold
    {
        get => _blackBarDetectionThreshold;
        set => RaiseAndSetIfChanged(ref _blackBarDetectionThreshold, value);
    }

    // Color controls (UI shows percentages: -100..100 or 0..100)
    public int Brightness
    {
        get => _brightness;
        set => RaiseAndSetIfChanged(ref _brightness, value);
    }

    public int Contrast
    {
        get => _contrast;
        set => RaiseAndSetIfChanged(ref _contrast, value);
    }

    public int Saturation
    {
        get => _saturation;
        set => RaiseAndSetIfChanged(ref _saturation, value);
    }

    public int ColorTemperature
    {
        get => _colorTemperature;
        set => RaiseAndSetIfChanged(ref _colorTemperature, value);
    }

    public int BlackPoint
    {
        get => _blackPoint;
        set => RaiseAndSetIfChanged(ref _blackPoint, value);
    }

    public int WhitePoint
    {
        get => _whitePoint;
        set => RaiseAndSetIfChanged(ref _whitePoint, value);
    }

    public int AutoExposureStrength
    {
        get => _autoExposureStrength;
        set => RaiseAndSetIfChanged(ref _autoExposureStrength, value);
    }

    public int HighlightCompression
    {
        get => _highlightCompression;
        set => RaiseAndSetIfChanged(ref _highlightCompression, value);
    }

    // Smoothing & performance
    public int SmoothingFactor
    {
        get => _smoothingFactor;
        set => RaiseAndSetIfChanged(ref _smoothingFactor, value);
    }

    public int FrameSkip
    {
        get => _frameSkip;
        set => RaiseAndSetIfChanged(ref _frameSkip, value);
    }

    public int CaptureFpsLimit
    {
        get => _captureFpsLimit;
        set => RaiseAndSetIfChanged(ref _captureFpsLimit, value);
    }

    public bool EnableValidation
    {
        get => _enableValidation;
        set => RaiseAndSetIfChanged(ref _enableValidation, value);
    }

    private IScreenCaptureService _screenCaptureService => AmbilightBootstrapper.ScreenCaptureService!;

    public void Load()
    {
        X = _properties.X;
        Y = _properties.Y;
        Width = _properties.Width;
        Height = _properties.Height;

        FlipHorizontal = _properties.FlipHorizontal;
        FlipVertical = _properties.FlipVertical;
        DownscaleLevel = _properties.DownscaleLevel;

        BlackBarDetectionTop = _properties.BlackBarDetectionTop;
        BlackBarDetectionBottom = _properties.BlackBarDetectionBottom;
        BlackBarDetectionLeft = _properties.BlackBarDetectionLeft;
        BlackBarDetectionRight = _properties.BlackBarDetectionRight;
        BlackBarDetectionThreshold = _properties.BlackBarDetectionThreshold;

        Brightness = (int)((_properties.Brightness.CurrentValue + 1f) * 100f);
        Contrast = (int)((_properties.Contrast.CurrentValue + 1f) * 100f);
        Saturation = (int)((_properties.Saturation.CurrentValue + 1f) * 100f);
        ColorTemperature = (int)(6500f - _properties.ColorTemperature.CurrentValue * 5500f);
        BlackPoint = _properties.BlackPoint;
        WhitePoint = _properties.WhitePoint;
        AutoExposureStrength = (int)(_properties.AutoExposureStrength.CurrentValue * 100f);
        HighlightCompression = (int)(_properties.HighlightCompression.CurrentValue * 100f);
        SmoothingFactor = (int)(_properties.SmoothingFactor.CurrentValue * 100f);
        FrameSkip = _properties.FrameSkip;
        CaptureFpsLimit = _properties.CaptureFpsLimit;

        if (_properties.CaptureFullScreen && SelectedCaptureScreen != null)
        {
            X = 0;
            Y = 0;
            Width = SelectedCaptureScreen.Display.Width;
            Height = SelectedCaptureScreen.Display.Height;
        }
    }

    public void Save()
    {
        _properties.X.SetCurrentValue(X);
        _properties.Y.SetCurrentValue(Y);
        _properties.Width.SetCurrentValue(Width);
        _properties.Height.SetCurrentValue(Height);

        _properties.FlipHorizontal.SetCurrentValue(FlipHorizontal);
        _properties.FlipVertical.SetCurrentValue(FlipVertical);
        _properties.DownscaleLevel.SetCurrentValue(DownscaleLevel);

        _properties.BlackBarDetectionTop.SetCurrentValue(BlackBarDetectionTop);
        _properties.BlackBarDetectionBottom.SetCurrentValue(BlackBarDetectionBottom);
        _properties.BlackBarDetectionLeft.SetCurrentValue(BlackBarDetectionLeft);
        _properties.BlackBarDetectionRight.SetCurrentValue(BlackBarDetectionRight);
        _properties.BlackBarDetectionThreshold.SetCurrentValue(BlackBarDetectionThreshold);

        _properties.Brightness.SetCurrentValue((Brightness / 100f) - 1f);
        _properties.Contrast.SetCurrentValue((Contrast / 100f) - 1f);
        _properties.Saturation.SetCurrentValue((Saturation / 100f) - 1f);
        _properties.ColorTemperature.SetCurrentValue((6500f - ColorTemperature) / 5500f);
        _properties.BlackPoint.SetCurrentValue(BlackPoint);
        _properties.WhitePoint.SetCurrentValue(WhitePoint);
        _properties.AutoExposureStrength.SetCurrentValue(AutoExposureStrength / 100f);
        _properties.HighlightCompression.SetCurrentValue(HighlightCompression / 100f);
        _properties.SmoothingFactor.SetCurrentValue(SmoothingFactor / 100f);
        _properties.FrameSkip.SetCurrentValue(FrameSkip);
        _properties.CaptureFpsLimit.SetCurrentValue(CaptureFpsLimit);

        if (SelectedCaptureScreen != null)
        {
            _properties.GraphicsCardVendorId.SetCurrentValue(SelectedCaptureScreen.Display.GraphicsCard.VendorId);
            _properties.GraphicsCardDeviceId.SetCurrentValue(SelectedCaptureScreen.Display.GraphicsCard.DeviceId);
            _properties.DisplayName.SetCurrentValue(SelectedCaptureScreen.Display.DeviceName);

            if (OperatingSystem.IsWindows())
                _properties.MonitorDevicePath.SetCurrentValue(MonitorIdentifier.GetMonitorDevicePath(SelectedCaptureScreen.Display.DeviceName) ?? "");

            _properties.CaptureFullScreen.SetCurrentValue(X == 0 &&
                                                          Y == 0 &&
                                                          SelectedCaptureScreen.Display.Width == Width &&
                                                          SelectedCaptureScreen.Display.Height == Height);

            CaptureRegionDisplay = new CaptureRegionDisplayViewModel(SelectedCaptureScreen.Display, _properties);
        }

        Task.Run(() => AmbilightLayerBrush.RecreateCaptureZone()).ConfigureAwait(false);
    }

    private async void Initialize(CompositeDisposable d)
    {
        if (!await CreateCaptureScreens())
            return;

        Load();
        _saveOnChange = true;
        EnableValidation = true;

        Disposable.Create(() =>
        {
            CaptureScreens.Clear();
            _updateTimer.Stop();
        }).DisposeWith(d);
    }

    private async Task<bool> CreateCaptureScreens()
    {
        CaptureScreens.AddRange(_screenCaptureService.GetGraphicsCards()
            .SelectMany(gg => _screenCaptureService.GetDisplays(gg))
            .Select(d => new CaptureScreenViewModel(d))
            .ToList());

        if (!CaptureScreens.Any())
        {
            await _windowService.CreateContentDialog().WithTitle("No displays found").WithContent("The plugin could not locate any displays. Try updating your graphics drivers")
                .WithDefaultButton(ContentDialogButton.Close)
                .ShowAsync();

            RequestClose();
            return false;
        }

        // Try to match by stable monitor path first (survives display ID shifts)
        CaptureScreenViewModel? matched = null;
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(_properties.MonitorDevicePath.CurrentValue))
        {
            var monitorMap = MonitorIdentifier.BuildMonitorToAdapterMap();
            if (monitorMap.TryGetValue(_properties.MonitorDevicePath.CurrentValue, out string? currentAdapterName))
                matched = CaptureScreens.FirstOrDefault(s => s.Display.DeviceName.Equals(currentAdapterName, StringComparison.OrdinalIgnoreCase));
        }

        // Fall back to legacy GPU + DisplayName matching
        matched ??= CaptureScreens.FirstOrDefault(s => s.Display.GraphicsCard.VendorId == _properties.GraphicsCardVendorId &&
                                                        s.Display.GraphicsCard.DeviceId == _properties.GraphicsCardDeviceId &&
                                                        s.Display.DeviceName == _properties.DisplayName.CurrentValue);

        SelectedCaptureScreen = matched ?? CaptureScreens.First();
        SelectedCaptureScreen.IsSelected = true;
        return true;
    }

    private void Update()
    {
        foreach (CaptureScreenViewModel captureScreenViewModel in CaptureScreens)
            captureScreenViewModel.Update();
        CaptureRegionEditor?.Update();
        CaptureRegionDisplay?.Update();
    }

    private void ExecuteResetRegion()
    {
        if (SelectedCaptureScreen == null)
            return;

        X = 0;
        Y = 0;
        Width = SelectedCaptureScreen.Display.Width;
        Height = SelectedCaptureScreen.Display.Height;

        Save();
    }

    private void OnSelectedCaptureScreenChanged(CaptureScreenViewModel? selected)
    {
        foreach (CaptureScreenViewModel captureScreen in CaptureScreens)
            captureScreen.IsSelected = captureScreen == selected;

        if (SelectedCaptureScreen == null)
            return;

        // Reset the region if it no longer fits
        if (X + Width > SelectedCaptureScreen.Display.Width || Y + Height > SelectedCaptureScreen.Display.Height)
            ExecuteResetRegion();
        // Recreate the region editor for the screen
        if (CaptureRegionEditor?.Display != SelectedCaptureScreen.Display)
            CaptureRegionEditor = new CaptureRegionEditorViewModel(this, SelectedCaptureScreen.Display);
        if (CaptureRegionDisplay?.Display != SelectedCaptureScreen.Display)
            CaptureRegionDisplay = new CaptureRegionDisplayViewModel(SelectedCaptureScreen.Display, _properties);
        
        if (_saveOnChange)
            Save();
    }
}