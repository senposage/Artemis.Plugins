using System.IO;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.Screens;

/// <summary>
/// Provides a GLSL syntax highlighting definition for AvaloniaEdit.
/// Covers GLSL types, control flow, built-in functions, and Shadertoy uniforms.
/// Colors follow VS Code dark theme conventions.
/// </summary>
internal static class GlslHighlighting
{
    private static IHighlightingDefinition? _definition;

    public static IHighlightingDefinition Definition =>
        _definition ??= LoadDefinition();

    private static IHighlightingDefinition LoadDefinition()
    {
        using var reader = XmlReader.Create(new StringReader(Xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private const string Xshd = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="GLSL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">

          <Color name="Comment"          foreground="#57A64A" />
          <Color name="Preprocessor"     foreground="#808080" />
          <Color name="Keyword"          foreground="#569CD6" />
          <Color name="BuiltinType"      foreground="#4EC9B0" />
          <Color name="BuiltinFunction"  foreground="#DCDCAA" />
          <Color name="ShaderToyBuiltin" foreground="#9CDCFE" />
          <Color name="Number"           foreground="#B5CEA8" />

          <RuleSet>
            <!-- Comments -->
            <Span color="Comment" begin="//" end="\n" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />

            <!-- Preprocessor directives -->
            <Span color="Preprocessor" begin="#" end="\n" />

            <!-- GLSL scalar / vector / matrix types -->
            <Keywords color="BuiltinType">
              <Word>void</Word>
              <Word>bool</Word>   <Word>int</Word>    <Word>uint</Word>
              <Word>float</Word>  <Word>double</Word>
              <Word>vec2</Word>   <Word>vec3</Word>   <Word>vec4</Word>
              <Word>ivec2</Word>  <Word>ivec3</Word>  <Word>ivec4</Word>
              <Word>uvec2</Word>  <Word>uvec3</Word>  <Word>uvec4</Word>
              <Word>bvec2</Word>  <Word>bvec3</Word>  <Word>bvec4</Word>
              <Word>dvec2</Word>  <Word>dvec3</Word>  <Word>dvec4</Word>
              <Word>mat2</Word>   <Word>mat3</Word>   <Word>mat4</Word>
              <Word>mat2x2</Word> <Word>mat2x3</Word> <Word>mat2x4</Word>
              <Word>mat3x2</Word> <Word>mat3x3</Word> <Word>mat3x4</Word>
              <Word>mat4x2</Word> <Word>mat4x3</Word> <Word>mat4x4</Word>
              <Word>sampler2D</Word>     <Word>sampler3D</Word>
              <Word>samplerCube</Word>   <Word>sampler2DShadow</Word>
              <Word>isampler2D</Word>    <Word>usampler2D</Word>
            </Keywords>

            <!-- Control flow and qualifiers -->
            <Keywords color="Keyword">
              <Word>if</Word>       <Word>else</Word>
              <Word>for</Word>      <Word>while</Word>   <Word>do</Word>
              <Word>break</Word>    <Word>continue</Word>
              <Word>return</Word>   <Word>discard</Word>
              <Word>in</Word>       <Word>out</Word>     <Word>inout</Word>
              <Word>uniform</Word>  <Word>varying</Word> <Word>attribute</Word>
              <Word>const</Word>    <Word>struct</Word>
              <Word>precision</Word>
              <Word>mediump</Word>  <Word>highp</Word>   <Word>lowp</Word>
              <Word>true</Word>     <Word>false</Word>
              <Word>layout</Word>   <Word>location</Word>
            </Keywords>

            <!-- GLSL built-in functions -->
            <Keywords color="BuiltinFunction">
              <Word>radians</Word>   <Word>degrees</Word>
              <Word>sin</Word>      <Word>cos</Word>     <Word>tan</Word>
              <Word>asin</Word>     <Word>acos</Word>    <Word>atan</Word>
              <Word>sinh</Word>     <Word>cosh</Word>    <Word>tanh</Word>
              <Word>pow</Word>      <Word>exp</Word>     <Word>log</Word>
              <Word>exp2</Word>     <Word>log2</Word>
              <Word>sqrt</Word>     <Word>inversesqrt</Word>
              <Word>abs</Word>      <Word>sign</Word>
              <Word>floor</Word>    <Word>trunc</Word>   <Word>round</Word>
              <Word>roundEven</Word><Word>ceil</Word>    <Word>fract</Word>
              <Word>mod</Word>      <Word>modf</Word>
              <Word>min</Word>      <Word>max</Word>     <Word>clamp</Word>
              <Word>mix</Word>      <Word>step</Word>    <Word>smoothstep</Word>
              <Word>isnan</Word>    <Word>isinf</Word>
              <Word>length</Word>   <Word>distance</Word>
              <Word>dot</Word>      <Word>cross</Word>
              <Word>normalize</Word><Word>faceforward</Word>
              <Word>reflect</Word>  <Word>refract</Word>
              <Word>matrixCompMult</Word><Word>outerProduct</Word>
              <Word>transpose</Word><Word>determinant</Word><Word>inverse</Word>
              <Word>lessThan</Word>       <Word>lessThanEqual</Word>
              <Word>greaterThan</Word>    <Word>greaterThanEqual</Word>
              <Word>equal</Word>          <Word>notEqual</Word>
              <Word>any</Word>            <Word>all</Word>    <Word>not</Word>
              <Word>textureSize</Word>    <Word>texture</Word>
              <Word>textureProj</Word>    <Word>textureLod</Word>
              <Word>textureOffset</Word>  <Word>texelFetch</Word>
              <Word>texelFetchOffset</Word>
              <Word>textureProjOffset</Word><Word>textureLodOffset</Word>
              <Word>textureGrad</Word>    <Word>textureGradOffset</Word>
              <Word>texture2D</Word>      <Word>textureCube</Word>
              <Word>dFdx</Word>           <Word>dFdy</Word>    <Word>fwidth</Word>
              <Word>mainImage</Word>
            </Keywords>

            <!-- Shadertoy built-in uniforms and GLSL built-in variables -->
            <Keywords color="ShaderToyBuiltin">
              <Word>iTime</Word>             <Word>iTimeDelta</Word>
              <Word>iFrame</Word>            <Word>iFrameRate</Word>
              <Word>iResolution</Word>       <Word>iMouse</Word>
              <Word>iDate</Word>             <Word>iSampleRate</Word>
              <Word>iChannel0</Word>         <Word>iChannel1</Word>
              <Word>iChannel2</Word>         <Word>iChannel3</Word>
              <Word>iChannelTime</Word>      <Word>iChannelResolution</Word>
              <Word>gl_FragCoord</Word>      <Word>gl_FragDepth</Word>
              <Word>gl_Position</Word>       <Word>gl_PointSize</Word>
              <Word>gl_VertexID</Word>       <Word>gl_InstanceID</Word>
            </Keywords>

            <!-- Numeric literals (int, float, hex, scientific) -->
            <Rule color="Number">
              \b0[xX][0-9a-fA-F]+[uU]?\b
              |
              \b(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?[uUfF]?\b
            </Rule>
          </RuleSet>

        </SyntaxDefinition>
        """;
}
