using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace OpenRGB.NET.Tests;

public class ProtocolV6ConnectionTests
{
    [Fact]
    public async Task ClientUsesStableIdAndRaisesHotplugEvent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const uint stableId = 0xF0000001;

        Task server = Task.Run(async () =>
        {
            using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
            NetworkStream stream = socket.GetStream();

            Packet clientName = await ReadPacket(stream, timeout.Token);
            Assert.Equal(CommandId.SetClientName, clientName.Command);

            Packet negotiation = await ReadPacket(stream, timeout.Token);
            Assert.Equal(CommandId.RequestProtocolVersion, negotiation.Command);
            Assert.Equal(6u, BitConverter.ToUInt32(negotiation.Data));
            await WritePacket(stream, 0, CommandId.RequestProtocolVersion, BitConverter.GetBytes(6u), timeout.Token);

            // Protocol v6 servers may send these asynchronously. They must not break the receive loop.
            await WritePacket(stream, 0, CommandId.SetServerName, Encoding.ASCII.GetBytes("Fixture\0"), timeout.Token);

            Packet countRequest = await ReadPacket(stream, timeout.Token);
            Assert.Equal(CommandId.RequestControllerCount, countRequest.Command);
            byte[] controllerList = new byte[8];
            BitConverter.GetBytes(1u).CopyTo(controllerList, 0);
            BitConverter.GetBytes(stableId).CopyTo(controllerList, 4);
            await WritePacket(stream, 0, CommandId.RequestControllerCount, controllerList, timeout.Token);

            Packet rescan = await ReadPacket(stream, timeout.Token);
            Assert.Equal(CommandId.RequestDeviceRescan, rescan.Command);
            Assert.Empty(rescan.Data);

            Packet update = await ReadPacket(stream, timeout.Token);
            Assert.Equal(CommandId.UpdateLeds, update.Command);
            Assert.Equal(stableId, update.DeviceId);

            await WritePacket(stream, 0, CommandId.DetectionStarted, [], timeout.Token);
            await WritePacket(stream, 0, CommandId.DetectionProgressChanged, BitConverter.GetBytes(10u), timeout.Token);
            await WritePacket(stream, 0, CommandId.DeviceListUpdated, [], timeout.Token);
            await WritePacket(stream, 0, CommandId.DetectionProgressChanged, BitConverter.GetBytes(90u), timeout.Token);
            await WritePacket(stream, 0, CommandId.DetectionEnded, [], timeout.Token);
        }, timeout.Token);

        try
        {
            using var client = new OpenRgbClient(port: port, timeoutMs: 2000);
            var hotplug = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detectionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var eventOrder = new List<string>();
            client.DeviceListUpdated += (_, _) => { eventOrder.Add("updated"); hotplug.TrySetResult(); };
            client.ConnectionLost += (_, _) => disconnected.TrySetResult();
            client.DetectionStarted += (_, _) => { eventOrder.Add("started"); detectionStarted.TrySetResult(); };
            client.DetectionEnded += (_, _) => { eventOrder.Add("ended"); detectionEnded.TrySetResult(); };

            Assert.Equal(ProtocolVersion.V6.Number, client.CommonProtocolVersion.Number);
            Assert.Equal(1, client.GetControllerCount());
            client.RequestDeviceRescan();
            client.UpdateLeds(0, [new Color(1, 2, 3)]);

            await hotplug.Task.WaitAsync(timeout.Token);
            await detectionStarted.Task.WaitAsync(timeout.Token);
            await detectionEnded.Task.WaitAsync(timeout.Token);
            Assert.Equal(new[] { "started", "updated", "ended" }, eventOrder);
            await server;
            await disconnected.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<Packet> ReadPacket(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[PacketHeader.LENGTH];
        await stream.ReadExactlyAsync(header, cancellationToken);
        PacketHeader parsed = PacketHeader.FromSpan(header);
        byte[] data = new byte[parsed.DataLength];
        if (data.Length > 0)
            await stream.ReadExactlyAsync(data, cancellationToken);
        return new Packet(parsed.DeviceId, parsed.Command, data);
    }

    private static async Task WritePacket(NetworkStream stream, uint deviceId, CommandId command, byte[] data,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("ORGB"));
            writer.Write(deviceId);
            writer.Write((uint)command);
            writer.Write((uint)data.Length);
            writer.Write(data);
        }

        await stream.WriteAsync(buffer.ToArray(), cancellationToken);
    }

    private readonly record struct Packet(uint DeviceId, CommandId Command, byte[] Data);
}
