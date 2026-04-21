using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class PortalPipeWireFrameReader : IPortalPipeWireFrameReader
{
    private static readonly ILogger Logger = Log.ForContext<PortalPipeWireFrameReader>();

    internal const int MaxFpsLimit = 144;

    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    private readonly PortalPipeWireOutput _output;
    private readonly int _pipeWireRemoteFd;
    private readonly object _frameLock = new();
    private readonly object _configurationLock = new();
    private readonly object _processLock = new();
    private string? _pipeline;
    private Process? _process;
    private Task? _readerTask;
    private CancellationTokenSource? _cancellationTokenSource;
    private byte[] _latestFrame = [];
    private volatile bool _hasFrame;
    private int _frameWidth;
    private int _frameHeight;
    private int _frameSize;
    private int _sourceDownscaleLevel;
    private int _fpsLimit = 30;
    private int _pipelineAttempt;
    private CancellationTokenSource? _reconfigDelay;
    private int _missingFrameLogCount;
    private int _receivedFrameLogCount;
    private int _processExitedLogCount;

    public PortalPipeWireFrameReader(PortalPipeWireOutput output, int pipeWireRemoteFd)
    {
        _output = output;
        _pipeWireRemoteFd = pipeWireRemoteFd;
        Configure(0, _fpsLimit);
    }

    public void Configure(int sourceDownscaleLevel, int fpsLimit)
    {
        sourceDownscaleLevel = Math.Clamp(sourceDownscaleLevel, 0, 8);
        fpsLimit = fpsLimit <= 0 ? 30 : Math.Clamp(fpsLimit, 1, MaxFpsLimit);

        lock (_configurationLock)
        {
            int frameWidth = Math.Max(1, _output.Display.Width >> sourceDownscaleLevel);
            int frameHeight = Math.Max(1, _output.Display.Height >> sourceDownscaleLevel);
            if (frameWidth == _frameWidth &&
                frameHeight == _frameHeight &&
                sourceDownscaleLevel == _sourceDownscaleLevel &&
                fpsLimit == _fpsLimit)
                return;

            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _frameSize = frameWidth * frameHeight * 4;
            _sourceDownscaleLevel = sourceDownscaleLevel;
            _fpsLimit = fpsLimit;
            _pipelineAttempt = 0;

            lock (_frameLock)
                _latestFrame = new byte[_frameSize];
            _hasFrame = false;

            // Debounce restarts while sliders/zones are changing. This keeps FPS
            // changes from spawning overlapping GStreamer processes.
            _reconfigDelay?.Cancel();
            _reconfigDelay?.Dispose();
            var delay = new CancellationTokenSource();
            _reconfigDelay = delay;
            _ = Task.Delay(TimeSpan.FromMilliseconds(200), delay.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    RestartProcessIfRunning();
            }, TaskScheduler.Default);
        }
    }

    public bool TryCopyLatestFrame(ref byte[] destination, out int sourceWidth, out int sourceHeight, out int sourceDownscaleLevel)
    {
        EnsureStarted();
        if (_process is { HasExited: true } && _processExitedLogCount < 5)
        {
            _processExitedLogCount++;
            Logger.Warning("GStreamer PipeWire reader exited for {Display} with code {ExitCode}", _output.StableId, _process.ExitCode);
        }

        if (!_hasFrame)
        {
            if (_missingFrameLogCount < 5)
            {
                _missingFrameLogCount++;
                Logger.Information("No PipeWire frame available yet for {Display} (node={NodeId})", _output.StableId, _output.NodeId);
            }
            sourceWidth = 0;
            sourceHeight = 0;
            sourceDownscaleLevel = 0;
            return false;
        }

        lock (_frameLock)
        {
            if (destination.Length != _latestFrame.Length)
                destination = new byte[_latestFrame.Length];

            _latestFrame.AsSpan().CopyTo(destination);
            sourceWidth = _frameWidth;
            sourceHeight = _frameHeight;
            sourceDownscaleLevel = _sourceDownscaleLevel;
        }
        return true;
    }

    public void Restart()
    {
        lock (_processLock)
        {
            StopNoLock();
            EnsureStartedNoLock();
        }
    }

    public void Dispose()
    {
        try { _reconfigDelay?.Cancel(); }
        catch { }
        _reconfigDelay?.Dispose();
        _reconfigDelay = null;
        Stop();
    }

    private void EnsureStarted()
    {
        lock (_processLock)
            EnsureStartedNoLock();
    }

    private void EnsureStartedNoLock()
    {
        if (_process is { HasExited: false })
            return;

        if (_process is { HasExited: true } && !_hasFrame)
            _pipelineAttempt++;

        StopNoLock();
        _cancellationTokenSource = new CancellationTokenSource();
        _process = StartGStreamerProcess();
        _processExitedLogCount = 0;
        AmbilightLinuxDiagnostics.Write(Logger, $"started GStreamer PipeWire reader pid={_process.Id} for {_output.StableId}");
        Logger.Information("Started GStreamer PipeWire reader pid={Pid} for {Display}", _process.Id, _output.StableId);
        _readerTask = Task.Run(() => ReadFrames(_process, _cancellationTokenSource.Token));
        _ = Task.Run(() => WarnIfNoFramesArrive(_process, _cancellationTokenSource.Token));
    }

    private void RestartProcessIfRunning()
    {
        lock (_processLock)
        {
            if (_process == null)
                return;

            StopNoLock();
        }
    }

    private Process StartGStreamerProcess()
    {
        int frameWidth;
        int frameHeight;
        int frameSize;
        int sourceDownscaleLevel;
        int fpsLimit;
        lock (_configurationLock)
        {
            frameWidth = _frameWidth;
            frameHeight = _frameHeight;
            frameSize = _frameSize;
            sourceDownscaleLevel = _sourceDownscaleLevel;
            fpsLimit = _fpsLimit;
        }

        string pipeline = BuildPipeline(frameWidth, frameHeight, fpsLimit);
        _pipeline = pipeline;

        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(pipeline);

        Logger.Information("Starting GStreamer PipeWire pipeline for {Display}: node={NodeId} size={Width}x{Height} downscale={Downscale} fps={Fps} fd={Fd}",
            _output.StableId, _output.NodeId, frameWidth, frameHeight, sourceDownscaleLevel, fpsLimit, _pipeWireRemoteFd);
        AmbilightLinuxDiagnostics.Write(Logger,
            $"starting GStreamer pipeline display={_output.StableId} node={_output.NodeId} size={frameWidth}x{frameHeight} downscale={sourceDownscaleLevel} fps={fpsLimit} frameBytes={frameSize} fd={_pipeWireRemoteFd}");

        int originalFdFlags = PreparePipeWireFdForChildProcess();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start GStreamer PipeWire capture.");
        }
        finally
        {
            RestorePipeWireFdFlags(originalFdFlags);
        }

        _ = Task.Run(() => ReadGStreamerStdErr(process));
        return process;
    }

    private string BuildPipeline(int frameWidth, int frameHeight, int fpsLimit)
    {
        string source = $"pipewiresrc fd={_pipeWireRemoteFd} path={_output.NodeId} do-timestamp=true";
        bool needsScale = frameWidth != _output.Display.Width || frameHeight != _output.Display.Height;

        // videorate with max-rate limits fps by dropping frames without strict caps negotiation
        string rateLimit = $"videorate drop-only=true max-rate={fpsLimit} !";

        if (needsScale)
        {
            // pipewiresrc always outputs native resolution — videoscale handles downscaling.
            // Use progressive fallback: direct BGRx/BGRA first (no convert), then with videoconvert.
            return _pipelineAttempt switch
            {
                0 =>
                    "exec gst-launch-1.0 -q " +
                    $"{source} ! {rateLimit} " +
                    "videoscale ! " +
                    $"video/x-raw,format=BGRx,width={frameWidth},height={frameHeight} ! " +
                    "fdsink fd=1 sync=false",
                _ =>
                    "exec gst-launch-1.0 -q " +
                    $"{source} ! {rateLimit} " +
                    "videoscale ! videoconvert ! " +
                    $"video/x-raw,format=BGRA,width={frameWidth},height={frameHeight} ! " +
                    "fdsink fd=1 sync=false"
            };
        }

        // Native resolution — try progressively more compatible pipelines
        return _pipelineAttempt switch
        {
            0 =>
                // Best case: pipewiresrc already gives BGRx/BGRA, no conversion needed
                "exec gst-launch-1.0 -q " +
                $"{source} ! {rateLimit} " +
                $"'video/x-raw,format=(string){{BGRx,BGRA}}' ! " +
                "fdsink fd=1 sync=false",
            1 =>
                // Add videoconvert for compositors that give a different format
                "exec gst-launch-1.0 -q " +
                $"{source} ! {rateLimit} " +
                "videoconvert ! video/x-raw,format=BGRA ! " +
                "fdsink fd=1 sync=false",
            _ =>
                // Full fallback: no format constraints at all
                "exec gst-launch-1.0 -q " +
                $"{source} ! {rateLimit} " +
                "videoconvert ! " +
                "fdsink fd=1 sync=false"
        };
    }

    private int PreparePipeWireFdForChildProcess()
    {
        int flags = fcntl(_pipeWireRemoteFd, F_GETFD);
        if (flags < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            AmbilightLinuxDiagnostics.Write(Logger, $"fcntl(F_GETFD) failed for PipeWire fd={_pipeWireRemoteFd} errno={errno}");
            Logger.Warning("fcntl(F_GETFD) failed for PipeWire fd={Fd} errno={Errno}", _pipeWireRemoteFd, errno);
            return -1;
        }

        if ((flags & FD_CLOEXEC) == 0)
        {
            AmbilightLinuxDiagnostics.Write(Logger, $"PipeWire fd={_pipeWireRemoteFd} already inheritable by child process");
            return flags;
        }

        int updatedFlags = flags & ~FD_CLOEXEC;
        if (fcntl(_pipeWireRemoteFd, F_SETFD, updatedFlags) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            AmbilightLinuxDiagnostics.Write(Logger, $"fcntl(F_SETFD clear FD_CLOEXEC) failed for PipeWire fd={_pipeWireRemoteFd} errno={errno}");
            Logger.Warning("fcntl(F_SETFD clear FD_CLOEXEC) failed for PipeWire fd={Fd} errno={Errno}", _pipeWireRemoteFd, errno);
            return flags;
        }

        AmbilightLinuxDiagnostics.Write(Logger, $"cleared FD_CLOEXEC on PipeWire fd={_pipeWireRemoteFd} for GStreamer child process");
        return flags;
    }

    private void RestorePipeWireFdFlags(int originalFlags)
    {
        if (originalFlags < 0)
            return;

        if (fcntl(_pipeWireRemoteFd, F_SETFD, originalFlags) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            AmbilightLinuxDiagnostics.Write(Logger, $"fcntl(F_SETFD restore) failed for PipeWire fd={_pipeWireRemoteFd} errno={errno}");
            Logger.Warning("fcntl(F_SETFD restore) failed for PipeWire fd={Fd} errno={Errno}", _pipeWireRemoteFd, errno);
        }
    }

    private async Task ReadGStreamerStdErr(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    AmbilightLinuxDiagnostics.Write(Logger, $"GStreamer stderr for {_output.StableId}: {line}");
                    Logger.Warning("GStreamer PipeWire capture for {Display}: {Line}", _output.StableId, line);
                }
            }

            string remaining = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                AmbilightLinuxDiagnostics.Write(Logger, $"GStreamer remaining stderr for {_output.StableId}: {remaining}");
                Logger.Warning("GStreamer PipeWire capture for {Display} wrote stderr: {Stderr}", _output.StableId, remaining);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed while reading GStreamer stderr for {Display}", _output.StableId);
        }
    }

    private async Task WarnIfNoFramesArrive(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (_hasFrame || cancellationToken.IsCancellationRequested)
                return;

            Logger.Warning(
                "No PipeWire frames received after 5 seconds for {Display}. processExited={Exited} exitCode={ExitCode} pipeline={Pipeline}",
                _output.StableId,
                process.HasExited,
                process.HasExited ? process.ExitCode.ToString() : "(running)",
                _pipeline ?? "(unknown)");
            AmbilightLinuxDiagnostics.Write(Logger,
                $"no PipeWire frames after 5 seconds for {_output.StableId}; processExited={process.HasExited} exitCode={(process.HasExited ? process.ExitCode.ToString() : "(running)")} pipeline={_pipeline ?? "(unknown)"}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReadFrames(Process process, CancellationToken cancellationToken)
    {
        int frameSize;
        lock (_configurationLock)
            frameSize = _frameSize;

        byte[] frame = new byte[frameSize];
        Stream output = process.StandardOutput.BaseStream;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                int read = 0;
                while (read < frame.Length)
                {
                    int chunk = await output.ReadAsync(frame.AsMemory(read, frame.Length - read), cancellationToken).ConfigureAwait(false);
                    if (chunk == 0)
                    {
                        AmbilightLinuxDiagnostics.Write(Logger,
                            $"GStreamer stdout ended for {_output.StableId} after {read}/{frame.Length} bytes of a frame");
                        Logger.Warning("GStreamer PipeWire stdout ended for {Display} after {BytesRead}/{FrameSize} bytes of a frame", _output.StableId, read, frame.Length);
                        return;
                    }
                    read += chunk;
                }

                lock (_frameLock)
                {
                    if (_latestFrame.Length == frame.Length)
                        frame.AsSpan().CopyTo(_latestFrame);
                    else
                        continue;
                }
                _hasFrame = true;
                if (_receivedFrameLogCount < 3)
                {
                    _receivedFrameLogCount++;
                    AmbilightLinuxDiagnostics.Write(Logger,
                        $"received PipeWire frame {_receivedFrameLogCount} for {_output.StableId} ({frame.Length} bytes)");
                    Logger.Information("Received PipeWire frame {Count} for {Display} ({Bytes} bytes)", _receivedFrameLogCount, _output.StableId, frame.Length);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "GStreamer PipeWire frame reader failed for {Display}", _output.StableId);
        }
    }

    private void Stop()
    {
        lock (_processLock)
            StopNoLock();
    }

    private void StopNoLock()
    {
        try { _cancellationTokenSource?.Cancel(); }
        catch { }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch { }

        _process?.Dispose();
        _process = null;
        _readerTask = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _hasFrame = false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
