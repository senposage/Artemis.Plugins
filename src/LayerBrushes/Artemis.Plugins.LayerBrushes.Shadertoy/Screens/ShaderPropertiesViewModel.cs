using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes;
using Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes.PropertyGroups;
using Artemis.UI.Shared.LayerBrushes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI;
using SkiaSharp;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.Screens;

public class ShaderPropertiesViewModel : BrushConfigurationViewModel
{
    private SKBitmap? _previewBitmap;
    private readonly ShaderToyShaderProperties _properties;
    private readonly DispatcherTimer _updateTimer;

    // ------------------------------------------------------------------ dimensions / fps / audio
    private int _width;
    public int Width { get => _width; set => RaiseAndSetIfChanged(ref _width, value); }

    private int _height;
    public int Height { get => _height; set => RaiseAndSetIfChanged(ref _height, value); }

    private int _maxFps;
    public int MaxFps { get => _maxFps; set => RaiseAndSetIfChanged(ref _maxFps, value); }

    private bool _enableAudio;
    public bool EnableAudio { get => _enableAudio; set => RaiseAndSetIfChanged(ref _enableAudio, value); }

    private bool _cubicResize;
    public bool CubicResize { get => _cubicResize; set => RaiseAndSetIfChanged(ref _cubicResize, value); }

    private double _audioDbFloor;
    public double AudioDbFloor { get => _audioDbFloor; set => RaiseAndSetIfChanged(ref _audioDbFloor, value); }

    private double _audioAttack;
    public double AudioAttack { get => _audioAttack; set => RaiseAndSetIfChanged(ref _audioAttack, value); }

    private double _audioSmoothing;
    public double AudioSmoothing { get => _audioSmoothing; set => RaiseAndSetIfChanged(ref _audioSmoothing, value); }

    private int _audioMinFreq;
    public int AudioMinFreq { get => _audioMinFreq; set => RaiseAndSetIfChanged(ref _audioMinFreq, value); }

    private int _audioMaxFreq;
    public int AudioMaxFreq { get => _audioMaxFreq; set => RaiseAndSetIfChanged(ref _audioMaxFreq, value); }

    private int _audioSpectrumMode;
    public int AudioSpectrumMode
    {
        get => _audioSpectrumMode;
        set { RaiseAndSetIfChanged(ref _audioSpectrumMode, value); this.RaisePropertyChanged(nameof(AudioFreqRangeVisible)); }
    }

    public bool AudioFreqRangeVisible => _audioSpectrumMode != 2;

    private bool _isLoading = true;

    // ------------------------------------------------------------------ shader error
    private string _shaderException = string.Empty;
    public string ShaderException
    {
        get => _shaderException;
        set => RaiseAndSetIfChanged(ref _shaderException, value);
    }

    // ------------------------------------------------------------------ passes
    public ObservableCollection<PassEditorViewModel> Passes { get; } = [];

    private PassEditorViewModel? _selectedPass;
    public PassEditorViewModel? SelectedPass
    {
        get => _selectedPass;
        set
        {
            RaiseAndSetIfChanged(ref _selectedPass, value);
            this.RaisePropertyChanged(nameof(CanRemoveSelectedPass));
        }
    }

    public bool CanAddBufferA => Passes.All(p => p.Type != PassType.BufferA);
    public bool CanAddBufferB => Passes.All(p => p.Type != PassType.BufferB);
    public bool CanAddBufferC => Passes.All(p => p.Type != PassType.BufferC);
    public bool CanAddBufferD => Passes.All(p => p.Type != PassType.BufferD);
    public bool CanAddCommon  => Passes.All(p => p.Type != PassType.Common);
    public bool CanAddTexture => Passes.All(p => p.Type != PassType.Texture);
    public bool CanRemoveSelectedPass => SelectedPass != null && SelectedPass.Type != PassType.Image;

    // ------------------------------------------------------------------ preset library
    public ObservableCollection<string> PresetNames { get; } = [];

    private static string? _lastSelectedPreset;

    private string? _selectedPreset;
    public string? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            RaiseAndSetIfChanged(ref _selectedPreset, value);
            if (value == null) return;
            SavePresetName    = value;
            _lastSelectedPreset = value;
            LoadSelectedPreset();
        }
    }

    private string _savePresetName = string.Empty;
    public string SavePresetName
    {
        get => _savePresetName;
        set => RaiseAndSetIfChanged(ref _savePresetName, value);
    }

    // ------------------------------------------------------------------ preview
    public Image? DisplayPreviewImage { get; set; }

    private WriteableBitmap? _previewImage;
    public WriteableBitmap? PreviewImage
    {
        get => _previewImage;
        set => RaiseAndSetIfChanged(ref _previewImage, value);
    }

    public ShaderToyLayerBrush ShaderToyLayerBrush { get; }

    // ------------------------------------------------------------------ ctor
    public ShaderPropertiesViewModel(ShaderToyLayerBrush layerBrush) : base(layerBrush)
    {
        ShaderToyLayerBrush = layerBrush;
        _properties = layerBrush.Properties.Shader;

        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 30.0), DispatcherPriority.Normal,
            (_, _) => UpdatePreview());

        Passes.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(CanAddBufferA));
            this.RaisePropertyChanged(nameof(CanAddBufferB));
            this.RaisePropertyChanged(nameof(CanAddBufferC));
            this.RaisePropertyChanged(nameof(CanAddBufferD));
            this.RaisePropertyChanged(nameof(CanAddCommon));
            this.RaisePropertyChanged(nameof(CanAddTexture));
        };

        _properties.Width.PropertyChanged  += OnSizeChanged;
        _properties.Height.PropertyChanged += OnSizeChanged;

        this.WhenActivated(Initialize);
    }

    // ------------------------------------------------------------------ load / save
    public void Load()
    {
        _isLoading = true;
        Width        = _properties.Width;
        Height       = _properties.Height;
        MaxFps       = _properties.MaxFps;
        EnableAudio  = _properties.EnableAudio;
        CubicResize  = _properties.CubicResize;
        AudioDbFloor      = _properties.AudioDbFloor.CurrentValue;
        AudioAttack       = _properties.AudioAttack.CurrentValue;
        AudioSmoothing    = _properties.AudioSmoothing.CurrentValue;
        AudioMinFreq      = _properties.AudioMinFreq.CurrentValue;
        AudioMaxFreq      = _properties.AudioMaxFreq.CurrentValue;
        AudioSpectrumMode = _properties.AudioSpectrumMode.CurrentValue;

        RefreshPresetNames();
        if (_lastSelectedPreset != null && PresetNames.Contains(_lastSelectedPreset))
            _selectedPreset = _lastSelectedPreset;   // set backing field — no reload

        LoadDefinition(ShaderToyLayerBrush.ResolveDefinition());
        _isLoading = false;
    }

    public void Save()
    {
        if (_isLoading) return;
        _properties.Width.SetCurrentValue(Width);
        _properties.Height.SetCurrentValue(Height);
        _properties.MaxFps.SetCurrentValue(MaxFps);
        _properties.EnableAudio.SetCurrentValue(EnableAudio);
        _properties.CubicResize.SetCurrentValue(CubicResize);
        _properties.AudioDbFloor.SetCurrentValue((float)AudioDbFloor);
        _properties.AudioAttack.SetCurrentValue((float)AudioAttack);
        _properties.AudioSmoothing.SetCurrentValue((float)AudioSmoothing);
        _properties.AudioMinFreq.SetCurrentValue(AudioMinFreq);
        _properties.AudioMaxFreq.SetCurrentValue(AudioMaxFreq);
        _properties.AudioSpectrumMode.SetCurrentValue(AudioSpectrumMode);

        var def = BuildDefinition();
        ShaderToyLayerBrush.ApplyDefinition(def);
        ShaderException = ShaderToyLayerBrush.ShaderError ?? string.Empty;
        RecreatePreviewSurface();
    }

    // ------------------------------------------------------------------ pass management
    public void AddPass(PassType type)
    {
        if (Passes.Any(p => p.Type == type)) return;
        var newPass = new PassEditorViewModel(type);

        // Insert in UI order: Image(0), BufferA(1)..D(4), Common(5)
        int pos = UiOrder(type);
        int idx = 0;
        while (idx < Passes.Count && UiOrder(Passes[idx].Type) < pos) idx++;
        Passes.Insert(idx, newPass);
        SelectedPass = newPass;
    }

    public void RemoveSelectedPass()
    {
        if (SelectedPass == null || SelectedPass.Type == PassType.Image) return;
        var remove = SelectedPass;
        SelectedPass = Passes.FirstOrDefault(p => p != remove);
        Passes.Remove(remove);
    }

    private static int UiOrder(PassType t) => t switch
    {
        PassType.Image   => 0,
        PassType.BufferA => 1,
        PassType.BufferB => 2,
        PassType.BufferC => 3,
        PassType.BufferD => 4,
        PassType.Texture => 5,
        PassType.Common  => 6,
        _                => 99
    };

    // ------------------------------------------------------------------ preset commands
    public void LoadSelectedPreset()
    {
        if (SelectedPreset == null || ShaderLibrary.Instance == null) return;
        var entry = ShaderLibrary.Instance.Entries.FirstOrDefault(e => e.Name == SelectedPreset);
        if (entry != null) ApplyDefinition(entry.Shader);
    }

    public void SaveCurrentPreset()
    {
        if (string.IsNullOrWhiteSpace(SavePresetName) || ShaderLibrary.Instance == null) return;
        ShaderLibrary.Instance.Save(SavePresetName, BuildDefinition());
        RefreshPresetNames();
        SavePresetName = string.Empty;
    }

    public void DeleteSelectedPreset()
    {
        if (SelectedPreset == null || ShaderLibrary.Instance == null) return;
        ShaderLibrary.Instance.Delete(SelectedPreset);
        SelectedPreset = null;
        RefreshPresetNames();
    }

    // ------------------------------------------------------------------ helpers
    private void ApplyDefinition(ShaderDefinition def)
    {
        LoadDefinition(def);
        ShaderToyLayerBrush.ApplyDefinition(def);
        ShaderException = ShaderToyLayerBrush.ShaderError ?? string.Empty;
        RecreatePreviewSurface();
    }

    private void LoadDefinition(ShaderDefinition def)
    {
        Passes.Clear();

        // UI tab order: Image first, then Buffers A-D, then Common
        foreach (var pass in def.Passes
            .Where(p => p.Type != PassType.Common)
            .OrderBy(p => UiOrder(p.Type)))
            Passes.Add(new PassEditorViewModel(pass));

        // Ensure there's always an Image tab
        if (!Passes.Any(p => p.Type == PassType.Image))
            Passes.Insert(0, new PassEditorViewModel(PassType.Image));

        // Common last
        var common = def.Passes.FirstOrDefault(p => p.Type == PassType.Common);
        if (common != null) Passes.Add(new PassEditorViewModel(common));

        SelectedPass = Passes.FirstOrDefault();
    }

    private ShaderDefinition BuildDefinition()
    {
        // Execution order: Common → BufferA-D → Image
        var passes = Passes
            .Select(p => p.ToShaderPass())
            .OrderBy(p => p.Type switch
            {
                PassType.Texture => 0,
                PassType.Common  => 1,
                PassType.BufferA => 2,
                PassType.BufferB => 3,
                PassType.BufferC => 4,
                PassType.BufferD => 5,
                PassType.Image   => 6,
                _                => 7
            })
            .ToArray();
        return new ShaderDefinition { Title = "Custom", Passes = passes };
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        if (ShaderLibrary.Instance == null) return;
        foreach (var e in ShaderLibrary.Instance.Entries)
            PresetNames.Add(e.Name);
    }

    // ------------------------------------------------------------------ mouse input
    internal void UpdateMouse(float shaderX, float shaderY, bool pressed, float clickX, float clickY)
        => ShaderToyLayerBrush.SetMouse(shaderX, shaderY, pressed, clickX, clickY);

    // ------------------------------------------------------------------ lifecycle
    private void Initialize(CompositeDisposable d)
    {
        Load();
        RecreatePreviewSurface();
        _updateTimer.Start();

        Disposable.Create(() =>
        {
            _updateTimer.Stop();
            _previewBitmap?.Dispose();
            _previewBitmap = null;
        }).DisposeWith(d);
    }

    private unsafe void UpdatePreview()
    {
        if (PreviewImage == null || _previewBitmap == null) return;
        if (!ShaderToyLayerBrush.RenderPreview(_previewBitmap)) return;

        using ILockedFramebuffer fb = PreviewImage.Lock();
        var src = new ReadOnlySpan<byte>((void*)_previewBitmap.GetPixels(), _previewBitmap.ByteCount);
        src.CopyTo(new Span<byte>((void*)fb.Address, src.Length));
        DisplayPreviewImage?.InvalidateVisual();
    }

    private void RecreatePreviewSurface()
    {
        int w = Math.Max(1, _properties.Width);
        int h = Math.Max(1, _properties.Height);

        PreviewImage?.Dispose();
        PreviewImage = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                           PixelFormat.Rgba8888, AlphaFormat.Opaque);
        _previewBitmap?.Dispose();
        _previewBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
    }

    private void OnSizeChanged(object? sender, PropertyChangedEventArgs e)
    {
        RecreatePreviewSurface();
        ShaderToyLayerBrush.RecreateRenderer();
        ShaderException = ShaderToyLayerBrush.ShaderError ?? string.Empty;
    }
}
