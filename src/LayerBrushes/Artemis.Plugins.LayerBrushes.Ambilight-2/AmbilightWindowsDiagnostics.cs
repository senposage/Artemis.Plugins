using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight;

internal static class AmbilightWindowsDiagnostics
{
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

    [Conditional("DEBUG")]
    public static void Write(ILogger logger, string message)
    {
        logger.Debug("[Ambilight/Windows] {Message}", message);
        Write(message);
    }

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never break plugin startup.
        }
    }
}
