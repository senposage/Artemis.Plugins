using System.Text.RegularExpressions;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Converts Shadertoy / GLSL fragment shaders to OpenGL ES 3.0 GLSL for ANGLE+WARP.
///
/// Shadertoy GLSL is already very close to GLES 3.0 — the conversion is minimal:
///   • Strip #version  (we inject "#version 300 es")
///   • Strip top-level in/out declarations  (header provides iResolution etc.)
///   • Wrap void mainImage(out vec4, in vec2) with a void main() entry point
///
/// No Y-flip is needed: both Shadertoy and OpenGL ES use bottom-left origin.
/// The Y-flip is handled in GlesShaderRenderer.RenderToBuffer when copying to
/// the SKBitmap (which uses top-left origin).
/// </summary>
internal static partial class ShaderConverter
{
    // Shadertoy uniform block — matches shadertoy.com's exact spec verbatim.
    // iMouse is stubbed to (0,0,0,0); iChannel0..3 are bound to 1×1 black textures.
    internal const string UNIFORM_BLOCK = """
        uniform vec3      iResolution;
        uniform float     iTime;
        uniform float     iTimeDelta;
        uniform float     iFrameRate;
        uniform int       iFrame;
        uniform float     iChannelTime[4];
        uniform vec3      iChannelResolution[4];
        uniform vec4      iMouse;
        uniform sampler2D iChannel0;
        uniform sampler2D iChannel1;
        uniform sampler2D iChannel2;
        uniform sampler2D iChannel3;
        uniform vec4      iDate;
        uniform float     iSampleRate;
        """;

    // Header injected for Shadertoy mainImage() shaders.
    // _fragOut is the actual GLES output; mainImage writes into it via its out param.
    private const string SHADERTOY_HEADER = """
        #version 300 es
        precision highp float;
        precision highp int;

        """ + UNIFORM_BLOCK + """

        out vec4 _fragOut;

        """;

    private const string SHADERTOY_MAIN = """

        void main() {
            mainImage(_fragOut, gl_FragCoord.xy);
        }
        """;

    // Header for classic void main() shaders that output to fragColor.
    private const string CLASSIC_HEADER = """
        #version 300 es
        precision highp float;
        precision highp int;

        """ + UNIFORM_BLOCK + """

        out vec4 fragColor;

        """;

    public static string ConvertToGles(string source)
    {
        string s = source;

        s = VersionRegex().Replace(s, "");          // strip #version
        s = InOutDeclRegex().Replace(s, "");         // strip top-level in/out decls
        s = UniformRedeclRegex().Replace(s, "");     // strip redeclarations of Shadertoy uniforms
        s = FragCoordAliasRegex().Replace(s, "");    // strip `TYPE fc = gl_FragCoord.xy;`

        if (MainImageRegex().IsMatch(s))
            // Shadertoy mainImage() — wrap with our void main()
            return SHADERTOY_HEADER + s + SHADERTOY_MAIN;

        // Classic void main() — inject header that declares `out vec4 fragColor`
        // (the user shader's own `out vec4 fragColor;` was already stripped above)
        return CLASSIC_HEADER + s;
    }

    [GeneratedRegex(@"^\s*#version\b[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\s*(?:in|out)\s+\w+\s+\w+\s*;[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex InOutDeclRegex();

    // Strip redeclarations of the standard Shadertoy uniforms so shaders that
    // define their own copies don't collide with our injected UNIFORM_BLOCK.
    [GeneratedRegex(
        @"^\s*uniform\s+\S+\s+(?:iResolution|iTime|iTimeDelta|iFrameRate|iFrame|iChannelTime|iChannelResolution|iMouse|iChannel0|iChannel1|iChannel2|iChannel3|iDate|iSampleRate)\s*(?:\[\d+\])?\s*;[^\n]*\n?",
        RegexOptions.Multiline)]
    private static partial Regex UniformRedeclRegex();

    [GeneratedRegex(@"^\s*\w+\s+fragCoord\s*=\s*gl_FragCoord\.\w+\s*;[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex FragCoordAliasRegex();

    // The `in` qualifier on the second parameter is optional — many real shaders omit it.
    [GeneratedRegex(@"void\s+mainImage\s*\(\s*out\s+\w+\s+\w+\s*,\s*(?:in\s+)?\w+\s+\w+\s*\)")]
    private static partial Regex MainImageRegex();
}
