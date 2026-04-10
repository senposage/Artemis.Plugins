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
        private SKColorFilter? _cachedColorFilter;
        private float _lastBrightness = float.NaN;
        private float _lastContrast = float.NaN;
        private float _lastSaturation = float.NaN;
        private float _lastTemperature = float.NaN;
        private int _lastBlackPoint = -1;
        private int _lastWhitePoint = -1;

        #endregion

        #region Methods

        public override void Update(double deltaTime)
        {
            if (_captureZone == null) return;

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
            if (_captureZone == null) return;

            try
            {
                AmbilightCaptureProperties properties = Properties.Capture;
                using (_captureZone.Lock())
                {
                    RefImage<ColorBGRA> image = _captureZone.GetRefImage<ColorBGRA>();
                    if (image.Width == 0 || image.Height == 0) return;

                    if (properties.BlackBarDetectionTop || properties.BlackBarDetectionBottom || properties.BlackBarDetectionLeft || properties.BlackBarDetectionRight)
                        image = image.RemoveBlackBars(properties.BlackBarDetectionThreshold,
                                                      properties.BlackBarDetectionTop, properties.BlackBarDetectionBottom,
                                                      properties.BlackBarDetectionLeft, properties.BlackBarDetectionRight);

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

                            DrawSmoothedWithColorAdjustments(canvas, bounds, paint, properties);
                        }
                        else
                        {
                            DrawWithColorAdjustments(canvas, skImage, bounds, paint, properties);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Capture zone was disposed during a display change - safe to ignore
            }
        }

        /// <summary>
        /// Draws from the smoothed bitmap without creating an intermediate SKImage GPU texture copy.
        /// </summary>
        private void DrawSmoothedWithColorAdjustments(SKCanvas canvas, SKRect bounds, SKPaint paint, AmbilightCaptureProperties props)
        {
            SKPaint effectivePaint = GetColorAdjustedPaint(paint, props);
            canvas.DrawBitmap(_smoothedBitmap!, bounds, effectivePaint);
        }

        private void DrawWithColorAdjustments(SKCanvas canvas, SKImage image, SKRect bounds, SKPaint paint, AmbilightCaptureProperties props)
        {
            SKPaint effectivePaint = GetColorAdjustedPaint(paint, props);
            canvas.DrawImage(image, bounds, effectivePaint);
        }

        /// <summary>
        /// Returns a paint with the cached color filter applied, or the original paint if no adjustments are needed.
        /// The color filter is only rebuilt when parameters actually change.
        /// </summary>
        private SKPaint GetColorAdjustedPaint(SKPaint paint, AmbilightCaptureProperties props)
        {
            float brightness = props.Brightness.CurrentValue;
            float contrast = props.Contrast.CurrentValue;
            float saturation = props.Saturation.CurrentValue;
            float temperature = props.ColorTemperature.CurrentValue;
            int blackPoint = Math.Clamp((int)props.BlackPoint, 0, 50);
            int whitePoint = Math.Clamp((int)props.WhitePoint, 200, 255);

            bool hasColorAdjustment = brightness != 0f || contrast != 0f || saturation != 0f ||
                                      temperature != 0f || blackPoint != 0 || whitePoint != 255;

            if (!hasColorAdjustment)
            {
                // Dispose cached resources when adjustments are turned off
                if (_cachedColorFilter != null)
                {
                    _cachedColorFilter.Dispose();
                    _cachedColorFilter = null;
                    _colorPaint?.Dispose();
                    _colorPaint = null;
                    _lastBrightness = float.NaN;
                }
                return paint;
            }

            // Only rebuild the color filter when parameters have actually changed
            if (brightness != _lastBrightness || contrast != _lastContrast || saturation != _lastSaturation ||
                temperature != _lastTemperature || blackPoint != _lastBlackPoint || whitePoint != _lastWhitePoint)
            {
                _lastBrightness = brightness;
                _lastContrast = contrast;
                _lastSaturation = saturation;
                _lastTemperature = temperature;
                _lastBlackPoint = blackPoint;
                _lastWhitePoint = whitePoint;

                _cachedColorFilter?.Dispose();
                Span<float> matrix = stackalloc float[20];
                BuildColorMatrix(matrix, brightness, contrast, saturation, temperature, blackPoint, whitePoint);
                _cachedColorFilter = SKColorFilter.CreateColorMatrix(matrix.ToArray());
            }

            // Reuse the paint — clone once, then just update the filter
            if (_colorPaint == null)
                _colorPaint = paint.Clone();
            _colorPaint.ColorFilter = _cachedColorFilter;
            return _colorPaint;
        }

        private static void BuildColorMatrix(Span<float> m, float brightness, float contrast, float saturation, float temperature, int blackPoint, int whitePoint)
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
                _captureZone = screenCapture.RegisterCaptureZone(x, y, width, height, props.DownscaleLevel);
                _captureZone.AutoUpdate = false;

                // Apply FPS limit to the capture loop
                if (screenCapture is AmbilightScreenCapture ambilightCapture)
                    ambilightCapture.SetFpsLimit(props.CaptureFpsLimit);
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
            _cachedColorFilter?.Dispose();
            _cachedColorFilter = null;
            _colorPaint?.Dispose();
            _colorPaint = null;
        }

        #endregion
    }
}