using System;
using OpenRGB.NET.Utils;

namespace OpenRGB.NET;

internal readonly struct SegmentsReader : ISpanReader<Segment[]>
{
    public static Segment[] ReadFrom(ref SpanReader reader, ProtocolVersion? protocolVersion = default, int? index = default,
        int? outerCount = default)
    {
        if (protocolVersion is not { } protocol)
            throw new ArgumentNullException(nameof(protocolVersion));

        var segmentCount = reader.Read<ushort>();
        var segments = new Segment[segmentCount];

        for (var i = 0; i < segmentCount; i++)
        {
            var name = reader.ReadLengthAndString();
            var type = (ZoneType)reader.Read<uint>();
            var start = reader.Read<uint>();
            var ledCount = reader.Read<uint>();

            if (protocol.SupportsStableDeviceIds)
            {
                var matrixLength = reader.Read<ushort>();
                if (matrixLength > 0)
                    _ = MatrixMapReader.ReadFrom(ref reader);
                _ = reader.Read<uint>(); // segment flags
            }

            segments[i] = new Segment(i, name, type, start, ledCount);
        }

        return segments;
    }
}
