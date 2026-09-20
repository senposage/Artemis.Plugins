using System;

namespace OpenRGB.NET;

/// <summary>
///     Describes a Protocol Version
/// </summary>
public readonly struct ProtocolVersion
{
    private ProtocolVersion(uint number,
        bool supportsVendorString, bool supportsProfileControls,
        bool supportsBrightnessAndSaveMode, bool supportsSegmentsAndPlugins)
    {
        Number = number;
        SupportsVendorString = supportsVendorString;
        SupportsProfileControls = supportsProfileControls;
        SupportsBrightnessAndSaveMode = supportsBrightnessAndSaveMode;
        SupportsSegmentsAndPlugins = supportsSegmentsAndPlugins;
    }

    /// <summary>
    ///     The number of the protocol version
    /// </summary>
    public uint Number { get; }

    /// <summary>
    ///     Whether the protocol version supports the vendor string
    /// </summary>
    public bool SupportsVendorString { get; }

    /// <summary>
    ///     Whether the protocol version supports profile controls
    /// </summary>
    public bool SupportsProfileControls { get; }

    /// <summary>
    ///     Whether the protocol version supports brightness and save mode
    /// </summary>
    public bool SupportsBrightnessAndSaveMode { get; }

    /// <summary>
    ///     Whether the protocol version supports segments
    /// </summary>
    public bool SupportsSegmentsAndPlugins { get; }

    /// <summary>
    ///     Whether the protocol includes controller/zone flags and alternate LED names.
    /// </summary>
    public bool SupportsControllerFlags => Number >= 5;

    /// <summary>
    ///     Whether controllers are addressed by stable 32-bit IDs instead of list indexes.
    /// </summary>
    public bool SupportsStableDeviceIds => Number >= 6;

    /// <summary>
    ///     The invalid protocol version.
    /// </summary>
    public static readonly ProtocolVersion Invalid = new(0, false, false, false, false);

    /// <summary>
    ///     The protocol version 0, the initial release.
    /// </summary>
    public static readonly ProtocolVersion V0 = new(0, false, false, false, false);

    /// <summary>
    ///     The protocol version 1, with vendor string.
    /// </summary>
    public static readonly ProtocolVersion V1 = new(1, true, false, false, false);

    /// <summary>
    ///     The protocol version 2, with profile controls.
    /// </summary>
    public static readonly ProtocolVersion V2 = new(2, true, true, false, false);

    /// <summary>
    ///     The protocol version 3, with brightness and save mode.
    /// </summary>
    public static readonly ProtocolVersion V3 = new(3, true, true, true, false);

    /// <summary>
    ///     The protocol version 4, with segments.
    /// </summary>
    public static readonly ProtocolVersion V4 = new(4, true, true, true, true);

    /// <summary>
    ///     The protocol version 5, with controller and zone flags.
    /// </summary>
    public static readonly ProtocolVersion V5 = new(5, true, true, true, true);

    /// <summary>
    ///     The protocol version 6, with stable controller IDs and hotplug-safe updates.
    /// </summary>
    public static readonly ProtocolVersion V6 = new(6, true, true, true, true);

    /// <summary>
    ///     Gets a protocol version from a number
    /// </summary>
    public static ProtocolVersion FromNumber(uint number) => number switch
    {
        0 => V0,
        1 => V1,
        2 => V2,
        3 => V3,
        4 => V4,
        5 => V5,
        6 => V6,
        _ => throw new ArgumentOutOfRangeException(nameof(number), number, "Unknown protocol version")
    };
}
