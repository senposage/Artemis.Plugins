using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wlroots;

internal static class WlrootsProcess
{
    private static readonly ILogger Logger = Log.ForContext(typeof(WlrootsProcess));

    public static bool CanRun(string fileName, params string[] arguments)
    {
        try
        {
            RunText(fileName, arguments, TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "{FileName} is unavailable", fileName);
            return false;
        }
    }

    public static string RunText(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        byte[] bytes = RunBytes(fileName, arguments, timeout);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static byte[] RunBytes(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using Process process = Start(fileName, arguments);
        using var output = new MemoryStream();
        Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> readError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"{fileName} did not finish within {timeout.TotalSeconds:N0} seconds.");
        }

        copyOutput.GetAwaiter().GetResult();
        string stderr = readError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {stderr}");

        return output.ToArray();
    }

    private static Process Start(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { }
    }
}
