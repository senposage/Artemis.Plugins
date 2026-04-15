using System;
using System.Diagnostics;
using System.Threading;
using SkiaSharp;
using static Artemis.Plugins.LayerBrushes.Shadertoy.GlesNative;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Compiles and renders a Shadertoy GLSL shader via ANGLE+WARP OpenGL ES 3.0.
/// All GPU commands execute on the CPU via WARP — no hardware GPU involvement,
/// no driver conflicts with games or Artemis's own renderer.
///
/// Each instance owns its own FBO + texture; the EGL context is shared across
/// all instances via the single GlesRenderThread.
/// </summary>
internal sealed unsafe class GlesShaderRenderer : IDisposable
{
    // Fullscreen quad via gl_VertexID — no VBO needed, avoids glBufferData issues.
    // Vertex IDs 0-5 → two triangles covering NDC [-1,1]×[-1,1].
    private const string VERT_SRC = """
        #version 300 es
        void main() {
            vec2 pos[6];
            pos[0] = vec2(-1.0, -1.0);
            pos[1] = vec2(-1.0,  1.0);
            pos[2] = vec2( 1.0, -1.0);
            pos[3] = vec2( 1.0, -1.0);
            pos[4] = vec2(-1.0,  1.0);
            pos[5] = vec2( 1.0,  1.0);
            gl_Position = vec4(pos[gl_VertexID], 0.0, 1.0);
        }
        """;

    private readonly GlesRenderThread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _bufferLock = new();
    private double _lastTime;
    private double _lastRenderTime = double.NegativeInfinity;
    private volatile int _maxFps = 10;
    private int _frame;

    /// <summary>Maximum render rate. Updated live — no renderer recreation needed.</summary>
    public int MaxFps
    {
        get => _maxFps;
        set => _maxFps = Math.Max(1, value);
    }

    private uint _vao, _fbo, _tex, _prog;
    private uint[] _channelTex = new uint[4]; // iChannel0..3 stub textures

    // Uniform locations
    private int _uRes, _uTime, _uDelta, _uFps, _uFrame, _uDate;
    private int _uMouse, _uSampleRate;
    private int _uChanTime, _uChanRes;                     // array base locations
    private int _uChan0, _uChan1, _uChan2, _uChan3;        // sampler locations

    // RGBA scratch buffer populated by the GL thread, read back on the calling thread.
    private byte[]? _pixels;

    public int Width  { get; }
    public int Height { get; }
    public string? ErrorMessage { get; private set; }
    public bool IsValid => _prog != 0;

    public GlesShaderRenderer(GlesRenderThread thread, string glslSource, int width, int height)
    {
        _thread = thread;
        Width   = Math.Max(1, width);
        Height  = Math.Max(1, height);
        _pixels = new byte[Width * Height * 4]; // RGBA

        _thread.Invoke(() => Setup(glslSource));
    }

    private void Setup(string src)
    {
        ShaderLogger.Log($"Setup: begin ({Width}x{Height})");

        // Clear any stale GL errors from context init
        uint stale = glGetError();
        if (stale != 0) ShaderLogger.Log($"Setup: stale GL error at entry = 0x{stale:X4}");

        // Empty VAO — vertex positions are generated in the vertex shader via gl_VertexID.
        // No VBO needed; avoids glBufferData P/Invoke issues.
        uint vao;
        glGenVertexArrays(1, &vao); _vao = vao;
        ShaderLogger.Log($"Setup: GenVertexArrays VAO={_vao} err=0x{glGetError():X4}");

        // Offscreen FBO + render texture
        uint fbo, tex;
        glGenTextures(1, &tex);     _tex = tex;
        glGenFramebuffers(1, &fbo); _fbo = fbo;
        glBindTexture(GL_TEXTURE_2D, _tex);
        glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, Width, Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        glBindFramebuffer(GL_FRAMEBUFFER, _fbo);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _tex, 0);

        uint fboStatus = glCheckFramebufferStatus(GL_FRAMEBUFFER);
        ShaderLogger.Log($"Setup: FBO={_fbo} TEX={_tex} status=0x{fboStatus:X4} (COMPLETE=0x{GL_FRAMEBUFFER_COMPLETE:X4}) glErr=0x{glGetError():X4}");
        if (fboStatus != GL_FRAMEBUFFER_COMPLETE)
        {
            ErrorMessage = $"FBO incomplete: 0x{fboStatus:X4}";
            glBindTexture(GL_TEXTURE_2D, 0);
            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            return;
        }
        glBindTexture(GL_TEXTURE_2D, 0);
        glBindFramebuffer(GL_FRAMEBUFFER, 0);

        // Stub 1×1 black textures for iChannel0..3
        // (shaders that reference iChannelN get a valid sampler, not undefined behaviour)
        byte[] black = [0, 0, 0, 255];
        fixed (uint* cp = _channelTex)
            glGenTextures(4, cp);
        fixed (byte* bp = black)
        {
            for (int i = 0; i < 4; i++)
            {
                glBindTexture(GL_TEXTURE_2D, _channelTex[i]);
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, bp);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            }
        }
        glBindTexture(GL_TEXTURE_2D, 0);

        // Compile and link shaders
        uint vert = CompileShader(GL_VERTEX_SHADER, VERT_SRC);
        ShaderLogger.Log($"Setup: vert shader={vert} err={ErrorMessage ?? "ok"}");
        if (vert == 0) return;

        string convertedSrc = ShaderConverter.ConvertToGles(src);
        ShaderLogger.Log($"Setup: converted GLSL ({convertedSrc.Length} chars):\n---GLSL---\n{convertedSrc}\n---END---");

        uint frag = CompileShader(GL_FRAGMENT_SHADER, convertedSrc);
        ShaderLogger.Log($"Setup: frag shader={frag} err={ErrorMessage ?? "ok"}");
        if (frag == 0) { glDeleteShader(vert); return; }

        uint prog = glCreateProgram();
        glAttachShader(prog, vert);
        glAttachShader(prog, frag);
        glLinkProgram(prog);
        glDeleteShader(vert);
        glDeleteShader(frag);

        int linked;
        glGetProgramiv(prog, GL_LINK_STATUS, &linked);
        if (linked == 0)
        {
            ErrorMessage = GetProgramInfoLog(prog);
            ShaderLogger.Log($"Setup: link FAILED: {ErrorMessage}");
            glDeleteProgram(prog);
            return;
        }
        _prog = prog;
        ShaderLogger.Log($"Setup: linked prog={_prog}");

        glUseProgram(_prog);

        // Shadertoy uniform locations
        _uRes        = glGetUniformLocation(_prog, "iResolution");
        _uTime       = glGetUniformLocation(_prog, "iTime");
        _uDelta      = glGetUniformLocation(_prog, "iTimeDelta");
        _uFps        = glGetUniformLocation(_prog, "iFrameRate");
        _uFrame      = glGetUniformLocation(_prog, "iFrame");
        _uDate       = glGetUniformLocation(_prog, "iDate");
        _uMouse      = glGetUniformLocation(_prog, "iMouse");
        _uSampleRate = glGetUniformLocation(_prog, "iSampleRate");
        _uChanTime   = glGetUniformLocation(_prog, "iChannelTime");
        _uChanRes    = glGetUniformLocation(_prog, "iChannelResolution");
        _uChan0      = glGetUniformLocation(_prog, "iChannel0");
        _uChan1      = glGetUniformLocation(_prog, "iChannel1");
        _uChan2      = glGetUniformLocation(_prog, "iChannel2");
        _uChan3      = glGetUniformLocation(_prog, "iChannel3");

        // Bind iChannel samplers to texture units 0..3
        if (_uChan0 >= 0) glUniform1i(_uChan0, 0);
        if (_uChan1 >= 0) glUniform1i(_uChan1, 1);
        if (_uChan2 >= 0) glUniform1i(_uChan2, 2);
        if (_uChan3 >= 0) glUniform1i(_uChan3, 3);

        ShaderLogger.Log($"Setup: uniforms iResolution={_uRes} iTime={_uTime} iFrame={_uFrame} iMouse={_uMouse} iChannel0={_uChan0}");
        glUseProgram(0);

        ShaderLogger.Log($"Setup: complete, IsValid={IsValid}");
    }

    private uint CompileShader(uint type, string src)
    {
        uint s = glCreateShader(type);
        glShaderSource(s, 1, [src], null);
        glCompileShader(s);
        int ok;
        glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        ErrorMessage = GetShaderInfoLog(s);
        glDeleteShader(s);
        return 0;
    }

    /// <summary>
    /// Renders one frame on the GL thread, then copies the result with a Y-flip
    /// into <paramref name="bitmap"/> (which must be RGBA8888 and Width×Height).
    /// Blocks until the frame is complete.
    /// </summary>
    /// <summary>
    /// Renders one frame (capped at 30fps; multiple callers in the same window share the
    /// cached frame) then Y-flips into <paramref name="bitmap"/> (RGBA8888, Width×Height).
    /// Thread-safe: safe to call from the Artemis render loop AND the preview timer concurrently.
    /// </summary>
    public void RenderToBuffer(SKBitmap bitmap)
    {
        if (!IsValid || _pixels == null) return;

        lock (_bufferLock)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastRenderTime >= 1.0 / _maxFps)
            {
                _thread.Invoke(RenderFrame);
                _lastRenderTime = now;
            }
            // else: reuse cached pixels — no WARP re-render

            // Y-flip: GL bottom-left origin → Skia top-left origin.
            int stride = Width * 4;
            var dstPtr = (byte*)bitmap.GetPixels();
            fixed (byte* src = _pixels)
            {
                for (int y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<byte>(src + (Height - 1 - y) * stride, stride)
                        .CopyTo(new Span<byte>(dstPtr + y * stride, stride));
                }
            }
        }
    }

    private void RenderFrame()
    {
        if (_pixels == null) return;

        double t  = _clock.Elapsed.TotalSeconds;
        double dt = t - _lastTime;
        _lastTime = t;
        _frame++;
        var now = DateTime.Now;

        glBindFramebuffer(GL_FRAMEBUFFER, _fbo);
        glViewport(0, 0, Width, Height);
        glClearColor(0f, 0f, 0f, 1f);
        glClear(GL_COLOR_BUFFER_BIT);
        glUseProgram(_prog);

        // Shadertoy uniforms — verbatim spec
        glUniform3f(_uRes,        (float)Width, (float)Height, 1f);
        glUniform1f(_uTime,       (float)t);
        glUniform1f(_uDelta,      (float)dt);
        glUniform1f(_uFps,        dt > 0 ? (float)(1.0 / dt) : 60f);
        glUniform1i(_uFrame,      _frame);                  // int, not float
        glUniform4f(_uDate,       now.Year, now.Month, now.Day, (float)now.TimeOfDay.TotalSeconds);
        glUniform4f(_uMouse,      0f, 0f, 0f, 0f);          // no mouse input
        glUniform1f(_uSampleRate, 44100f);

        // iChannelTime[4] and iChannelResolution[4] — stubs
        float* zeroes = stackalloc float[12]; // 4×3 for resolution
        if (_uChanTime >= 0) glUniform1fv(_uChanTime, 4, zeroes);
        if (_uChanRes  >= 0) glUniform3fv(_uChanRes,  4, zeroes);

        // Bind iChannel0..3 stub textures to texture units 0..3
        for (int i = 0; i < 4; i++)
        {
            glActiveTexture(GL_TEXTURE0 + (uint)i);
            glBindTexture(GL_TEXTURE_2D, _channelTex[i]);
        }
        glActiveTexture(GL_TEXTURE0); // reset active unit

        glBindVertexArray(_vao);
        glDrawArrays(GL_TRIANGLES, 0, 6);
        glBindVertexArray(0);

        uint errAfterDraw = glGetError();

        // glReadPixels is itself a pipeline sync point — glFinish() is redundant here.
        fixed (byte* p = _pixels)
            glReadPixels(0, 0, Width, Height, GL_RGBA, GL_UNSIGNED_BYTE, p);

        uint errAfterRead = glGetError();

        // Unbind textures
        for (int i = 0; i < 4; i++)
        {
            glActiveTexture(GL_TEXTURE0 + (uint)i);
            glBindTexture(GL_TEXTURE_2D, 0);
        }
        glActiveTexture(GL_TEXTURE0);

        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        glUseProgram(0);

        // Log first few frames for debugging
        if (_frame <= 3)
        {
            uint px0 = _pixels.Length >= 4
                ? (uint)(_pixels[0] | (_pixels[1] << 8) | (_pixels[2] << 16) | (_pixels[3] << 24))
                : 0;
            ShaderLogger.Log($"RenderFrame #{_frame}: errDraw=0x{errAfterDraw:X4} errRead=0x{errAfterRead:X4} pixels[0]=0x{px0:X8} t={t:F2}s");
        }
        else if (errAfterDraw != 0 || errAfterRead != 0)
        {
            ShaderLogger.Log($"RenderFrame #{_frame}: ERROR errDraw=0x{errAfterDraw:X4} errRead=0x{errAfterRead:X4}");
        }
    }

    public void Dispose()
    {
        _thread.Invoke(() =>
        {
            if (_prog != 0) { glDeleteProgram(_prog); _prog = 0; }
            uint fb = _fbo, tx = _tex, va = _vao;
            if (fb != 0) glDeleteFramebuffers(1, &fb);
            if (tx != 0) glDeleteTextures(1, &tx);
            if (va != 0) glDeleteVertexArrays(1, &va);
            fixed (uint* cp = _channelTex) glDeleteTextures(4, cp);
            _fbo = _tex = _vao = 0;
        });
        _pixels = null;
    }
}
