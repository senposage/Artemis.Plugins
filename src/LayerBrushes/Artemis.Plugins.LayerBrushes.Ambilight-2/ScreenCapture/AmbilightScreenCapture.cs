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
                s_displayOff[capture.Display.DeviceName] = capture._suspended || capture._displayOff;
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

        private int _zoneCount;
        private volatile bool _captureError;
        private volatile bool _suspended;
        private volatile bool _displayOff;
        private volatile int _fpsLimit;

        private bool _ddcEverWorked;

        private Task? _updateTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationToken _cancellationToken = CancellationToken.None;

        public Display Display => _screenCapture.Display;
        public bool HasCaptureError => _captureError;
        public bool ShouldOutputBlack => _suspended || _displayOff;
        public bool IsSuspended => _suspended;

        #endregion

        #region Events

        public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

        #endregion

        #region Constructor / Dispose

        public AmbilightScreenCapture(IScreenCapture screenCapture)
        {
            _screenCapture = screenCapture;

            if (screenCapture is DX11ScreenCapture dx11)
                dx11.Timeout = 100;

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
            }
        }

        public void Dispose()
        {
            s_instances.TryRemove(Display.DeviceName, out _);
            _cancellationTokenSource?.Cancel();
            _updateTask = null;
            _screenCapture.Dispose();
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
            if (clearDisplayOffState)
                _displayOff = false;
            Logger.Debug("Capture resumed for {Display}", Display.DeviceName);
        }

        public void SetFpsLimit(int fps)
        {
            _fpsLimit = Math.Max(0, fps);
            if (_screenCapture is Wgc.WgcScreenCapture wgc)
                wgc.SetFpsLimit(_fpsLimit);
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
                        Logger.Debug("Display available: {Display} (wasOff={Off} wasSuspended={Susp})", Display.DeviceName, wasOff, wasSuspended);
                    break;

                case MonitorPowerState.PowerState.Off:
                    _ddcEverWorked = true;
                    s_ddcEverWorked[Display.DeviceName] = true;
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("Display DPMS off: {Display}", Display.DeviceName);
                    }
                    break;

                case MonitorPowerState.PowerState.NotPresent:
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("Display not present: {Display}", Display.DeviceName);
                    }
                    break;

                case MonitorPowerState.PowerState.DdcUnavailable:
                    if (_ddcEverWorked && !_displayOff)
                    {
                        // DDC was working before, now silent → deep sleep / physical off.
                        _displayOff = true;
                        s_displayOff[Display.DeviceName] = true;
                        Logger.Debug("DDC silent (prev. worked): {Display} treated as off", Display.DeviceName);
                    }
                    break;
            }
        }

        #endregion

        #region Capture loop

        private void UpdateLoop()
        {
            int consecutiveErrors = 0;
            var stopwatch = Stopwatch.StartNew();
            bool prevShouldOutputBlack = ShouldOutputBlack;

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
                    Thread.Sleep(50);
                    continue;
                }

                if (_displayOff)
                {
                    Thread.Sleep(200);
                    continue;
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
                    bool success = _screenCapture.CaptureScreen();
                    Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(success));
                    consecutiveErrors = 0;
                    _captureError = false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _captureError = true;
                    consecutiveErrors++;

                    if (consecutiveErrors == 1)
                        Logger.Warning(ex, "Screen capture failed for {Display}, will retry", Display.DeviceName);

                    Thread.Sleep(Math.Min(consecutiveErrors * 200, 2000));

                    if (consecutiveErrors % 10 == 0 && !IsFullscreenExclusiveAppRunning())
                    {
                        try
                        {
                            Logger.Information("Attempting to restart screen capture for {Display}", Display.DeviceName);
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

        ICaptureZone IScreenCapture.RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel)
        {
            lock (_screenCapture)
            {
                ICaptureZone zone = _screenCapture.RegisterCaptureZone(x, y, width, height, downscaleLevel);
                _zoneCount++;

                if (_updateTask == null)
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _cancellationToken = _cancellationTokenSource.Token;
                    _updateTask = Task.Factory.StartNew(UpdateLoop, _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }

                return zone;
            }
        }

        public bool UnregisterCaptureZone(ICaptureZone captureZone)
        {
            lock (_screenCapture)
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
            lock (_screenCapture)
                _screenCapture.UpdateCaptureZone(captureZone, x, y, width, height, downscaleLevel);
        }

        public bool CaptureScreen() => false;

        public void Restart() => _screenCapture.Restart();

        #endregion

    }
}
