using System;
using Artemis.Core;
using Artemis.Core.Services;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;
using Microsoft.Win32;
using ScreenCapture.NET;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight
{
    public class AmbilightBootstrapper : PluginBootstrapper
    {
        private ILogger? _logger;
        private IPluginManagementService? _managementService;
        private PluginFeatureInfo? _brushProvider;

        #region Properties & Fields

        internal static AmbilightScreenCaptureService? ScreenCaptureService { get; private set; }

        #endregion

        #region Methods

        public override void OnPluginEnabled(Plugin plugin)
        {
            _logger = plugin.Resolve<ILogger>();
            _managementService = plugin.Resolve<IPluginManagementService>();
            _brushProvider = plugin.GetFeatureInfo<AmbilightLayerBrushProvider>();

            IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
            ScreenCaptureService ??= new AmbilightScreenCaptureService(screenCaptureService);
            if (OperatingSystem.IsWindows())
            {
                // DisplaySettingsChanging fires BEFORE the change - suspend capture to prevent
                // native access violations in the graphics driver from stale DXGI resources.
                SystemEvents.DisplaySettingsChanging += SystemEventsOnDisplaySettingsChanging;
                SystemEvents.DisplaySettingsChanged += SystemEventsOnDisplaySettingsChanged;
            }
        }

        public override void OnPluginDisabled(Plugin plugin)
        {
            ScreenCaptureService?.Dispose();
            ScreenCaptureService = null;
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.DisplaySettingsChanging -= SystemEventsOnDisplaySettingsChanging;
                SystemEvents.DisplaySettingsChanged -= SystemEventsOnDisplaySettingsChanged;
            }
        }

        private static IScreenCaptureService CreateScreenCaptureService()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (WgcScreenCaptureService.IsSupported())
                    {
                        Log.ForContext<AmbilightBootstrapper>().Information("Using Windows Graphics Capture backend");
                        return new WgcScreenCaptureService();
                    }
                }
                catch (Exception ex)
                {
                    Log.ForContext<AmbilightBootstrapper>().Warning(ex, "WGC initialization failed, falling back to DX11");
                }

                Log.ForContext<AmbilightBootstrapper>().Information("Using DX11 Desktop Duplication backend");
                return new DX11ScreenCaptureService();
            }

            return new X11ScreenCaptureService();
        }

        private void SystemEventsOnDisplaySettingsChanging(object? sender, EventArgs e)
        {
            // Suspend all captures BEFORE the display change happens.
            // This stops the DX11 capture loop from calling into the driver with stale resources.
            _logger?.Debug("Display settings changing, suspending all screen captures");
            ScreenCaptureService?.SuspendAllCaptures();
        }

        private void SystemEventsOnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_brushProvider?.Instance == null || !_brushProvider.Instance.IsEnabled)
                _logger?.Debug("Display settings changed, but ambilight feature is disabled");
            else
            {
                _logger?.Debug("Display settings changed, restarting ambilight feature");

                _managementService?.DisablePluginFeature(_brushProvider.Instance, false);
                ScreenCaptureService?.Dispose();
                IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
                ScreenCaptureService = new AmbilightScreenCaptureService(screenCaptureService);
                _managementService?.EnablePluginFeature(_brushProvider.Instance, false);
            }
        }

        #endregion
    }
}