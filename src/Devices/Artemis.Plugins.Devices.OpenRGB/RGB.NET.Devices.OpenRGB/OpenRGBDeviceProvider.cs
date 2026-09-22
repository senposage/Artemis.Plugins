using OpenRGB.NET;
using RGB.NET.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RGB.NET.Devices.OpenRGB;

/// <inheritdoc />
/// <summary>
/// Represents a device provider responsible for OpenRGB devices.
/// </summary>
public sealed class OpenRGBDeviceProvider : AbstractRGBDeviceProvider
{
    #region Properties & Fields

    // ReSharper disable once InconsistentNaming
    private static readonly Lock _lock = new();
    private readonly Lock _connectionLock = new();
    private readonly object _detectionStateLock = new();
    private readonly List<OpenRgbClient> _clients = [];
    private readonly Dictionary<OpenRgbClient, OpenRGBServerDefinition> _clientDefinitions = [];
    private readonly Dictionary<OpenRGBServerDefinition, HashSet<uint>> _controllerIds = [];
    private readonly Dictionary<OpenRGBServerDefinition, PendingRemovalSnapshot> _pendingRemovalSnapshots = [];
    private readonly List<OpenRGBUpdateQueue> _updateQueues = [];
    private readonly Dictionary<IRGBDevice, (OpenRgbClient Client, uint ControllerId)> _deviceControllers = [];
    private readonly Dictionary<OpenRgbClient, long> _detectionGenerations = [];
    private readonly Dictionary<OpenRgbClient, long> _detectionEndedAt = [];
    private readonly HashSet<OpenRgbClient> _detectingClients = [];
    private static readonly TimeSpan DetectionSnapshotQuietPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemovalSnapshotStabilityPeriod = TimeSpan.FromMilliseconds(750);

    private static OpenRGBDeviceProvider? _instance;

    /// <summary>
    /// Gets the singleton <see cref="OpenRGBDeviceProvider"/> instance.
    /// </summary>
    public static OpenRGBDeviceProvider Instance
    {
        get
        {
            lock (_lock)
                return _instance ?? new OpenRGBDeviceProvider();
        }
    }

    /// <summary>
    /// Gets a list of all defined device-definitions.
    /// </summary>
    public List<OpenRGBServerDefinition> DeviceDefinitions { get; } = [];

    /// <summary>
    /// Indicates whether all devices will be added, or just the ones with a 'Direct' mode. Defaults to false.
    /// </summary>
    public bool ForceAddAllDevices { get; set; } = false;

    /// <summary>
    /// Indicates that a reduced controller list is waiting for a confirming snapshot.
    /// </summary>
    public bool HasPendingRemovals
    {
        get
        {
            lock (_connectionLock)
                return _pendingRemovalSnapshots.Count > 0;
        }
    }

    public event Action<OpenRGBServerDefinition>? DeviceListUpdated;
    public event Action<OpenRGBServerDefinition>? ConnectionLost;
    public event Action<OpenRGBServerDefinition>? DetectionStarted;
    public event Action<OpenRGBServerDefinition>? DetectionEnded;

    /// <summary>
    /// Defines which device types will be separated by zones. Defaults to <see cref="RGBDeviceType.LedStripe" /> | <see cref="RGBDeviceType.Mainboard"/> | <see cref="RGBDeviceType.Speaker" />.
    /// </summary>
    public RGBDeviceType PerZoneDeviceFlag { get; } = RGBDeviceType.LedStripe | RGBDeviceType.Mainboard | RGBDeviceType.Speaker;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenRGBDeviceProvider"/> class.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if this constructor is called even if there is already an instance of this class.</exception>
    public OpenRGBDeviceProvider()
    {
        lock (_lock)
        {
            if (_instance != null) throw new InvalidOperationException($"There can be only one instance of type {nameof(OpenRGBDeviceProvider)}");
            _instance = this;
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Adds the specified <see cref="OpenRGBServerDefinition" /> to this device-provider.
    /// </summary>
    /// <param name="deviceDefinition">The <see cref="OpenRGBServerDefinition"/> to add.</param>
    // ReSharper disable once UnusedMember.Global
    public void AddDeviceDefinition(OpenRGBServerDefinition deviceDefinition) => DeviceDefinitions.Add(deviceDefinition);

    public uint ProtocolVersionFor(OpenRGBServerDefinition definition)
    {
        lock (_connectionLock)
            return _clientDefinitions.FirstOrDefault(pair => ReferenceEquals(pair.Value, definition)).Key?.CommonProtocolVersion.Number ?? 0;
    }

    private void Subscribe(OpenRgbClient client)
    {
        lock (_detectionStateLock)
            _detectionGenerations.TryAdd(client, 0);
        client.DeviceListUpdated += ClientOnDeviceListUpdated;
        client.ConnectionLost += ClientOnConnectionLost;
        client.DetectionStarted += ClientOnDetectionStarted;
        client.DetectionEnded += ClientOnDetectionEnded;
    }

    private void Unsubscribe(OpenRgbClient client)
    {
        client.DeviceListUpdated -= ClientOnDeviceListUpdated;
        client.ConnectionLost -= ClientOnConnectionLost;
        client.DetectionStarted -= ClientOnDetectionStarted;
        client.DetectionEnded -= ClientOnDetectionEnded;
        lock (_detectionStateLock)
        {
            _detectingClients.Remove(client);
            _detectionGenerations.Remove(client);
            _detectionEndedAt.Remove(client);
        }
    }

    private bool TryGetDefinition(object? sender, out OpenRGBServerDefinition definition)
    {
        lock (_connectionLock)
        {
            if (sender is OpenRgbClient client && _clientDefinitions.TryGetValue(client, out OpenRGBServerDefinition? found))
            {
                definition = found;
                return true;
            }

            definition = null!;
            return false;
        }
    }

    private void ClientOnDeviceListUpdated(object? sender, EventArgs args)
    {
        if (TryGetDefinition(sender, out OpenRGBServerDefinition definition))
            DeviceListUpdated?.Invoke(definition);
    }

    private void ClientOnConnectionLost(object? sender, EventArgs args)
    {
        if (!TryGetDefinition(sender, out OpenRGBServerDefinition definition))
            return;
        definition.Connected = false;
        ConnectionLost?.Invoke(definition);
    }

    private void ClientOnDetectionStarted(object? sender, EventArgs args)
    {
        if (sender is OpenRgbClient client)
        {
            lock (_detectionStateLock)
            {
                _detectingClients.Add(client);
                _detectionGenerations[client] = _detectionGenerations.GetValueOrDefault(client) + 1;
            }
        }
        if (TryGetDefinition(sender, out OpenRGBServerDefinition definition))
            DetectionStarted?.Invoke(definition);
    }

    private void ClientOnDetectionEnded(object? sender, EventArgs args)
    {
        if (sender is OpenRgbClient client)
        {
            lock (_detectionStateLock)
            {
                _detectingClients.Remove(client);
                _detectionEndedAt[client] = DateTime.UtcNow.Ticks;
                _detectionGenerations[client] = _detectionGenerations.GetValueOrDefault(client) + 1;
            }
        }
        if (TryGetDefinition(sender, out OpenRGBServerDefinition definition))
            DetectionEnded?.Invoke(definition);
    }

    /// <summary>
    /// Reconciles the protocol-v6 controller IDs on existing SDK connections.
    /// </summary>
    public bool TryRefreshDevices()
    {
        lock (_connectionLock)
        {
            OpenRGBServerDefinition? refreshingDefinition = null;
            try
            {
                foreach (OpenRgbClient client in _clients)
                {
                    if (!client.Connected)
                        return false;

                    if (!TryBeginControllerSnapshot(client, out long detectionGeneration))
                        return false;

                    OpenRGBServerDefinition definition = _clientDefinitions[client];
                    refreshingDefinition = definition;
                    uint[] controllerIds = client.GetControllerIds();
                    HashSet<uint> currentIds = controllerIds.ToHashSet();
                    var devices = new Dictionary<uint, (int Index, Device Device)>();

                    for (int i = 0; i < controllerIds.Length; i++)
                    {
                        Device device = client.GetControllerData(i);
                        devices[controllerIds[i]] = (i, device);
                    }

                    if (!IsControllerSnapshotValid(client, detectionGeneration))
                        return false;

                    if (!_controllerIds.TryGetValue(definition, out HashSet<uint>? previousIds))
                        return false;

                    (uint[] removedControllers, uint[] addedControllers) = DiffControllerIds(previousIds, currentIds);
                    bool removalsConfirmed;
                    if (removedControllers.Length == 0)
                    {
                        _pendingRemovalSnapshots.Remove(definition);
                        removalsConfirmed = true;
                    }
                    else
                    {
                        removalsConfirmed = IsRemovalSnapshotStable(definition, currentIds);
                    }
                    if (!removalsConfirmed)
                        removedControllers = [];

                    foreach (uint removed in removedControllers)
                    {
                        List<IRGBDevice> removedDevices = _deviceControllers
                            .Where(pair => ReferenceEquals(pair.Value.Client, client) && pair.Value.ControllerId == removed)
                            .Select(pair => pair.Key).ToList();
                        foreach (IRGBDevice removedDevice in removedDevices)
                        {
                            RemoveDevice(removedDevice);
                            _deviceControllers.Remove(removedDevice);
                            removedDevice.Dispose();
                        }

                        _updateQueues.RemoveAll(queue => queue.UsesClient(client) && queue.ControllerId == removed);
                    }

                    foreach (uint added in addedControllers)
                    {
                        if (!devices.TryGetValue(added, out (int Index, Device Device) addedData))
                            continue;

                        foreach (IRGBDevice addedDevice in CreateDevicesForController(client, definition, addedData.Index, added, addedData.Device))
                            AddDevice(addedDevice);
                    }

                    _controllerIds[definition] = removalsConfirmed
                        ? currentIds
                        : previousIds.Union(currentIds).ToHashSet();
                    definition.Connected = true;
                    definition.LastError = null;
                }

                return true;
            }
            catch (Exception exception)
            {
                // A timed-out request can leave request and response packets out of sync.
                // Force the wrapper to replace this client instead of repeatedly querying
                // a connection whose subsequent snapshots may be incomplete.
                if (refreshingDefinition != null)
                {
                    refreshingDefinition.Connected = false;
                    refreshingDefinition.LastError = exception.Message;
                }
                Throw(exception);
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces dead SDK connections and reconciles their protocol-v6 controller IDs.
    /// </summary>
    /// <returns><c>true</c> when every disconnected server was connected and reconciled.</returns>
    public bool TryReconnectDevices()
    {
        lock (_connectionLock)
        {
            var replacements = new List<(OpenRgbClient? OldClient, OpenRgbClient NewClient, OpenRGBServerDefinition Definition, HashSet<uint> Ids,
                Dictionary<uint, (int Index, Device Device)> Devices)>();

            try
            {
                foreach (OpenRGBServerDefinition definition in DeviceDefinitions.Where(definition => !definition.Connected))
                {
                    OpenRgbClient? oldClient = _clientDefinitions
                        .FirstOrDefault(pair => ReferenceEquals(pair.Value, definition)).Key;
                    var newClient = new OpenRgbClient(definition.Ip, definition.Port, definition.ClientName);
                    long detectionGeneration = 0;
                    int detectionInProgress = 0;
                    long detectionEndedAt = 0;

                    void DetectionStarted(object? sender, EventArgs args)
                    {
                        Volatile.Write(ref detectionInProgress, 1);
                        Interlocked.Increment(ref detectionGeneration);
                    }

                    void DetectionEnded(object? sender, EventArgs args)
                    {
                        Volatile.Write(ref detectionInProgress, 0);
                        Interlocked.Exchange(ref detectionEndedAt, DateTime.UtcNow.Ticks);
                        Interlocked.Increment(ref detectionGeneration);
                    }

                    // A replacement connection must observe detection notifications before
                    // its first snapshot. Otherwise a reconnect can publish the temporary
                    // controller list that OpenRGB exposes halfway through device detection.
                    newClient.DetectionStarted += DetectionStarted;
                    newClient.DetectionEnded += DetectionEnded;
                    try
                    {
                        long snapshotGeneration = Interlocked.Read(ref detectionGeneration);
                        uint[] controllerIds = newClient.GetControllerIds();
                        HashSet<uint> ids = controllerIds.ToHashSet();
                        var devices = new Dictionary<uint, (int Index, Device Device)>();

                        for (int i = 0; i < controllerIds.Length; i++)
                        {
                            Device device = newClient.GetControllerData(i);
                            devices[controllerIds[i]] = (i, device);

                            int directModeIndex = Array.FindIndex(device.Modes, mode => mode.Name == "Direct");
                            if (directModeIndex >= 0)
                                newClient.UpdateMode(i, device, directModeIndex);
                        }

                        long endedAt = Interlocked.Read(ref detectionEndedAt);
                        bool detectionJustEnded = endedAt > 0 && DateTime.UtcNow.Ticks - endedAt < DetectionSnapshotQuietPeriod.Ticks;
                        if (Volatile.Read(ref detectionInProgress) != 0 ||
                            Interlocked.Read(ref detectionGeneration) != snapshotGeneration || detectionJustEnded)
                            throw new InvalidOperationException("OpenRGB device detection changed while reading the replacement controller snapshot");

                        HashSet<uint> previousIds = _controllerIds.GetValueOrDefault(definition) ?? [];
                        uint[] removedControllers = previousIds.Except(ids).ToArray();
                        if (removedControllers.Length > 0 && !IsRemovalSnapshotStable(definition, ids))
                            throw new InvalidOperationException("OpenRGB controller removals are waiting for a confirming snapshot");
                        if (removedControllers.Length == 0)
                            _pendingRemovalSnapshots.Remove(definition);

                        replacements.Add((oldClient, newClient, definition, ids, devices));
                    }
                    catch
                    {
                        newClient.Dispose();
                        throw;
                    }
                    finally
                    {
                        newClient.DetectionStarted -= DetectionStarted;
                        newClient.DetectionEnded -= DetectionEnded;
                    }
                }

                foreach ((OpenRgbClient? oldClient, OpenRgbClient newClient, OpenRGBServerDefinition definition, HashSet<uint> ids,
                             Dictionary<uint, (int Index, Device Device)> devices) in replacements)
                {
                    HashSet<uint> previousIds = _controllerIds.GetValueOrDefault(definition) ?? [];
                    uint[] retainedControllers = previousIds.Intersect(ids).ToArray();
                    (uint[] removedControllers, uint[] addedControllers) = DiffControllerIds(previousIds, ids);
                    foreach (uint retained in retainedControllers)
                    {
                        foreach (OpenRGBUpdateQueue queue in _updateQueues.Where(queue => oldClient != null &&
                                     queue.UsesClient(oldClient) && queue.ControllerId == retained))
                            queue.ReplaceClient(newClient, retained);

                        foreach (IRGBDevice rgbDevice in _deviceControllers
                                     .Where(pair => oldClient != null && ReferenceEquals(pair.Value.Client, oldClient) && pair.Value.ControllerId == retained)
                                     .Select(pair => pair.Key).ToList())
                            _deviceControllers[rgbDevice] = (newClient, retained);
                    }

                    foreach (uint removed in removedControllers)
                    {
                        List<IRGBDevice> removedDevices = _deviceControllers
                            .Where(pair => oldClient != null && ReferenceEquals(pair.Value.Client, oldClient) && pair.Value.ControllerId == removed)
                            .Select(pair => pair.Key).ToList();
                        foreach (IRGBDevice removedDevice in removedDevices)
                        {
                            RemoveDevice(removedDevice);
                            _deviceControllers.Remove(removedDevice);
                            removedDevice.Dispose();
                        }

                        if (oldClient != null)
                            _updateQueues.RemoveAll(queue => queue.UsesClient(oldClient) && queue.ControllerId == removed);
                    }

                    foreach (uint added in addedControllers)
                    {
                        if (!devices.TryGetValue(added, out (int Index, Device Device) addedData))
                            continue;
                        foreach (IRGBDevice addedDevice in CreateDevicesForController(newClient, definition, addedData.Index, added, addedData.Device))
                            AddDevice(addedDevice);
                    }

                    if (oldClient != null)
                    {
                        int clientIndex = _clients.IndexOf(oldClient);
                        _clients[clientIndex] = newClient;
                        _clientDefinitions.Remove(oldClient);
                    }
                    else
                    {
                        _clients.Add(newClient);
                    }
                    _clientDefinitions[newClient] = definition;
                    Subscribe(newClient);
                    _controllerIds[definition] = ids;
                    definition.Connected = true;
                    definition.LastError = null;
                    if (oldClient != null)
                    {
                        Unsubscribe(oldClient);
                        oldClient.Dispose();
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                foreach ((_, OpenRgbClient newClient, OpenRGBServerDefinition definition, _, _) in replacements)
                {
                    newClient.Dispose();
                    definition.Connected = false;
                    definition.LastError = exception.Message;
                }

                try { Throw(exception); }
                catch { }
                return false;
            }
        }
    }

    /// <inheritdoc />
    protected override void InitializeSDK()
    {
        foreach (OpenRGBServerDefinition? deviceDefinition in DeviceDefinitions)
        {
            try
            {
                OpenRgbClient openRgb = new(ip: deviceDefinition.Ip, port: deviceDefinition.Port, name: deviceDefinition.ClientName, autoConnect: true);
                _clients.Add(openRgb);
                _clientDefinitions[openRgb] = deviceDefinition;
                Subscribe(openRgb);
                deviceDefinition.Connected = true;
            }
            catch (Exception e)
            {
                deviceDefinition.Connected = false;
                deviceDefinition.LastError = e.Message;
                try { Throw(e); }
                catch { }
            }
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<IRGBDevice> LoadDevices()
    {
        var loadedDevices = new List<IRGBDevice>();

        foreach (OpenRgbClient? openRgb in _clients)
        {
            OpenRGBServerDefinition definition = _clientDefinitions[openRgb];
            try
            {
                uint[] controllerIds = openRgb.GetControllerIds();
                var controllerData = new List<(int Index, uint Id, Device Device)>(controllerIds.Length);

                // Treat the initial snapshot as one transaction. OpenRGB can answer the
                // controller-list request while a long detection pass is still rebuilding
                // individual controller data. Publishing only part of that snapshot makes
                // Artemis believe devices were removed and can invalidate layer bindings.
                for (int i = 0; i < controllerIds.Length; i++)
                {
                    Device device = openRgb.GetControllerData(i);
                    controllerData.Add((i, controllerIds[i], device));
                }

                _controllerIds[definition] = controllerIds.ToHashSet();
                _pendingRemovalSnapshots.Remove(definition);

                foreach ((int index, uint controllerId, Device device) in controllerData)
                    loadedDevices.AddRange(CreateDevicesForController(openRgb, definition, index, controllerId, device));

                definition.Connected = true;
                definition.LastError = null;
            }
            catch (Exception exception)
            {
                // A request timeout during startup means the server is reachable but its
                // device list is not ready yet. Do not surface this as a critical RGB.NET
                // initialization failure: the Artemis feature must finish enabling so its
                // reconnect timer can retry later with a fresh client.
                definition.Connected = false;
                definition.LastError = exception.Message;
                _controllerIds[definition] = [];
                _pendingRemovalSnapshots.Remove(definition);
            }
        }

        return loadedDevices;
    }

    private IReadOnlyList<IRGBDevice> CreateDevicesForController(OpenRgbClient client, OpenRGBServerDefinition definition,
        int controllerIndex, uint controllerId, Device device)
    {
        int directModeIndex = Array.FindIndex(device.Modes, d => d.Name == "Direct");
        if (directModeIndex >= 0)
            client.UpdateMode(controllerIndex, device, directModeIndex);
        else if (!ForceAddAllDevices)
            return [];

        if (device.Zones.Length == 0 || device.Zones.All(z => z.LedCount == 0))
            return [];

        OpenRGBUpdateQueue updateQueue = new(GetUpdateTrigger(), controllerIndex, client, device);
        _updateQueues.Add(updateQueue);
        string serverIdentity = $"{definition.Ip}:{definition.Port}";
        var result = new List<IRGBDevice>();

        bool anyZoneHasSegments = device.Zones.Any(z => z.Segments.Length > 0);
        bool splitDeviceByZones = anyZoneHasSegments || PerZoneDeviceFlag.HasFlag(Helper.GetRgbNetDeviceType(device.Type));
        if (!splitDeviceByZones)
        {
            result.Add(new OpenRGBGenericDevice(new OpenRGBDeviceInfo(device, serverIdentity, controllerId), updateQueue));
        }
        else
        {
            int totalLedCount = 0;
            for (int zoneIndex = 0; zoneIndex < device.Zones.Length; zoneIndex++)
            {
                Zone zone = device.Zones[zoneIndex];
                if (zone.LedCount <= 0)
                    continue;

                if (zone.Segments.Length <= 0)
                {
                    result.Add(new OpenRGBZoneDevice(new OpenRGBDeviceInfo(device, serverIdentity, controllerId), totalLedCount, zoneIndex, zone, updateQueue));
                    totalLedCount += (int)zone.LedCount;
                }
                else
                {
                    for (int segmentIndex = 0; segmentIndex < zone.Segments.Length; segmentIndex++)
                    {
                        Segment segment = zone.Segments[segmentIndex];
                        result.Add(new OpenRGBSegmentDevice(new OpenRGBDeviceInfo(device, serverIdentity, controllerId), totalLedCount,
                            zoneIndex, segmentIndex, segment, updateQueue));
                        totalLedCount += (int)segment.LedCount;
                    }
                }
            }
        }

        foreach (IRGBDevice rgbDevice in result)
            _deviceControllers[rgbDevice] = (client, controllerId);
        return result;
    }

    internal static (uint[] Removed, uint[] Added) DiffControllerIds(IEnumerable<uint> previousIds, IEnumerable<uint> currentIds)
    {
        HashSet<uint> previous = previousIds.ToHashSet();
        HashSet<uint> current = currentIds.ToHashSet();
        return (previous.Except(current).ToArray(), current.Except(previous).ToArray());
    }

    private bool IsRemovalSnapshotStable(OpenRGBServerDefinition definition, HashSet<uint> currentIds)
    {
        if (!_pendingRemovalSnapshots.TryGetValue(definition, out PendingRemovalSnapshot? pending) ||
            !pending.ControllerIds.SetEquals(currentIds))
        {
            _pendingRemovalSnapshots[definition] = new PendingRemovalSnapshot(currentIds.ToHashSet(), DateTime.UtcNow);
            return false;
        }

        if (DateTime.UtcNow - pending.FirstSeenAt < RemovalSnapshotStabilityPeriod)
            return false;

        _pendingRemovalSnapshots.Remove(definition);
        return true;
    }

    private bool TryBeginControllerSnapshot(OpenRgbClient client, out long generation)
    {
        lock (_detectionStateLock)
        {
            generation = _detectionGenerations.GetValueOrDefault(client);
            long endedAt = _detectionEndedAt.GetValueOrDefault(client);
            bool detectionJustEnded = endedAt > 0 && DateTime.UtcNow.Ticks - endedAt < DetectionSnapshotQuietPeriod.Ticks;
            return !_detectingClients.Contains(client) && !detectionJustEnded;
        }
    }

    private bool IsControllerSnapshotValid(OpenRgbClient client, long generation)
    {
        lock (_detectionStateLock)
            return !_detectingClients.Contains(client) && _detectionGenerations.GetValueOrDefault(client) == generation;
    }

    /// <inheritdoc />
    protected override void Reset()
    {
        try
        {
            base.Reset();
        }
        finally
        {
            // A failed RGB.NET device/update-trigger teardown must not retain dead sockets.
            DisposeClients();
        }
    }

    private void DisposeClients()
    {
        foreach (OpenRgbClient client in _clients)
        {
            try
            {
                Unsubscribe(client);
                client.Dispose();
            }
            catch { /* at least we tried */ }
        }

        _clients.Clear();
        _clientDefinitions.Clear();
        _controllerIds.Clear();
        _pendingRemovalSnapshots.Clear();
        _updateQueues.Clear();
        _deviceControllers.Clear();
        lock (_detectionStateLock)
        {
            _detectingClients.Clear();
            _detectionGenerations.Clear();
            _detectionEndedAt.Clear();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                // RGB.NET teardown may throw when an update loop died. Always release
                // the singleton so Artemis can construct a healthy provider next time.
                DisposeClients();
                DeviceDefinitions.Clear();
                if (ReferenceEquals(_instance, this))
                    _instance = null;
            }
        }
    }

    #endregion

    private sealed record PendingRemovalSnapshot(HashSet<uint> ControllerIds, DateTime FirstSeenAt);
}
