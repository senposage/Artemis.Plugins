using System;
using System.Text.Json;
using System.Threading;
using Artemis.Core.LayerBrushes;
using Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes.PropertyGroups;
using Artemis.Plugins.LayerBrushes.Shadertoy.Screens;
using Artemis.UI.Shared.LayerBrushes;
using SkiaSharp;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.LayerBrushes;

public class ShaderToyLayerBrush : LayerBrush<ShaderToyPropertyGroup>
{
    private readonly Lock _lock = new();
    private GlesMultiPassRenderer? _renderer;
    private SKBitmap? _bitmap;

    /// <summary>Last shader compile/link error; null when the shader is valid.</summary>
    public string? ShaderError { get; private set; }

    public override void EnableLayerBrush()
    {
        ConfigurationDialog = new LayerBrushConfigurationDialog<ShaderPropertiesViewModel>(1300, 650);
        lock (_lock) RecreateRenderer();
    }

    public override void DisableLayerBrush()
    {
        lock (_lock) DestroyRenderer();
    }

    /// <summary>Called by the UI after the user edits shader source, dimensions, or imports a new shader.</summary>
    public void RecreateRenderer()
    {
        lock (_lock)
        {
            DestroyRenderer();
            ShaderError = null;

            var thread = ShaderToyLayerBrushProvider.RenderThread;
            if (thread == null) return;

            try
            {
                int w = Properties.Shader.Width.CurrentValue;
                int h = Properties.Shader.Height.CurrentValue;

                ShaderDefinition def = ResolveDefinition();
                var r = new GlesMultiPassRenderer(thread, def, w, h);
                if (!r.IsValid) { ShaderError = r.ErrorMessage; r.Dispose(); return; }
                _renderer = r;
                _bitmap   = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            }
            catch (Exception ex) { ShaderError = ex.Message; }
        }
    }

    /// <summary>
    /// Returns the current <see cref="ShaderDefinition"/>:
    /// either parsed from <c>ShaderJson</c>, or wrapped from the raw <c>Shader</c> GLSL.
    /// </summary>
    public ShaderDefinition ResolveDefinition()
    {
        string json = Properties.Shader.ShaderJson.CurrentValue;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { return JsonSerializer.Deserialize<ShaderDefinition>(json) ?? FallbackDef(); }
            catch { /* fall through */ }
        }
        return FallbackDef();
    }

    private ShaderDefinition FallbackDef() =>
        ShaderDefinition.FromGlsl(Properties.Shader.Shader.CurrentValue);

    /// <summary>
    /// Applies a <see cref="ShaderDefinition"/> directly and recreates the renderer,
    /// bypassing the property round-trip so preset/fetch loading takes effect immediately.
    /// </summary>
    public void ApplyDefinition(ShaderDefinition def)
    {
        lock (_lock)
        {
            DestroyRenderer();
            ShaderError = null;

            // Persist for save/reload.
            Properties.Shader.ShaderJson.SetCurrentValue(JsonSerializer.Serialize(def));
            if (def.IsSinglePass)
                Properties.Shader.Shader.SetCurrentValue(def.Passes[0].Source);

            var thread = ShaderToyLayerBrushProvider.RenderThread;
            if (thread == null) return;

            try
            {
                int w = Properties.Shader.Width.CurrentValue;
                int h = Properties.Shader.Height.CurrentValue;
                var r = new GlesMultiPassRenderer(thread, def, w, h);
                if (!r.IsValid) { ShaderError = r.ErrorMessage; r.Dispose(); return; }
                _renderer = r;
                _bitmap   = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            }
            catch (Exception ex) { ShaderError = ex.Message; }
        }
    }

    /// <summary>Render into <paramref name="bitmap"/> (shared renderer, preview path).</summary>
    internal bool RenderPreview(SKBitmap bitmap)
    {
        lock (_lock)
        {
            if (_renderer == null || _bitmap == null) return false;
            _renderer.EnableAudio    = Properties.Shader.EnableAudio.CurrentValue;
            AudioCapture.DbFloor        = Properties.Shader.AudioDbFloor.CurrentValue;
            AudioCapture.Attack         = Properties.Shader.AudioAttack.CurrentValue;
            AudioCapture.Smoothing      = Properties.Shader.AudioSmoothing.CurrentValue;
            AudioCapture.MinFrequency   = Properties.Shader.AudioMinFreq.CurrentValue;
            AudioCapture.MaxFrequency   = Properties.Shader.AudioMaxFreq.CurrentValue;
            AudioCapture.FrequencyScale = (SpectrumMode)Properties.Shader.AudioSpectrumMode.CurrentValue;
            _renderer.RenderToBuffer(bitmap);
            return true;
        }
    }

    /// <summary>Updates iMouse on the current renderer.  No-op if no renderer exists.</summary>
    internal void SetMouse(float x, float y, bool pressed, float clickX, float clickY)
    {
        lock (_lock)
            _renderer?.SetMouse(x, y, pressed, clickX, clickY);
    }

    private void DestroyRenderer()
    {
        _renderer?.Dispose(); _renderer = null;
        _bitmap?.Dispose();   _bitmap   = null;
    }

    public override void Update(double deltaTime) { }

    public override void Render(SKCanvas canvas, SKRect bounds, SKPaint paint)
    {
        lock (_lock)
        {
            if (_renderer == null || _bitmap == null) return;
            _renderer.MaxFps         = Properties.Shader.MaxFps.CurrentValue;
            _renderer.EnableAudio    = Properties.Shader.EnableAudio.CurrentValue;
            AudioCapture.DbFloor        = Properties.Shader.AudioDbFloor.CurrentValue;
            AudioCapture.Smoothing      = Properties.Shader.AudioSmoothing.CurrentValue;
            AudioCapture.MinFrequency   = Properties.Shader.AudioMinFreq.CurrentValue;
            AudioCapture.MaxFrequency   = Properties.Shader.AudioMaxFreq.CurrentValue;
            AudioCapture.FrequencyScale = (SpectrumMode)Properties.Shader.AudioSpectrumMode.CurrentValue;
            _renderer.RenderToBuffer(_bitmap);
            using var drawPaint = paint.Clone();
            if (Properties.Shader.CubicResize.CurrentValue)
            {
#pragma warning disable CS0618
                drawPaint.FilterQuality = SKFilterQuality.High;
#pragma warning restore CS0618
            }
            canvas.DrawBitmap(_bitmap, bounds, drawPaint);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        lock (_lock) DestroyRenderer();
    }
}
