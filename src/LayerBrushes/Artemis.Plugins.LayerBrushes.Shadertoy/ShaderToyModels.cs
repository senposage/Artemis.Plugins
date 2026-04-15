using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>Which render pass type this is.</summary>
public enum PassType { Image, BufferA, BufferB, BufferC, BufferD, Common, Texture }

/// <summary>
/// What a shader channel (iChannel0..3) is sourced from.
/// Anything we can't provide is stubbed with a 1×1 black texture.
/// </summary>
public enum ChannelInputType
{
    None,
    BufferA, BufferB, BufferC, BufferD,
    Keyboard,   // 256×3 — stubbed all-zero (no keys pressed)
    Image,      // static image file loaded from Source path (legacy per-channel path)
    Noise2D,    // stubbed 1×1 black
    Noise3D,    // stubbed
    Audio,
    Texture,    // static image file defined in a Texture pass tab (appended — preserves all prior values)
}

public sealed class PassInput
{
    public int Channel { get; set; }
    public ChannelInputType Type { get; set; }
    /// <summary>File path for <see cref="ChannelInputType.Image"/> inputs.</summary>
    public string? Source { get; set; }
}

public sealed class ShaderPass
{
    public PassType Type { get; set; }
    public string Source { get; set; } = "";
    public PassInput[] Inputs { get; set; } = [];
}

/// <summary>
/// Full shader definition, covering single-pass (one Image pass) and
/// multi-pass (BufferA/B/C/D + Image) Shadertoy shaders.
/// Serialized to JSON and stored in ShaderToyShaderProperties.ShaderJson.
/// </summary>
public sealed class ShaderDefinition
{
    public string? ShadertoyId { get; set; }
    public string Title { get; set; } = "Untitled";
    public ShaderPass[] Passes { get; set; } = [];

    [JsonIgnore]
    public bool IsSinglePass =>
        Passes.Length == 1 && Passes[0].Type == PassType.Image;

    /// <summary>Create a single-pass definition from raw GLSL source.</summary>
    public static ShaderDefinition FromGlsl(string glsl) => new()
    {
        Title = "Custom",
        Passes = [new ShaderPass { Type = PassType.Image, Source = glsl }]
    };
}

