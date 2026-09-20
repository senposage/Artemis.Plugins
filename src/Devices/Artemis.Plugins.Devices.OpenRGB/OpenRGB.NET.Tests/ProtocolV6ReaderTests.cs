using System.Text;
using OpenRGB.NET.Utils;
using Xunit;

namespace OpenRGB.NET.Tests;

public class ProtocolV6ReaderTests
{
    [Fact]
    public void ControllerListReaderReadsStableIds()
    {
        byte[] data = Build(writer =>
        {
            writer.Write((uint)3);
            writer.Write(0xF0000001u);
            writer.Write(42u);
            writer.Write(uint.MaxValue);
        });

        var reader = new SpanReader(data);
        ControllerList result = ControllerListReader.ReadFrom(ref reader, ProtocolVersion.V6);

        Assert.Equal(new[] { 0xF0000001u, 42u, uint.MaxValue }, result.Ids);
    }

    [Fact]
    public void DeviceReaderConsumesProtocolV6Shape()
    {
        byte[] data = Build(writer =>
        {
            writer.Write(0u); // data size is not used by the model
            writer.Write((int)DeviceType.Keyboard);
            WriteString(writer, "Test Keyboard");
            WriteString(writer, "Artemis");
            WriteString(writer, "Protocol v6 fixture");
            WriteString(writer, "1.0");
            WriteString(writer, "serial");
            WriteString(writer, "usb:1");

            writer.Write((ushort)1); // device modes
            writer.Write(0); // active mode
            WriteMode(writer, "Direct");

            writer.Write((ushort)1); // zones
            WriteString(writer, "Main Zone");
            writer.Write((int)ZoneType.Linear);
            writer.Write(1u); // min LEDs
            writer.Write(1u); // max LEDs
            writer.Write(1u); // LED count
            writer.Write((ushort)0); // no zone matrix
            writer.Write((ushort)1); // segments
            WriteString(writer, "Segment 1");
            writer.Write((int)ZoneType.Linear);
            writer.Write(0u); // start
            writer.Write(1u); // LED count
            writer.Write((ushort)0); // no segment matrix (v6)
            writer.Write(0u); // segment flags (v6)
            writer.Write(0u); // zone flags (v5)
            writer.Write(-1); // active zone mode (v6)
            writer.Write((ushort)0); // zone modes (v6)
            WriteString(writer, "Main Zone Display");

            writer.Write((ushort)1); // LEDs
            WriteString(writer, "Key A"); // v6 has no LED value

            writer.Write((ushort)1); // colors
            writer.Write((byte)1);
            writer.Write((byte)2);
            writer.Write((byte)3);
            writer.Write((byte)0);

            writer.Write((ushort)1); // alternate LED names (v5)
            WriteString(writer, "A");
            writer.Write(0u); // controller flags (v5)
            WriteString(writer, "Keyboard Display");
            byte[] configuration = Encoding.ASCII.GetBytes("{}\0");
            writer.Write((uint)configuration.Length);
            writer.Write(configuration);
        });

        var reader = new SpanReader(data);
        Device device = DeviceReader.ReadFrom(ref reader, ProtocolVersion.V6, index: 7);

        Assert.Equal(7, device.Index);
        Assert.Equal("Test Keyboard", device.Name);
        Assert.Equal("Direct", Assert.Single(device.Modes).Name);
        Assert.Equal("Main Zone", Assert.Single(device.Zones).Name);
        Assert.Equal("Segment 1", Assert.Single(device.Zones[0].Segments).Name);
        Assert.Equal("Key A", Assert.Single(device.Leds).Name);
        Assert.Equal(new Color(1, 2, 3), Assert.Single(device.Colors));
    }

    private static void WriteMode(BinaryWriter writer, string name)
    {
        WriteString(writer, name);
        // Protocol v6 intentionally omits mode_value.
        writer.Write(0u); // flags
        writer.Write(0u); // speed min
        writer.Write(0u); // speed max
        writer.Write(0u); // brightness min
        writer.Write(100u); // brightness max
        writer.Write(0u); // colors min
        writer.Write(0u); // colors max
        writer.Write(0u); // speed
        writer.Write(100u); // brightness
        writer.Write(0u); // direction
        writer.Write(0u); // color mode
        writer.Write((ushort)0); // colors
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(checked((ushort)(bytes.Length + 1)));
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            write(writer);
        return stream.ToArray();
    }
}
