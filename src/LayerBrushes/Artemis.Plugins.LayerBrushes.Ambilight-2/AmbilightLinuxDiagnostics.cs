using System;
using System.IO;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight;

internal static class AmbilightLinuxDiagnostics
{
    private static readonly object Lock = new();

    public static string LogPath
    {
        get
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            return Path.Combine(localData, "Artemis", "Ambilight", "ambilight-linux-capture.log");
        }
    }

    public static void Write(ILogger logger, string message)
    {
        if (!OperatingSystem.IsLinux())
            return;

        logger.Warning("[Ambilight/Linux] {Message}", message);
        Write(message);
    }

    public static void Write(string message)
    {
        if (!OperatingSystem.IsLinux())
            return;

        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        try
        {
            Console.Error.WriteLine($"[Ambilight/Linux] {message}");
        }
        catch
        {
        }

        try
        {
            lock (Lock)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
