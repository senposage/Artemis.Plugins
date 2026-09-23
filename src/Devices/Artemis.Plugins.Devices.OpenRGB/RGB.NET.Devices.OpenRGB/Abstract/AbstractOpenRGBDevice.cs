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
        return string.IsNullOrWhiteSpace(location) ? $"Controller:{controllerId}" : $"Location:{location}";
    }

    /// <inheritdoc />
    public string PersistentId { get; }

    /// <inheritdoc />
    public string? LegacyPersistentId { get; }

    #endregion
}
