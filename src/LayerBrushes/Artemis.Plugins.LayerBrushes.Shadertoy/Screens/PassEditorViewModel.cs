using System.Collections.Generic;
using System.Linq;
using ReactiveUI;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.Screens;

/// <summary>Wraps a <see cref="ChannelInputType"/> with a display name for ComboBox binding.</summary>
public sealed record ChannelOption(ChannelInputType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>ViewModel for a single shader pass (Image, Buffer A-D, or Common).</summary>
public class PassEditorViewModel : ReactiveObject
{
    // ------------------------------------------------------------------ static options

    public static readonly ChannelOption[] ChannelOptions =
    [
        new(ChannelInputType.None,     "None"),
        new(ChannelInputType.BufferA,  "Buffer A"),
        new(ChannelInputType.BufferB,  "Buffer B"),
        new(ChannelInputType.BufferC,  "Buffer C"),
        new(ChannelInputType.BufferD,  "Buffer D"),
        new(ChannelInputType.Texture,  "Image (Texture pass)"),
        new(ChannelInputType.Audio,    "Audio"),
        new(ChannelInputType.Keyboard, "Keyboard"),
        new(ChannelInputType.Noise2D,  "Noise 2D"),
        new(ChannelInputType.Image,    "Image (file)"),
    ];

    private static ChannelOption OptionFor(ChannelInputType t) =>
        ChannelOptions.FirstOrDefault(o => o.Value == t) ?? ChannelOptions[0];

    // ------------------------------------------------------------------ identity

    public PassType Type { get; }

    public string TabTitle => Type switch
    {
        PassType.Image   => "Image",
        PassType.Common  => "Common",
        PassType.BufferA => "Buffer A",
        PassType.BufferB => "Buffer B",
        PassType.BufferC => "Buffer C",
        PassType.BufferD => "Buffer D",
        PassType.Texture => "Image File",
        _                => Type.ToString()
    };

    /// <summary>Channel selectors are shown for shader passes only.</summary>
    public bool ShowChannels => Type != PassType.Common && Type != PassType.Texture;

    /// <summary>True for the Texture pass — shows a file path field instead of a GLSL editor.</summary>
    public bool IsTexture => Type == PassType.Texture;
    public bool IsShader  => Type != PassType.Texture;

    // ------------------------------------------------------------------ source

    private string _source = string.Empty;
    public string Source { get => _source; set => this.RaiseAndSetIfChanged(ref _source, value); }

    // ------------------------------------------------------------------ channel selectors

    private ChannelOption _ch0 = ChannelOptions[0];
    private ChannelOption _ch1 = ChannelOptions[0];
    private ChannelOption _ch2 = ChannelOptions[0];
    private ChannelOption _ch3 = ChannelOptions[0];

    public ChannelOption Ch0 { get => _ch0; set { this.RaiseAndSetIfChanged(ref _ch0, value); this.RaisePropertyChanged(nameof(IsImageCh0)); } }
    public ChannelOption Ch1 { get => _ch1; set { this.RaiseAndSetIfChanged(ref _ch1, value); this.RaisePropertyChanged(nameof(IsImageCh1)); } }
    public ChannelOption Ch2 { get => _ch2; set { this.RaiseAndSetIfChanged(ref _ch2, value); this.RaisePropertyChanged(nameof(IsImageCh2)); } }
    public ChannelOption Ch3 { get => _ch3; set { this.RaiseAndSetIfChanged(ref _ch3, value); this.RaisePropertyChanged(nameof(IsImageCh3)); } }

    public bool IsImageCh0 => _ch0.Value == ChannelInputType.Image;
    public bool IsImageCh1 => _ch1.Value == ChannelInputType.Image;
    public bool IsImageCh2 => _ch2.Value == ChannelInputType.Image;
    public bool IsImageCh3 => _ch3.Value == ChannelInputType.Image;

    // File paths for Image-type channels
    private string _ch0Source = string.Empty;
    private string _ch1Source = string.Empty;
    private string _ch2Source = string.Empty;
    private string _ch3Source = string.Empty;

    public string Ch0Source { get => _ch0Source; set => this.RaiseAndSetIfChanged(ref _ch0Source, value); }
    public string Ch1Source { get => _ch1Source; set => this.RaiseAndSetIfChanged(ref _ch1Source, value); }
    public string Ch2Source { get => _ch2Source; set => this.RaiseAndSetIfChanged(ref _ch2Source, value); }
    public string Ch3Source { get => _ch3Source; set => this.RaiseAndSetIfChanged(ref _ch3Source, value); }

    // ------------------------------------------------------------------ ctors

    public PassEditorViewModel(PassType type) => Type = type;

    public PassEditorViewModel(ShaderPass pass)
    {
        Type    = pass.Type;
        _source = pass.Source;
        foreach (var inp in pass.Inputs)
        {
            var opt = OptionFor(inp.Type);
            switch (inp.Channel)
            {
                case 0: _ch0 = opt; _ch0Source = inp.Source ?? ""; break;
                case 1: _ch1 = opt; _ch1Source = inp.Source ?? ""; break;
                case 2: _ch2 = opt; _ch2Source = inp.Source ?? ""; break;
                case 3: _ch3 = opt; _ch3Source = inp.Source ?? ""; break;
            }
        }
    }

    // ------------------------------------------------------------------ auto-detect

    /// <summary>
    /// Scans the pass source for channel-type hints (e.g. "// iChannel0: noise")
    /// and fills any channel currently set to <see cref="ChannelInputType.None"/>.
    /// Existing non-None assignments are never overwritten.
    /// </summary>
    public void SuggestChannels()
    {
        if (!IsShader) return;
        var hints = ShaderChannelHints.Detect(Source);
        if (hints.TryGetValue(0, out var t0) && Ch0.Value == ChannelInputType.None) Ch0 = OptionFor(t0);
        if (hints.TryGetValue(1, out var t1) && Ch1.Value == ChannelInputType.None) Ch1 = OptionFor(t1);
        if (hints.TryGetValue(2, out var t2) && Ch2.Value == ChannelInputType.None) Ch2 = OptionFor(t2);
        if (hints.TryGetValue(3, out var t3) && Ch3.Value == ChannelInputType.None) Ch3 = OptionFor(t3);
    }

    // ------------------------------------------------------------------ export

    public ShaderPass ToShaderPass()
    {
        var inputs = new List<PassInput>();
        void Add(ChannelOption o, string src, int ch)
        {
            if (o.Value != ChannelInputType.None)
                inputs.Add(new PassInput { Channel = ch, Type = o.Value, Source = string.IsNullOrWhiteSpace(src) ? null : src });
        }
        Add(_ch0, _ch0Source, 0);
        Add(_ch1, _ch1Source, 1);
        Add(_ch2, _ch2Source, 2);
        Add(_ch3, _ch3Source, 3);
        return new ShaderPass { Type = Type, Source = Source, Inputs = [.. inputs] };
    }
}
