using Artemis.Core;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes.PropertyGroups;

public class ShaderToyShaderProperties : LayerPropertyGroup
{
    // Shadertoy-format default — paste any Shadertoy shader here.
    // iResolution/iTime/etc. are injected automatically by the converter.
    public const string SHADER_TEMPLATE =
        """
        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(uv, 0.5 + 0.5 * sin(iTime), 1.0);
        }
        """;

    public LayerProperty<string> Shader { get; set; }
    /// <summary>JSON <see cref="ShaderDefinition"/> for multi-pass shaders.
    /// Empty = use <see cref="Shader"/> as a single-pass source.</summary>
    public LayerProperty<string> ShaderJson { get; set; }
    public IntLayerProperty Width     { get; set; }
    public IntLayerProperty Height    { get; set; }
    public IntLayerProperty MaxFps    { get; set; }
    /// <summary>When false, Audio channel inputs receive a silent stub texture.</summary>
    public BoolLayerProperty EnableAudio { get; set; }
    /// <summary>When true, uses cubic (high-quality) filtering when scaling the rendered bitmap to LED bounds.</summary>
    public BoolLayerProperty CubicResize { get; set; }
    /// <summary>
    /// dB level below which FFT bins map to zero.  Raise to suppress noise floor.
    /// Range: -60 (catch everything) to -10 (only loud audio).  Default: -30.
    /// </summary>
    public FloatLayerProperty AudioDbFloor { get; set; }
    /// <summary>EMA alpha for rising edge (0 = instant, 0.99 = very slow).  Higher suppresses transient peaks.</summary>
    public FloatLayerProperty AudioAttack { get; set; }
    /// <summary>EMA alpha for falling edge (0 = instant, 0.99 = very slow).  Lower = bars snap back faster.</summary>
    public FloatLayerProperty AudioSmoothing { get; set; }
    /// <summary>Lowest frequency shown (Hz).  Used by Max and Average modes.</summary>
    public IntLayerProperty AudioMinFreq { get; set; }
    /// <summary>Highest frequency shown (Hz).  Used by Max and Average modes.</summary>
    public IntLayerProperty AudioMaxFreq { get; set; }
    /// <summary>Spectrum display mode: 0=Max, 1=Average, 2=Linear.</summary>
    public IntLayerProperty AudioSpectrumMode { get; set; }

    protected override void PopulateDefaults()
    {
        Shader.DefaultValue               = SHADER_TEMPLATE;
        ShaderJson.DefaultValue           = "";
        Width.DefaultValue                = 512;
        Height.DefaultValue               = 512;
        MaxFps.DefaultValue               = 10;
        EnableAudio.DefaultValue          = true;
        CubicResize.DefaultValue          = false;
        AudioDbFloor.DefaultValue         = -50f;
        AudioAttack.DefaultValue          = 0.7f;
        AudioSmoothing.DefaultValue       = 0.5f;
        AudioMinFreq.DefaultValue         = 20;
        AudioMaxFreq.DefaultValue         = 20_000;
        AudioSpectrumMode.DefaultValue    = 0; // Max
    }

    protected override void EnableProperties() { }
    protected override void DisableProperties() { }
}
