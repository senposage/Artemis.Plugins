using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;

internal static class LinuxMonitorIdentifier
{
    public static string? GetMonitorKey(Display display)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        if (display.DeviceName.StartsWith("portal:", StringComparison.OrdinalIgnoreCase))
            return display.DeviceName;

        return GetConnectorEdidKey(display.DeviceName);
    }

    public static Display? FindDisplayByMonitorKey(IScreenCaptureService service, string monitorKey)
    {
        foreach (GraphicsCard graphicsCard in service.GetGraphicsCards())
        {
            foreach (Display display in service.GetDisplays(graphicsCard))
            {
                string? candidateKey = GetMonitorKey(display);
                if (!string.IsNullOrWhiteSpace(candidateKey) && candidateKey.Equals(monitorKey, StringComparison.OrdinalIgnoreCase))
                    return display;
            }
        }

        return null;
    }

    private static string? GetConnectorEdidKey(string connectorName)
    {
        foreach (string edidPath in EnumerateEdidPaths(connectorName))
        {
            byte[] edid;
            try
            {
                edid = File.ReadAllBytes(edidPath);
            }
            catch
            {
                continue;
            }

            if (edid.Length < 128 || edid.All(b => b == 0))
                continue;

            string manufacturer = DecodeManufacturer(edid);
            ushort product = BitConverter.ToUInt16(edid, 10);
            uint serial = BitConverter.ToUInt32(edid, 12);
            string hash = Convert.ToHexString(SHA256.HashData(edid)).Substring(0, 16);
            return $"linux-edid:{manufacturer}:{product:X4}:{serial:X8}:{hash}";
        }

        return null;
    }

    private static IEnumerable<string> EnumerateEdidPaths(string connectorName)
    {
        string drmRoot = "/sys/class/drm";
        if (!Directory.Exists(drmRoot))
            yield break;

        foreach (string connectorDirectory in Directory.EnumerateDirectories(drmRoot, $"*-{connectorName}"))
            yield return Path.Combine(connectorDirectory, "edid");
    }

    private static string DecodeManufacturer(byte[] edid)
    {
        ushort manufacturer = (ushort)((edid[8] << 8) | edid[9]);
        Span<char> chars = stackalloc char[3];
        chars[0] = DecodeManufacturerCharacter((manufacturer >> 10) & 0x1f);
        chars[1] = DecodeManufacturerCharacter((manufacturer >> 5) & 0x1f);
        chars[2] = DecodeManufacturerCharacter(manufacturer & 0x1f);
        return new string(chars);
    }

    private static char DecodeManufacturerCharacter(int value)
    {
        return value is >= 1 and <= 26 ? (char)('A' + value - 1) : '?';
    }
}
