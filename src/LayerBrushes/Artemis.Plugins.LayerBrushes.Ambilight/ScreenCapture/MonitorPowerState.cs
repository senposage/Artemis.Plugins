using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;

/// <summary>
/// Per-monitor power state detection via two complementary mechanisms:
/// 1. HMONITOR availability — detects physical disconnect / power-off (monitor vanishes from EnumDisplayMonitors)
/// 2. DDC/CI VCP 0xD6 — detects DPMS standby/suspend/off per physical monitor over I2C
///
/// Both run without elevation from a standard desktop session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MonitorPowerState
{
    private const byte VCP_POWER_MODE = 0xD6;
    private const uint POWER_ON = 0x01;
    // 0x02 = Standby, 0x03 = Suspend, 0x04 = Off (soft), 0x05 = Hard off

    internal enum PowerState
    {
        /// <summary>Monitor is on (VCP 0xD6 = 0x01).</summary>
        On,
        /// <summary>Monitor is in DPMS standby, suspend, or off (VCP 0xD6 = 0x02–0x05).</summary>
        Off,
        /// <summary>HMONITOR no longer present in EnumDisplayMonitors — monitor disconnected or fully powered off.</summary>
        NotPresent,
        /// <summary>DDC/CI query failed — monitor may not support it.</summary>
        DdcUnavailable,
    }

    /// <summary>
    /// Queries the power state of the physical monitor behind the given display adapter.
    /// </summary>
    public static PowerState QueryPowerState(string displayDeviceName)
    {
        IntPtr hMonitor = GetMonitorHandle(displayDeviceName);
        if (hMonitor == IntPtr.Zero)
            return PowerState.NotPresent;

        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            return PowerState.DdcUnavailable;

        var monitors = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            return PowerState.DdcUnavailable;

        try
        {
            if (GetVCPFeatureAndVCPFeatureReply(
                    monitors[0].hPhysicalMonitor,
                    VCP_POWER_MODE,
                    IntPtr.Zero,
                    out uint currentValue,
                    out _))
            {
                return currentValue == POWER_ON ? PowerState.On : PowerState.Off;
            }

            return PowerState.DdcUnavailable;
        }
        finally
        {
            DestroyPhysicalMonitors(count, monitors);
        }
    }

    #region HMONITOR lookup

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorProc lpfnEnum, IntPtr dwData);

    private delegate bool EnumMonitorProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private static IntPtr GetMonitorHandle(string deviceName)
    {
        IntPtr result = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info) &&
                info.szDevice.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                result = hMon;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    #endregion

    #region DDC/CI via dxva2.dll

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint physicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint physicalMonitorArraySize, [In] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hPhysicalMonitor,
        byte bVCPCode,
        IntPtr pvct,
        out uint pdwCurrentValue,
        out uint pdwMaximumValue);

    #endregion
}
