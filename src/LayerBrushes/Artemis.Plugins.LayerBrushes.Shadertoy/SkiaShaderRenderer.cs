using System;
using System.Diagnostics;
using SkiaSharp;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Renders SkSL shaders using SKRuntimeEffect. Runs entirely within SkiaSharp's
/// render pipeline — no separate OpenGL/Vulkan context, no GPU readback, no thread
/// marshalling. Works with whatever backend Artemis uses.
/// </summary>
internal sealed class SkiaShaderRenderer : IDisposable
{
    private SKRuntimeEffect? _effect;
    private string? _errorMessage;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastFrameTime;
    private int _frame;

    public int Width { get; }
    public int Height { get; }
    public string ShaderSource { get; }
    public string? ErrorMessage => _errorMessage;
    public bool IsValid => _effect != null;

    public SkiaShaderRenderer(string shaderSource, int width, int height)
    {
        ShaderSource = shaderSource;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        Compile(shaderSource);
    }

    private void Compile(string source)
    {
        _effect?.Dispose();
        _effect = null;
        _errorMessage = null;

        // DIAGNOSTIC: test basic P/Invoke into libSkiaSharp before trying SKRuntimeEffect
        try
        {
            using var testBitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque);
            _errorMessage = "diagnostic: basic SkiaSharp P/Invoke OK, SKRuntimeEffect disabled";
        }
        catch (Exception ex)
        {
            _errorMessage = "diagnostic: basic SkiaSharp P/Invoke FAILED: " + ex.Message;
        }
    }

    public void Render(SKCanvas canvas, SKRect bounds, SKPaint? basePaint = null)
    {
        if (_effect == null) return;

        double currentTime = _stopwatch.Elapsed.TotalSeconds;
        double deltaTime = currentTime - _lastFrameTime;
        _lastFrameTime = currentTime;
        _frame++;

        DateTime now = DateTime.Now;

        var uniforms = new SKRuntimeEffectUniforms(_effect)
        {
            ["iResolution"] = new[] { (float)Width, (float)Height, 1.0f }, // float3: width, height, pixel-ratio
            ["iTime"] = (float)currentTime,
            ["iTimeDelta"] = (float)deltaTime,
            ["iFrameRate"] = deltaTime > 0 ? (float)(1.0 / deltaTime) : 0f,
            ["iFrame"] = (float)_frame,
            ["iDate"] = new[] { (float)now.Year, (float)now.Month, (float)now.Day, (float)now.TimeOfDay.TotalSeconds },
        };

        using var shader = _effect.ToShader(false, uniforms);
        if (shader == null) return;

        using var paint = basePaint?.Clone() ?? new SKPaint();
        paint.Shader = shader;

        // Map the shader's local space (0,0)-(Width,Height) onto `bounds`. Without
        // this, fragCoord would be in device coordinates, so a shader expecting
        // iResolution-relative pixels would render incorrectly when bounds isn't
        // anchored at (0,0) or has a different size.
        int save = canvas.Save();
        canvas.Translate(bounds.Left, bounds.Top);
        canvas.Scale(bounds.Width / Width, bounds.Height / Height);
        canvas.DrawRect(0, 0, Width, Height, paint);
        canvas.RestoreToCount(save);
    }

    public void RenderToBuffer(SKBitmap bitmap)
    {
        if (_effect == null) return;

        using var canvas = new SKCanvas(bitmap);
        Render(canvas, new SKRect(0, 0, bitmap.Width, bitmap.Height));
    }

    public void Dispose()
    {
        _effect?.Dispose();
        _effect = null;
    }
}

