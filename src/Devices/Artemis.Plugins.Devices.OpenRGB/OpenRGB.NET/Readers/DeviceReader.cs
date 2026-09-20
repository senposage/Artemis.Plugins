using System;
using OpenRGB.NET.Utils;

namespace OpenRGB.NET;

internal readonly struct DeviceReader : ISpanReader<Device>
{
    public static Device ReadFrom(ref SpanReader reader, ProtocolVersion? protocolVersion = default, int? index = default, int? outerCount = default)
    {
        if (protocolVersion is not { } protocol)
            throw new ArgumentNullException(nameof(protocolVersion));
        if (index is not { } deviceIndex)
            throw new ArgumentNullException(nameof(index));

        // ReSharper disable once UnusedVariable
        var dataSize = reader.Read<uint>();

        var deviceType = reader.Read<int>();
        var name = reader.ReadLengthAndString();
        var vendor = protocol.SupportsVendorString ? reader.ReadLengthAndString() : null;
        var description = reader.ReadLengthAndString();
        var version = reader.ReadLengthAndString();
        var serial = reader.ReadLengthAndString();
        var location = reader.ReadLengthAndString();
        var modeCount = reader.Read<ushort>();
        var activeMode = reader.Read<int>();
        var modes = ModesReader.ReadFrom(ref reader, protocol, outerCount: modeCount);
        var zones = ZonesReader.ReadFrom(ref reader, protocol, index: deviceIndex);
        var leds = LedsReader.ReadFrom(ref reader, protocol);
        var colors = ColorsReader.ReadFrom(ref reader);

        if (protocol.SupportsControllerFlags)
        {
            var alternateNameCount = reader.Read<ushort>();
            for (var i = 0; i < alternateNameCount; i++)
                _ = reader.ReadLengthAndString();
            _ = reader.Read<uint>(); // controller flags
        }

        if (protocol.SupportsStableDeviceIds)
        {
            _ = reader.ReadLengthAndString(); // controller display name
            var configurationLength = checked((int)reader.Read<uint>());
            _ = reader.ReadBytes(configurationLength);
        }

        return new Device(deviceIndex, (DeviceType)deviceType,
            name, vendor, description, version, serial, location,
            activeMode, modes, zones, leds, colors);
    }
}
