using System;
using System.Threading;
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
        private IRenderService? _renderService;
        private PluginFeatureInfo? _brushProvider;
        private WindowsDisplayStateMonitor? _displayStateMonitor;

        // Debounce DisplaySettingsChanged: Windows fires multiple events as it works through a
        // mode switch (resolution change, topology reconfigure, power state). Waiting 5 seconds
        // ensures all events have fired and the display state has fully settled before we restart
        // the capture service and run DDC checks on the new instances.
        private Timer? _displaySettledTimer;
        private const int DisplaySettledDelayMs = 10000;

        #region Properties & Fields

        internal static AmbilightScreenCaptureService? ScreenCaptureService { get; private set; }

        #endregion

        #region Methods

        public override void OnPluginEnabled(Plugin plugin)
        {
            _logger = plugin.Resolve<ILogger>();
            _managementService = plugin.Resolve<IPluginManagementService>();
            _renderService = plugin.Resolve<IRenderService>();
            _brushProvider = plugin.GetFeatureInfo<AmbilightLayerBrushProvider>();

            IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
            ScreenCaptureService ??= new AmbilightScreenCaptureService(screenCaptureService);
            if (OperatingSystem.IsWindows())
            {
                // DisplaySettingsChanging fires BEFORE the change - suspend capture to prevent
                // native access violations in the graphics driver from stale DXGI resources.
                SystemEvents.DisplaySettingsChanging += SystemEventsOnDisplaySettingsChanging;
                SystemEvents.DisplaySettingsChanged += SystemEventsOnDisplaySettingsChanged;
                SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
                _displayStateMonitor = new WindowsDisplayStateMonitor();
                _displayStateMonitor.DisplayStateChanged += DisplayStateMonitorOnDisplayStateChanged;
            }
        }

        public override void OnPluginDisabled(Plugin plugin)
        {
            _displaySettledTimer?.Dispose();
            _displaySettledTimer = null;

            if (_displayStateMonitor != null)
            {
                _displayStateMonitor.DisplayStateChanged -= DisplayStateMonitorOnDisplayStateChanged;
                _displayStateMonitor.Dispose();
                _displayStateMonitor = null;
            }

            ScreenCaptureService?.Dispose();
            ScreenCaptureService = null;
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.DisplaySettingsChanging -= SystemEventsOnDisplaySettingsChanging;
                SystemEvents.DisplaySettingsChanged -= SystemEventsOnDisplaySettingsChanged;
                SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
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
            _logger?.Debug("Display settings changing, suspending all screen captures");
            ScreenCaptureService?.SuspendAllCaptures();
            RequestImmediateRender();
        }

        private void SystemEventsOnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_brushProvider?.Instance == null || !_brushProvider.Instance.IsEnabled)
            {
                _logger?.Debug("Display settings changed, but ambilight feature is disabled");
                return;
            }

            // Windows fires multiple DisplaySettingsChanged events during a mode switch
            // (one per stage: topology, resolution, power state). Debounce by resetting
            // the timer on every event — restart only fires once things have settled.
            _logger?.Debug("Display settings changed, waiting {Delay}ms for display state to settle", DisplaySettledDelayMs);
            _displaySettledTimer?.Dispose();
            _displaySettledTimer = new Timer(_ =>
            {
                _logger?.Debug("Display state settled, restarting ambilight feature");
                RestartAmbilightFeature();
            }, null, DisplaySettledDelayMs, Timeout.Infinite);
        }

        private void SystemEventsOnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    _logger?.Debug("System suspend detected, suspending all screen captures");
                    ScreenCaptureService?.SuspendAllCaptures();
                    RequestImmediateRender();
                    break;
                case PowerModes.Resume:
                    if (_brushProvider?.Instance == null || !_brushProvider.Instance.IsEnabled)
                        _logger?.Debug("System resumed, but ambilight feature is disabled");
                    else
                    {
                        _logger?.Debug("System resumed, restarting ambilight feature");
                        RestartAmbilightFeature();
                    }
                    break;
            }
        }

        private void DisplayStateMonitorOnDisplayStateChanged(object? sender, int displayState)
        {
            // displayState: 0 = off, 1 = on, 2 = dimmed
            _logger?.Debug("GUID_CONSOLE_DISPLAY_STATE = {State}", displayState);

            if (displayState != 0)
            {
                // Do NOT resume here.
                //
                // When a secondary DP monitor is physically powered off, Windows:
                //   1. fires displayState=0  → we suspend (correct)
                //   2. fires displayState=1  → the *remaining* primary just became active
                //
                // Calling ResumeAllCaptures() at step 2 clears _suspended on ALL captures
                // (including the now-off secondary) before the DDC poll has had a chance to
                // set _displayOff=true on that capture. The secondary LED then flashes back
                // on for up to 5 seconds until the next DDC tick.
                //
                // Fix: ignore display-on events here entirely. AmbilightScreenCapture's DDC
                // poll (CheckDisplayPowerState) clears _suspended automatically when it
                // confirms a display is On.
                return;
            }

            _logger?.Debug("Display idle/power-off detected, suspending all screen captures");
            ScreenCaptureService?.SuspendAllCaptures();
            RequestImmediateRender();
        }

        private void RestartAmbilightFeature()
        {
            if (_brushProvider?.Instance == null)
                return;

            // Snapshot the current suspended/off state of every capture BEFORE disposal.
            // New instances will inherit this so they don't trust a potentially-stale DDC "On"
            // from a headless DP display.  The DDC poll clears it once the display is confirmed on.
            AmbilightScreenCapture.PersistDisplayOffState();

            _managementService?.DisablePluginFeature(_brushProvider.Instance, false);
            ScreenCaptureService?.Dispose();
            IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
            ScreenCaptureService = new AmbilightScreenCaptureService(screenCaptureService);
            _managementService?.EnablePluginFeature(_brushProvider.Instance, false);
            RequestImmediateRender();
        }

        private void RequestImmediateRender()
        {
            _renderService?.Surface.Update(true);
        }

        #endregion
    }
}
