using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Artemis.Core;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Services;
using RGB.NET.Core;
using RGB.NET.Devices.OpenRGB;
using Serilog;
using RGBDeviceProvider = RGB.NET.Devices.OpenRGB.OpenRGBDeviceProvider;
using Timer = System.Timers.Timer;

namespace Artemis.Plugins.Devices.OpenRGB;

[PluginFeature(Name = "OpenRGB Device Provider")]
public class OpenRGBDeviceProvider : DeviceProvider
{
    private static readonly TimeSpan SnapshotRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger _logger;
    private readonly IDeviceService _deviceService;
    private readonly IProfileService _profileService;
    private readonly PluginSetting<List<OpenRGBServerDefinition>> _deviceDefinitionsSettings;
    private readonly PluginSetting<bool> _forceAddAllDevicesSetting;
    private readonly PluginSetting<bool> _requestDirectForActiveLayersSetting;
    private readonly Timer _refreshTimer;
    private readonly Timer _reconnectTimer;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly HashSet<OpenRGBServerDefinition> _detectingServers = [];
    private readonly Dictionary<string, string> _sdkStatuses = [];
    private readonly object _stateLock = new();
    private volatile bool _running;

    public OpenRGBDeviceProvider(IDeviceService deviceService, IProfileService profileService, PluginSettings settings, ILogger logger)
    {
        _logger = logger;
        _deviceService = deviceService;
        _profileService = profileService;
        _forceAddAllDevicesSetting = settings.GetSetting("ForceAddAllDevices", false);
        _requestDirectForActiveLayersSetting = settings.GetSetting("RequestDirectForActiveLayers", true);
        _deviceDefinitionsSettings = settings.GetSetting("DeviceDefinitions", new List<OpenRGBServerDefinition>
        {
            new() { ClientName = "Artemis", Ip = "127.0.0.1", Port = 6742 }
        });

        CreateMissingLedsSupported = false;
        RemoveExcessiveLedsSupported = true;
        _refreshTimer = new Timer(500) { AutoReset = false };
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _reconnectTimer = new Timer(1000) { AutoReset = false };
        _reconnectTimer.Elapsed += OnReconnectTimerElapsed;
    }

    public override RGBDeviceProvider RgbDeviceProvider => RGBDeviceProvider.Instance;

    public override string GetDeviceIdentifier(IRGBDevice device) =>
        device is IOpenRGBDevice openRgbDevice ? openRgbDevice.PersistentId : base.GetDeviceIdentifier(device);

    public override string? GetReconnectionSignature(IRGBDevice device)
    {
        if (device.DeviceInfo is not OpenRGBDeviceInfo deviceInfo || device is not IOpenRGBDevice openRgbDevice)
            return null;

        // This is deliberately a provider-level logical-device signature, not a hardware identifier. It remains
        // stable when OpenRGB recreates a controller with a different runtime location, while the split-device part
        // and LED topology prevent a controller and one of its zones from being reconciled as the same device.
        int separatorIndex = openRgbDevice.PersistentId.LastIndexOf('|');
        string partIdentity = separatorIndex >= 0 ? openRgbDevice.PersistentId[(separatorIndex + 1)..] : string.Empty;
        string ledTopology = string.Join(",", device.Select(led => led.Id.ToString()).OrderBy(id => id, StringComparer.Ordinal));
        return string.Join("|", deviceInfo.ServerIdentity, deviceInfo.Manufacturer, deviceInfo.Model, deviceInfo.DeviceType, partIdentity, ledTopology);
    }

    public override IEnumerable<string> GetLegacyDeviceIdentifiers(IRGBDevice device) =>
        device is IOpenRGBDevice { LegacyPersistentId: { } legacyPersistentId }
            ? [legacyPersistentId]
            : [];

    public override string? GetParentDeviceIdentifier(string deviceIdentifier)
    {
        int separatorIndex = deviceIdentifier.LastIndexOf('|');
        if (separatorIndex < 0 || separatorIndex == deviceIdentifier.Length - 1)
            return null;

        string partIdentity = deviceIdentifier[(separatorIndex + 1)..];
        return partIdentity.StartsWith("zone:", StringComparison.Ordinal)
            ? deviceIdentifier[..separatorIndex]
            : null;
    }

    public string SdkStatus
    {
        get
        {
            lock (_stateLock)
                return _sdkStatuses.Count == 0
                    ? "SDK status: not connected"
                    : "SDK status: " + string.Join("; ", _sdkStatuses.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} — {pair.Value}"));
        }
    }

    public override void Enable()
    {
        _running = true;
        RGBDeviceProvider provider = RgbDeviceProvider;

        provider.Exception += Provider_OnException;
        provider.DeviceListUpdated += ProviderOnDeviceListUpdated;
        provider.ConnectionLost += ProviderOnConnectionLost;
        provider.DetectionStarted += ProviderOnDetectionStarted;
        provider.DetectionEnded += ProviderOnDetectionEnded;
        _deviceService.DeviceAdded += DeviceServiceOnDeviceAdded;
        _deviceService.DeviceReconnected += DeviceServiceOnDeviceAdded;
        _profileService.ProfileActivated += ProfileServiceOnProfileActivated;

        foreach (OpenRGBServerDefinition definition in _deviceDefinitionsSettings.Value)
            provider.DeviceDefinitions.Add(definition);
        provider.ForceAddAllDevices = _forceAddAllDevicesSetting.Value;
        provider.ForceRequestDirectModeOnRefresh = _requestDirectForActiveLayersSetting.Value;
        provider.IsSdkDeviceInUse = IsSdkDeviceInUse;

        try
        {
            _deviceService.AddDeviceProvider(this);
        }
        catch
        {
            _deviceService.DeviceAdded -= DeviceServiceOnDeviceAdded;
            _deviceService.DeviceReconnected -= DeviceServiceOnDeviceAdded;
            _profileService.ProfileActivated -= ProfileServiceOnProfileActivated;
            DetachProviderEvents(provider);
            provider.IsSdkDeviceInUse = null;
            provider.ForceRequestDirectModeOnRefresh = false;
            provider.Dispose();
            _running = false;
            throw;
        }

        UpdateStatuses();
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
        if (provider.DeviceDefinitions.Any(definition => !definition.Connected))
            _reconnectTimer.Start();
    }

    public override void Disable()
    {
        _running = false;
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        _deviceService.DeviceAdded -= DeviceServiceOnDeviceAdded;
        _deviceService.DeviceReconnected -= DeviceServiceOnDeviceAdded;
        _profileService.ProfileActivated -= ProfileServiceOnProfileActivated;
        RGBDeviceProvider provider = RgbDeviceProvider;

        try
        {
            _deviceService.RemoveDeviceProvider(this);
        }
        finally
        {
            DetachProviderEvents(provider);
            provider.IsSdkDeviceInUse = null;
            provider.ForceRequestDirectModeOnRefresh = false;
            provider.Dispose();
            lock (_stateLock)
            {
                _detectingServers.Clear();
                _sdkStatuses.Clear();
            }
        }
    }

    private void DetachProviderEvents(RGBDeviceProvider provider)
    {
        provider.Exception -= Provider_OnException;
        provider.DeviceListUpdated -= ProviderOnDeviceListUpdated;
        provider.ConnectionLost -= ProviderOnConnectionLost;
        provider.DetectionStarted -= ProviderOnDetectionStarted;
        provider.DetectionEnded -= ProviderOnDetectionEnded;
    }

    private void Provider_OnException(object sender, ExceptionEventArgs args)
    {
        // Connection attempts and snapshot confirmation are expected while OpenRGB
        // restarts or scans. Logging their stack traces on every retry creates a
        // surprising amount of allocation and disk activity.
        if (args.Exception is TimeoutException ||
            args.Exception.Message == "OpenRGB controller removals are waiting for a confirming snapshot")
        {
            _logger.Debug("OpenRGB reconciliation deferred: {Message}", args.Exception.Message);
            return;
        }

        _logger.Debug(args.Exception, "OpenRGB Exception: {message}", args.Exception.Message);
    }

    private void ProviderOnDetectionStarted(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            _detectingServers.Add(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "device detection in progress";
        }
        // A previously queued refresh/reconnect must not sample OpenRGB while its
        // controller registry is intentionally incomplete.
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        _logger.Debug("OpenRGB detection started for {Server}; pausing device-list reconciliation", GetDefinitionKey(definition));
    }

    private void ProviderOnDetectionEnded(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            _detectingServers.Remove(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "device detection complete; waiting for device list to settle";
        }
        _logger.Debug("OpenRGB detection ended for {Server}; waiting for the post-detection quiet period", GetDefinitionKey(definition));
        QueueReconcile();
    }

    private void ProviderOnDeviceListUpdated(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            if (_detectingServers.Contains(definition))
                return;
        }

        QueueReconcile();
    }

    private void ProviderOnConnectionLost(OpenRGBServerDefinition definition)
    {
        definition.Connected = false;
        lock (_stateLock)
        {
            _detectingServers.Remove(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "disconnected";
        }
        ScheduleReconnect(SnapshotRetryDelay);
    }

    private void DeviceServiceOnDeviceAdded(object? sender, DeviceEventArgs args)
    {
        if (_requestDirectForActiveLayersSetting.Value && ReferenceEquals(args.Device.DeviceProvider, this))
            QueueReconcile();
    }

    private void ProfileServiceOnProfileActivated(object? sender, ProfileConfigurationEventArgs args)
    {
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
    }

    private bool IsSdkDeviceInUse(OpenRGBServerDefinition definition, uint sdkDeviceId)
    {
        string serverIdentity = GetDefinitionKey(definition);
        HashSet<ArtemisDevice> devices = _deviceService.Devices
            .Where(device => device.IsEnabled && device.IsConnected &&
                             device.RgbDevice.DeviceInfo is OpenRGBDeviceInfo info &&
                             info.ServerIdentity == serverIdentity && info.ControllerId == sdkDeviceId)
            .ToHashSet();
        if (devices.Count == 0)
            return false;

        return _profileService.ProfileCategories
            .SelectMany(category => category.ProfileConfigurations)
            .Select(configuration => configuration.Profile)
            .Where(profile => profile != null)
            .Any(profile => profile!.GetAllLayers().Any(layer =>
                layer.Enabled && layer.Leds.Any(led => devices.Contains(led.Device))));
    }

    private void QueueReconcile()
    {
        if (!_running)
            return;

        // OpenRGB emits several list changes while a detection pass is settling. Wait
        // for a short quiet period so Artemis never sees an intermediate controller list.
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e) => await ReconcileDeviceList();

    private async Task ReconcileDeviceList()
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            lock (_stateLock)
            {
                if (_detectingServers.Count > 0)
                    return;
            }

            if (!RgbDeviceProvider.TryRefreshDevices() || RgbDeviceProvider.HasPendingRemovals)
                ScheduleReconnect(GetReconnectDelay());
            UpdateStatuses();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            lock (_stateLock)
            {
                if (_detectingServers.Count > 0)
                {
                    ScheduleReconnect(SnapshotRetryDelay);
                    return;
                }
            }

            bool repaired = RgbDeviceProvider.DeviceDefinitions.Any(definition => !definition.Connected)
                ? RgbDeviceProvider.TryReconnectDevices()
                : RgbDeviceProvider.TryRefreshDevices();
            if (!repaired)
            {
                ScheduleReconnect(GetReconnectDelay());
                return;
            }

            if (RgbDeviceProvider.HasPendingRemovals)
                ScheduleReconnect(SnapshotRetryDelay);

            UpdateStatuses();
            if (_requestDirectForActiveLayersSetting.Value)
                QueueReconcile();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void UpdateStatuses()
    {
        lock (_stateLock)
        {
            foreach (OpenRGBServerDefinition definition in RgbDeviceProvider.DeviceDefinitions)
                _sdkStatuses[GetDefinitionKey(definition)] = definition.Connected
                    ? $"protocol v{RgbDeviceProvider.ProtocolVersionFor(definition)}, connected"
                    : $"disconnected{(string.IsNullOrWhiteSpace(definition.LastError) ? string.Empty : $": {definition.LastError}")}";
        }
    }

    private TimeSpan GetReconnectDelay() =>
        RgbDeviceProvider.DeviceDefinitions.Any(definition => !definition.Connected)
            ? ConnectionRetryDelay
            : SnapshotRetryDelay;

    private void ScheduleReconnect(TimeSpan delay)
    {
        if (!_running)
            return;

        _reconnectTimer.Stop();
        _reconnectTimer.Interval = delay.TotalMilliseconds;
        _reconnectTimer.Start();
    }

    private static string GetDefinitionKey(OpenRGBServerDefinition definition) => $"{definition.Ip}:{definition.Port}";
}
