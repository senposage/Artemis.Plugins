using RGB.NET.Core;

namespace RGB.NET.Devices.OpenRGB;

/// <summary>
/// Represents a generic OpenRGB Device.
/// </summary>
public interface IOpenRGBDevice : IRGBDevice
{
    /// <summary>
    /// Gets an identifier containing the OpenRGB server, reported device location, and split-device part.
    /// </summary>
    string PersistentId { get; }
}
