using System;
using OpenRGB.NET.Utils;

namespace OpenRGB.NET;

internal readonly struct ZonesReader : ISpanReader<Zone[]>
{
    public static Zone[] ReadFrom(ref SpanReader reader, ProtocolVersion? protocolVersion = default, int? index = default, int? outerCount = default)
    {
        if (protocolVersion is not { } protocol)
            throw new ArgumentNullException(nameof(protocolVersion));
        if (index is not { } deviceIndex)
            throw new ArgumentNullException(nameof(index));

        var zoneCount = reader.Read<ushort>();
        var zones = new Zone[zoneCount];

        for (var i = 0; i < zoneCount; i++)
        {
            var name = reader.ReadLengthAndString();
            var type = (ZoneType)reader.Read<uint>();
            var ledsMin = reader.Read<uint>();
            var ledsMax = reader.Read<uint>();
            var ledCount = reader.Read<uint>();
            var zoneMatrixLength = reader.Read<ushort>();
            var matrixMap = zoneMatrixLength > 0 ? MatrixMapReader.ReadFrom(ref reader) : null;
            var segments = protocol.SupportsSegmentsAndPlugins ? SegmentsReader.ReadFrom(ref reader, protocolVersion) : [];

            if (protocol.SupportsControllerFlags)
                _ = reader.Read<uint>(); // zone flags

            if (protocol.SupportsStableDeviceIds)
            {
                _ = reader.Read<int>(); // active zone mode
                var zoneModeCount = reader.Read<ushort>();
                _ = ModesReader.ReadFrom(ref reader, protocol, outerCount: zoneModeCount);
                _ = reader.ReadLengthAndString(); // zone display name
            }

            zones[i] = new Zone(i, deviceIndex, name, type, ledCount, ledsMin, ledsMax, matrixMap, segments);
        }

        return zones;
    }
}
