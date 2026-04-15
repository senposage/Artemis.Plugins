using System;
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
