using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenCapture.NET;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture
{
    public sealed class AmbilightScreenCapture : IScreenCapture
    {
        #region Native

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

        private static bool IsFullscreenExclusiveAppRunning()
        {
            if (!OperatingSystem.IsWindows()) return false;
            return SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
        }

        #endregion

        #region Static power-state poll

        private static readonly ILogger Logger = Log.ForContext<AmbilightScreenCapture>();

        // All active captures keyed by DeviceName — the 5-second timer iterates this.
        private static readonly ConcurrentDictionary<string, AmbilightScreenCapture> s_instances =
            new(StringComparer.OrdinalIgnoreCase);

        // Persists DDC history across RestartAmbilightFeature() so a new instance knows
        // DDC previously worked, enabling it to treat DdcUnavailable as "monitor is sleeping"
        // rather than "monitor has no DDC support".
        private static readonly ConcurrentDictionary<string, bool> s_ddcEverWorked =
            new(StringComparer.OrdinalIgnoreCase);

        // Persists the display-off state across RestartAmbilightFeature() so the new instance
        // does not trust a potentially-stale DDC "On" reading from the constructor.
        //
        // Problem without this: after a DP display physically powers off, Windows keeps a headless
        // virtual display whose DDC chip can still respond "On". When RestartAmbilightFeature()
        // creates a new AmbilightScreenCapture, the constructor calls CheckDisplayPowerState()
        // immediately, gets "On", and starts with _displayOff=false.  The static DDC poll then
        // fires 0-5 s later and corrects it — but that window is the "LEDs coming back" the user
        // sees.
        //
        // Fix: PersistDisplayOffState() snapshots _suspended||_displayOff on all running instances
        // before they are disposed.  The new constructor inherits that state and skips the
        // immediate DDC check.  The DDC poll (still every 5 s) remains the sole authority for
        // clearing the off state once the display is genuinely back on.
        //
        // ClearDisplayOffState() is called on system resume so that waking up is still instant.
        private static readonly ConcurrentDictionary<string, bool> s_displayOff =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Call this BEFORE disposing old instances in RestartAmbilightFeature().
        /// Saves the per-monitor suspended/off state so new instances can inherit it.
        /// </summary>
        internal static void PersistDisplayOffState()
        {
            foreach (AmbilightScreenCapture capture in s_instances.Values)
            {
                bool persistOff = capture._displayOff || (capture._suspended && capture._ddcEverWorked);
                s_displayOff[capture.Display.DeviceName] = persistOff;
                if (OperatingSystem.IsWindows())
                    AmbilightWindowsDiagnostics.Write(Logger,
                        $"persist display-off state for {capture.Display.DeviceName}: {persistOff} (suspended={capture._suspended} displayOff={capture._displayOff} ddcEverWorked={capture._ddcEverWorked})");
            }
        }

        /// <summary>
        /// Clears all persisted off-state entries.  Call this when you know all displays are on
        /// (e.g. explicit user-initiated "enable all" action).  Not called on system resume —
        /// the DDC poll will clear individual entries as each display confirms On.
        /// </summary>
        internal static void ClearDisplayOffState() => s_displayOff.Clear();

        // Single shared timer — polls every 5 s, outside of every capture's own loop.
        private static readonly Timer s_powerPollTimer = new Timer(OnPowerPollTick, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        private static void OnPowerPollTick(object? state)
        {
            if (!OperatingSystem.IsWindows()) return;
            foreach (AmbilightScreenCapture capture in s_instances.Values)
            {
                // Only check displays that are actually selected (have an active capture zone).
                if (capture._zoneCount > 0)
                    capture.CheckDisplayPowerState();
            }
        }

        #endregion

        #region Instance fields

        private readonly IScreenCapture _screenCapture;
        private readonly object _captureLock = new();

        private int _zoneCount;
        private volatile bool _captureError;
        private volatile bool _suspended;
        private volatile bool _displayOff;
        private volatile int _fpsLimit;

        private bool _ddcEverWorked;
        private DateTimeOffset _captureBackoffUntil = DateTimeOffset.MinValue;
        private bool _restartAfterBackoff;
        private int _captureBackoffSeconds = 2;
        private bool _captureLoopEntered;
        private DateTimeOffset _nextBlackSkipLog = DateTimeOffset.MinValue;
        private DateTimeOffset _nextCaptureFalseLog = DateTimeOffset.MinValue;

        private Task? _updateTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationToken _cancellationToken = CancellationToken.None;

        public Display Display => _screenCapture.Display;
        public bool HasCaptureError => _captureError;
        public bool ShouldOutputBlack => _suspended || _displayOff;
        public bool IsSuspended => _suspended;
        public string CaptureBackendDetails => _screenCapture is ICaptureBackendStatus status
            ? status.CaptureBackendDetails
            : _screenCapture.GetType().Name;

        #endregion

        #region Events

        public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

        #endregion

        #region Constructor / Dispose

        public AmbilightScreenCapture(IScreenCapture screenCapture)
        {
            _screenCapture = screenCapture;

#if WINDOWS
            if (screenCapture is DX11ScreenCapture dx11)
                dx11.Timeout = 100;
#endif

            s_instances[Display.DeviceName] = this;

            if (OperatingSystem.IsWindows())
            {
                if (s_ddcEverWorked.TryGetValue(Display.DeviceName, out bool ever) && ever)
                    _ddcEverWorked = true;

                if (s_displayOff.TryGetValue(Display.DeviceName, out bool prevOff) && prevOff)
                {
                    // Previous instance was suspended or display-off — inherit that state.
                    // Skip the immediate DDC check: on a headless DP display the DDC chip can
                    // still answer "On" right after power-off, which would incorrectly clear
                    // _displayOff.  The 5-second DDC poll will call CheckDisplayPowerState()
                    // once a zone is registered, and is the sole authority for clearing this.
                    _displayOff = true;
                }
                else
                {
                    CheckDisplayPowerState();
                }

                AmbilightWindowsDiagnostics.Write(Logger,
                    $"created capture wrapper for {Display.DeviceName}; backend={CaptureBackendDetails}; initial displayOff={_displayOff}; ddcEverWorked={_ddcEverWorked}");
            }
        }

        public void Dispose()
        {
            s_instances.TryRemove(Display.DeviceName, out _);
            _cancellationTokenSource?.Cancel();

            Task? updateTask = _updateTask;
            _updateTask = null;
            if (updateTask != null && Task.CurrentId != updateTask.Id)
            {
                try { updateTask.Wait(TimeSpan.FromSeconds(2)); }
                catch { /* Best effort: the capture lock below prevents disposal races. */ }
            }

            lock (_captureLock)
                _screenCapture.Dispose();

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        #endregion

        #region Public control

        public void Suspend()
        {
            _suspended = true;
            Logger.Debug("Capture suspended for {Display}", Display.DeviceName);
        }

        public void Resume(bool clearDisplayOffState = false)
        {
            _suspended = false;
            _captureError = false;
            _restartAfterBackoff = false;
            _captureBackoffUntil = DateTimeOffset.MinValue;
            _captureBackoffSeconds = 2;
            if (clearDisplayOffState)
                _displayOff = false;
            Logger.Debug("Capture resumed for {Display}", Display.DeviceName);
        }

        public void SetFpsLimit(int fps)
        {
            fps = Math.Max(0, fps);
            if (_fpsLimit == fps)
                return;

            _fpsLimit = fps;
            lock (_captureLock)
            {
                if (_screenCapture is IConfigurableCaptureFps configurableCapture)
                    configurableCapture.SetFpsLimit(_fpsLimit);
#if WINDOWS
                if (_screenCapture is Wgc.WgcScreenCapture wgc)
                    wgc.SetFpsLimit(_fpsLimit);
#endif
            }
        }

        public void SetForceGStreamerPipeWire(bool forceGStreamer)
        {
            if (!OperatingSystem.IsLinux())
                return;

            lock (_captureLock)
            {
                if (_screenCapture is PortalPipeWire.PortalPipeWireScreenCapture portalCapture)
                    portalCapture.SetForceGStreamer(forceGStreamer);
            }
        }

        #endregion

        #region Power-state check

        /// <summary>
        /// Called by the static 5-second timer (once a zone is registered) and once from the
        /// constructor when no persisted off-state exists.
        ///
        ///   On             → display is on; clear _displayOff and _suspended; update s_displayOff.
        ///   Off            → DPMS standby/off; set _displayOff; update s_displayOff.
        ///   NotPresent     → physical disconnect; set _displayOff; update s_displayOff.
        ///   DdcUnavailable + DDC previously worked → deep sleep; set _displayOff; update s_displayOff.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void CheckDisplayPowerState()
        {
            switch (MonitorPowerState.QueryPowerState(Display.DeviceName))
            {
                case MonitorPowerState.PowerState.On:
                    _ddcEverWorked = true;
                    s_ddcEverWorked[Display.DeviceName] = true;
                    bool wasOff = _displayOff;
                    bool wasSuspended = _suspended;
                    if (_displayOff) { _displayOff = false; s_displayOff[Display.DeviceName] = false; }
                    if (_suspended) _suspended = false;
                    if (wasOff || wasSuspended)
                    {
                        Logger.Debug("Display available: {Display} (wasOff={Off} wasSuspended={Susp})", Display.DeviceName, wasOff, wasSuspended);
                        AmbilightWindowsDiagnostics.Write(Logger,
                            $"display available: {Display.DeviceName} (wasOff={wasOff} wasSuspended={wasSuspended})");
                    }
                    break;

                case MonitorPowerState.PowerState.Off:
                    _ddcEverWorked = true;
                    s_ddcEverWorked[Display.DeviceName] = true;
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("Display DPMS off: {Display}", Display.DeviceName);
                        AmbilightWindowsDiagnostics.Write(Logger, $"display DPMS off: {Display.DeviceName}");
                    }
                    break;

                case MonitorPowerState.PowerState.NotPresent:
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("Display not present: {Display}", Display.DeviceName);
                        AmbilightWindowsDiagnostics.Write(Logger, $"display not present: {Display.DeviceName}");
                    }
                    break;

                case MonitorPowerState.PowerState.DdcUnavailable:
                    if (_ddcEverWorked && !_displayOff)
                    {
                        // DDC was working before, now silent → deep sleep / physical off.
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("DDC silent (prev. worked): {Display} treated as off", Display.DeviceName);
                        AmbilightWindowsDiagnostics.Write(Logger, $"DDC silent after previously working: {Display.DeviceName} treated as off");
                    }
                    else if (_displayOff && !_ddcEverWorked)
                    {
                        _displayOff = false;
                        s_displayOff[Display.DeviceName] = false;
                        AmbilightWindowsDiagnostics.Write(Logger,
                            $"DDC unavailable and no previous DDC success for {Display.DeviceName}; clearing inherited display-off state");
                    }
                    break;
            }
        }

        #endregion

        #region Capture loop

        private void UpdateLoopSafe()
        {
            try
            {
                UpdateLoop();
            }
            catch (OperationCanceledException)
            {
                if (OperatingSystem.IsWindows())
                    AmbilightWindowsDiagnostics.Write(Logger, $"capture update loop stopped for {Display.DeviceName}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Capture update loop crashed for {Display}", Display.DeviceName);
                if (OperatingSystem.IsWindows())
                    AmbilightWindowsDiagnostics.Write(Logger,
                        $"capture update loop crashed for {Display.DeviceName}: {ex.GetBaseException().Message}");
            }
        }

        private void UpdateLoop()
        {
            int consecutiveErrors = 0;
            var stopwatch = Stopwatch.StartNew();
            bool prevShouldOutputBlack = ShouldOutputBlack;
            if (OperatingSystem.IsWindows())
                AmbilightWindowsDiagnostics.Write(Logger,
                    $"capture update loop started for {Display.DeviceName}; backend={CaptureBackendDetails}; suspended={_suspended}; displayOff={_displayOff}; fps={_fpsLimit}");

            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                bool currentBlack = ShouldOutputBlack;
                if (currentBlack != prevShouldOutputBlack)
                {
                    prevShouldOutputBlack = currentBlack;
                    Logger.Information("{Display} ShouldOutputBlack → {Value} (suspended={S} displayOff={D})",
                        Display.DeviceName, currentBlack, _suspended, _displayOff);
                }

                if (_suspended)
                {
                    LogBlackSkipIfNeeded("suspended");
                    Thread.Sleep(50);
                    continue;
                }

                if (_displayOff)
                {
                    LogBlackSkipIfNeeded("displayOff");
                    Thread.Sleep(200);
                    continue;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (_captureBackoffUntil > now)
                {
                    Thread.Sleep(Math.Min(500, Math.Max(50, (int)(_captureBackoffUntil - now).TotalMilliseconds)));
                    continue;
                }

                if (_restartAfterBackoff)
                {
                    try
                    {
                        Logger.Information("Restarting screen capture for {Display} after GPU/capture backoff", Display.DeviceName);
                        lock (_captureLock)
                            _screenCapture.Restart();
                    }
                    catch (Exception restartEx)
                    {
                        Logger.Debug(restartEx, "Restart after backoff failed for {Display}, will keep retrying", Display.DeviceName);
                        _captureBackoffUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(_captureBackoffSeconds, 60));
                        _captureBackoffSeconds = Math.Min(_captureBackoffSeconds * 2, 60);
                        continue;
                    }
                    finally
                    {
                        _restartAfterBackoff = false;
                    }
                }

                // FPS limiting
                int fpsLimit = _fpsLimit;
                if (fpsLimit > 0)
                {
                    long targetTicks = Stopwatch.Frequency / fpsLimit;
                    long elapsed = stopwatch.ElapsedTicks;
                    if (elapsed < targetTicks)
                    {
                        int sleepMs = (int)((targetTicks - elapsed) * 1000 / Stopwatch.Frequency);
                        if (sleepMs > 0) Thread.Sleep(sleepMs);
                    }
                    stopwatch.Restart();
                }

                try
                {
                    if (!_captureLoopEntered && OperatingSystem.IsWindows())
                    {
                        _captureLoopEntered = true;
                        AmbilightWindowsDiagnostics.Write(Logger,
                            $"first capture attempt for {Display.DeviceName}; backend={CaptureBackendDetails}; zones={_zoneCount}; fps={_fpsLimit}");
                    }

                    bool success;
                    lock (_captureLock)
                        success = _screenCapture.CaptureScreen();
                    if (!success)
                        LogCaptureFalseIfNeeded();
                    Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(success));
                    consecutiveErrors = 0;
                    _captureError = false;
                    _captureBackoffSeconds = 2;
                    _captureBackoffUntil = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _captureError = true;
                    consecutiveErrors++;
                    bool likelyGpuFault = IsLikelyGpuCaptureFault(ex);

                    if (consecutiveErrors == 1)
                        Logger.Warning(ex, "Screen capture failed for {Display}, will retry", Display.DeviceName);

                    if (OperatingSystem.IsWindows())
                        AmbilightWindowsDiagnostics.Write(Logger,
                            $"capture exception for {Display.DeviceName}; backend={CaptureBackendDetails}; consecutiveErrors={consecutiveErrors}; message={ex.GetBaseException().Message}");

                    if (likelyGpuFault)
                    {
                        int backoffSeconds = Math.Min(_captureBackoffSeconds, 60);
                        _captureBackoffSeconds = Math.Min(_captureBackoffSeconds * 2, 60);
                        _captureBackoffUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(backoffSeconds);
                        _restartAfterBackoff = true;
                        Logger.Warning(ex,
                            "GPU/display capture fault detected for {Display}; backing off capture for {Seconds}s to avoid driver reset loops",
                            Display.DeviceName,
                            backoffSeconds);
                        continue;
                    }

                    Thread.Sleep(Math.Min(consecutiveErrors * 200, 2000));

                    if (consecutiveErrors % 10 == 0 && !IsFullscreenExclusiveAppRunning())
                    {
                        try
                        {
                            Logger.Information("Attempting to restart screen capture for {Display}", Display.DeviceName);
                            lock (_captureLock)
                                _screenCapture.Restart();
                        }
                        catch (Exception restartEx)
                        {
                            Logger.Debug(restartEx, "Restart failed for {Display}, will keep retrying", Display.DeviceName);
                        }
                    }
                }
            }
        }

        private static bool IsLikelyGpuCaptureFault(Exception exception)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                string text = $"{current.GetType().FullName} {current.Message}";
                if (text.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("DXGI_ERROR_DEVICE_RESET", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("DXGI_ERROR_DRIVER_INTERNAL_ERROR", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("device removed", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("device lost", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("DeviceRemovedReason", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("0x887A0006", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("0x887A0020", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        ICaptureZone IScreenCapture.RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel)
        {
            lock (_captureLock)
            {
                ICaptureZone zone = _screenCapture.RegisterCaptureZone(x, y, width, height, downscaleLevel);
                _zoneCount++;

                if (_updateTask == null)
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _cancellationToken = _cancellationTokenSource.Token;
                    _updateTask = Task.Factory.StartNew(UpdateLoopSafe, _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (OperatingSystem.IsWindows())
                        AmbilightWindowsDiagnostics.Write(Logger,
                            $"started capture update task for {Display.DeviceName}; backend={CaptureBackendDetails}; zones={_zoneCount}; suspended={_suspended}; displayOff={_displayOff}");
                }

                return zone;
            }
        }

        public bool UnregisterCaptureZone(ICaptureZone captureZone)
        {
            lock (_captureLock)
            {
                bool result = _screenCapture.UnregisterCaptureZone(captureZone);
                if (result) _zoneCount--;

                if (_zoneCount == 0 && _updateTask != null)
                {
                    _cancellationTokenSource?.Cancel();
                    _updateTask = null;
                }

                return result;
            }
        }

        public void UpdateCaptureZone(ICaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
        {
            lock (_captureLock)
                _screenCapture.UpdateCaptureZone(captureZone, x, y, width, height, downscaleLevel);
        }

        public bool CaptureScreen() => false;

        private void LogBlackSkipIfNeeded(string reason)
        {
            if (!OperatingSystem.IsWindows())
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < _nextBlackSkipLog)
                return;

            AmbilightWindowsDiagnostics.Write(Logger,
                $"capture loop skip for {Display.DeviceName}: {reason} (suspended={_suspended} displayOff={_displayOff} ddcEverWorked={_ddcEverWorked})");
            _nextBlackSkipLog = now + TimeSpan.FromSeconds(5);
        }

        private void LogCaptureFalseIfNeeded()
        {
            if (!OperatingSystem.IsWindows())
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < _nextCaptureFalseLog)
                return;

            AmbilightWindowsDiagnostics.Write(Logger,
                $"capture returned no frame for {Display.DeviceName}; backend={CaptureBackendDetails}; suspended={_suspended}; displayOff={_displayOff}; zones={_zoneCount}");
            _nextCaptureFalseLog = now + TimeSpan.FromSeconds(5);
        }

        public void Restart()
        {
            lock (_captureLock)
                _screenCapture.Restart();
        }

        #endregion

    }
}
