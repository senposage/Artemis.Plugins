using System;
using Artemis.Core;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.PropertyGroups
{
    public class AmbilightCaptureProperties : LayerPropertyGroup
    {
        public IntLayerProperty GraphicsCardVendorId { get; set; }
        public IntLayerProperty GraphicsCardDeviceId { get; set; }
        public LayerProperty<string> DisplayName { get; set; }
        /// <summary>
        /// Stable monitor hardware path that persists across display power cycles.
        /// </summary>
        public LayerProperty<string> MonitorDevicePath { get; set; }

        public IntLayerProperty X { get; set; }
        public IntLayerProperty Y { get; set; }
        public IntLayerProperty Width { get; set; }
        public IntLayerProperty Height { get; set; }
        public BoolLayerProperty CaptureFullScreen { get; set; }

        public BoolLayerProperty FlipHorizontal { get; set; }
        public BoolLayerProperty FlipVertical { get; set; }
        public IntLayerProperty DownscaleLevel { get; set; }

        public BoolLayerProperty BlackBarDetectionTop { get; set; }
        public BoolLayerProperty BlackBarDetectionBottom { get; set; }
        public BoolLayerProperty BlackBarDetectionLeft { get; set; }
        public BoolLayerProperty BlackBarDetectionRight { get; set; }
        public IntLayerProperty BlackBarDetectionThreshold { get; set; }

        // Color controls
        public LayerProperty<float> Brightness { get; set; }
        public LayerProperty<float> Contrast { get; set; }
        public LayerProperty<float> Saturation { get; set; }
        public LayerProperty<float> ColorTemperature { get; set; }
        public IntLayerProperty BlackPoint { get; set; }
        public IntLayerProperty WhitePoint { get; set; }
        /// <summary>
        /// Auto-exposure strength 0–1. At > 0 the render path scales output brightness
        /// inversely with scene luminance so a full-white frame does not blast at 100%.
        /// </summary>
        public LayerProperty<float> AutoExposureStrength { get; set; }

        /// <summary>
        /// Highlight compression strength 0–1.  Applies a per-channel smooth roll-off above a
        /// knee point so bright/white content is dimmed without touching darks or midtones.
        /// </summary>
        public LayerProperty<float> HighlightCompression { get; set; }

        // Smoothing
        public LayerProperty<float> SmoothingFactor { get; set; }

        // Performance
        public IntLayerProperty FrameSkip { get; set; }
        public IntLayerProperty CaptureFpsLimit { get; set; }


        protected override void PopulateDefaults()
        {
            DownscaleLevel.DefaultValue = 6;
            Brightness.DefaultValue = 0f;
            Contrast.DefaultValue = 0f;
            Saturation.DefaultValue = 0f;
            ColorTemperature.DefaultValue = 0f;
            BlackPoint.DefaultValue = 0;
            WhitePoint.DefaultValue = 255;
            AutoExposureStrength.DefaultValue = 0f;
            HighlightCompression.DefaultValue = 0f;
            SmoothingFactor.DefaultValue = 0f;
            FrameSkip.DefaultValue = 0;
            CaptureFpsLimit.DefaultValue = 30;
        }

        protected override void EnableProperties()
        {
        }

        protected override void DisableProperties()
        {
        }

        public void ApplyDisplay(Display display, bool includeRegion)
        {
            GraphicsCardVendorId.BaseValue = display.GraphicsCard.VendorId;
            GraphicsCardDeviceId.BaseValue = display.GraphicsCard.DeviceId;
            DisplayName.BaseValue = display.DeviceName;

            if (OperatingSystem.IsWindows())
                MonitorDevicePath.BaseValue = MonitorIdentifier.GetMonitorDevicePath(display.DeviceName) ?? "";

            if (includeRegion)
            {
                X.BaseValue = 0;
                Y.BaseValue = 0;
                Width.BaseValue = display.Width;
                Height.BaseValue = display.Height;
                CaptureFullScreen.BaseValue = true;
            }

            // Always true of course if includeRegion is true
            CaptureFullScreen.BaseValue = X == 0 & Y == 0 && Width == display.Width && Height == display.Height;
        }
    }
}