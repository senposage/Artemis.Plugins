using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>Lightweight file logger for diagnosing EGL/GLES issues at runtime.</summary>
internal static class ShaderLogger
{
    private static string? _logPath;
    private static readonly Lock _lock = new();

    /// <summary>Call once from ShaderToyLayerBrushProvider.Enable() with Plugin.Directory.FullName.</summary>
    public static void Init(string pluginDir)
    {
        _logPath = Path.Combine(pluginDir, "shader_debug.log");
        try
        {
            File.WriteAllText(_logPath,
                $"=== EGL Shaderbrush debug log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch { _logPath = null; }
    }

    public static void Log(string message)
    {
        if (_logPath == null) return;
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}

/// <summary>
/// Tracks native resources owned by this plugin. Driver allocations can be charged
/// to Windows rather than Artemis, so this ledger is intentionally independent of
/// managed-memory and working-set reporting.
/// </summary>
internal static class ShaderRuntimeDiagnostics
{
    private const long ReportIntervalMs = 60_000;

    private static long _nextReportTick;
    private static long _liveRendererCount;
    private static long _rendererCreateCount;
    private static long _rendererDisposeCount;
    private static long _liveGlTextureBytes;
    private static long _peakGlTextureBytes;
    private static long _liveWasapiGraphs;
    private static long _wasapiGraphCreateCount;
    private static long _wasapiGraphDisposeCount;

    public static void AddGlResources(long bytes)
    {
        long live = Interlocked.Add(ref _liveGlTextureBytes, bytes);
        UpdatePeak(ref _peakGlTextureBytes, live);
    }

    public static void RendererCreated()
    {
        Interlocked.Increment(ref _liveRendererCount);
        Interlocked.Increment(ref _rendererCreateCount);
    }

    public static void RendererDisposed(long bytes)
    {
        Interlocked.Decrement(ref _liveRendererCount);
        Interlocked.Increment(ref _rendererDisposeCount);
        if (bytes != 0)
            Interlocked.Add(ref _liveGlTextureBytes, -bytes);
    }

    public static void WasapiGraphOpened()
    {
        Interlocked.Increment(ref _liveWasapiGraphs);
        Interlocked.Increment(ref _wasapiGraphCreateCount);
    }

    public static void WasapiGraphClosed()
    {
        Interlocked.Decrement(ref _liveWasapiGraphs);
        Interlocked.Increment(ref _wasapiGraphDisposeCount);
    }

    public static void ReportIfDue()
    {
        long now = Environment.TickCount64;
        long next = Interlocked.Read(ref _nextReportTick);
        if (now < next || Interlocked.CompareExchange(ref _nextReportTick, now + ReportIntervalMs, next) != next)
            return;

        using Process process = Process.GetCurrentProcess();
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        ShaderLogger.Log(
            $"RuntimeLedger: managed={managed:N0} private={process.PrivateMemorySize64:N0} workingSet={process.WorkingSet64:N0} handles={process.HandleCount} " +
            $"glRenderers={Interlocked.Read(ref _liveRendererCount)} create/dispose={Interlocked.Read(ref _rendererCreateCount)}/{Interlocked.Read(ref _rendererDisposeCount)} " +
            $"trackedGlBytes={Interlocked.Read(ref _liveGlTextureBytes):N0} peakTrackedGlBytes={Interlocked.Read(ref _peakGlTextureBytes):N0} " +
            $"wasapiGraphs={Interlocked.Read(ref _liveWasapiGraphs)} create/dispose={Interlocked.Read(ref _wasapiGraphCreateCount)}/{Interlocked.Read(ref _wasapiGraphDisposeCount)} " +
            $"audioStarts/stops={AudioCapture.StartCount}/{AudioCapture.StopCount}");
    }

    private static void UpdatePeak(ref long peak, long value)
    {
        long observed;
        while ((observed = Interlocked.Read(ref peak)) < value)
        {
            if (Interlocked.CompareExchange(ref peak, value, observed) == observed)
                return;
        }
    }
}
