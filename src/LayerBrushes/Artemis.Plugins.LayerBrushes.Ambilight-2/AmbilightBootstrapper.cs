using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Core;
using Artemis.Core.Services;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;
#if WINDOWS
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;
#endif
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wlroots;
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
        private CancellationTokenSource? _screenCaptureInitCancellation;

        // Debounce DisplaySettingsChanged: Windows fires multiple events as it works through a
        // mode switch (resolution change, topology reconfigure, power state). Waiting 5 seconds
        // ensures all events have fired and the display state has fully settled before we restart
        // the capture service and run DDC checks on the new instances.
        private Timer? _displaySettledTimer;
        private const int DisplaySettledDelayMs = 10000;

        #region Properties & Fields

        internal static AmbilightScreenCaptureService? ScreenCaptureService { get; private set; }
        internal static Exception? ScreenCaptureInitializationFailure { get; private set; }
        private static readonly object ScreenCaptureServiceLock = new();

        #endregion

        #region Methods

        public override void OnPluginEnabled(Plugin plugin)
        {
            _logger = plugin.Resolve<ILogger>();
            _managementService = plugin.Resolve<IPluginManagementService>();
            _renderService = plugin.Resolve<IRenderService>();
            _brushProvider = plugin.GetFeatureInfo<AmbilightLayerBrushProvider>();
            ScreenCaptureInitializationFailure = null;

            if (OperatingSystem.IsLinux())
            {
                AmbilightLinuxDiagnostics.Write(_logger ?? Log.ForContext<AmbilightBootstrapper>(),
                    $"plugin enabled; fallback diagnostics at {AmbilightLinuxDiagnostics.LogPath}");
                _logger?.Information("Starting Linux screen capture service initialization in the background");
                _screenCaptureInitCancellation = new CancellationTokenSource();
                _ = Task.Run(() => InitializeLinuxScreenCaptureServiceAsync(plugin, _screenCaptureInitCancellation.Token));
            }
            else
            {
            if (OperatingSystem.IsWindows())
            {
                AmbilightWindowsDiagnostics.Write(_logger ?? Log.ForContext<AmbilightBootstrapper>(),
                    $"plugin enabled; fallback diagnostics at {AmbilightWindowsDiagnostics.LogPath}");
            }

            try
            {
                IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
                SetScreenCaptureService(new AmbilightScreenCaptureService(screenCaptureService));
            }
            catch (Exception ex)
            {
                ScreenCaptureInitializationFailure = ex;
                if (OperatingSystem.IsWindows())
                {
                    AmbilightWindowsDiagnostics.Write(_logger ?? Log.ForContext<AmbilightBootstrapper>(),
                        $"screen capture service unavailable: {ex.GetBaseException().Message}");
                }
                _logger?.Error(ex, "Screen capture service unavailable — plugin will load but capture is disabled");
            }
            }
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
            _screenCaptureInitCancellation?.Cancel();
            _screenCaptureInitCancellation?.Dispose();
            _screenCaptureInitCancellation = null;

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

        private async Task InitializeScreenCaptureServiceAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                IScreenCaptureService screenCaptureService = CreateScreenCaptureService(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var ambilightScreenCaptureService = new AmbilightScreenCaptureService(screenCaptureService);
                if (!SetScreenCaptureService(ambilightScreenCaptureService))
                {
                    ambilightScreenCaptureService.Dispose();
                    return;
                }

                AmbilightLinuxDiagnostics.Write(_logger ?? Log.ForContext<AmbilightBootstrapper>(), "screen capture service initialized");
                _logger?.Information("Linux screen capture service initialized");
                RequestImmediateRender();
            }
            catch (OperationCanceledException)
            {
                _logger?.Debug("Linux screen capture service initialization was cancelled");
            }
            catch (Exception ex)
            {
                ScreenCaptureInitializationFailure = ex;
                _logger?.Error(ex, "Screen capture service unavailable - plugin will load but capture is disabled");
            }
        }

        private async Task InitializeLinuxScreenCaptureServiceAsync(Plugin plugin, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                ShowLinuxFirstRunPromptIfNeeded(plugin);
                cancellationToken.ThrowIfCancellationRequested();
                await InitializeScreenCaptureServiceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.Debug("Linux screen capture service initialization was cancelled");
            }
            catch (Exception ex)
            {
                ScreenCaptureInitializationFailure = ex;
                _logger?.Error(ex, "Linux screen capture service initialization failed before capture service creation");
            }
        }

        private static bool SetScreenCaptureService(AmbilightScreenCaptureService screenCaptureService)
        {
            lock (ScreenCaptureServiceLock)
            {
                if (ScreenCaptureService != null)
                    return false;

                ScreenCaptureService = screenCaptureService;
                ScreenCaptureInitializationFailure = null;
                return true;
            }
        }

        private static IScreenCaptureService CreateScreenCaptureService(CancellationToken cancellationToken = default)
        {
            if (OperatingSystem.IsWindows())
            {
#if WINDOWS
                ILogger logger = Log.ForContext<AmbilightBootstrapper>();

                try
                {
                    bool wgcSupported = WgcScreenCaptureService.IsSupported(out string? wgcUnsupportedReason);
                    logger.Information("Windows Graphics Capture support check returned {Supported}. Reason: {Reason}",
                        wgcSupported,
                        wgcUnsupportedReason ?? "(none)");
                    AmbilightWindowsDiagnostics.Write(logger,
                        $"WGC support check returned {wgcSupported}. Reason: {wgcUnsupportedReason ?? "(none)"}");

                    if (wgcSupported)
                    {
                        logger.Information("Using Windows Graphics Capture backend");
                        AmbilightWindowsDiagnostics.Write(logger, "using Windows Graphics Capture backend");
                        return new WgcScreenCaptureService();
                    }

                    logger.Warning("Windows Graphics Capture is not supported on this system, falling back to DX11 Desktop Duplication. Reason: {Reason}",
                        wgcUnsupportedReason ?? "unknown");
                    AmbilightWindowsDiagnostics.Write(logger,
                        $"WGC unsupported; falling back to DX11 Desktop Duplication. Reason: {wgcUnsupportedReason ?? "unknown"}");
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Windows Graphics Capture initialization failed, falling back to DX11 Desktop Duplication");
                    AmbilightWindowsDiagnostics.Write(logger,
                        $"WGC initialization failed; falling back to DX11 Desktop Duplication: {ex.GetBaseException().Message}");
                }
#else
                Log.ForContext<AmbilightBootstrapper>().Warning(
                    "Windows Graphics Capture was not compiled into this Ambilight v2 build, falling back to DX11 Desktop Duplication");
                AmbilightWindowsDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(),
                    "WGC was not compiled into this build; falling back to DX11 Desktop Duplication");
#endif
                Log.ForContext<AmbilightBootstrapper>().Information("Using DX11 Desktop Duplication backend");
                AmbilightWindowsDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(), "using DX11 Desktop Duplication backend");
                return new DX11ScreenCaptureService();
            }

            if (OperatingSystem.IsLinux())
            {
                Exception? portalFailure = null;
                Exception? wlrootsFailure = null;

                AmbilightLinuxDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(),
                    $"creating Linux capture service; WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(not set)"} DBUS_SESSION_BUS_ADDRESS={Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "(not set)"}");
                Log.ForContext<AmbilightBootstrapper>().Information(
                    "Linux: WAYLAND_DISPLAY={WD} DBUS_SESSION_BUS_ADDRESS={DB}",
                    Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(not set)",
                    Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "(not set)");

                AllowPortalProcessInspection();

                try
                {
                    if (PortalPipeWireScreenCaptureService.IsSupported(out string? portalUnsupportedReason))
                    {
                        AmbilightLinuxDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(), "attempting PipeWire portal backend");
                        Log.ForContext<AmbilightBootstrapper>().Information("Attempting PipeWire portal backend");
                        return new PortalPipeWireScreenCaptureService(cancellationToken);
                    }

                    portalFailure = new PlatformNotSupportedException($"PipeWire portal is not supported: {portalUnsupportedReason}");
                    AmbilightLinuxDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(), $"PipeWire portal unavailable: {portalUnsupportedReason}");
                    Log.ForContext<AmbilightBootstrapper>().Information("PipeWire portal unavailable: {Reason}", portalUnsupportedReason);
                }
                catch (Exception ex)
                {
                    portalFailure = ex;
                    AmbilightLinuxDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(), $"PipeWire portal failed: {ex.GetBaseException().Message}");
                    Log.ForContext<AmbilightBootstrapper>().Warning(ex, "PipeWire portal failed, trying wlroots");
                }

                try
                {
                    if (WlrootsScreenCaptureService.IsSupported(out string? wlrootsUnsupportedReason))
                    {
                        Log.ForContext<AmbilightBootstrapper>().Information("Attempting wlroots backend");
                        return new WlrootsScreenCaptureService();
                    }

                    wlrootsFailure = new PlatformNotSupportedException($"wlroots is not supported: {wlrootsUnsupportedReason}");
                    AmbilightLinuxDiagnostics.Write(Log.ForContext<AmbilightBootstrapper>(), $"wlroots unavailable: {wlrootsUnsupportedReason}");
                    Log.ForContext<AmbilightBootstrapper>().Information("wlroots unavailable: {Reason}", wlrootsUnsupportedReason);
                }
                catch (Exception ex)
                {
                    wlrootsFailure = ex;
                    Log.ForContext<AmbilightBootstrapper>().Warning(ex, "wlroots failed");
                }

                throw new PlatformNotSupportedException(
                    "No Linux screen capture backend available. " +
                    "PipeWire portal requires: xdg-desktop-portal + a compositor backend (xdg-desktop-portal-gnome/kde/hyprland/wlr) + gstreamer + gst-plugin-pipewire. " +
                    "wlroots requires: grim + wlr-randr/swaymsg/hyprctl. " +
                    $"Portal error: {portalFailure?.GetBaseException().Message ?? "not attempted"}. " +
                    $"wlroots error: {wlrootsFailure?.GetBaseException().Message ?? "not attempted"}.",
                    portalFailure ?? wlrootsFailure);
            }

            Log.ForContext<AmbilightBootstrapper>().Information("Using X11 backend");
            try
            {
                var svc = new X11ScreenCaptureService();
                Log.ForContext<AmbilightBootstrapper>().Information("X11 backend created successfully");
                return svc;
            }
            catch (Exception ex)
            {
                Log.ForContext<AmbilightBootstrapper>().Error(ex, "X11 backend creation failed");
                throw;
            }
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

        /// <summary>
        /// On first Linux load, writes a prominent notice telling the user to run the
        /// bundled artemis-portal-setup.sh script. Artemis does not currently ship a
        /// .desktop entry on Linux, which XDG portal screen cast requires to identify
        /// the calling app. The script creates one under ~/.local/share/applications/
        /// and a systemd-run launcher that passes the Wayland/DBus session env vars
        /// through to the Artemis process.
        /// </summary>
        private void ShowLinuxFirstRunPromptIfNeeded(Plugin plugin)
        {
            try
            {
                string stamp = Path.Combine(plugin.Directory.FullName, ".linux-setup-acknowledged");
                if (File.Exists(stamp))
                    return;

                string scriptPath = Path.Combine(plugin.Directory.FullName, "linux", "artemis-portal-setup.sh");
                string? artemisExe = Environment.ProcessPath;

                bool setupOk = TryRunLinuxSetupScript(scriptPath, artemisExe);

                string msg = setupOk
                    ? "=== Ambilight v2 Linux first-run setup ===\n" +
                      "The plugin just ran artemis-portal-setup.sh and created a new\n" +
                      "desktop entry at ~/.local/share/applications/org.artemisrgb.Artemis.desktop\n" +
                      "with a launcher that passes WAYLAND_DISPLAY/DBUS_SESSION_BUS_ADDRESS\n" +
                      "through so the XDG portal can identify Artemis.\n\n" +
                      "!!! ACTION REQUIRED !!!\n" +
                      "FULLY CLOSE Artemis now and relaunch it via the NEW shortcut the\n" +
                      "script just created (not the one Artemis installed, and not the\n" +
                      "binary directly). From the app menu, or via:\n\n" +
                      "  gtk-launch org.artemisrgb.Artemis\n\n" +
                      "Launching the Artemis binary directly, or launching via Artemis's\n" +
                      "own shortcut, BYPASSES the portal-compatible desktop entry. You\n" +
                      "WILL NOT get the screen sharing permission prompt from your\n" +
                      "compositor, and screen capture WILL NOT work.\n\n" +
                      "Delete this plugin's .linux-setup-acknowledged file to re-run\n" +
                      "the setup next time."
                    : "=== Ambilight v2 Linux first-run setup ===\n" +
                      "The plugin tried to run artemis-portal-setup.sh automatically but\n" +
                      "it failed (see preceding log lines). Run it manually:\n\n" +
                      $"  chmod +x \"{scriptPath}\"\n" +
                      $"  \"{scriptPath}\" \"{artemisExe ?? "/path/to/Artemis"}\"\n\n" +
                      "Then FULLY CLOSE Artemis and relaunch it via the newly-created\n" +
                      "shortcut (not the Artemis-installed one, not the binary directly),\n" +
                      "or via: gtk-launch org.artemisrgb.Artemis\n\n" +
                      "Launching Artemis any other way BYPASSES the portal-compatible\n" +
                      "desktop entry. You WILL NOT get the screen sharing permission\n" +
                      "prompt from your compositor, and screen capture WILL NOT work.\n\n" +
                      "Delete this plugin's .linux-setup-acknowledged file to re-try.";

                _logger?.Warning(msg);
                AmbilightLinuxDiagnostics.Write(_logger ?? Log.ForContext<AmbilightBootstrapper>(), msg);

                if (setupOk)
                {
                    try { File.WriteAllText(stamp, DateTime.UtcNow.ToString("O")); }
                    catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show Linux first-run prompt");
            }
        }

        private bool TryRunLinuxSetupScript(string scriptPath, string? artemisExe)
        {
            if (!File.Exists(scriptPath))
            {
                _logger?.Warning("Linux setup script not found at {Path}", scriptPath);
                return false;
            }

            if (string.IsNullOrWhiteSpace(artemisExe) || !File.Exists(artemisExe))
            {
                _logger?.Warning("Could not determine Artemis executable path for setup script (got {Exe})", artemisExe ?? "(null)");
                return false;
            }

            try
            {
                // chmod +x the script — Content Include doesn't preserve the exec bit
                using (Process chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
                {
                    chmod.WaitForExit(5000);
                }

                var psi = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add(scriptPath);
                psi.ArgumentList.Add(artemisExe);

                using Process proc = Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);

                if (proc.ExitCode != 0)
                {
                    _logger?.Warning("Setup script exited with code {Code}. stdout: {Stdout} stderr: {Stderr}",
                        proc.ExitCode, stdout, stderr);
                    return false;
                }

                _logger?.Information("Setup script completed successfully: {Stdout}", stdout);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Failed to run Linux setup script");
                return false;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);

        private const int PR_SET_DUMPABLE = 4;
        private const int PR_SET_PTRACER = 0x59616d61;
        private static readonly nint PR_SET_PTRACER_ANY = -1;

        private static void AllowPortalProcessInspection()
        {
            if (!OperatingSystem.IsLinux())
                return;

            ILogger logger = Log.ForContext<AmbilightBootstrapper>();

            // xdg-desktop-portal validates unsandboxed callers by inspecting /proc/<pid>/root.
            // Hardened systems can deny that unless the process is dumpable and Yama ptrace
            // restrictions explicitly allow unrelated same-user processes to inspect it.
            int dumpableResult = prctl(PR_SET_DUMPABLE, 1, 0, 0, 0);
            int dumpableErrno = Marshal.GetLastWin32Error();
            int ptracerResult = prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0);
            int ptracerErrno = Marshal.GetLastWin32Error();

            if (dumpableResult == 0)
                logger.Information("prctl(PR_SET_DUMPABLE, 1) succeeded");
            else
                logger.Warning("prctl(PR_SET_DUMPABLE, 1) failed errno={Errno}", dumpableErrno);

            if (ptracerResult == 0)
                logger.Information("prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY) succeeded");
            else
                logger.Warning("prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY) failed errno={Errno}", ptracerErrno);

            try
            {
                string ptraceScopePath = "/proc/sys/kernel/yama/ptrace_scope";
                if (File.Exists(ptraceScopePath))
                    logger.Information("kernel.yama.ptrace_scope={PtraceScope}", File.ReadAllText(ptraceScopePath).Trim());
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Could not read kernel.yama.ptrace_scope");
            }
        }

    }
}
