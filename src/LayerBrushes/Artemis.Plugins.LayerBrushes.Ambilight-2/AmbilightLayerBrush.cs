using System;
using System.Linq;
using Artemis.Core.LayerBrushes;
using Artemis.Plugins.LayerBrushes.Ambilight.PropertyGroups;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using Artemis.Plugins.LayerBrushes.Ambilight.Screens;
using Artemis.UI.Shared.LayerBrushes;
using HPPH;
using ScreenCapture.NET;
using SkiaSharp;

namespace Artemis.Plugins.LayerBrushes.Ambilight
{
    public class AmbilightLayerBrush : LayerBrush<AmbilightPropertyGroup>
    {
        #region Properties & Fields

        private IScreenCaptureService? _screenCaptureService => AmbilightBootstrapper.ScreenCaptureService;
        public bool PropertiesOpen { get; set; }

        private Display? _display;
        private ICaptureZone? _captureZone;
        private bool _creatingCaptureZone;

        // Frame skip
        private int _frameCounter;

        // Smoothing
        private SKBitmap? _smoothedBitmap;

        // Cached color adjustment state — avoids recreating GPU resources every frame
        private SKPaint? _colorPaint;
        private SKColorFilter? _cachedColorFilter;   // final filter applied to paint (may be composed)
        private SKColorFilter? _cachedHighlightFilter; // LUT filter, rebuilt only when compression changes
        private float _lastBrightness = float.NaN;
        private float _lastContrast = float.NaN;
        private float _lastSaturation = float.NaN;
        private float _lastTemperature = float.NaN;
        private int _lastBlackPoint = -1;
        private int _lastWhitePoint = -1;
        private float _lastExposureScale = float.NaN;
        private float _lastHighlightCompression = float.NaN;

        // Auto-exposure: smoothed scale applied on top of the color matrix.
        // 1.0 = no reduction, approaches 0 for very bright scenes at high strength.
        private float _exposureScale = 1f;

        #endregion

        #region Methods

        public override void Update(double deltaTime)
        {
            if (_captureZone == null)
            {
                if (_screenCaptureService != null)
                    RecreateCaptureZone();

                if (_captureZone == null)
                    return;
            }

            int frameSkip = Properties.Capture.FrameSkip;
            if (frameSkip > 0)
            {
                _frameCounter++;
                if (_frameCounter <= frameSkip)
                    return;
                _frameCounter = 0;
            }

            _captureZone.RequestUpdate();
        }

        public override unsafe void Render(SKCanvas canvas, SKRect bounds, SKPaint paint)
        {
            if (ShouldRenderBlack())
            {
                RenderBlack(canvas, bounds);
                return;
            }

            try
            {
                AmbilightCaptureProperties properties = Properties.Capture;
                using (_captureZone.Lock())
                {
                    RefImage<ColorBGRA> image = _captureZone.GetRefImage<ColorBGRA>();
                    if (image.Width == 0 || image.Height == 0)
                    {
                        RenderBlack(canvas, bounds);
                        return;
                    }

                    if (properties.BlackBarDetectionTop || properties.BlackBarDetectionBottom || properties.BlackBarDetectionLeft || properties.BlackBarDetectionRight)
                        image = image.RemoveBlackBars(properties.BlackBarDetectionThreshold,
                                                      properties.BlackBarDetectionTop, properties.BlackBarDetectionBottom,
                                                      properties.BlackBarDetectionLeft, properties.BlackBarDetectionRight);

                    // Auto-exposure: compute average scene luminance and smooth a scale factor.
                    // fast dim (rate 0.10) so a flash is absorbed quickly;
                    // slow recover (rate 0.02) so the LEDs don't strobe when content changes.
                    float autoExpStrength = properties.AutoExposureStrength.CurrentValue;
                    if (autoExpStrength > 0f)
                    {
                        float avgLum = ComputeAverageLuminance(image);
                        float target = 1f / (1f + avgLum * autoExpStrength * 4f);
                        float rate = target < _exposureScale ? 0.10f : 0.02f;
                        _exposureScale += (target - _exposureScale) * rate;
                    }
                    else
                    {
                        _exposureScale = 1f;
                    }

                    fixed (byte* img = image)
                    {
                        using SKImage skImage = SKImage.FromPixels(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), (nint)img, image.RawStride);

                        // Apply smoothing (temporal blend with previous frame)
                        float smoothing = Math.Clamp(properties.SmoothingFactor.CurrentValue, 0f, 0.95f);
                        if (smoothing > 0f)
                        {
                            // Ensure smoothed bitmap matches current frame size
                            if (_smoothedBitmap == null || _smoothedBitmap.Width != image.Width || _smoothedBitmap.Height != image.Height)
                            {
                                _smoothedBitmap?.Dispose();
                                _smoothedBitmap = new SKBitmap(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                                using var initCanvas = new SKCanvas(_smoothedBitmap);
                                initCanvas.DrawImage(skImage, 0, 0);
                            }
                            else
                            {
                                // Blend: smoothedBitmap = smoothedBitmap * smoothing + currentFrame * (1 - smoothing)
                                using var blendCanvas = new SKCanvas(_smoothedBitmap);
                                using var blendPaint = new SKPaint();
                                blendPaint.Color = new SKColor(255, 255, 255, (byte)((1f - smoothing) * 255));
                                blendCanvas.DrawImage(skImage, 0, 0, blendPaint);
                            }

                            DrawSmoothedWithColorAdjustments(canvas, bounds, paint, properties, _exposureScale);
                        }
                        else
                        {
                            DrawWithColorAdjustments(canvas, skImage, bounds, paint, properties, _exposureScale);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Capture zone was disposed during a display change - safe to ignore
                RenderBlack(canvas, bounds);
            }
        }

        private bool ShouldRenderBlack()
        {
            if (_captureZone == null || _display == null || _screenCaptureService == null)
                return true;

            IScreenCapture screenCapture = _screenCaptureService.GetScreenCapture(_display.Value);
            return screenCapture is AmbilightScreenCapture ambilightCapture && ambilightCapture.ShouldOutputBlack;
        }

        private void RenderBlack(SKCanvas canvas, SKRect bounds)
        {
            _smoothedBitmap?.Erase(SKColors.Black);
            canvas.DrawColor(SKColors.Black);
        }

        /// <summary>
        /// Draws from the smoothed bitmap without creating an intermediate SKImage GPU texture copy.
        /// </summary>
        private void DrawSmoothedWithColorAdjustments(SKCanvas canvas, SKRect bounds, SKPaint paint, AmbilightCaptureProperties props, float exposureScale)
        {
            SKPaint effectivePaint = GetColorAdjustedPaint(paint, props, exposureScale);
            canvas.DrawBitmap(_smoothedBitmap!, bounds, effectivePaint);
        }

        private void DrawWithColorAdjustments(SKCanvas canvas, SKImage image, SKRect bounds, SKPaint paint, AmbilightCaptureProperties props, float exposureScale)
        {
            SKPaint effectivePaint = GetColorAdjustedPaint(paint, props, exposureScale);
            canvas.DrawImage(image, bounds, effectivePaint);
        }

        /// <summary>
        /// Returns a paint with the cached color filter applied, or the original paint if no adjustments are needed.
        /// The color filter is only rebuilt when parameters actually change.
        /// </summary>
        private SKPaint GetColorAdjustedPaint(SKPaint paint, AmbilightCaptureProperties props, float exposureScale)
        {
            float brightness = props.Brightness.CurrentValue;
            float contrast = props.Contrast.CurrentValue;
            float saturation = props.Saturation.CurrentValue;
            float temperature = props.ColorTemperature.CurrentValue;
            int blackPoint = Math.Clamp((int)props.BlackPoint, 0, 50);
            int whitePoint = Math.Clamp((int)props.WhitePoint, 200, 255);
            float highlight = Math.Clamp(props.HighlightCompression.CurrentValue, 0f, 1f);

            bool hasAdjustment = brightness != 0f || contrast != 0f || saturation != 0f ||
                                  temperature != 0f || blackPoint != 0 || whitePoint != 255 ||
                                  exposureScale < 0.999f || highlight > 0.001f;

            if (!hasAdjustment)
            {
                if (_cachedColorFilter != null)
                {
                    _cachedHighlightFilter?.Dispose(); _cachedHighlightFilter = null;
                    _cachedColorFilter.Dispose();      _cachedColorFilter = null;
                    _colorPaint?.Dispose();            _colorPaint = null;
                    _lastBrightness = float.NaN;
                    _lastExposureScale = float.NaN;
                    _lastHighlightCompression = float.NaN;
                }
                return paint;
            }

            bool highlightChanged = Math.Abs(highlight - _lastHighlightCompression) > 0.002f;
            bool matrixChanged   = brightness != _lastBrightness || contrast != _lastContrast ||
                                   saturation != _lastSaturation || temperature != _lastTemperature ||
                                   blackPoint != _lastBlackPoint || whitePoint != _lastWhitePoint ||
                                   Math.Abs(exposureScale - _lastExposureScale) > 0.002f;

            if (highlightChanged || matrixChanged)
            {
                // Rebuild the highlight LUT only when the compression value changes
                if (highlightChanged)
                {
                    _lastHighlightCompression = highlight;
                    _cachedHighlightFilter?.Dispose();
                    _cachedHighlightFilter = highlight > 0.001f ? BuildHighlightFilter(highlight) : null;
                }

                if (matrixChanged)
                {
                    _lastBrightness = brightness; _lastContrast   = contrast;
                    _lastSaturation = saturation; _lastTemperature = temperature;
                    _lastBlackPoint = blackPoint; _lastWhitePoint  = whitePoint;
                    _lastExposureScale = exposureScale;
                }

                // Rebuild the final composed filter (highlight inner → color matrix outer)
                _cachedColorFilter?.Dispose();
                Span<float> matrix = stackalloc float[20];
                BuildColorMatrix(matrix, brightness, contrast, saturation, temperature, blackPoint, whitePoint, exposureScale);
                SKColorFilter matrixFilter = SKColorFilter.CreateColorMatrix(matrix.ToArray());

                if (_cachedHighlightFilter != null)
                {
                    // Highlight compression runs first (on raw pixel values), color adjustments on top
                    _cachedColorFilter = SKColorFilter.CreateCompose(matrixFilter, _cachedHighlightFilter);
                    matrixFilter.Dispose();
                }
                else
                {
                    _cachedColorFilter = matrixFilter;
                }
            }

            if (_colorPaint == null)
                _colorPaint = paint.Clone();
            _colorPaint.ColorFilter = _cachedColorFilter;
            return _colorPaint;
        }

        private static void BuildColorMatrix(Span<float> m, float brightness, float contrast, float saturation, float temperature, int blackPoint, int whitePoint, float exposureScale)
        {
            // Identity
            m.Clear();
            m[0] = 1; m[6] = 1; m[12] = 1; m[18] = 1;

            // Brightness: shift RGB
            if (brightness != 0f)
            {
                float b = brightness * 255f;
                m[4] += b;
                m[9] += b;
                m[14] += b;
            }

            // Contrast: scale around midpoint
            if (contrast != 0f)
            {
                float c = 1f + contrast;
                float t = 127.5f * (1f - c);
                m[0] *= c; m[1] *= c; m[2] *= c; m[4] += t;
                m[5] *= c; m[6] *= c; m[7] *= c; m[9] += t;
                m[10] *= c; m[11] *= c; m[12] *= c; m[14] += t;
            }

            // Saturation: mix with luminance
            if (saturation != 0f)
            {
                float s = 1f + saturation;
                float sr = (1f - s) * 0.2126f;
                float sg = (1f - s) * 0.7152f;
                float sb = (1f - s) * 0.0722f;

                Span<float> sat = stackalloc float[20];
                sat[0] = sr + s; sat[1] = sg;     sat[2] = sb;
                sat[5] = sr;     sat[6] = sg + s; sat[7] = sb;
                sat[10] = sr;    sat[11] = sg;    sat[12] = sb + s;
                sat[18] = 1;

                Span<float> tmp = stackalloc float[20];
                MultiplyColorMatrices(sat, m, tmp);
                tmp.CopyTo(m);
            }

            // Color temperature: warm (positive) shifts red up / blue down
            if (temperature != 0f)
            {
                float rShift = temperature * 30f;
                float bShift = -temperature * 30f;
                m[4] += rShift;
                m[14] += bShift;
            }

            // Black/white point: remap levels
            if (blackPoint != 0 || whitePoint != 255)
            {
                float bp = blackPoint / 255f;
                float wp = Math.Max(whitePoint, blackPoint + 1) / 255f;
                float scale = 1f / (wp - bp);
                float offset = -bp * scale * 255f;

                m[0] *= scale; m[4] += offset;
                m[6] *= scale; m[9] += offset;
                m[12] *= scale; m[14] += offset;
            }

            // Auto-exposure: scale all three RGB output rows uniformly.
            // Applied last so it doesn't interact with the other adjustments.
            if (exposureScale < 0.999f)
            {
                for (int i = 0; i < 5; i++)
                {
                    m[i]      *= exposureScale; // R row
                    m[5 + i]  *= exposureScale; // G row
                    m[10 + i] *= exposureScale; // B row
                }
            }
        }

        /// <summary>
        /// Builds a per-channel highlight-compression LUT as a Skia color filter.
        ///
        /// Content below the knee passes through unchanged.  Above the knee the output
        /// is smoothly compressed toward a ceiling using a smoothstep curve, so darks and
        /// midtones are unaffected while bright/white values are rolled off.
        ///
        /// strength=0   → identity (no-op, never actually called)
        /// strength=0.5 → ceiling≈80 %, knee≈68 %  — modest highlight roll-off
        /// strength=1.0 → ceiling≈60 %, knee≈54 %  — aggressive roll-off for flash-bang scenes
        /// </summary>
        private static SKColorFilter BuildHighlightFilter(float strength)
        {
            // ceiling: maximum output value — scales from 1.0 (no effect) down to 0.6
            float ceiling = 1f - strength * 0.4f;
            // knee: where compression starts — tracks just below ceiling, never above 0.75
            float knee    = Math.Min(0.75f, ceiling - 0.01f);

            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float x      = i / 255f;
                float output = x <= knee
                    ? x
                    : knee + (ceiling - knee) * SmoothStep((x - knee) / (1f - knee));
                table[i] = (byte)Math.Clamp((int)(output * 255f + 0.5f), 0, 255);
            }
            // Apply the same curve to R, G, B; leave alpha untouched
            return SKColorFilter.CreateTable(null, table, table, table);
        }

        private static float SmoothStep(float t) => t * t * (3f - 2f * t);

        /// <summary>
        /// Returns the average BT.709 luminance of the image, normalised to 0–1.
        /// Operates on the already-downscaled buffer so pixel count is small (typically ~500 px).
        /// </summary>
        private static unsafe float ComputeAverageLuminance(RefImage<ColorBGRA> image)
        {
            if (image.Width == 0 || image.Height == 0) return 0f;
            fixed (byte* ptr = image)
            {
                long sum = 0;
                int stride = image.RawStride;
                for (int y = 0; y < image.Height; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < image.Width; x++)
                    {
                        byte* p = row + x * 4; // BGRA: p[0]=B, p[1]=G, p[2]=R
                        sum += p[2] * 2126L + p[1] * 7152L + p[0] * 722L;
                    }
                }
                return (float)(sum / ((double)image.Width * image.Height * 10000.0 * 255.0));
            }
        }

        private static void MultiplyColorMatrices(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result)
        {
            result.Clear();
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    float sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += a[row * 5 + k] * b[k * 5 + col];
                    if (col == 4)
                        sum += a[row * 5 + 4];
                    result[row * 5 + col] = sum;
                }
            }
        }

        public override void EnableLayerBrush()
        {
            ConfigurationDialog = new LayerBrushConfigurationDialog<CapturePropertiesViewModel>(1300, 650);
            RecreateCaptureZone();
        }

        public void RecreateCaptureZone()
        {
            if (_creatingCaptureZone || _screenCaptureService == null)
                return;

            try
            {
                _creatingCaptureZone = true;
                RemoveCaptureZone();
                AmbilightCaptureProperties props = Properties.Capture;
                bool defaulting = props.GraphicsCardDeviceId == 0 || props.GraphicsCardVendorId == 0 || props.DisplayName.CurrentValue == null;

                // Try to resolve display by stable monitor path first (survives display ID shifts)
                if (!defaulting && OperatingSystem.IsWindows() && !string.IsNullOrEmpty(props.MonitorDevicePath.CurrentValue))
                    _display = MonitorIdentifier.FindDisplayByMonitorPath(_screenCaptureService, props.MonitorDevicePath.CurrentValue);
                else if (!defaulting && OperatingSystem.IsLinux() && !string.IsNullOrEmpty(props.MonitorDevicePath.CurrentValue))
                    _display = LinuxMonitorIdentifier.FindDisplayByMonitorKey(_screenCaptureService, props.MonitorDevicePath.CurrentValue);

                if (_display == null)
                {
                    // Fall back to legacy GPU + DisplayName matching
                    GraphicsCard? graphicsCard = _screenCaptureService.GetGraphicsCards()
                        .Where(gg => defaulting || (gg.VendorId == props.GraphicsCardVendorId) && (gg.DeviceId == props.GraphicsCardDeviceId))
                        .Cast<GraphicsCard?>()
                        .FirstOrDefault();
                    if (graphicsCard == null)
                        return;

                    _display = _screenCaptureService.GetDisplays(graphicsCard.Value)
                        .Where(d => defaulting || d.DeviceName.Equals(props.DisplayName.CurrentValue, StringComparison.OrdinalIgnoreCase))
                        .Cast<Display?>()
                        .FirstOrDefault();
                }

                if (_display == null)
                    return;

                // If we're defaulting or always capturing full screen, apply the display to the properties
                if (defaulting || props.CaptureFullScreen.CurrentValue)
                    props.ApplyDisplay(_display.Value, true);

                // Stick to a valid region within the display
                int width = Math.Min(_display.Value.Width, props.Width);
                int height = Math.Min(_display.Value.Height, props.Height);
                int x = Math.Min(_display.Value.Width - width, props.X);
                int y = Math.Min(_display.Value.Height - height, props.Y);
                IScreenCapture screenCapture = _screenCaptureService.GetScreenCapture(_display.Value);
                if (screenCapture is AmbilightScreenCapture ambilightCapture)
                {
                    ambilightCapture.SetForceGStreamerPipeWire(props.ForceGStreamerPipeWire);
                    ambilightCapture.SetFpsLimit(props.CaptureFpsLimit);
                }

                _captureZone = screenCapture.RegisterCaptureZone(x, y, width, height, props.DownscaleLevel);
                _captureZone.AutoUpdate = false;

            }
            finally
            {
                _creatingCaptureZone = false;
            }
        }

        private void RemoveCaptureZone()
        {
            if ((_display != null) && (_captureZone != null))
                _screenCaptureService.GetScreenCapture(_display.Value).UnregisterCaptureZone(_captureZone);
            _captureZone = null;
            _display = null;
        }

        public override void DisableLayerBrush()
        {
            RemoveCaptureZone();
            _smoothedBitmap?.Dispose();
            _smoothedBitmap = null;
            _cachedHighlightFilter?.Dispose();
            _cachedHighlightFilter = null;
            _cachedColorFilter?.Dispose();
            _cachedColorFilter = null;
            _colorPaint?.Dispose();
            _colorPaint = null;
            _exposureScale = 1f;
            _lastExposureScale = float.NaN;
            _lastHighlightCompression = float.NaN;
        }

        #endregion
    }
}
