using System;
using RGB.NET.Core;

namespace RGB.NET.Devices.OpenRGB;

/// <inheritdoc cref="AbstractRGBDevice{TDeviceInfo}" />
/// <summary>
/// Represents a generic OpenRGB Device.
/// </summary>
public abstract class AbstractOpenRGBDevice<TDeviceInfo> : AbstractRGBDevice<TDeviceInfo>, IOpenRGBDevice
    where TDeviceInfo : OpenRGBDeviceInfo
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AbstractOpenRGBDevice{TDeviceInfo}"/> class.
    /// </summary>
    /// <param name="info">The generic information provided by OpenRGB for this device.</param>
    /// <param name="updateQueue">The queue used to update this device.</param>
    protected AbstractOpenRGBDevice(TDeviceInfo info, IUpdateQueue updateQueue, string partIdentity)
        : base(info, updateQueue)
    {
        string locationIdentity = GetControllerIdentity(info.OpenRGBDevice.Location, info.ControllerId);
        string deviceIdentity = GetControllerIdentity(info.OpenRGBDevice.Location, info.ControllerId, info.OpenRGBDevice.Name);
        PersistentId = $"OpenRGB:{info.ServerIdentity}|{deviceIdentity}|{partIdentity}";
        string locationPersistentId = $"OpenRGB:{info.ServerIdentity}|{locationIdentity}|{partIdentity}";
        LegacyPersistentId = locationPersistentId == PersistentId ? null : locationPersistentId;
    }

    internal static string GetControllerIdentity(string? location, uint controllerId, string? controllerName = null)
    {
        // OpenRGB alternates the Vulcan II Max between HID interfaces during a
        // rescan. The interfaces do not report a serial, but the product VID/PID
        // is stable, unlike the MI_01/MI_03 path and instance suffix.
        if (controllerName == "Roccat Vulcan II Max" &&
            location?.Contains("VID_1E7D&PID_2EE2", StringComparison.OrdinalIgnoreCase) == true)
            return "HidProduct:VID_1E7D&PID_2EE2";

        return string.IsNullOrWhiteSpace(location) ? $"Controller:{controllerId}" : $"Location:{location}";
    }

    /// <inheritdoc />
    public string PersistentId { get; }

    /// <inheritdoc />
    public string? LegacyPersistentId { get; }

    #endregion
}
