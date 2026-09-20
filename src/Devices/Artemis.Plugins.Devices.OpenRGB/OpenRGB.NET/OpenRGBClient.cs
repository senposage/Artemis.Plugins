using System;
using System.Runtime.InteropServices;

namespace OpenRGB.NET;

/// <summary>
///     Client for the OpenRGB SDK.
/// </summary>
public sealed class OpenRgbClient : IDisposable, IOpenRgbClient
{
    private const int MaxProtocolNumber = 6;
    private readonly string _name;
    private readonly string _ip;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly uint _protocolVersionNumber;
    private readonly OpenRgbConnection _connection;
    private readonly object _controllerIdsLock = new();
    private uint[] _controllerIds = [];

    /// <inheritdoc />
    public bool Connected => _connection.Connected;

    /// <inheritdoc />
    public ProtocolVersion MaxSupportedProtocolVersion => ProtocolVersion.FromNumber(MaxProtocolNumber);

    /// <inheritdoc />
    public ProtocolVersion ClientProtocolVersion => ProtocolVersion.FromNumber(_protocolVersionNumber);

    /// <inheritdoc />
    public ProtocolVersion CommonProtocolVersion => _connection.CurrentProtocolVersion;

    /// <inheritdoc />
    public event EventHandler<EventArgs>? DeviceListUpdated;

    /// <inheritdoc />
    public event EventHandler<EventArgs>? ConnectionLost;

    /// <inheritdoc />
    public event EventHandler<EventArgs>? DetectionStarted;

    /// <inheritdoc />
    public event EventHandler<EventArgs>? DetectionEnded;

    /// <summary>
    ///     Sets all the needed parameters to connect to the server.
    ///     Connects to the server immediately unless autoConnect is set to false.
    /// </summary>
    public OpenRgbClient(string ip = "127.0.0.1",
        int port = 6742,
        string name = "OpenRGB.NET",
        bool autoConnect = true,
        int timeoutMs = 1000,
        uint protocolVersionNumber = MaxProtocolNumber)
    {
        _ip = ip;
        _port = port;
        _name = name;
        _timeoutMs = timeoutMs;
        _protocolVersionNumber = protocolVersionNumber;
        _connection = new OpenRgbConnection(this);

        if (protocolVersionNumber > MaxProtocolNumber)
            throw new ArgumentException("Client protocol version provided higher than supported.",
                nameof(protocolVersionNumber));

        if (autoConnect) Connect();
    }

    /// <inheritdoc />
    public void Connect()
    {
        if (Connected)
            return;

        _connection.Connect(_name, _ip, _port, _timeoutMs, _protocolVersionNumber);
    }

    internal void OnDeviceListUpdated()
    {
        DeviceListUpdated?.Invoke(this, EventArgs.Empty);
    }

    internal void OnConnectionLost()
    {
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    internal void OnDetectionStarted()
    {
        DetectionStarted?.Invoke(this, EventArgs.Empty);
    }

    internal void OnDetectionEnded()
    {
        DetectionEnded?.Invoke(this, EventArgs.Empty);
    }

    #region API

    /// <inheritdoc />
    public int GetControllerCount() => GetControllerIds().Length;

    /// <inheritdoc />
    public uint[] GetControllerIds()
    {
        ControllerList controllers = _connection.Request<EmptyArg, ControllerListReader, ControllerList>(
            CommandId.RequestControllerCount, 0, new EmptyArg());

        lock (_controllerIdsLock)
        {
            _controllerIds = controllers.Ids;
            return [.. _controllerIds];
        }
    }

    /// <summary>
    /// Returns the protocol-level identifier for a controller index.
    /// </summary>
    public uint GetControllerId(int deviceIndex) => ResolveControllerId(deviceIndex);

    /// <inheritdoc />
    public Device GetControllerData(int deviceId)
    {
        if (deviceId < 0)
            throw new ArgumentException("Unexpected device Id", nameof(deviceId));

        uint controllerId = ResolveControllerId(deviceId);
        return _connection.RequestWithReaderIndex<ProtocolVersionArg, DeviceReader, Device>(
            CommandId.RequestControllerData, controllerId, deviceId,
            new ProtocolVersionArg(_connection.CurrentProtocolVersion));
    }

    /// <inheritdoc />
    public Device[] GetAllControllerData()
    {
        var count = GetControllerCount();

        var controllers = new Device[count];
        for (var i = 0; i < count; i++)
            controllers[i] = GetControllerData(i);

        return controllers;
    }

    /// <inheritdoc />
    public void RequestDeviceRescan()
    {
        if (CommonProtocolVersion.Number < 5)
            throw new NotSupportedException($"Device rescans require protocol v5 or newer; negotiated v{CommonProtocolVersion.Number}.");

        _connection.Send(CommandId.RequestDeviceRescan, 0, new EmptyArg());
    }

    /// <inheritdoc />
    public string[] GetProfiles()
    {
        if (!CommonProtocolVersion.SupportsProfileControls)
            throw new NotSupportedException($"Not supported on protocol version {CommonProtocolVersion.Number}");

        return _connection.Request<EmptyArg, ProfilesReader, string[]>(CommandId.RequestProfiles, 0, new EmptyArg());
    }

    /// <inheritdoc />
    public Plugin[] GetPlugins()
    {
        if (!CommonProtocolVersion.SupportsSegmentsAndPlugins)
            throw new NotSupportedException($"Not supported on protocol version {CommonProtocolVersion.Number}");

        return _connection.Request<EmptyArg, PluginsReader, Plugin[]>(CommandId.RequestPlugins, 0, new EmptyArg());
    }

    /// <inheritdoc />
    public void ResizeZone(int deviceId, int zoneId, int size)
    {
        _connection.Send(CommandId.ResizeZone, ResolveControllerId(deviceId), new Args<uint, uint>((uint)zoneId, (uint)size));
    }

    /// <inheritdoc />
    public void UpdateLeds(int deviceId, ReadOnlySpan<Color> colors)
    {
        if (colors.Length == 0)
            throw new ArgumentException("The colors span is empty.", nameof(colors));

        if (deviceId < 0)
            throw new ArgumentException("Invalid deviceId", nameof(deviceId));

        var bytes = MemoryMarshal.Cast<Color, byte>(colors);

        _connection.Send(CommandId.UpdateLeds, ResolveControllerId(deviceId), new UpdateLedsArg((ushort)colors.Length), bytes);
    }

    /// <summary>
    /// Updates LEDs using a protocol-level controller identifier.
    /// </summary>
    public void UpdateLedsByControllerId(uint controllerId, ReadOnlySpan<Color> colors)
    {
        if (colors.Length == 0)
            throw new ArgumentException("The colors span is empty.", nameof(colors));

        var bytes = MemoryMarshal.Cast<Color, byte>(colors);
        _connection.Send(CommandId.UpdateLeds, controllerId, new UpdateLedsArg((ushort)colors.Length), bytes);
    }

    /// <inheritdoc />
    public void UpdateZoneLeds(int deviceId, int zoneId, ReadOnlySpan<Color> colors)
    {
        if (colors.Length == 0)
            throw new ArgumentException("The colors span is empty.", nameof(colors));

        if (deviceId < 0)
            throw new ArgumentException("Invalid device id.", nameof(deviceId));

        if (zoneId < 0)
            throw new ArgumentException("Invalid zone id", nameof(zoneId));

        var bytes = MemoryMarshal.Cast<Color, byte>(colors);

        _connection.Send(CommandId.UpdateZoneLeds, ResolveControllerId(deviceId), new UpdateZoneLedsArg((uint)zoneId, (ushort)colors.Length), bytes);
    }

    /// <inheritdoc />
    public void UpdateSingleLed(int deviceId, int ledId, Color color)
    {
        if (deviceId < 0)
            throw new ArgumentException("Invalid device id.", nameof(deviceId));

        if (ledId < 0)
            throw new ArgumentException("Invalid led id", nameof(ledId));

        _connection.Send(CommandId.UpdateSingleLed, ResolveControllerId(deviceId), new Args<uint, Color>((uint)ledId, color));
    }

    /// <inheritdoc />
    public void SetCustomMode(int deviceId)
    {
        _connection.Send(CommandId.SetCustomMode, ResolveControllerId(deviceId), new EmptyArg());
    }

    /// <inheritdoc />
    public void LoadProfile(string profile)
    {
        _connection.Send(CommandId.LoadProfile, 0, new StringArg(profile));
    }

    /// <inheritdoc />
    public void SaveProfile(string profile)
    {
        _connection.Send(CommandId.SaveProfile, 0, new StringArg(profile));
    }

    /// <inheritdoc />
    public void DeleteProfile(string profile)
    {
        _connection.Send(CommandId.DeleteProfile, 0, new StringArg(profile));
    }

    /// <inheritdoc />
    public void UpdateMode(int deviceId, int modeId,
        uint? speed = null,
        Direction? direction = null,
        Color[]? colors = default)
    {
        UpdateMode(deviceId, GetControllerData(deviceId), modeId, speed, direction, colors);
    }

    /// <inheritdoc />
    public void UpdateMode(int deviceId, Device targetDevice, int modeId,
        uint? speed = null,
        Direction? direction = null,
        Color[]? colors = default)
    {
        if (deviceId < 0)
            throw new ArgumentException("Invalid deviceId", nameof(deviceId));

        if (modeId < 0 || modeId >= targetDevice.Modes.Length)
            throw new ArgumentException(nameof(modeId));

        var targetMode = targetDevice.Modes[modeId];

        if (speed.HasValue)
        {
            if (!targetMode.SupportsSpeed)
                throw new InvalidOperationException("Cannot set speed on a mode that doesn't use this parameter");

            targetMode.SetSpeed(speed.Value);
        }

        if (direction.HasValue)
        {
            if (!targetMode.SupportsDirection)
                throw new InvalidOperationException("Cannot set direction on a mode that doesn't use this parameter");

            targetMode.SetDirection(direction.Value);
        }

        if (colors != null)
        {
            if (colors.Length != targetMode.Colors.Length)
                throw new InvalidOperationException("Incorrect number of colors supplied");

            targetMode.SetColors(colors);
        }

        _connection.Send(CommandId.UpdateMode, ResolveControllerId(deviceId), new ModeOperationArg((uint)modeId, new ModeArg(targetMode)));
    }

    /// <inheritdoc />
    public void SaveMode(int deviceId, int modeId)
    {
        var targetDevice = GetControllerData(deviceId);

        if (modeId > targetDevice.Modes.Length)
            throw new ArgumentException(nameof(modeId));

        var targetMode = targetDevice.Modes[modeId];

        _connection.Send(CommandId.SaveMode, ResolveControllerId(deviceId), new ModeOperationArg((uint)modeId, new ModeArg(targetMode)));
    }

    /// <inheritdoc />
    public void PluginSpecific(int pluginId, int pluginPacketType, ReadOnlySpan<byte> data)
    {
        if (!CommonProtocolVersion.SupportsSegmentsAndPlugins)
            throw new NotSupportedException($"Not supported on protocol version {CommonProtocolVersion.Number}");

        _connection.Send(CommandId.PluginSpecific, (uint)pluginId, new Args<uint>((uint)pluginPacketType), data);
    }

    #endregion

    private uint ResolveControllerId(int deviceIndex)
    {
        if (deviceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex));

        if (!CommonProtocolVersion.SupportsStableDeviceIds)
            return (uint)deviceIndex;

        lock (_controllerIdsLock)
        {
            if (_controllerIds.Length > 0)
                return GetMappedControllerId(deviceIndex);
        }

        // Preserve the pre-v6 API behavior where callers could request controller zero
        // without explicitly fetching the count first.
        GetControllerCount();

        lock (_controllerIdsLock)
            return GetMappedControllerId(deviceIndex);
    }

    private uint GetMappedControllerId(int deviceIndex)
    {
        if ((uint)deviceIndex >= (uint)_controllerIds.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndex), deviceIndex,
                "The controller index is no longer present in the OpenRGB device list.");
        }

        return _controllerIds[deviceIndex];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }
}
