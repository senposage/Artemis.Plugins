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

                            using SKImage smoothedImage = SKImage.FromBitmap(_smoothedBitmap);
                            DrawWithColorAdjustments(canvas, smoothedImage, bounds, paint, properties);
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

        private static void DrawWithColorAdjustments(SKCanvas canvas, SKImage image, SKRect bounds, SKPaint paint, AmbilightCaptureProperties props)
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
                canvas.DrawImage(image, bounds, paint);
                return;
            }

            using var colorPaint = paint.Clone();

            // Build color matrix combining all adjustments
            float[] matrix = BuildColorMatrix(brightness, contrast, saturation, temperature, blackPoint, whitePoint);
            colorPaint.ColorFilter = SKColorFilter.CreateColorMatrix(matrix);

            canvas.DrawImage(image, bounds, colorPaint);
        }

        private static float[] BuildColorMatrix(float brightness, float contrast, float saturation, float temperature, int blackPoint, int whitePoint)
        {
            // Start with identity
            float[] m = {
                1, 0, 0, 0, 0,
                0, 1, 0, 0, 0,
                0, 0, 1, 0, 0,
                0, 0, 0, 1, 0
            };

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
                float c = 1f + contrast; // -1..1 maps to 0..2
                float t = 127.5f * (1f - c);
                m[0] *= c; m[1] *= c; m[2] *= c; m[4] += t;
                m[5] *= c; m[6] *= c; m[7] *= c; m[9] += t;
                m[10] *= c; m[11] *= c; m[12] *= c; m[14] += t;
            }

            // Saturation: mix with luminance
            if (saturation != 0f)
            {
                float s = 1f + saturation; // -1..1 maps to 0..2
                float sr = (1f - s) * 0.2126f;
                float sg = (1f - s) * 0.7152f;
                float sb = (1f - s) * 0.0722f;

                float[] sat = {
                    sr + s, sg,     sb,     0, 0,
                    sr,     sg + s, sb,     0, 0,
                    sr,     sg,     sb + s, 0, 0,
                    0,      0,      0,      1, 0
                };
                m = MultiplyColorMatrices(sat, m);
            }

            // Color temperature: warm (positive) shifts red up / blue down, cool (negative) is opposite
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

            return m;
        }

        private static float[] MultiplyColorMatrices(float[] a, float[] b)
        {
            float[] result = new float[20];
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
            return result;
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
        }

        #endregion
    }
}