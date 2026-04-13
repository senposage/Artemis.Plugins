using System;
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

        /// <summary>
        /// Returns true when a fullscreen exclusive D3D app (game) is active.
        /// Calling into DXGI while this is true can crash the NVIDIA driver.
        /// </summary>
        private static bool IsFullscreenExclusiveAppRunning()
        {
            if (!OperatingSystem.IsWindows()) return false;
            return SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
        }

        #endregion

        #region Properties & Fields

        private static readonly ILogger Logger = Log.ForContext<AmbilightScreenCapture>();

        private readonly IScreenCapture _screenCapture;

        private int _zoneCount = 0;
        private volatile bool _captureError;
        private volatile bool _suspended;
        private volatile bool _displayOff;
        private volatile int _fpsLimit;

        // DDC/CI per-monitor power state detection
        private bool _ddcEverWorked;
        private int _ddcConsecutiveFailures;
        private const int DDC_MAX_INITIAL_FAILURES = 3;

        private Task? _updateTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationToken _cancellationToken = CancellationToken.None;

        public Display Display => _screenCapture.Display;

        /// <summary>
        /// Indicates the capture has entered an error state (display lost, resolution change, etc.)
        /// </summary>
        public bool HasCaptureError => _captureError;

        /// <summary>
        /// Indicates capture output should be suppressed and rendered as black.
        /// </summary>
        public bool ShouldOutputBlack => _suspended || _captureError || _displayOff;

        public bool IsSuspended => _suspended;

        #endregion

        #region Events

        public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

        #endregion

        #region Constructors

        public AmbilightScreenCapture(IScreenCapture screenCapture)
        {
            this._screenCapture = screenCapture;

            if (screenCapture is DX11ScreenCapture dx11ScreenCapture)
                dx11ScreenCapture.Timeout = 100;

            // Pre-populate display power state immediately so that if this capture is being
            // constructed during a feature restart triggered by a monitor power-off, LEDs
            // don't briefly flash back on before the first DDC/CI poll runs in UpdateLoop.
            if (OperatingSystem.IsWindows())
                CheckDisplayPowerState();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Suspends capture immediately. Must be called BEFORE display changes occur
        /// to prevent native access violations in the graphics driver.
        /// </summary>
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

        /// <summary>
        /// Sets the maximum capture rate. 0 = unlimited.
        /// Also forwards to WGC's MinUpdateInterval so DWM can skip capture work.
        /// </summary>
        public void SetFpsLimit(int fps)
        {
            _fpsLimit = Math.Max(0, fps);

            // Forward to WGC so it can set MinUpdateInterval on the session.
            if (_screenCapture is Wgc.WgcScreenCapture wgc)
                wgc.SetFpsLimit(_fpsLimit);
        }


        private void UpdateLoop()
        {
            int consecutiveErrors = 0;
            var stopwatch = Stopwatch.StartNew();
            var powerCheckTimer = Stopwatch.StartNew();
            bool firstPowerCheck = true;

            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                // When suspended (display change incoming), sleep instead of calling into D3D11.
                if (_suspended)
                {
                    Thread.Sleep(50);
                    continue;
                }

                // Per-monitor DPMS check via DDC/CI (every ~2s, immediate on first iteration)
                if (OperatingSystem.IsWindows() && (firstPowerCheck || powerCheckTimer.ElapsedMilliseconds >= 2000))
                {
                    firstPowerCheck = false;
                    powerCheckTimer.Restart();
                    CheckDisplayPowerState();
                }

                // When display is DPMS-off, sleep until next power check
                if (_displayOff)
                {
                    Thread.Sleep(200);
                    continue;
                }

                // FPS limiting
                int fpsLimit = _fpsLimit;
                if (fpsLimit > 0)
                {
                    long targetTicksPerFrame = Stopwatch.Frequency / fpsLimit;
                    long elapsed = stopwatch.ElapsedTicks;
                    if (elapsed < targetTicksPerFrame)
                    {
                        int sleepMs = (int)((targetTicksPerFrame - elapsed) * 1000 / Stopwatch.Frequency);
                        if (sleepMs > 0)
                            Thread.Sleep(sleepMs);
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

                    // Back off to avoid hammering a lost display
                    Thread.Sleep(Math.Min(consecutiveErrors * 200, 2000));

                    // Only attempt Restart() when no fullscreen exclusive app is running.
                    // Re-creating DXGI resources while a game holds the output can crash
                    // the NVIDIA driver with a native access violation (c0000005).
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

        /// <summary>
        /// Queries the physical monitor's power state and updates <see cref="_displayOff"/>.
        /// Two detection layers:
        /// 1. HMONITOR presence — if the monitor vanishes from EnumDisplayMonitors, it's off/disconnected.
        /// 2. DDC/CI VCP 0xD6 — detects DPMS standby/suspend/off per physical monitor.
        /// Falls back to the system-wide GUID_CONSOLE_DISPLAY_STATE handler when both are unavailable.
        /// </summary>
        private void CheckDisplayPowerState()
        {
            // If DDC never worked and we've exhausted initial probes, only check HMONITOR presence.
            bool ddcExhausted = !_ddcEverWorked && _ddcConsecutiveFailures >= DDC_MAX_INITIAL_FAILURES;

            MonitorPowerState.PowerState state = MonitorPowerState.QueryPowerState(Display.DeviceName);

            switch (state)
            {
                case MonitorPowerState.PowerState.On:
                    _ddcEverWorked = true;
                    _ddcConsecutiveFailures = 0;
                    if (_displayOff)
                    {
                        _displayOff = false;
                        Logger.Debug("Display power restored via DDC/CI for {Display}", Display.DeviceName);
                    }
                    break;

                case MonitorPowerState.PowerState.Off:
                    _ddcEverWorked = true;
                    _ddcConsecutiveFailures = 0;
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        Logger.Debug("Display DPMS off detected via DDC/CI for {Display}", Display.DeviceName);
                    }
                    break;

                case MonitorPowerState.PowerState.NotPresent:
                    // Monitor disappeared from EnumDisplayMonitors — disconnected or fully powered off
                    if (!_displayOff)
                    {
                        _displayOff = true;
                        Logger.Debug("Monitor not present for {Display}, treating as display off", Display.DeviceName);
                    }
                    break;

                case MonitorPowerState.PowerState.DdcUnavailable:
                    _ddcConsecutiveFailures++;

                    // If DDC previously worked but now fails, the monitor likely entered
                    // deep power-off (some monitors stop responding to I2C entirely)
                    if (_ddcEverWorked && !_displayOff)
                    {
                        _displayOff = true;
                        Logger.Debug("DDC/CI unresponsive for {Display}, treating as display off", Display.DeviceName);
                    }

                    if (!_ddcEverWorked && _ddcConsecutiveFailures == DDC_MAX_INITIAL_FAILURES)
                        Logger.Debug("DDC/CI not available for {Display}, using system-wide detection only", Display.DeviceName);
                    break;
            }
        }

        ICaptureZone IScreenCapture.RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel)
        {
            lock (_screenCapture)
            {
                ICaptureZone captureZone = _screenCapture.RegisterCaptureZone(x, y, width, height, downscaleLevel);
                _zoneCount++;

                if (_updateTask == null)
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _cancellationToken = _cancellationTokenSource.Token;
                    _updateTask = Task.Factory.StartNew(UpdateLoop, _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }

                return captureZone;
            }
        }

        public bool UnregisterCaptureZone(ICaptureZone captureZone)
        {
            ICaptureZone inner = captureZone;
            lock (_screenCapture)
            {
                bool result = _screenCapture.UnregisterCaptureZone(inner);
                if (result)
                    _zoneCount--;

                if ((_zoneCount == 0) && (_updateTask != null))
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

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _updateTask = null;

            _screenCapture.Dispose();
        }

        #endregion
    }
}
