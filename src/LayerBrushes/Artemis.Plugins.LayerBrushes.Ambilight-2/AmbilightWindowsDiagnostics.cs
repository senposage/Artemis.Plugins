using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight;

internal static class AmbilightWindowsDiagnostics
{
    private const long MaxLogBytes = 16L * 1024 * 1024;
    private const long RuntimeReportIntervalMs = 60_000;
    private static readonly Lock WriteLock = new();
    private static long _nextRuntimeReportTick;
    private static long _liveWgcSessionCount;
    private static long _wgcSessionCreateCount;
    private static long _wgcSessionDisposeCount;
    private static long _trackedWgcBytes;
    private static long _peakTrackedWgcBytes;

    public static string LogPath
    {
        get
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                localData = Path.GetTempPath();
            return Path.Combine(localData, "Artemis", "Ambilight", "ambilight-windows-capture.log");
        }
    }

    public static void Write(ILogger logger, string message)
    {
        logger.Debug("[Ambilight/Windows] {Message}", message);
    }

    /// <summary>
    /// The release build persists only the low-rate resource ledger. Capture-event
    /// detail remains available through the normal Serilog debug sink when enabled.
    /// </summary>
    public static void Write(string message)
    {
        // Intentionally no-op in release builds. This method remains to avoid making
        // capture diagnostics a source of disk, cache, or I/O pressure.
    }

    private static void AppendRuntimeLedger(string message)
    {
        lock (WriteLock)
        {
            try
            {
                string path = LogPath;
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                {
                    File.WriteAllText(path,
                        $"=== Ambilight diagnostics rotated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ==={Environment.NewLine}");
                }

                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never break plugin startup.
            }
        }
    }

    public static void WgcSessionCreated(long bytes)
    {
        Interlocked.Increment(ref _liveWgcSessionCount);
        Interlocked.Increment(ref _wgcSessionCreateCount);
        AdjustTrackedWgcBytes(bytes);
    }

    public static void WgcSessionDisposed(long bytes)
    {
        Interlocked.Decrement(ref _liveWgcSessionCount);
        Interlocked.Increment(ref _wgcSessionDisposeCount);
        AdjustTrackedWgcBytes(-bytes);
    }

    public static void AdjustTrackedWgcBytes(long delta)
    {
        long live = Interlocked.Add(ref _trackedWgcBytes, delta);
        long observed;
        while ((observed = Interlocked.Read(ref _peakTrackedWgcBytes)) < live)
        {
            if (Interlocked.CompareExchange(ref _peakTrackedWgcBytes, live, observed) == observed)
                break;
        }
    }

    public static void ReportWgcRuntime()
    {
        long now = Environment.TickCount64;
        long next = Interlocked.Read(ref _nextRuntimeReportTick);
        if (now < next || Interlocked.CompareExchange(ref _nextRuntimeReportTick, now + RuntimeReportIntervalMs, next) != next)
            return;

        using Process process = Process.GetCurrentProcess();
        AppendRuntimeLedger(
            $"WGC RuntimeLedger: managed={GC.GetTotalMemory(forceFullCollection: false):N0} private={process.PrivateMemorySize64:N0} workingSet={process.WorkingSet64:N0} handles={process.HandleCount} " +
            $"sessions={Interlocked.Read(ref _liveWgcSessionCount)} create/dispose={Interlocked.Read(ref _wgcSessionCreateCount)}/{Interlocked.Read(ref _wgcSessionDisposeCount)} " +
            $"trackedWgcBytes={Interlocked.Read(ref _trackedWgcBytes):N0} peakTrackedWgcBytes={Interlocked.Read(ref _peakTrackedWgcBytes):N0}");
    }
}
