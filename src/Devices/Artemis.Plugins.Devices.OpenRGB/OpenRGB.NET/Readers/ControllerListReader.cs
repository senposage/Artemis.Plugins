using System;
using OpenRGB.NET.Utils;

namespace OpenRGB.NET;

internal readonly record struct ControllerList(uint[] Ids)
{
    public int Count => Ids.Length;
}

internal readonly struct ControllerListReader : ISpanReader<ControllerList>
{
    public static ControllerList ReadFrom(ref SpanReader reader, ProtocolVersion? protocolVersion = default,
        int? index = default, int? outerCount = default)
    {
        if (protocolVersion is not { } protocol)
            throw new ArgumentNullException(nameof(protocolVersion));

        var count = checked((int)reader.Read<uint>());
        var ids = new uint[count];

        for (var i = 0; i < count; i++)
            ids[i] = protocol.SupportsStableDeviceIds ? reader.Read<uint>() : (uint)i;

        return new ControllerList(ids);
    }
}
