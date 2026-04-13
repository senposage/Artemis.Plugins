using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;

/// <summary>
/// Maps volatile display device names (e.g. \\.\DISPLAY1) to stable monitor hardware paths
/// that persist across display power cycles and ID reassignment.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MonitorIdentifier
{
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    /// <summary>
    /// Gets the stable monitor device path for a given display adapter name.
    /// </summary>
    /// <param name="adapterDeviceName">Adapter name like \\.\DISPLAY1</param>
    /// <returns>Stable monitor device ID, or null if not found</returns>
    public static string? GetMonitorDevicePath(string adapterDeviceName)
    {
        var device = new DISPLAY_DEVICE();
        device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

        // Enumerate monitors attached to this adapter
        if (EnumDisplayDevices(adapterDeviceName, 0, ref device, EDD_GET_DEVICE_INTERFACE_NAME))
        {
            if (!string.IsNullOrEmpty(device.DeviceID))
                return device.DeviceID;
        }

        // Retry without interface name flag to get hardware ID
        device = new DISPLAY_DEVICE();
        device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        if (EnumDisplayDevices(adapterDeviceName, 0, ref device, 0))
        {
            if (!string.IsNullOrEmpty(device.DeviceID))
                return device.DeviceID;
        }

        return null;
    }

    /// <summary>
    /// Builds a lookup from stable monitor device path to the current adapter device name.
    /// </summary>
    public static Dictionary<string, string> BuildMonitorToAdapterMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var adapter = new DISPLAY_DEVICE();
        adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            string? monitorPath = GetMonitorDevicePath(adapter.DeviceName);
            if (monitorPath != null)
                map.TryAdd(monitorPath, adapter.DeviceName);

            adapter = new DISPLAY_DEVICE();
            adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }

        return map;
    }

    /// <summary>
    /// Finds the current Display that matches a saved stable monitor path.
    /// </summary>
    public static Display? FindDisplayByMonitorPath(IScreenCaptureService service, string monitorDevicePath)
    {
        var monitorMap = BuildMonitorToAdapterMap();
        if (!monitorMap.TryGetValue(monitorDevicePath, out string? currentAdapterName))
            return null;

        foreach (GraphicsCard gc in service.GetGraphicsCards())
        {
            foreach (Display display in service.GetDisplays(gc))
            {
                if (display.DeviceName.Equals(currentAdapterName, StringComparison.OrdinalIgnoreCase))
                    return display;
            }
        }

        return null;
    }
}
