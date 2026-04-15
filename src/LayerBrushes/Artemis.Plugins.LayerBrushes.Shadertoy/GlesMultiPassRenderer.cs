using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SkiaSharp;
using static Artemis.Plugins.LayerBrushes.Shadertoy.GlesNative;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Multi-pass Shadertoy renderer.  Handles single-pass (Image only) and
/// multi-pass (BufferA/B/C/D + Image) shaders via ANGLE + D3D11.
///
/// Buffer passes use ping-pong FBOs so self-referencing shaders (e.g. fluid sims)
/// read the previous frame's own output without data hazards.
///
/// Stubbed channels:
///   Keyboard  → 256×3 all-zero texture  (no key presses)
///   Audio/Noise/Video/Cubemap → 1×1 black texture
/// </summary>
internal sealed unsafe class GlesMultiPassRenderer : IDisposable
{
    // Fullscreen quad via gl_VertexID — no VBO needed.
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

    // ------------------------------------------------------------------
    // Per-pass state
    private sealed class PassState
    {
        public PassType   Type;
        public uint       Program;
        public PassInput[] Inputs = [];
        // Buffer passes only — ping-pong [0] and [1]
        public uint[] Tex = [0, 0];
        public uint[] Fbo = [0, 0];
        // Uniform locations
        public int uRes, uTime, uDelta, uFps, uFrame, uDate, uMouse, uSampleRate;
        public int uChanTime, uChanRes;
        public int uChan0, uChan1, uChan2, uChan3;
    }

    // ------------------------------------------------------------------
    private readonly GlesRenderThread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _bufferLock = new();

    private uint _vao;          // shared fullscreen quad
    private uint _imageFbo;     // output FBO for the Image pass
    private uint _imageTex;     // output texture (for ReadPixels)
    private uint _stubTex;      // 1×1 black — unused channels
    private uint _keyboardTex;  // 256×3 all-zero keyboard stub
    private uint _vertShader;   // shared vertex shader
    private uint _audioTex;     // 512×2 audio texture (FFT row 0 + waveform row 1)
    private bool _hasAudioInput;// true if any pass references an Audio channel
    private readonly byte[] _audioTexData = new byte[512 * 2 * 4]; // staging buffer

    // Image-file channel textures: file path → GL texture handle
    private readonly Dictionary<string, uint> _imageTextures = [];
    // File path of the Texture pass (if present), for resolving ChannelInputType.Texture
    private string? _texturePassPath;

    private PassState[] _passes = [];
    private int  _pingPong;     // 0 or 1: index for "just-rendered / current read" buffer textures
    private int  _frame;
    private double _lastTime;
    private double _lastRenderTime = double.NegativeInfinity;
    private volatile int _maxFps = 10;
    private byte[]? _pixels;

    // Mouse state — Shadertoy iMouse convention.
    // xy = current/last position (shader pixels, Y=0 at bottom).
    // zw = click position: positive while button held, negative when released.
    private float _mouseX, _mouseY;
    private float _mouseClickX, _mouseClickY;
    private bool  _mouseDown;

    public int    Width  { get; }
    public int    Height { get; }
    public bool   IsValid    => _passes.Length > 0 && ErrorMessage == null;
    public string? ErrorMessage { get; private set; }

    public int MaxFps
    {
        get => _maxFps;
        set => _maxFps = Math.Max(1, value);
    }

    /// <summary>When false, Audio channel inputs receive the silent stub texture.</summary>
    public bool EnableAudio { get; set; }

    // ------------------------------------------------------------------

    public GlesMultiPassRenderer(GlesRenderThread thread, ShaderDefinition shader, int width, int height)
    {
        _thread = thread;
        Width   = Math.Max(1, width);
        Height  = Math.Max(1, height);
        _pixels = new byte[Width * Height * 4];
        _thread.Invoke(() => Setup(shader));
    }

    // ------------------------------------------------------------------
    private void Setup(ShaderDefinition shader)
    {
        ShaderLogger.Log($"MultiPass.Setup: begin '{shader.Title}', {shader.Passes.Length} pass(es)");

        // Clear stale errors
        while (glGetError() != 0) { }

        // Shared vertex shader
        _vertShader = CompileShader(GL_VERTEX_SHADER, VERT_SRC);
        if (_vertShader == 0) return;

        // Shared VAO (gl_VertexID — no VBO)
        uint vao; glGenVertexArrays(1, &vao); _vao = vao;

        // Stub textures
        _stubTex     = MakeStubTex(1, 1, null);          // 1×1 black
        _keyboardTex = MakeStubTex(256, 3, null);        // 256×3 all-zero keyboard

        // Image pass output FBO
        CreateFboTex(Width, Height, out _imageFbo, out _imageTex);
        if (_imageFbo == 0) { ErrorMessage = "Failed to create image FBO"; return; }

        // Load Texture pass image (if present) so it's ready for channel binding.
        foreach (var p in shader.Passes)
            if (p.Type == PassType.Texture) { _texturePassPath = p.Source; LoadOrGetImageTex(p.Source); break; }

        // Extract common source — prepended to every other pass before conversion.
        string commonSrc = "";
        foreach (var p in shader.Passes)
            if (p.Type == PassType.Common) { commonSrc = p.Source.Trim(); break; }

        // Build passes in order (BufferA→D, then Image)
        var passStates = new List<PassState>();
        foreach (var pass in shader.Passes)
        {
            if (pass.Type == PassType.Common || pass.Type == PassType.Texture) continue;

            var state = new PassState { Type = pass.Type, Inputs = pass.Inputs };

            // Compile fragment shader (prepend common source if present)
            string src = string.IsNullOrEmpty(commonSrc)
                ? pass.Source
                : commonSrc + "\n\n" + pass.Source;
            string converted = ShaderConverter.ConvertToGles(src);
            uint frag = CompileShader(GL_FRAGMENT_SHADER, converted);
            if (frag == 0)
            {
                // ErrorMessage already set by CompileShader
                glDeleteShader(_vertShader); _vertShader = 0;
                return;
            }

            // Link program
            uint prog = glCreateProgram();
            glAttachShader(prog, _vertShader);
            glAttachShader(prog, frag);
            glLinkProgram(prog);
            glDeleteShader(frag);

            int linked; glGetProgramiv(prog, GL_LINK_STATUS, &linked);
            if (linked == 0)
            {
                ErrorMessage = $"Pass {pass.Type} link error: {GetProgramInfoLog(prog)}";
                ShaderLogger.Log($"MultiPass.Setup: {ErrorMessage}");
                glDeleteProgram(prog);
                return;
            }
            state.Program = prog;

            // Query uniforms
            glUseProgram(prog);
            state.uRes        = glGetUniformLocation(prog, "iResolution");
            state.uTime       = glGetUniformLocation(prog, "iTime");
            state.uDelta      = glGetUniformLocation(prog, "iTimeDelta");
            state.uFps        = glGetUniformLocation(prog, "iFrameRate");
            state.uFrame      = glGetUniformLocation(prog, "iFrame");
            state.uDate       = glGetUniformLocation(prog, "iDate");
            state.uMouse      = glGetUniformLocation(prog, "iMouse");
            state.uSampleRate = glGetUniformLocation(prog, "iSampleRate");
            state.uChanTime   = glGetUniformLocation(prog, "iChannelTime");
            state.uChanRes    = glGetUniformLocation(prog, "iChannelResolution");
            state.uChan0      = glGetUniformLocation(prog, "iChannel0");
            state.uChan1      = glGetUniformLocation(prog, "iChannel1");
            state.uChan2      = glGetUniformLocation(prog, "iChannel2");
            state.uChan3      = glGetUniformLocation(prog, "iChannel3");

            // Bind sampler uniforms to texture units 0..3
            if (state.uChan0 >= 0) glUniform1i(state.uChan0, 0);
            if (state.uChan1 >= 0) glUniform1i(state.uChan1, 1);
            if (state.uChan2 >= 0) glUniform1i(state.uChan2, 2);
            if (state.uChan3 >= 0) glUniform1i(state.uChan3, 3);
            glUseProgram(0);

            // Buffer passes get ping-pong FBOs
            if (pass.Type != PassType.Image)
            {
                CreateFboTex(Width, Height, out state.Fbo[0], out state.Tex[0]);
                CreateFboTex(Width, Height, out state.Fbo[1], out state.Tex[1]);
                if (state.Fbo[0] == 0 || state.Fbo[1] == 0)
                {
                    ErrorMessage = $"Failed to create FBO for pass {pass.Type}";
                    return;
                }
                ShaderLogger.Log($"MultiPass.Setup: pass {pass.Type} FBO[{state.Fbo[0]},{state.Fbo[1]}] TEX[{state.Tex[0]},{state.Tex[1]}]");
            }

            ShaderLogger.Log($"MultiPass.Setup: pass {pass.Type} prog={prog}");
            passStates.Add(state);
        }

        _passes = [.. passStates];

        // Check whether any pass references an Audio channel
        _hasAudioInput = false;
        foreach (var ps in _passes)
            foreach (var inp in ps.Inputs)
                if (inp.Type == ChannelInputType.Audio) { _hasAudioInput = true; break; }

        // Start audio capture lazily — never during plugin Enable() so we don't
        // race with Artemis.Plugins.Audio's WASAPI/DryIoc initialization.
        if (_hasAudioInput) AudioCapture.Start();

        // Create audio texture (512×2 RGBA8) if needed
        if (_hasAudioInput)
        {
            uint at; glGenTextures(1, &at); _audioTex = at;
            glBindTexture(GL_TEXTURE_2D, at);
            fixed (byte* p = _audioTexData)
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, 512, 2, 0,
                             GL_RGBA, GL_UNSIGNED_BYTE, p);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            glBindTexture(GL_TEXTURE_2D, 0);
        }

        ShaderLogger.Log($"MultiPass.Setup: complete, {_passes.Length} passes, audio={_hasAudioInput}, IsValid={IsValid}");
    }

    // ------------------------------------------------------------------
    private void RenderFrame()
    {
        if (_pixels == null || _passes.Length == 0) return;

        double t  = _clock.Elapsed.TotalSeconds;
        double dt = t - _lastTime;
        _lastTime = t;
        _frame++;
        var now = DateTime.Now;

        // ---- Upload audio texture ----
        if (_hasAudioInput && _audioTex != 0 && EnableAudio && AudioCapture.IsRunning)
        {
            AudioCapture.FillTexture(_audioTexData);
            glBindTexture(GL_TEXTURE_2D, _audioTex);
            fixed (byte* p = _audioTexData)
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 512, 2,
                                GL_RGBA, GL_UNSIGNED_BYTE, p);
            glBindTexture(GL_TEXTURE_2D, 0);
        }

        // ---- Buffer passes (A → B → C → D) ----
        // Read from _pingPong index, write to (1-_pingPong) index.
        int readIdx  = _pingPong;
        int writeIdx = 1 - _pingPong;

        foreach (var pass in _passes)
        {
            if (pass.Type == PassType.Image) continue;

            glBindFramebuffer(GL_FRAMEBUFFER, pass.Fbo[writeIdx]);
            glViewport(0, 0, Width, Height);
            glClearColor(0f, 0f, 0f, 1f);
            glClear(GL_COLOR_BUFFER_BIT);
            glUseProgram(pass.Program);

            SetUniforms(pass, t, dt, now);
            BindChannels(pass, readIdx);

            glBindVertexArray(_vao);
            glDrawArrays(GL_TRIANGLES, 0, 6);
            glBindVertexArray(0);
            glUseProgram(0);
        }

        // Flip ping-pong: what we just rendered is now the "current" read source.
        _pingPong = writeIdx;

        // ---- Image pass ----
        var imgPass = FindPass(PassType.Image);
        if (imgPass != null)
        {
            glBindFramebuffer(GL_FRAMEBUFFER, _imageFbo);
            glViewport(0, 0, Width, Height);
            glClearColor(0f, 0f, 0f, 1f);
            glClear(GL_COLOR_BUFFER_BIT);
            glUseProgram(imgPass.Program);

            SetUniforms(imgPass, t, dt, now);
            BindChannels(imgPass, _pingPong); // read from just-rendered buffers

            glBindVertexArray(_vao);
            glDrawArrays(GL_TRIANGLES, 0, 6);
            glBindVertexArray(0);

            uint errAfterDraw = glGetError();

            fixed (byte* p = _pixels)
                glReadPixels(0, 0, Width, Height, GL_RGBA, GL_UNSIGNED_BYTE, p);

            uint errAfterRead = glGetError();

            glBindFramebuffer(GL_FRAMEBUFFER, 0);
            glUseProgram(0);

            if (_frame <= 3 || errAfterDraw != 0 || errAfterRead != 0)
                ShaderLogger.Log($"RenderFrame #{_frame}: errDraw=0x{errAfterDraw:X4} errRead=0x{errAfterRead:X4} t={t:F2}s" +
                    $" audio={_hasAudioInput}/ena={EnableAudio}/run={AudioCapture.IsRunning}");
        }

        // Unbind all texture units
        for (uint i = 0; i < 4; i++)
        {
            glActiveTexture(GL_TEXTURE0 + i);
            glBindTexture(GL_TEXTURE_2D, 0);
        }
        glActiveTexture(GL_TEXTURE0);
    }

    private void SetUniforms(PassState pass, double t, double dt, DateTime now)
    {
        float* zeroes = stackalloc float[12];

        glUniform3f(pass.uRes,        (float)Width, (float)Height, 1f);
        glUniform1f(pass.uTime,       (float)t);
        glUniform1f(pass.uDelta,      (float)dt);
        glUniform1f(pass.uFps,        dt > 0 ? (float)(1.0 / dt) : 60f);
        glUniform1i(pass.uFrame,      _frame);
        glUniform4f(pass.uDate,       now.Year, now.Month, now.Day, (float)now.TimeOfDay.TotalSeconds);
        float mSign = _mouseDown ? 1f : -1f;
        glUniform4f(pass.uMouse, _mouseX, _mouseY, mSign * _mouseClickX, mSign * _mouseClickY);
        glUniform1f(pass.uSampleRate, 44100f);
        if (pass.uChanTime >= 0) glUniform1fv(pass.uChanTime, 4, zeroes);
        if (pass.uChanRes  >= 0) glUniform3fv(pass.uChanRes,  4, zeroes);
    }

    private void BindChannels(PassState pass, int pingPongReadIdx)
    {
        int[] locs = [pass.uChan0, pass.uChan1, pass.uChan2, pass.uChan3];
        for (int i = 0; i < 4; i++)
        {
            glActiveTexture(GL_TEXTURE0 + (uint)i);
            glBindTexture(GL_TEXTURE_2D, ResolveChannel(pass, i, pingPongReadIdx));
        }
        glActiveTexture(GL_TEXTURE0);
    }

    private uint ResolveChannel(PassState pass, int channel, int pingPongReadIdx)
    {
        foreach (var inp in pass.Inputs)
        {
            if (inp.Channel != channel) continue;
            return inp.Type switch
            {
                ChannelInputType.BufferA  => GetBufferTex(PassType.BufferA, pingPongReadIdx),
                ChannelInputType.BufferB  => GetBufferTex(PassType.BufferB, pingPongReadIdx),
                ChannelInputType.BufferC  => GetBufferTex(PassType.BufferC, pingPongReadIdx),
                ChannelInputType.BufferD  => GetBufferTex(PassType.BufferD, pingPongReadIdx),
                ChannelInputType.Keyboard => _keyboardTex,
                ChannelInputType.Audio    => _audioTex != 0 ? _audioTex : _stubTex,
                ChannelInputType.Texture  => LoadOrGetImageTex(_texturePassPath),
                ChannelInputType.Image    => LoadOrGetImageTex(inp.Source),
                _                         => _stubTex,
            };
        }
        return _stubTex;
    }

    private uint LoadOrGetImageTex(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return _stubTex;
        if (_imageTextures.TryGetValue(path, out uint cached)) return cached;
        if (!File.Exists(path))
        {
            ShaderLogger.Log($"ImageTex: file not found: {path}");
            return _stubTex;
        }

        try
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp == null) { ShaderLogger.Log($"ImageTex: decode failed: {path}"); return _stubTex; }

            SKBitmap src = bmp.ColorType == SKColorType.Rgba8888
                ? bmp
                : bmp.Copy(SKColorType.Rgba8888);
            bool needsDispose = !ReferenceEquals(src, bmp);

            uint t; glGenTextures(1, &t);
            glBindTexture(GL_TEXTURE_2D, t);
            glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, src.Width, src.Height, 0,
                         GL_RGBA, GL_UNSIGNED_BYTE, (void*)src.GetPixels());
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
            glBindTexture(GL_TEXTURE_2D, 0);

            if (needsDispose) src.Dispose();

            _imageTextures[path] = t;
            ShaderLogger.Log($"ImageTex: loaded {path} → tex {t} ({bmp.Width}×{bmp.Height})");
            return t;
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"ImageTex: error loading {path}: {ex.Message}");
            return _stubTex;
        }
    }

    private uint GetBufferTex(PassType type, int readIdx)
    {
        foreach (var p in _passes)
            if (p.Type == type) return p.Tex[readIdx];
        return _stubTex;
    }

    private PassState? FindPass(PassType type)
    {
        foreach (var p in _passes)
            if (p.Type == type) return p;
        return null;
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// Updates iMouse state.  xy = current position in shader pixels (Y=0 at bottom).
    /// On press, clickX/Y are set and zw go positive.  On release, zw go negative.
    /// </summary>
    public void SetMouse(float x, float y, bool pressed, float clickX, float clickY)
    {
        _mouseX = x; _mouseY = y; _mouseDown = pressed;
        _mouseClickX = clickX; _mouseClickY = clickY;
    }

    // ------------------------------------------------------------------
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

            // Y-flip: GL bottom-left → Skia top-left
            int stride  = Width * 4;
            var dstPtr  = (byte*)bitmap.GetPixels();
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

    // ------------------------------------------------------------------
    public void Dispose()
    {
        _thread.Invoke(Teardown);
        _pixels = null;
    }

    private void Teardown()
    {
        foreach (var pass in _passes)
        {
            if (pass.Program != 0) { glDeleteProgram(pass.Program); pass.Program = 0; }
            for (int i = 0; i < 2; i++)
            {
                uint f = pass.Fbo[i], t = pass.Tex[i];
                if (f != 0) glDeleteFramebuffers(1, &f);
                if (t != 0) glDeleteTextures(1, &t);
                pass.Fbo[i] = pass.Tex[i] = 0;
            }
        }
        _passes = [];

        if (_vertShader != 0) { glDeleteShader(_vertShader); _vertShader = 0; }

        uint imageFbo = _imageFbo, imageTex = _imageTex;
        uint stub = _stubTex, kbd = _keyboardTex, audioTex = _audioTex;
        uint vao = _vao;

        if (imageFbo  != 0) glDeleteFramebuffers(1, &imageFbo);
        if (imageTex  != 0) glDeleteTextures(1, &imageTex);
        if (stub      != 0) glDeleteTextures(1, &stub);
        if (kbd       != 0) glDeleteTextures(1, &kbd);
        if (audioTex  != 0) glDeleteTextures(1, &audioTex);
        if (vao       != 0) glDeleteVertexArrays(1, &vao);

        _imageFbo = _imageTex = _stubTex = _keyboardTex = _audioTex = _vao = 0;

        foreach (uint imgTex in _imageTextures.Values)
        {
            uint t = imgTex;
            if (t != 0) glDeleteTextures(1, &t);
        }
        _imageTextures.Clear();
    }

    // ------------------------------------------------------------------
    // GL helpers

    private uint CompileShader(uint type, string src)
    {
        uint s = glCreateShader(type);
        glShaderSource(s, 1, [src], null);
        glCompileShader(s);
        int ok; glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        ErrorMessage = GetShaderInfoLog(s);
        ShaderLogger.Log($"CompileShader failed (type={type:X}): {ErrorMessage}");
        glDeleteShader(s);
        return 0;
    }

    private static void CreateFboTex(int w, int h, out uint fbo, out uint tex)
    {
        uint f, t;
        glGenTextures(1, &t);
        glBindTexture(GL_TEXTURE_2D, t);
        glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
        glBindTexture(GL_TEXTURE_2D, 0);

        glGenFramebuffers(1, &f);
        glBindFramebuffer(GL_FRAMEBUFFER, f);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, t, 0);
        uint status = glCheckFramebufferStatus(GL_FRAMEBUFFER);
        glBindFramebuffer(GL_FRAMEBUFFER, 0);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            ShaderLogger.Log($"CreateFboTex: FBO incomplete 0x{status:X4}");
            glDeleteTextures(1, &t);
            glDeleteFramebuffers(1, &f);
            fbo = tex = 0;
        }
        else { fbo = f; tex = t; }
    }

    private uint MakeStubTex(int w, int h, byte[]? data)
    {
        uint t; glGenTextures(1, &t);
        glBindTexture(GL_TEXTURE_2D, t);
        if (data != null)
        {
            fixed (byte* p = data)
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, p);
        }
        else
        {
            // All-zero texture (black / no-key-press)
            var zero = new byte[w * h * 4];
            fixed (byte* p = zero)
                glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, p);
        }
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
        glBindTexture(GL_TEXTURE_2D, 0);
        return t;
    }
}
